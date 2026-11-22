using Sheaf.Core;
using Sheaf.Core.IO;
using Sheaf.Core.Ocr;
using Sheaf.Core.Scanning;
using Sheaf.Ui;
using Sheaf.Ui.Controls;

namespace Sheaf.Tools;

/// <summary>Adds invisible OCR text to scanned PDFs so they can be searched and copied from.</summary>
internal sealed class OcrTool() : Tool(Definition)
{
    public static readonly ToolInfo Definition = new("ocr", "Make Searchable",
        "Read the text in scanned pages so you can search and copy it.",
        ToolCategory.ProtectAndOptimize, "ocr", FileKind.Pdf, "Make Searchable", "Reading text…");

    DropDown<string>? _language;

    public override string? Unavailable => TextRecognizer.Languages.Count == 0
        ? "Windows has no text recognition installed. Add a language with “Optical character recognition” in Windows Settings › Time & language › Language."
        : null;

    protected override Control? BuildOptions()
    {
        IReadOnlyList<OcrLanguage> languages = TextRecognizer.Languages;
        if (languages.Count == 0) return null;
        _language = new DropDown<string>("Language", [.. languages.Select(language => (language.Name, language.Tag))]) { Stretch = true };
        if (TextRecognizer.DefaultLanguage is { } preferred) _language.Value = preferred.Tag;
        return Make.Stack(
            new FormField("Language of the text", _language),
            Make.Hint("Uses the text recognition built into Windows; nothing leaves this PC. Pages that already have text are left as they are."));
    }

    public override async Task<ToolResult> RunAsync(ToolRun run)
    {
        string? language = _language?.Value;
        int read = 0, skipped = 0;
        ToolResult result = await run.EachFileAsync("Reading", file =>
        {
            string path = run.Output.FileFor(file, "searchable");
            var (pages, alreadyText) = SearchablePdf.MakeAsync(file.Path, path, language, null, run.Cancel).GetAwaiter().GetResult();
            read += pages;
            skipped += alreadyText;
            if (pages == 0)
            {
                File.Delete(path);
                return [];
            }
            return [path];
        });
        string? detail = (read, skipped) switch
        {
            (0, > 0) => "Every page already has text, so nothing needed reading",
            (_, > 0) => $"Read {Words.Pages(read)}; {Words.Pages(skipped)} already had text",
            _ => $"Read {Words.Pages(read)}",
        };
        return result with { Detail = detail };
    }
}
