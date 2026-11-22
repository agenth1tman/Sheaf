namespace Sheaf.Core.IO;

[Flags]
public enum FileKind
{
    None = 0,
    Pdf = 1,
    Image = 2,
    Word = 4,
    Excel = 8,
    PowerPoint = 16,
}

public static class FileKinds
{
    static readonly Dictionary<string, FileKind> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = FileKind.Pdf,
        [".jpg"] = FileKind.Image,
        [".jpeg"] = FileKind.Image,
        [".png"] = FileKind.Image,
        [".bmp"] = FileKind.Image,
        [".gif"] = FileKind.Image,
        [".tif"] = FileKind.Image,
        [".tiff"] = FileKind.Image,
        [".doc"] = FileKind.Word,
        [".docx"] = FileKind.Word,
        [".rtf"] = FileKind.Word,
        [".odt"] = FileKind.Word,
        [".xls"] = FileKind.Excel,
        [".xlsx"] = FileKind.Excel,
        [".ods"] = FileKind.Excel,
        [".csv"] = FileKind.Excel,
        [".ppt"] = FileKind.PowerPoint,
        [".pptx"] = FileKind.PowerPoint,
        [".odp"] = FileKind.PowerPoint,
    };

    public static FileKind Of(string path) =>
        ByExtension.GetValueOrDefault(Path.GetExtension(path), FileKind.None);

    public static bool Matches(string path, FileKind accepted) => (Of(path) & accepted) != 0;

    public static IReadOnlyList<string> Extensions(FileKind kinds) =>
        [.. ByExtension.Where(pair => (pair.Value & kinds) != 0).Select(pair => pair.Key)];
}
