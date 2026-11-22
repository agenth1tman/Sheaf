namespace Sheaf.Core.IO;

/// <summary>Picks names for new files and folders without ever replacing an existing one.</summary>
public static class OutputNames
{
    const int MaxBaseNameLength = 120;

    public static string UniqueFile(string folder, string baseName, string extension)
    {
        string name = CleanName(baseName);
        string candidate = Path.Combine(folder, name + extension);
        for (int copy = 2; File.Exists(candidate) || Directory.Exists(candidate); copy++)
            candidate = Path.Combine(folder, $"{name} ({copy}){extension}");
        return candidate;
    }

    public static string CreateUniqueFolder(string parent, string name)
    {
        string clean = CleanName(name);
        string candidate = Path.Combine(parent, clean);
        for (int copy = 2; File.Exists(candidate) || Directory.Exists(candidate); copy++)
            candidate = Path.Combine(parent, $"{clean} ({copy})");
        Directory.CreateDirectory(candidate);
        return candidate;
    }

    public static string CleanName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Trim().Select(c => invalid.Contains(c) ? '-' : c).ToArray();
        string clean = new string(chars).Trim(' ', '.');
        if (clean.Length > MaxBaseNameLength) clean = clean[..MaxBaseNameLength].TrimEnd(' ', '.');
        return clean.Length == 0 ? "document" : clean;
    }
}
