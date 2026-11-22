using PdfSharp.Pdf;
using Sheaf.Core;
using Sheaf.Core.IO;
using Sheaf.Core.Pdf;
using Sheaf.Ui;
using Sheaf.Ui.Controls;

namespace Sheaf.Tools;

internal sealed class MergeTool() : Tool(Definition)
{
    public static readonly ToolInfo Definition = new("merge", "Merge PDF",
        "Combine several PDFs into one, in the order you choose.",
        ToolCategory.Organize, "merge", FileKind.Pdf, "Merge PDF", "Merging…", MinimumFiles: 2);

    protected override Control BuildOptions() =>
        Make.Hint("Pages follow the order of the list. Drag files, or use Move Up and Move Down, to change it.");

    public override Task<ToolResult> RunAsync(ToolRun run) => Task.Run(() =>
    {
        using PdfDocument merged = PageOps.Merge(run.Files.Select(file => new PdfSource(file.Path)), run.Cancel);
        int pages = merged.PageCount; // PDFsharp locks a document once it is saved
        string path = run.Output.NewFile(run.Files[0], "merged");
        PdfFiles.Save(merged, path);
        return ToolResult.Single(path, Words.Pages(pages));
    }, run.Cancel);
}

internal enum SplitMode { EveryPage, EveryN, Ranges }

internal sealed class SplitTool() : Tool(Definition)
{
    public static readonly ToolInfo Definition = new("split", "Split PDF",
        "Break a PDF into separate files by page ranges.",
        ToolCategory.Organize, "split", FileKind.Pdf, "Split PDF", "Splitting…");

    readonly ChoiceGroup<SplitMode> _mode = new("Split into", horizontal: false,
        ("One file per page", SplitMode.EveryPage),
        ("A file every few pages", SplitMode.EveryN),
        ("One file per range", SplitMode.Ranges));
    readonly InputBox _size = Make.Number(2, 1, 9999);
    readonly PagesField _ranges = new("Ranges", required: true);
    FormField? _sizeField;

    protected override Control BuildOptions()
    {
        _sizeField = new FormField("Pages per file", _size);
        _mode.Changed += (_, _) => ShowModeFields();
        var options = Make.Stack(new FormField("Split into", _mode), _sizeField, _ranges.Field);
        ShowModeFields();
        return options;
    }

    void ShowModeFields()
    {
        _sizeField!.Visible = _mode.Value == SplitMode.EveryN;
        _ranges.Field.Visible = _mode.Value == SplitMode.Ranges;
    }

    public override Control? Validate() => _mode.Value switch
    {
        SplitMode.EveryN => _sizeField!.Check(_size.Number is >= 1 ? null : "Enter how many pages go in each file.") ? null : _sizeField,
        SplitMode.Ranges => _ranges.Validate(),
        _ => null,
    };

    public override Task<ToolResult> RunAsync(ToolRun run)
    {
        SplitMode mode = _mode.Value;
        int size = _size.Number ?? 1;
        IReadOnlyList<PageSpan> ranges = _ranges.Spans;
        return run.EachFileAsync("Splitting", file =>
        {
            using PdfDocument input = PdfFiles.OpenForImport(file.Path);
            IReadOnlyList<PageSpan> spans = mode switch
            {
                SplitMode.EveryPage => PageOps.Chunks(input.PageCount, 1),
                SplitMode.EveryN => PageOps.Chunks(input.PageCount, size),
                _ => PageRanges.Fit(ranges, input.PageCount, file.Name),
            };
            return [PageFiles.Write(input, spans, file, run)];
        });
    }
}

internal sealed class ExtractTool() : Tool(Definition)
{
    public static readonly ToolInfo Definition = new("extract", "Extract Pages",
        "Copy the pages you pick into a new PDF.",
        ToolCategory.Organize, "extract", FileKind.Pdf, "Extract Pages", "Extracting…");

    readonly PagesField _pages = new("Pages to extract", required: true);
    readonly CheckBox _separate = Make.Check("Save each page as its own PDF");

    protected override Control BuildOptions() => Make.Stack(_pages.Field, _separate);

