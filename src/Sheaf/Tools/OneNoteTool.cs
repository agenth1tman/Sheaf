using Sheaf.Core;
using Sheaf.Core.IO;
using Sheaf.Core.Office;
using Sheaf.Ui;

namespace Sheaf.Tools;

/// <summary>Puts PDFs into OneNote, one OneNote page per file, with the PDF attached.</summary>
internal sealed class OneNoteTool() : Tool(Definition)
{
    public static readonly ToolInfo Definition = new("pdf-to-onenote", "PDF to OneNote",
        "Put a PDF on a OneNote page you can write on and search.",
        ToolCategory.Convert, "file", FileKind.Pdf, "Send to OneNote", "Sending…", FileBadge: "ONE");

    public override string? Unavailable => OneNoteExport.IsAvailable
        ? null
        : "Needs the OneNote desktop app, free from Microsoft at onenote.com. Other versions of OneNote can’t receive pages from other apps; in those, Insert → File Printout puts a PDF on a page.";

    protected override Control BuildOptions() => Make.Stack(Make.Hint(
        "Each PDF becomes a new page in OneNote’s Quick Notes: the PDF attached at the top, and every page below as a picture. OneNote reads the text in the pictures so you can search it, and you can write and draw over them."));

    public override async Task<ToolResult> RunAsync(ToolRun run)
    {
        int pages = 0;
        ToolResult result = await run.EachFileAsync("Sending", file =>
        {
            var progress = new Progress<string>(run.Report);
            pages += OneNoteExport.SendAsync(file.Path, progress, run.Cancel).GetAwaiter().GetResult();
            return [];
        });
        return result with { Detail = pages == 0 ? null : $"Sent {Words.Pages(pages)} to OneNote’s Quick Notes" };
    }
}
