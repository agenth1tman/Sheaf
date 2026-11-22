using System.Runtime.CompilerServices;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using Sheaf.Core.Fonts;

namespace Sheaf.Core.Tests;

static class TestSetup
{
    [ModuleInitializer]
    internal static void UseEmbeddedFonts() => EmbeddedFonts.UseForPdfOutput();
}

sealed class TempFolder : IDisposable
{
    public TempFolder() => Directory.CreateDirectory(Path);

    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sheaf-tests", Guid.NewGuid().ToString("N"));

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { }
    }
}

static class TestPdfs
{
    public const double A4Width = 595, A4Height = 842;

    /// <summary>Creates a PDF whose pages have the given widths (height A4) and show their page number.</summary>
    public static string WithWidths(string path, params double[] widths)
    {
        using var document = new PdfDocument();
        var font = new XFont(EmbeddedFonts.PdfFamily, 48);
        for (int i = 0; i < widths.Length; i++)
        {
            PdfPage page = document.AddPage();
            page.Width = XUnit.FromPoint(widths[i]);
            page.Height = XUnit.FromPoint(A4Height);
            using var gfx = XGraphics.FromPdfPage(page);
            gfx.DrawString($"{i + 1}", font, XBrushes.Black, new XRect(0, 0, widths[i], A4Height), XStringFormats.Center);
        }
        document.Save(path);
        return path;
    }

    public static string WithPages(string path, int pageCount) =>
        WithWidths(path, [.. Enumerable.Repeat(A4Width, pageCount)]);

    /// <summary>An A4 form with a text field "Name" (top edge 120 pt from the top) and an unticked check box "Agree".</summary>
    public static string WithForm(string path)
    {
        using var document = new PdfDocument();
        PdfPage page = document.AddPage();
        page.Width = XUnit.FromPoint(A4Width);
        page.Height = XUnit.FromPoint(A4Height);
        var fields = new PdfArray(document);
        var annotations = new PdfArray(document);

        PdfDictionary Widget(string name, string type, XRect rect)
        {
            var widget = new PdfDictionary(document);
            widget.Elements.SetName("/Type", "/Annot");
            widget.Elements.SetName("/Subtype", "/Widget");
            widget.Elements.SetName("/FT", type);
            widget.Elements.SetString("/T", name);
            widget.Elements["/Rect"] = new PdfRectangle(rect.TopLeft, rect.BottomRight);
            widget.Elements["/P"] = page.Reference;
            document.Internals.AddObject(widget);
            fields.Elements.Add(widget.Reference!);
            annotations.Elements.Add(widget.Reference!);
            return widget;
        }

        PdfDictionary text = Widget("Name", "/Tx", new XRect(100, A4Height - 140, 200, 20));
        text.Elements.SetString("/DA", "/Helv 12 Tf 0 g");
        PdfDictionary check = Widget("Agree", "/Btn", new XRect(100, A4Height - 200, 14, 14));
        var states = new PdfDictionary(document);
        states.Elements["/Yes"] = new PdfDictionary(document);
        states.Elements["/Off"] = new PdfDictionary(document);
        var appearance = new PdfDictionary(document);
        appearance.Elements["/N"] = states;
        check.Elements["/AP"] = appearance;
        check.Elements.SetName("/V", "/Off");
        check.Elements.SetName("/AS", "/Off");

        page.Elements["/Annots"] = annotations;
        var form = new PdfDictionary(document);
        form.Elements["/Fields"] = fields;
        document.Internals.AddObject(form);
        document.Internals.Catalog.Elements["/AcroForm"] = form.Reference;
        document.Save(path);
        return path;
    }

    public static IReadOnlyList<double> PageWidths(string path)
    {
        using PdfDocument document = PdfSharp.Pdf.IO.PdfReader.Open(path, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
        return [.. document.Pages.Cast<PdfPage>().Select(page => Math.Round(page.MediaBox.Width))];
    }
}
