using System.Globalization;
using System.Text;
using Microsoft.Win32;

namespace Sheaf.Core.Office;

/// <summary>
/// Office habits that break automation. Word asks before it turns a PDF into a document, and when Word runs hidden
/// that question has no window, so nobody can answer it and Word waits for ever. And when an app is stopped while it
/// opens a file, Office takes that for a crash: it asks about the file next time, and may list it under Disabled
/// Items and ask on every later try.
/// </summary>
internal static class OfficeRegistry
{
    const string OfficeKey = @"Software\Microsoft\Office";
    const string PdfPrompt = "DisableConvertPdfWarning";
    static readonly Lock Gate = new();
    static readonly List<(string Key, object? Before)> Saved = [];
    static int _quietRuns;

    /// <summary>Turns off Word's "Word will now convert your PDF" question until disposed, then restores the user's setting.</summary>
    public static IDisposable QuietPdfPrompt()
    {
        lock (Gate)
        {
            if (_quietRuns++ == 0)
            {
                Saved.Clear();
                foreach (string version in Versions())
                {
                    if (!HasKey($@"{OfficeKey}\{version}\Word")) continue;
                    string key = $@"{OfficeKey}\{version}\Word\Options";
                    using RegistryKey options = Registry.CurrentUser.CreateSubKey(key);
                    Saved.Add((key, options.GetValue(PdfPrompt)));
                    options.SetValue(PdfPrompt, 1, RegistryValueKind.DWord);
                }
            }
        }
        return new Restore();
    }

    sealed class Restore : IDisposable
    {
        bool _done;

        public void Dispose()
        {
            lock (Gate)
            {
                if (_done || --_quietRuns > 0) { _done = true; return; }
                _done = true;
                foreach (var (key, before) in Saved)
                {
                    using RegistryKey? options = Registry.CurrentUser.OpenSubKey(key, writable: true);
                    if (before is null) options?.DeleteValue(PdfPrompt, throwOnMissingValue: false);
                    else options?.SetValue(PdfPrompt, before, RegistryValueKind.DWord);
                }
                Saved.Clear();
            }
        }
    }

    /// <summary>Takes these files off the app's Disabled Items list, so Office opens them without asking first.</summary>
    public static void ForgetDisabled(string app, IEnumerable<string> files)
    {
        string[] paths = [.. files.Select(Path.GetFullPath)];
        foreach (string version in Versions())
        {
            using RegistryKey? items = Registry.CurrentUser.OpenSubKey($@"{OfficeKey}\{version}\{app}\Resiliency\DisabledItems", writable: true);
            if (items is null) continue;
            foreach (string name in items.GetValueNames())
            {
                // Each entry holds the file's path in UTF-16 after a short header.
                if (items.GetValue(name) is not byte[] data) continue;
                string text = Encoding.Unicode.GetString(data);
                if (paths.Any(path => text.Contains(path, StringComparison.OrdinalIgnoreCase))) items.DeleteValue(name, throwOnMissingValue: false);
            }
        }
    }

    /// <summary>
    /// Clears the note an Office app keeps while it runs (its process ID, in Resiliency\StartupItems). When the app is
    /// stopped that note stays, and the next start takes it for a crash and asks about the file it had open.
    /// </summary>
    public static void ForgetSession(string app, int processId)
    {
        foreach (string version in Versions())
        {
            using RegistryKey? items = Registry.CurrentUser.OpenSubKey($@"{OfficeKey}\{version}\{app}\Resiliency\StartupItems", writable: true);
            if (items is null) continue;
            foreach (string name in items.GetValueNames())
                if (items.GetValue(name) is byte[] { Length: >= 8 } data && BitConverter.ToInt32(data, 4) == processId)
                    items.DeleteValue(name, throwOnMissingValue: false);
        }
    }

    /// <summary>The Office versions with settings for this user, like "16.0".</summary>
    static IEnumerable<string> Versions()
    {
        using RegistryKey? office = Registry.CurrentUser.OpenSubKey(OfficeKey);
        return office?.GetSubKeyNames().Where(name => double.TryParse(name, NumberStyles.Float, CultureInfo.InvariantCulture, out _)).ToList() ?? [];
    }

    static bool HasKey(string path)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(path);
        return key is not null;
    }
}
