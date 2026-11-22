using System.Drawing;
using System.Drawing.Imaging;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using Sheaf.Core.Editing;
using Sheaf.Core.Office.BuiltIn;
using Sheaf.Core.Pdf;
using Sheaf.Core.Rendering;

namespace Sheaf.Core.Tests;

public class PageAndImageTests
{
    [Fact]
    public void APageBoxWrittenTopFirstStillOpens()
    {
        using var temp = new TempFolder();
        string path = temp.File("reversed.pdf");
        using (var document = new PdfDocument())
        {
            PdfPage page = document.AddPage();
            page.Width = XUnit.FromPoint(612);
            page.Height = XUnit.FromPoint(792);
            // Some writers give the corners top first: allowed by the PDF standard.
            page.Elements["/MediaBox"] = new PdfArray(document, new PdfInteger(0), new PdfInteger(792), new PdfInteger(612), new PdfInteger(0));
            document.Save(path);
        }

        EditDocument opened = DocumentStore.Open(path, temp.File("work"), null);

        Assert.Equal(612, opened.Pages[0].Width, 0.5);
        Assert.Equal(792, opened.Pages[0].Height, 0.5);
    }

    [Fact]
    public void ControlCharactersAreLeftOutOfWordText()
    {
        Assert.Equal("Question 1", PdfToWord.XmlSafe("Question\u0010 1"));
        Assert.Equal("plain", PdfToWord.XmlSafe("plain"));
        Assert.Equal("emoji 😀 stays", PdfToWord.XmlSafe("emoji 😀 stays"));
    }

    [Fact]
    public async Task AMirroredPageIsSavedMirroredWhileObjectsStay()
    {
        using var temp = new TempFolder();
        string source = temp.File("box.pdf");
        using (var document = new PdfDocument())
        {
            PdfPage page = document.AddPage();
            page.Width = XUnit.FromPoint(400);
            page.Height = XUnit.FromPoint(400);
            using var gfx = XGraphics.FromPdfPage(page);
            gfx.DrawRectangle(XBrushes.Black, 20, 20, 100, 100);
            document.Save(source);
        }
        EditDocument edit = DocumentStore.Open(source, temp.File("work"), null);
        edit.Pages[0].MirrorX = true;
        edit.Pages[0].Objects.Add(new PageObject { Kind = MarkKind.Rectangle, X = 20, Y = 280, Width = 100, Height = 100, StrokeWidth = 0, FillColor = Color.Red.ToArgb() });
        DocumentStore.Save(edit, temp.File("out.pdf"), keepEditable: false);

        using var preview = await PdfPreview.OpenAsync(temp.File("out.pdf"));
        using Bitmap page400 = await preview.RenderAsync(0, 400);
        Assert.True(page400.GetPixel(70, 70).GetBrightness() > 0.9f, "The box should have moved off the left.");
        Assert.True(page400.GetPixel(330, 70).GetBrightness() < 0.1f, "The box should be on the right.");
        Color added = page400.GetPixel(70, 330);
        Assert.True(added.R > 200 && added.G < 80, $"The added object should stay on the left: {added}");
    }

    [Fact]
    public void PicturesCanBeCroppedTurnedRoundedAndFaded()
    {
        byte[] picture = Png(200, 100, Color.SteelBlue);

        using (var cropped = Image.FromStream(new MemoryStream(ImageLook.Crop(picture, new RectangleF(0.5f, 0, 0.5f, 1)))))
            Assert.Equal(new Size(100, 100), cropped.Size);
        using (var turned = Image.FromStream(new MemoryStream(ImageLook.Turn(picture, clockwise: true))))
            Assert.Equal(new Size(100, 200), turned.Size);

        var item = new PageObject { Kind = MarkKind.Image, Width = 200, Height = 100, CornerRadius = 30, Opacity = 0.5f };
        using var baked = new Bitmap(new MemoryStream(ImageLook.Bake(picture, item)));
        Assert.Equal(0, baked.GetPixel(1, 1).A);
        Assert.InRange(baked.GetPixel(100, 50).A, 110, 145);
        Assert.Same(picture, ImageLook.Bake(picture, new PageObject { Kind = MarkKind.Image, Width = 200, Height = 100 }));
    }

    static byte[] Png(int width, int height, Color color)
    {
        using var bitmap = new Bitmap(width, height);
        using (Graphics g = Graphics.FromImage(bitmap)) g.Clear(color);
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    [Fact]
    public void HugePicturesAreFittedAsTheyAreAdded()
    {
        using var big = new Bitmap(6000, 3000);
        using (Graphics g = Graphics.FromImage(big)) g.Clear(Color.SteelBlue);
        using var stream = new MemoryStream();
        big.Save(stream, ImageFormat.Png);
        byte[] fitted = ImageLook.Fit(stream.ToArray());
        using var result = Image.FromStream(new MemoryStream(fitted));
        Assert.Equal(ImageLook.MaxSide, result.Width);
        Assert.Equal(2000, result.Height);
        // A picture that already fits comes back as it was.
        Assert.Same(fitted, ImageLook.Fit(fitted));
    }

    [Fact]
    public void RecolouredPicturesKeepTheirTransparency()
    {
        // A white dot on a transparent picture, like an icon.
        using var icon = new Bitmap(20, 20);
        icon.SetPixel(10, 10, Color.White);
        var item = new PageObject { Kind = MarkKind.Image, Bounds = new RectangleF(0, 0, 20, 20), Tint = Color.FromArgb(255, 37, 99, 235).ToArgb() };
        Assert.True(ImageLook.IsStyled(item));
        using Bitmap recoloured = ImageLook.Render(icon, item, 100);
        Color dot = recoloured.GetPixel(10, 10), empty = recoloured.GetPixel(2, 2);
        Assert.Equal((37, 99, 235), (dot.R, dot.G, dot.B));
        Assert.Equal(0, empty.A);
    }

    [Fact]
    public void BlankImagePagesArePixelsAsPoints()
    {
        using var temp = new TempFolder();
        EditDocument document = DocumentStore.CreateBlank(temp.File("work"), 1920 * 0.75f, 1080 * 0.75f);
        Assert.Equal(1440, document.Pages[0].Width, 0.01f);
        using PdfDocument pdf = PdfFiles.OpenForImport(document.Sources[0]);
        Assert.Equal(1440, pdf.Pages[0].Width.Point, 0.01);
        Assert.Equal(810, pdf.Pages[0].Height.Point, 0.01);
    }
}
