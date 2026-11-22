using System.Drawing;
using System.Drawing.Drawing2D;
using Sheaf.Core.Ocr;
using Sheaf.Core.Scanning;
using Pig = UglyToad.PdfPig;

namespace Sheaf.Core.Tests;

public class ScanningTests
{
    /// <summary>A photo of a white sheet lying at an angle on a dark desk, with a line of dark text on it.</summary>
    static Bitmap Photo(PointF[] sheet)
    {
        var photo = new Bitmap(800, 600);
        using Graphics g = Graphics.FromImage(photo);
        g.Clear(Color.FromArgb(60, 52, 45));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.FillPolygon(Brushes.WhiteSmoke, sheet);
        using var font = new Font("Arial", 22, FontStyle.Bold, GraphicsUnit.Pixel);
        g.DrawString("Quarterly totals", font, Brushes.Black, sheet[0].X + 40, sheet[0].Y + 60);
        return photo;
    }

    static readonly PointF[] Sheet = [new(170, 60), new(640, 90), new(610, 560), new(140, 520)];

    [Fact]
    public void FindsTheCornersOfAPage()
    {
        using Bitmap photo = Photo(Sheet);
        PointF[] corners = PageCorners.Find(photo) ?? throw new Xunit.Sdk.XunitException("No page found.");
        for (int i = 0; i < 4; i++)
        {
            float distance = MathF.Sqrt(MathF.Pow(corners[i].X - Sheet[i].X, 2) + MathF.Pow(corners[i].Y - Sheet[i].Y, 2));
            Assert.True(distance < 12, $"Corner {i} found at {corners[i]}, expected near {Sheet[i]}.");
        }
    }

    [Fact]
    public void StraightensAndCleansThePage()
    {
        using Bitmap photo = Photo(Sheet);
        using Bitmap page = ScanImage.Straighten(photo, Sheet);
        Assert.InRange(page.Width / (float)page.Height, 0.9f, 1.1f);
        using Bitmap clean = ScanImage.Clean(page, ScanFilter.BlackAndWhite);
        Color corner = clean.GetPixel(clean.Width - 10, clean.Height - 10);
        Assert.Equal(255, corner.R);
    }

    [Fact]
    public async Task ScannedPagesBecomeASearchablePdf()
    {
        if (TextRecognizer.DefaultLanguage is null) return; // No OCR language installed on this PC.
        using var temp = new TempFolder();
        using var image = new Bitmap(1240, 1754);
        using (Graphics g = Graphics.FromImage(image))
        {
            g.Clear(Color.White);
            using var font = new Font("Arial", 48, GraphicsUnit.Pixel);
            g.DrawString("Sheaf reads scanned pages", font, Brushes.Black, 120, 200);
        }
        await ScanWriter.WriteAsync([new PageImage(image, BlackAndWhite: true)], temp.File("scan.pdf"), TextRecognizer.DefaultLanguage.Tag);

        using var pdf = Pig.PdfDocument.Open(temp.File("scan.pdf"));
        Assert.Equal(595, Math.Round(pdf.GetPage(1).Width));
        Assert.Contains("scanned", pdf.GetPage(1).Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheTextLayerCanBeFoundButNotSeen()
    {
        using var temp = new TempFolder();
        string path = temp.File("layer.pdf");
        using (var document = new PdfSharp.Pdf.PdfDocument())
        {
            PdfSharp.Pdf.PdfPage page = document.AddPage();
            TextLayer.Add(page, [new Core.Editing.PageWord("Hidden", new RectangleF(100, 100, 200, 40))]);
            document.Save(path);
        }

        using (var preview = await Core.Rendering.PdfPreview.OpenAsync(path))
        using (Bitmap render = await preview.RenderAsync(0, 595))
        {
            for (int y = 100; y < 140; y += 2)
                for (int x = 100; x < 300; x += 2)
                    Assert.True(render.GetPixel(x, y).GetBrightness() > 0.98f, $"Text shows at {x},{y}.");
        }
        using var pdf = Pig.PdfDocument.Open(path);
        Assert.Contains("Hidden", pdf.GetPage(1).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImagePdfsGetATextLayer()
    {
        if (TextRecognizer.DefaultLanguage is null) return;
        using var temp = new TempFolder();
        using var image = new Bitmap(1240, 1754);
        using (Graphics g = Graphics.FromImage(image))
        {
            g.Clear(Color.White);
            using var font = new Font("Arial", 48, GraphicsUnit.Pixel);
            g.DrawString("Invoice from the copier", font, Brushes.Black, 120, 200);
        }
        await ScanWriter.WriteAsync([new PageImage(image, BlackAndWhite: false)], temp.File("copier.pdf"), language: null);

        var (read, skipped) = await SearchablePdf.MakeAsync(temp.File("copier.pdf"), temp.File("searchable.pdf"), null);

        Assert.Equal((1, 0), (read, skipped));
        using var pdf = Pig.PdfDocument.Open(temp.File("searchable.pdf"));
        Assert.Contains("copier", pdf.GetPage(1).Text, StringComparison.OrdinalIgnoreCase);
    }
}
