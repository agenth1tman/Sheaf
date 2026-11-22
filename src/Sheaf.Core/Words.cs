using System.Globalization;

namespace Sheaf.Core;

/// <summary>Counts and sizes written the way they appear in the UI.</summary>
public static class Words
{
    public static string Count(int count, string one, string many) =>
        $"{count.ToString("N0", CultureInfo.CurrentCulture)} {(count == 1 ? one : many)}";

    public static string Pages(int count) => Count(count, "page", "pages");

    public static string Files(int count) => Count(count, "file", "files");

    public static string Size(long bytes)
    {
        var culture = CultureInfo.CurrentCulture;
        if (bytes < 1024) return $"{bytes.ToString("N0", culture)} bytes";
        if (bytes < 1024 * 1024) return $"{(bytes / 1024.0).ToString("N0", culture)} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{(bytes / 1024.0 / 1024.0).ToString("N1", culture)} MB";
        return $"{(bytes / 1024.0 / 1024.0 / 1024.0).ToString("N2", culture)} GB";
    }
}
