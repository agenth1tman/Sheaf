using System.Globalization;

namespace Sheaf.Platform;

/// <summary>A plain text log of startups, finished tasks and errors, so "it failed once" can be traced.</summary>
internal static class ActivityLog
{
    const long RotateAtBytes = 512 * 1024;
    static readonly Lock WriteLock = new();

    public static void Write(string message)
    {
        string line = $"{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)}  {message}{Environment.NewLine}";
        lock (WriteLock)
        {
            try
            {
                AppPaths.EnsureDataFolder();
                RotateIfLarge();
                File.AppendAllText(AppPaths.LogFile, line);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public static void Error(string context, Exception ex) => Write($"ERROR {context}: {ex}");

    static void RotateIfLarge()
    {
        var file = new FileInfo(AppPaths.LogFile);
        if (file.Exists && file.Length > RotateAtBytes)
            File.Move(file.FullName, Path.ChangeExtension(file.FullName, ".old.log"), overwrite: true);
    }
}