    public override Control? Validate() => _pages.Validate();

    public override Task<ToolResult> RunAsync(ToolRun run)
    {
        var pages = _pages.Snapshot();
        bool separate = _separate.Checked;
        return run.EachFileAsync("Extracting", file =>
        {
            using PdfDocument input = PdfFiles.OpenForImport(file.Path);
            IReadOnlyList<int> indexes = pages(input.PageCount, file.Name);
            if (separate)
                return [PageFiles.Write(input, [.. indexes.Select(i => new PageSpan(i + 1, i + 1))], file, run, "pages")];
            using PdfDocument output = PageOps.Assemble(input, indexes);
            string path = run.Output.FileFor(file, "extracted");
            PdfFiles.Save(output, path);
            return [path];
        });
    }
}

internal sealed class RemovePagesTool() : Tool(Definition)
{
    public static readonly ToolInfo Definition = new("remove", "Remove Pages",
        "Delete the pages you don’t need.",
        ToolCategory.Organize, "remove-page", FileKind.Pdf, "Remove Pages", "Removing…");

    readonly PagesField _pages = new("Pages to remove", required: true);

    protected override Control BuildOptions() => Make.Stack(_pages.Field);

    public override Control? Validate() => _pages.Validate();

    public override Task<ToolResult> RunAsync(ToolRun run)
    {
        var pages = _pages.Snapshot();
        return run.EachFileAsync("Removing pages from file", file =>
        {
            using PdfDocument input = PdfFiles.OpenForImport(file.Path);
            IReadOnlyList<int> keep = PageOps.Without(input.PageCount, pages(input.PageCount, file.Name));
            if (keep.Count == 0)
                throw new ToolException($"That would remove every page of “{file.Name}”.");
            using PdfDocument output = PageOps.Assemble(input, keep);
            string path = run.Output.FileFor(file, "pages-removed");
            PdfFiles.Save(output, path);
            return [path];
        });
    }
}

internal sealed class RotateTool() : Tool(Definition)
{
    public static readonly ToolInfo Definition = new("rotate", "Rotate PDF",
        "Turn pages that were scanned sideways or upside down.",
        ToolCategory.Organize, "rotate", FileKind.Pdf, "Rotate PDF", "Rotating…");

    readonly ChoiceGroup<int> _direction = new("Turn", horizontal: false,
        ("Right, 90° clockwise", 90), ("Left, 90° counterclockwise", 270), ("Upside down, 180°", 180));
    readonly PagesField _pages = new("Pages", required: false);

    protected override Control BuildOptions() => Make.Stack(new FormField("Turn", _direction), _pages.Field);

    public override Control? Validate() => _pages.Validate();

    public override Task<ToolResult> RunAsync(ToolRun run)
    {
        int degrees = _direction.Value;
        var pages = _pages.Snapshot();
        return run.EachFileAsync("Rotating", file =>
        {
            using PdfDocument document = PdfFiles.OpenForModify(file.Path);
            PageOps.Rotate(document, pages(document.PageCount, file.Name), degrees);
            string path = run.Output.FileFor(file, "rotated");
            PdfFiles.Save(document, path);
            return [path];
        });
    }
}

/// <summary>Writes one file per page span; several files go into their own new folder.</summary>
internal static class PageFiles
{
    public static string Write(PdfDocument input, IReadOnlyList<PageSpan> spans, SourceFile source, ToolRun run, string folderSuffix = "split")
    {
        if (spans.Count == 1)
            return Save(input, spans[0], run.Output.FileFor(source, $"pages-{spans[0]}"));

        string folder = run.Output.NewFolder(source, folderSuffix);
        foreach (PageSpan span in spans)
        {
            run.Cancel.ThrowIfCancellationRequested();
            Save(input, span, OutputNames.UniqueFile(folder, $"{source.BaseName}-{span}", ".pdf"));
        }
        return folder;
    }

    static string Save(PdfDocument input, PageSpan span, string path)
    {
        using PdfDocument part = PageOps.Assemble(input, Enumerable.Range(span.First - 1, span.Last - span.First + 1));
        PdfFiles.Save(part, path);
        return path;
    }
}
