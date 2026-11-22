namespace Sheaf.Platform;

/// <summary>A file a tool produced.</summary>
internal sealed record HistoryEntry(string Path, string Tool, DateTime Made, long Size);

/// <summary>The list on the Recent page: files Sheaf has made, newest first.</summary>
internal static class History
{
    const int Limit = 300;
    static List<HistoryEntry>? _entries;

    public static event EventHandler? Changed;

    public static IReadOnlyList<HistoryEntry> Entries => _entries ??= Load();

    public static void Add(IEnumerable<string> paths, string tool)
    {
        var added = paths.Select(path => new HistoryEntry(path, tool, DateTime.Now, SizeOf(path))).Reverse().ToList();
        if (added.Count == 0) return;
        var known = added.Select(entry => entry.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Replace([.. added, .. Entries.Where(entry => !known.Contains(entry.Path))]);
    }

    /// <summary>A document opened in a tab, so Home lists it under Recent.</summary>
    public static void Remember(string path) => Add([path], "Opened");

    /// <summary>The newest entries that still exist on disk, each path once.</summary>
    public static IReadOnlyList<HistoryEntry> Recent(int count) =>
        [.. Entries.Where(entry => File.Exists(entry.Path) || Directory.Exists(entry.Path)).DistinctBy(entry => entry.Path.ToUpperInvariant()).Take(count)];

    public static void Remove(IEnumerable<HistoryEntry> entries)
    {
        var removed = entries.ToHashSet();
        Replace([.. Entries.Where(entry => !removed.Contains(entry))]);
    }

    public static void Clear() => Replace([]);

    static void Replace(List<HistoryEntry> entries)
    {
        _entries = entries.Count > Limit ? entries[..Limit] : entries;
        SettingsStore.WriteJson(AppPaths.HistoryFile, _entries, AppJson.Default.ListHistoryEntry);
        Changed?.Invoke(null, EventArgs.Empty);
    }

    static List<HistoryEntry> Load() =>
        SettingsStore.ReadJson(AppPaths.HistoryFile, AppJson.Default.ListHistoryEntry) ?? [];

    static long SizeOf(string path)
    {
        if (File.Exists(path)) return new FileInfo(path).Length;
        if (!Directory.Exists(path)) return 0;
        return new DirectoryInfo(path).EnumerateFiles().Sum(file => file.Length);
    }
}
