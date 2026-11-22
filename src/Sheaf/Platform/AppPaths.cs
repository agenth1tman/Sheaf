namespace Sheaf.Platform;

/// <summary>Where Sheaf keeps its own files. Sandbox mode moves all of them into one folder for test runs.</summary>
internal static class AppPaths
{
    public static string DataFolder { get; private set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sheaf");

    public static bool IsSandbox { get; private set; }

    public static string SettingsFile => Path.Combine(DataFolder, "settings.json");

    public static string HistoryFile => Path.Combine(DataFolder, "history.json");

    public static string LogFile => Path.Combine(DataFolder, "activity.log");

    public static string FontsFolder => Path.Combine(DataFolder, "Fonts");

    public static string SignatureFile => Path.Combine(DataFolder, "signature.json");

    public static string ColorsFile => Path.Combine(DataFolder, "colors.json");

    /// <summary>Where open documents keep their working copies; each tab removes its own folder when it closes.</summary>
    public static string WorkFolder => IsSandbox ? Path.Combine(DataFolder, "work") : Path.Combine(Path.GetTempPath(), "Sheaf", "work");

    public static string NewWorkFolder() => Path.Combine(WorkFolder, Guid.NewGuid().ToString("N"));

    /// <summary>Removes working copies left behind by a crash, once they are a day old.</summary>
    public static void CleanOldWork()
    {
        try
        {
            if (!Directory.Exists(WorkFolder)) return;
            foreach (var folder in new DirectoryInfo(WorkFolder).EnumerateDirectories().Where(d => d.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-1)))
                folder.Delete(recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public static void UseSandbox(string folder)
    {
        DataFolder = Path.GetFullPath(folder);
        IsSandbox = true;
    }

    public static void EnsureDataFolder() => Directory.CreateDirectory(DataFolder);
}
