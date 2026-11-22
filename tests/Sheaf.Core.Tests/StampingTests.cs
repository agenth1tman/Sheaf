using System.Drawing;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using Sheaf.Core.Pdf;
using Sheaf.Core.Rendering;

namespace Sheaf.Core.Tests;

public class StampingTests
{
    const int RenderWidth = 300;

    [Theory]
    [InlineData(0, false)]
    [InlineData(90, false)]
    [InlineData(180, false)]
    [InlineData(270, false)]
    [InlineData(90, true)]
    public async Task PageNumberLandsWhereTheReaderSeesTheCorner(int rotation, bool offsetMediaBox)
    {
        using var temp = new TempFolder();
        string path = BlankPage(temp.File("page.pdf"), rotation, offsetMediaBox);

        using (PdfDocument document = PdfFiles.OpenForModify(path))
        {
            Stamping.AddPageNumbers(document, [0], new PageNumberStyle(StampPosition.TopLeft, "{n}", 7, 60, 20));
            PdfFiles.Save(document, path);
        }

        RectangleF ink = await InkBounds(path);
        Assert.True(ink.Right < 0.35f && ink.Bottom < 0.35f, $"Ink at {ink}");
    }

    [Fact]
    public async Task PageNumberAtBottomRight()
    {
        using var temp = new TempFolder();
        string path = BlankPage(temp.File("page.pdf"), 0, false);
        using (PdfDocument document = PdfFiles.OpenForModify(path))
        {
            Stamping.AddPageNumbers(document, [0], new PageNumberStyle(StampPosition.BottomRight, "Page {n} of {total}", 1, 30, 20));
            PdfFiles.Save(document, path);
        }

        RectangleF ink = await InkBounds(path);
        Assert.True(ink.Left > 0.4f && ink.Top > 0.8f, $"Ink at {ink}");
    }

    [Fact]
    public async Task WatermarkSitsInTheMiddleAndFitsThePage()
    {
        using var temp = new TempFolder();
        string path = BlankPage(temp.File("page.pdf"), 90, false);
        using (PdfDocument document = PdfFiles.OpenForModify(path))
        {
            Stamping.AddWatermark(document, [0], new WatermarkStyle("CONFIDENTIAL DRAFT COPY", 200, Color.Red, 0.5, Diagonal: true, Tiled: false));
            PdfFiles.Save(document, path);
        }

        RectangleF ink = await InkBounds(path);
        Assert.True(ink.Left > 0.02f && ink.Right < 0.98f, $"Ink at {ink}");
        Assert.InRange((ink.Left + ink.Right) / 2, 0.45f, 0.55f);
    }

    [Fact]
    public void PageNumberTextFillsTokens() =>
        Assert.Equal("Page 3 of 9", Stamping.PageNumberText("Page {n} of {total}", 3, 9));

    static string BlankPage(string path, int rotation, bool offsetMediaBox)
    {
        using var document = new PdfDocument();
        PdfPage page = document.AddPage();
        page.Width = XUnit.FromPoint(400);
        page.Height = XUnit.FromPoint(600);
        page.Rotate = rotation;
        if (offsetMediaBox) page.MediaBox = new PdfRectangle(new XPoint(100, 100), new XPoint(500, 700));
        document.Save(path);
        return path;
    }

    /// <summary>The box around every non-white pixel, as fractions of the rendered page.</summary>
    static async Task<RectangleF> InkBounds(string path)
    {
        using var preview = await PdfPreview.OpenAsync(path);
        using Bitmap bitmap = await preview.RenderAsync(0, RenderWidth);
        int left = bitmap.Width, top = bitmap.Height, right = -1, bottom = -1;
        for (int y = 0; y < bitmap.Height; y++)
            for (int x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y).GetBrightness() > 0.9f) continue;
                left = Math.Min(left, x); top = Math.Min(top, y);
                right = Math.Max(right, x); bottom = Math.Max(bottom, y);
            }
        Assert.True(right >= 0, "Nothing was drawn.");
        return RectangleF.FromLTRB(left / (float)bitmap.Width, top / (float)bitmap.Height,
            (right + 1) / (float)bitmap.Width, (bottom + 1) / (float)bitmap.Height);
    }

    [Fact]
    public async Task ImageWatermarkIsFadedToItsOpacity()
    {
        using var temp = new TempFolder();
        string path = BlankPage(temp.File("page.pdf"), 0, false);
        string logo = temp.File("logo.png");
        using (var image = new Bitmap(40, 40))
        {
            using (Graphics g = Graphics.FromImage(image)) g.Clear(Color.Black);
            image.Save(logo, System.Drawing.Imaging.ImageFormat.Png);
        }
        using (PdfDocument document = PdfFiles.OpenForModify(path))
        using (Stamping.AddWatermark(document, [0], new WatermarkStyle("", 10, Color.Black, 0.5, Diagonal: false, Tiled: false, ImagePath: logo, ImageScale: 0.5)))
            PdfFiles.Save(document, path);

        using var preview = await PdfPreview.OpenAsync(path);
        using Bitmap page = await preview.RenderAsync(0, RenderWidth);
        Color middle = page.GetPixel(page.Width / 2, page.Height / 2);
        Assert.InRange(middle.R, 100, 155);
    }

    [Fact]
    public async Task PageNumbersUseTheirColour()
    {
        using var temp = new TempFolder();
        string path = BlankPage(temp.File("page.pdf"), 0, false);
        using (PdfDocument document = PdfFiles.OpenForModify(path))
        {
            Stamping.AddPageNumbers(document, [0], new PageNumberStyle(StampPosition.TopLeft, "{n}", 8, 60, 20, Fonts.EmbeddedFonts.PdfSemiBoldFamily, Color.Red));
            PdfFiles.Save(document, path);
        }
        using var preview = await PdfPreview.OpenAsync(path);
        using Bitmap page = await preview.RenderAsync(0, RenderWidth);
        bool red = false;
        for (int y = 0; y < page.Height / 3 && !red; y++)
            for (int x = 0; x < page.Width / 3 && !red; x++)
            {
                Color c = page.GetPixel(x, y);
                red = c.R > 200 && c.G < 60 && c.B < 60;
            }
        Assert.True(red, "No red page number.");
    }
}
