using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sheaf.Platform;

internal sealed class AppSettings
{
    public int WindowX { get; set; }
    public int WindowY { get; set; }
    public int WindowWidth { get; set; }
    public int WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }
    public string LastPage { get; set; } = "home";
    public bool SaveNextToOriginal { get; set; } = true;
    public string OutputFolder { get; set; } = "";
    public bool OpenFolderWhenDone { get; set; }
    public string LastOpenFolder { get; set; } = "";
    public List<string> RecentTools { get; set; } = [];
    public Ui.ThemeMode Theme { get; set; } = Ui.ThemeMode.Dark;

    public bool HasWindowBounds => WindowWidth > 0 && WindowHeight > 0;

    public void Clamp()
    {
        WindowWidth = Math.Clamp(WindowWidth, 0, 20000);
        WindowHeight = Math.Clamp(WindowHeight, 0, 20000);
        LastPage ??= "home";
        OutputFolder ??= "";
        LastOpenFolder ??= "";
        RecentTools = [.. (RecentTools ?? []).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().Take(SettingsStore.RecentToolLimit)];
        if (!SaveNextToOriginal && !Directory.Exists(OutputFolder)) SaveNextToOriginal = true;
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(List<HistoryEntry>))]
[JsonSerializable(typeof(Core.Editing.PageObject))]
[JsonSerializable(typeof(List<int>))]
internal sealed partial class AppJson : JsonSerializerContext;

/// <summary>Colours picked by hand (a custom colour, or one taken from the page), newest first, kept between runs.</summary>
internal static class RecentColors
{
    public const int Limit = 5;
    static List<int>? _colors;

    public static IReadOnlyList<Color> All => [.. Load().Select(Color.FromArgb)];

    public static void Add(Color color)
    {
        if (color.A == 0) return;
        List<int> colors = Load();
        colors.Remove(color.ToArgb());
        colors.Insert(0, color.ToArgb());
        if (colors.Count > Limit) colors.RemoveRange(Limit, colors.Count - Limit);
        SettingsStore.WriteJson(AppPaths.ColorsFile, colors, AppJson.Default.ListInt32);
    }

    static List<int> Load() => _colors ??= SettingsStore.ReadJson(AppPaths.ColorsFile, AppJson.Default.ListInt32) ?? [];
}

/// <summary>The signature people sign with: kept on this computer only, in the data folder.</summary>
internal static class SignatureStore
{
    public static Core.Editing.PageObject? Load() => SettingsStore.ReadJson(AppPaths.SignatureFile, AppJson.Default.PageObject);

    public static void Save(Core.Editing.PageObject signature) => SettingsStore.WriteJson(AppPaths.SignatureFile, signature, AppJson.Default.PageObject);

    public static void Remove()
    {
        try { File.Delete(AppPaths.SignatureFile); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ActivityLog.Error("removing the signature", ex); }
    }
}

/// <summary>Loads and saves settings.json in the data folder. Never Properties.Settings: that is tied to the exe path.</summary>
internal static class SettingsStore
{
    public const int RecentToolLimit = 4;

    public static event EventHandler? Saved;

    public static AppSettings Load()
    {
        AppSettings settings = ReadJson(AppPaths.SettingsFile, AppJson.Default.AppSettings) ?? new AppSettings();
        settings.Clamp();
        return settings;
    }

    public static void Save(AppSettings settings)
    {
        WriteJson(AppPaths.SettingsFile, settings, AppJson.Default.AppSettings);
        Saved?.Invoke(null, EventArgs.Empty);
    }

    internal static T? ReadJson<T>(string path, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type) where T : class
    {
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(path), type);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            ActivityLog.Error($"reading {Path.GetFileName(path)}; kept it as .bad and started from defaults", ex);
            try { File.Move(path, path + ".bad", overwrite: true); } catch (IOException) { }
            return null;
        }
    }

    /// <summary>Writes a temporary file first and swaps it in, so a crash mid-write never corrupts the file.</summary>
    internal static void WriteJson<T>(string path, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type)
    {
        try
        {
            AppPaths.EnsureDataFolder();
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(value, type));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ActivityLog.Error($"saving {Path.GetFileName(path)}", ex);
        }
    }
}
