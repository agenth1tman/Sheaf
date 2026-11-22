using PdfSharp.Pdf;
using Sheaf.Core;
using Sheaf.Core.IO;
using Sheaf.Core.Office;
using Sheaf.Core.Pdf;
using Sheaf.Core.Rendering;
using Sheaf.Ui;
using Sheaf.Ui.Controls;

namespace Sheaf.Tools;

internal sealed class ImagesToPdfTool() : Tool(Definition)
{
    public static readonly ToolInfo Definition = new("images-to-pdf", "Images to PDF",
        "Turn JPG, PNG and other images into a PDF.",
        ToolCategory.Convert, "image", FileKind.Image, "Convert to PDF", "Converting…", FileBadge: "JPG");

    readonly ChoiceGroup<PageSizeChoice> _size = new("Page size", horizontal: true,
        ("Fit image", PageSizeChoice.FitImage), ("A4", PageSizeChoice.A4), ("Letter", PageSizeChoice.Letter));
    readonly ChoiceGroup<PageOrientationChoice> _orientation = new("Orientation", horizontal: true,
        ("Auto", PageOrientationChoice.Auto), ("Portrait", PageOrientationChoice.Portrait), ("Landscape", PageOrientationChoice.Landscape));
    readonly ChoiceGroup<double> _margin = new("Margin", horizontal: true, ("None", 0), ("Small", 18), ("Large", 54));
    readonly CheckBox _combine = Make.Check("Combine all images into one PDF", isChecked: true);

    protected override Control BuildOptions()
    {
        _size.Value = PageSizeChoice.A4;
        _size.Changed += (_, _) => _orientation.Enabled = _size.Value != PageSizeChoice.FitImage;
        return Make.Stack(new FormField("Page size", _size), new FormField("Orientation", _orientation),
            new FormField("Margin", _margin), _combine);
    }

    public override Task<ToolResult> RunAsync(ToolRun run)
    {
        var layout = new ImagePageLayout(_size.Value, _orientation.Value, _margin.Value);
        return _combine.Checked ? Combined(run, layout) : OnePerImage(run, layout);
    }

    static Task<ToolResult> OnePerImage(ToolRun run, ImagePageLayout layout) => run.EachFileAsync("Converting", file =>
    {
        using var document = new PdfDocument();
        ImagePages.AddImagePage(document, file.Path, layout);
        string path = run.Output.NewFile(file, file.BaseName);
        PdfFiles.Save(document, path);
        return [path];
    });

    static Task<ToolResult> Combined(ToolRun run, ImagePageLayout layout) => Task.Run(() =>
    {
        var failures = new List<string>();
        using var document = new PdfDocument();
        for (int i = 0; i < run.Files.Count; i++)
        {
            run.Cancel.ThrowIfCancellationRequested();
            run.Report($"Adding image {i + 1} of {run.Files.Count}…", i, run.Files.Count);
            try { ImagePages.AddImagePage(document, run.Files[i].Path, layout); }
            catch (ToolException ex) { failures.Add(ex.Message); }
        }
        int pages = document.PageCount; // PDFsharp locks a document once it is saved
        if (pages == 0) return new ToolResult([], failures);

        string path = run.Output.NewFile(run.Files[0], run.Files.Count == 1 ? run.Files[0].BaseName : "images");
        PdfFiles.Save(document, path);
        return new ToolResult([path], failures, Words.Pages(pages));
    }, run.Cancel);
}

internal sealed class PdfToImagesTool() : Tool(Definition)
{
    public static readonly ToolInfo Definition = new("pdf-to-images", "PDF to Images",
        "Save each page as a JPG or PNG image.",
        ToolCategory.Convert, "image", FileKind.Pdf, "Convert to Images", "Converting…", FileBadge: "PNG");

    readonly ChoiceGroup<PageImageFormat> _format = new("Format", horizontal: true,
        ("JPG", PageImageFormat.Jpeg), ("PNG", PageImageFormat.Png));
    readonly ChoiceGroup<int> _dpi = new("Resolution", horizontal: false,
        ("Screen, 96 dpi", 96), ("Standard, 150 dpi", 150), ("Print, 300 dpi", 300));
    readonly PagesField _pages = new("Pages", required: false);

    protected override Control BuildOptions()
    {
        _dpi.Value = 150;
        return Make.Stack(new FormField("Format", _format), new FormField("Resolution", _dpi), _pages.Field);
    }

    public override Control? Validate() => _pages.Validate();

    public override Task<ToolResult> RunAsync(ToolRun run)
    {
        PageImageFormat format = _format.Value;
        int dpi = _dpi.Value;
        var pages = _pages.Snapshot();
        string extension = format == PageImageFormat.Png ? ".png" : ".jpg";
        return run.EachFileAsync("Converting", file =>
        {
            using PdfPreview preview = PdfPreview.OpenAsync(file.Path).GetAwaiter().GetResult();
            IReadOnlyList<int> indexes = pages(preview.PageCount, file.Name);
            if (indexes.Count == 1)
            {
                string single = run.Output.FileFor(file, $"page-{indexes[0] + 1}", extension);
                preview.SaveImageAsync(indexes[0], single, format, dpi, run.Cancel).GetAwaiter().GetResult();
                return [single];
            }

            string folder = run.Output.NewFolder(file, "images");
            string digits = new('0', preview.PageCount.ToString().Length);
            for (int i = 0; i < indexes.Count; i++)
            {
                int index = indexes[i];
                run.Cancel.ThrowIfCancellationRequested();
                run.Report($"Saving page {index + 1} of {preview.PageCount}…", i, indexes.Count);
                string path = Path.Combine(folder, $"{file.BaseName}-{(index + 1).ToString(digits)}{extension}");
                preview.SaveImageAsync(index, path, format, dpi, run.Cancel).GetAwaiter().GetResult();
            }
            return [folder];
        });
    }
}

