using System.Runtime.InteropServices;
using System.Xml.Linq;
using Sheaf.Core.Rendering;

namespace Sheaf.Core.Office;

/// <summary>
/// Sends a PDF to OneNote: a new page in Quick Notes with the PDF attached and every page below it as a picture
/// (OneNote reads the text in pictures itself, so the page can be searched). Works with the OneNote desktop app,
/// which other apps can reach; OneNote for Windows 10 can't be reached by any app.
/// </summary>
public static class OneNoteExport
{
    const string ProgId = "OneNote.Application";
    const int UnfiledNotesSection = 1, NewPageStyleDefault = 0, Schema2013 = 2;
    const double PictureWidth = 540, Left = 36, Top = 86, Gap = 18;
    const int Dpi = 150;
    static readonly XNamespace One = "http://schemas.microsoft.com/office/onenote/2013/onenote";

    public static bool IsAvailable => Type.GetTypeFromProgID(ProgId) is not null;

    /// <summary>A rendered PDF page: PNG bytes and its size in pixels.</summary>
    public sealed record PagePicture(byte[] Png, int Width, int Height);

    /// <summary>The OneNote page content: title, the attached PDF, then each page's picture under the one before.</summary>
    public static string PageXml(string pageId, string title, string pdfPath, IReadOnlyList<PagePicture> pictures)
    {
        var page = new XElement(One + "Page", new XAttribute("ID", pageId),
            new XElement(One + "Title", new XElement(One + "OE", new XElement(One + "T", new XCData(title)))));
        page.Add(new XElement(One + "InsertedFile",
            new XAttribute("pathSource", pdfPath), new XAttribute("preferredName", Path.GetFileName(pdfPath)),
            new XElement(One + "Position", new XAttribute("x", Left), new XAttribute("y", Top), new XAttribute("z", 0))));
        double y = Top + 40;
        int z = 1;
        foreach (PagePicture picture in pictures)
        {
            double height = PictureWidth * picture.Height / Math.Max(1, picture.Width);
            page.Add(new XElement(One + "Image",
                new XElement(One + "Position", new XAttribute("x", Left), new XAttribute("y", Math.Round(y, 1)), new XAttribute("z", z++)),
                new XElement(One + "Size", new XAttribute("width", PictureWidth), new XAttribute("height", Math.Round(height, 1)), new XAttribute("isSetByUser", "true")),
                new XElement(One + "Data", Convert.ToBase64String(picture.Png))));
            y += height + Gap;
        }
        return new XDocument(new XDeclaration("1.0", "utf-8", null), page).ToString();
    }

    /// <summary>Renders the PDF's pages and puts them on a new page in OneNote's Quick Notes; returns how many pages went.</summary>
    public static async Task<int> SendAsync(string pdfPath, IProgress<string>? progress = null, CancellationToken cancel = default)
    {
        if (!IsAvailable)
            throw new ToolException("Needs the OneNote desktop app (free from Microsoft at onenote.com). OneNote for Windows 10 can’t receive pages from other apps.");
        var pictures = new List<PagePicture>();
        using (PdfPreview preview = await PdfPreview.OpenAsync(pdfPath).ConfigureAwait(false))
        {
            for (int i = 0; i < preview.PageCount; i++)
            {
                cancel.ThrowIfCancellationRequested();
                progress?.Report($"Preparing page {i + 1} of {preview.PageCount}…");
                SizeF size = preview.GetPageSize(i);
                using Bitmap image = await preview.RenderAsync(i, (int)(size.Width / 72 * Dpi), cancel).ConfigureAwait(false);
                using var png = new MemoryStream();
                image.Save(png, System.Drawing.Imaging.ImageFormat.Png);
                pictures.Add(new PagePicture(png.ToArray(), image.Width, image.Height));
            }
        }
        progress?.Report("Sending to OneNote…");
        string title = Path.GetFileNameWithoutExtension(pdfPath);
        await RunOnStaThread(() => Send(Path.GetFullPath(pdfPath), title, pictures)).ConfigureAwait(false);
        return pictures.Count;
    }

    static void Send(string pdfPath, string title, IReadOnlyList<PagePicture> pictures)
    {
        object? application = null;
        try
        {
            application = Activator.CreateInstance(Type.GetTypeFromProgID(ProgId)!);
            dynamic oneNote = application!;
            string sectionPath = "", sectionId = "", pageId = "";
            oneNote.GetSpecialLocation(UnfiledNotesSection, out sectionPath);
            oneNote.OpenHierarchy(sectionPath, "", out sectionId, 0);
            oneNote.CreateNewPage(sectionId, out pageId, NewPageStyleDefault);
            oneNote.UpdatePageContent(PageXml(pageId, title, pdfPath, pictures), DateTime.MinValue, Schema2013, false);
            oneNote.NavigateTo(pageId, "", false);
        }
        catch (COMException ex)
        {
            throw new ToolException($"OneNote didn’t take the pages: {ex.Message}", ex);
        }
        finally
        {
            if (application is not null) Marshal.FinalReleaseComObject(application);
        }
    }

    static Task RunOnStaThread(Action action)
    {
        var completion = new TaskCompletionSource();
        var thread = new Thread(() =>
        {
            try { action(); completion.SetResult(); }
            catch (Exception ex) { completion.SetException(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
