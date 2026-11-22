using Sheaf.Core.IO;
using Sheaf.Core.Office;

namespace Sheaf.Tools;

/// <summary>
/// Every tool, in the order Home shows them. Batch tools (a <see cref="Tool"/> with a file list) and document
/// tools (open a PDF in the editor in one of its modes) live side by side. Add new tools here.
/// </summary>
internal static class ToolCatalog
{
    public static readonly ToolInfo Organize = new("organize", "Organize Pages", "Reorder, turn, add and delete pages.",
        ToolCategory.Organize, "pages", FileKind.Pdf, "Save", "Saving…", DocumentMode: "pages");

    static readonly ToolInfo[] DocumentTools =
    [
        Organize,
        new("edit", "Edit PDF", "Add text, shapes, drawings and images.", ToolCategory.EditAndReview, "draw", FileKind.Pdf, "Save", "Saving…", DocumentMode: "edit"),
        new("comment", "Comment", "Highlight, underline and add notes.", ToolCategory.EditAndReview, "comment", FileKind.Pdf, "Save", "Saving…", DocumentMode: "comment"),
        new("sign", "Fill & Sign", "Fill in forms and sign.", ToolCategory.EditAndReview, "signature", FileKind.Pdf, "Save", "Saving…", DocumentMode: "sign"),
        new("redact", "Redact", "Hide text, QR codes and images for good.", ToolCategory.ProtectAndOptimize, "redact", FileKind.Pdf, "Apply", "Redacting…", DocumentMode: "redact"),
    ];

    /// <summary>
    /// Each batch tool's description and its factory. Home lists the descriptions; a tool (with its option controls)
    /// is only built when its tab opens, and each tab gets its own.
    /// </summary>
    static readonly (ToolInfo Info, Func<Tool> Create)[] BatchTools =
    [
        (MergeTool.Definition, () => new MergeTool()),
        (SplitTool.Definition, () => new SplitTool()),
        (RemovePagesTool.Definition, () => new RemovePagesTool()),
        (ExtractTool.Definition, () => new ExtractTool()),
        (RotateTool.Definition, () => new RotateTool()),
        (CompressTool.Definition, () => new CompressTool()),
        (ImagesToPdfTool.Definition, () => new ImagesToPdfTool()),
        Office(new ToolInfo("word-to-pdf", "Word to PDF", "Convert DOC and DOCX documents to PDF.",
            ToolCategory.Convert, "file", FileKind.Word, "Convert to PDF", "Converting…", FileBadge: "DOC"), Conversion.WordToPdf, ".pdf"),
        Office(new ToolInfo("excel-to-pdf", "Excel to PDF", "Convert XLS and XLSX spreadsheets to PDF.",
            ToolCategory.Convert, "file", FileKind.Excel, "Convert to PDF", "Converting…", FileBadge: "XLS"), Conversion.ExcelToPdf, ".pdf"),
        Office(new ToolInfo("powerpoint-to-pdf", "PowerPoint to PDF", "Convert PPT and PPTX slides to PDF.",
            ToolCategory.Convert, "file", FileKind.PowerPoint, "Convert to PDF", "Converting…", FileBadge: "PPT"), Conversion.PowerPointToPdf, ".pdf"),
        (PdfToImagesTool.Definition, () => new PdfToImagesTool()),
        Office(new ToolInfo("pdf-to-word", "PDF to Word", "Turn a PDF into an editable DOCX document.",
            ToolCategory.Convert, "file", FileKind.Pdf, "Convert to Word", "Converting…", FileBadge: "DOC"), Conversion.PdfToWord, ".docx"),
        (PageNumbersTool.Definition, () => new PageNumbersTool()),
        (WatermarkTool.Definition, () => new WatermarkTool()),
        (ProtectTool.Definition, () => new ProtectTool()),
        (UnlockTool.Definition, () => new UnlockTool()),
        (RemoveHiddenDataTool.Definition, () => new RemoveHiddenDataTool()),
        (OcrTool.Definition, () => new OcrTool()),
        (OneNoteTool.Definition, () => new OneNoteTool()),
    ];

    static (ToolInfo, Func<Tool>) Office(ToolInfo info, Conversion conversion, string outputExtension) =>
        (info, () => new OfficeTool(info, conversion, outputExtension));

    static readonly string[] Order =
    [
        "merge", "split", "organize", "extract", "remove", "rotate",
        "edit", "comment", "sign", "numbers", "watermark",
        "images-to-pdf", "word-to-pdf", "excel-to-pdf", "powerpoint-to-pdf", "pdf-to-images", "pdf-to-word", "pdf-to-onenote",
        "redact", "protect", "unlock", "compress", "ocr", "remove-hidden",
    ];

    /// <summary>All tools for Home, grouped by category in display order.</summary>
    public static IReadOnlyList<ToolInfo> All { get; } =
        [.. BatchTools.Select(tool => tool.Info).Concat(DocumentTools).OrderBy(info => Array.IndexOf(Order, info.Id))];

    /// <summary>A fresh instance of a batch tool for a new tab.</summary>
    public static Tool? Create(string id) => BatchTools.FirstOrDefault(tool => tool.Info.Id == id).Create?.Invoke();

    public static ToolInfo? FindInfo(string id) => All.FirstOrDefault(info => info.Id == id);

    /// <summary>The editor mode a document tool opens ("edit", "redact"), or null for batch tools.</summary>
    public static string? FindPageTool(string id) => DocumentTools.FirstOrDefault(info => info.Id == id)?.DocumentMode;

    /// <summary>The tool that opens a file passed on the command line or dropped, for files that aren't PDFs.</summary>
    public static string? ForFile(string path) => FileKinds.Of(path) switch
    {
        FileKind.Image => "images-to-pdf",
        FileKind.Word => "word-to-pdf",
        FileKind.Excel => "excel-to-pdf",
        FileKind.PowerPoint => "powerpoint-to-pdf",
        _ => null,
    };
}