/// <summary>
/// Word, Excel or PowerPoint to PDF, and PDF to Word: with Microsoft Office or LibreOffice when one is installed,
/// and with Sheaf's own converter otherwise (or when chosen).
/// </summary>
internal sealed class OfficeTool(ToolInfo info, Conversion conversion, string outputExtension) : Tool(info)
{
    ChoiceGroup<ConverterApp>? _converter;
    WrapLabel? _note;

    protected override Control BuildOptions()
    {
        ConverterApp installed = OfficeConverter.AppFor(conversion);
        _note = Make.Hint(Note(ConverterApp.BuiltIn));
        if (installed == ConverterApp.BuiltIn) return Make.Stack(_note);
        string name = installed == ConverterApp.MicrosoftOffice ? $"Microsoft {OfficeConverter.OfficeAppName(conversion)}" : "LibreOffice";
        _converter = new ChoiceGroup<ConverterApp>("Convert with", horizontal: true, ("Built In", ConverterApp.BuiltIn), (name, installed));
        _converter.Changed += (_, _) => _note.Text = Note(_converter.Value);
        return Make.Stack(new FormField("Convert with", _converter), _note);
    }

    string Note(ConverterApp app) => (app, conversion) switch
    {
        (ConverterApp.BuiltIn, Conversion.PdfToWord) =>
            "Converts inside Sheaf, no Office needed. Text, its sizes and weights, and pictures come through; columns and tables become plain paragraphs. Scanned pages come in as pictures with their text read by Windows.",
        (ConverterApp.BuiltIn, Conversion.PowerPointToPdf) =>
            "Converts inside Sheaf, no Office needed. Slides keep their text, shapes, pictures and tables; charts, SmartArt and effects are left out.",
        (ConverterApp.BuiltIn, Conversion.ExcelToPdf) =>
            "Converts inside Sheaf, no Office needed. Each sheet becomes a table of its cells; charts and pictures are left out.",
        (ConverterApp.BuiltIn, _) =>
            "Converts inside Sheaf, no Office needed. Text, lists, tables and pictures come through; headers, footers and text boxes are left out. Reads .docx (save .doc files as .docx first).",
        (ConverterApp.LibreOffice, Conversion.PdfToWord) => "Converts with LibreOffice in the background. Text and pictures come through; pages with complex layouts may shift.",
        (ConverterApp.LibreOffice, _) => "Converts with LibreOffice in the background, charts included. Reads older .doc, .xls and .ppt files too.",
        (_, Conversion.PdfToWord) => "Converts with Microsoft Word in the background. Columns and tables stay; pages with complex layouts may shift.",
        (_, Conversion.PowerPointToPdf) => "Converts with Microsoft PowerPoint, so slides look exactly as they do there, charts and effects included. Reads .ppt files too. PowerPoint shows a small progress window while it works.",
        _ => $"Converts with Microsoft {OfficeConverter.OfficeAppName(conversion)} in the background, so the PDF looks exactly as the file does there. Reads {(conversion == Conversion.ExcelToPdf ? ".xls" : ".doc")} files too.",
    };

    public override async Task<ToolResult> RunAsync(ToolRun run)
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var jobs = run.Files.Select(file => new ConversionJob(file.Path, ReserveName(run, file, taken))).ToList();
        var progress = new Progress<int>(done => run.Report($"Converted {done} of {jobs.Count}…", done, jobs.Count));
        run.Report(jobs.Count == 1 ? Info.BusyText : $"Converting 1 of {jobs.Count}…");

        IReadOnlyList<Exception?> errors = await OfficeConverter.ConvertAsync(conversion, jobs, progress, run.Cancel, _converter?.Value ?? ConverterApp.BuiltIn);
        // Files skipped after Cancel aren't failures: the run reads as stopped, and finished files stay.
        if (errors.Any(error => error is OperationCanceledException)) run.Cancel.ThrowIfCancellationRequested();
        var outputs = jobs.Where((_, i) => errors[i] is null).Select(job => job.Output).ToList();
        var failures = errors.OfType<Exception>().Select(Describe).ToList();
        return new ToolResult(outputs, failures);
    }

    string ReserveName(ToolRun run, SourceFile file, HashSet<string> taken)
    {
        string path = run.Output.NewFile(file, file.BaseName, outputExtension);
        for (int copy = 2; !taken.Add(path); copy++)
            path = run.Output.NewFile(file, $"{file.BaseName} ({copy})", outputExtension);
        return path;
    }

    static string Describe(Exception error) =>
        error is ToolException ? error.Message : $"The conversion failed: {error.Message}";
}
