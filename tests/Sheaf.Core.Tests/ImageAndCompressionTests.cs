using System.Drawing;
using System.Drawing.Imaging;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using Sheaf.Core.Pdf;
using Sheaf.Core.Rendering;

namespace Sheaf.Core.Tests;

public class ImageAndCompressionTests
{
    [Fact]
    public void FitImagePageMatchesTheImage()
    {
        using var temp = new TempFolder();
        string png = SaveImage(temp.File("wide.png"), 200, 100, ImageFormat.Png);

        using (var document = new PdfDocument())
        {
            ImagePages.AddImagePage(document, png, new ImagePageLayout(PageSizeChoice.FitImage, PageOrientationChoice.Auto, 0));
            ImagePages.AddImagePage(document, png, new ImagePageLayout(PageSizeChoice.A4, PageOrientationChoice.Auto, 36));
            PdfFiles.Save(document, temp.File("out.pdf"));
        }

        using PdfDocument result = PdfFiles.OpenForImport(temp.File("out.pdf"));
        Assert.Equal(150, Math.Round(result.Pages[0].MediaBox.Width));
        Assert.Equal(75, Math.Round(result.Pages[0].MediaBox.Height));
        Assert.Equal(842, Math.Round(result.Pages[1].MediaBox.Width));
    }

    [Fact]
    public void NotAnImageGivesPlainMessage()
    {
        using var temp = new TempFolder();
        File.WriteAllText(temp.File("fake.jpg"), "nope");
        using var document = new PdfDocument();
        var error = Assert.Throws<ToolException>(() => ImagePages.AddImagePage(document, temp.File("fake.jpg"),
            new ImagePageLayout(PageSizeChoice.A4, PageOrientationChoice.Auto, 0)));
        Assert.Contains("can’t be read as an image", error.Message);
    }

    [Theory]
    [InlineData(CompressionLevel.Recommended)]
    [InlineData(CompressionLevel.Strong)]
    public async Task CompressionShrinksPhotosAndKeepsThePageReadable(CompressionLevel level)
    {
        using var temp = new TempFolder();
        string photo = SaveImage(temp.File("photo.jpg"), 3000, 2000, ImageFormat.Jpeg);
        string source = temp.File("photo.pdf");
        using (var document = new PdfDocument())
        {
            ImagePages.AddImagePage(document, photo, new ImagePageLayout(PageSizeChoice.A4, PageOrientationChoice.Auto, 0));
            PdfFiles.Save(document, source);
        }

        using (PdfDocument document = PdfFiles.OpenForModify(source))
        {
            Assert.Equal(1, Compression.Compress(document, level));
            PdfFiles.Save(document, temp.File("small.pdf"));
        }

        Assert.True(new FileInfo(temp.File("small.pdf")).Length < new FileInfo(source).Length * 0.6);
        using var preview = await PdfPreview.OpenAsync(temp.File("small.pdf"));
        using Bitmap page = await preview.RenderAsync(0, 200);
        Assert.True(page.GetPixel(page.Width / 2, page.Height / 2).GetBrightness() < 0.95f);
    }

    [Fact]
    public void StrongCompressionReencodesLosslessImages()
    {
        using var temp = new TempFolder();
        string png = SaveImage(temp.File("photo.png"), 1600, 1200, ImageFormat.Png);
        string source = temp.File("png.pdf");
        using (var document = new PdfDocument())
        {
            ImagePages.AddImagePage(document, png, new ImagePageLayout(PageSizeChoice.A4, PageOrientationChoice.Auto, 0));
            PdfFiles.Save(document, source);
        }

        using PdfDocument reopened = PdfFiles.OpenForModify(source);
        Assert.Equal(0, Compression.Compress(reopened, CompressionLevel.Recommended));
        Assert.Equal(1, Compression.Compress(reopened, CompressionLevel.Strong));
    }

    [Fact]
    public async Task PreviewReportsPagesAndRendersToWidth()
    {
        using var temp = new TempFolder();
        string source = TestPdfs.WithWidths(temp.File("a.pdf"), 300, 600);
        using var preview = await PdfPreview.OpenAsync(source);

        Assert.Equal(2, preview.PageCount);
        Assert.Equal(600, Math.Round(preview.GetPageSize(1).Width));
        using Bitmap bitmap = await preview.RenderAsync(0, 120);
        Assert.Equal(120, bitmap.Width);

        await preview.SaveImageAsync(1, temp.File("page.png"), PageImageFormat.Png, 144);
        using var saved = Image.FromFile(temp.File("page.png"));
        Assert.Equal(1200, saved.Width);
    }

    /// <summary>A noisy gradient, so JPEG can't squeeze it to nothing at high quality.</summary>
    static string SaveImage(string path, int width, int height, ImageFormat format)
    {
        var random = new Random(42);
        using var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(bitmap))
        using (var brush = new System.Drawing.Drawing2D.LinearGradientBrush(new Point(0, 0), new Point(width, height), Color.SteelBlue, Color.Orange))
            graphics.FillRectangle(brush, 0, 0, width, height);
        for (int i = 0; i < width * height / 8; i++)
            bitmap.SetPixel(random.Next(width), random.Next(height), Color.FromArgb(random.Next(256), random.Next(256), random.Next(256)));

        if (format == ImageFormat.Jpeg)
        {
            var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
            using var parameters = new EncoderParameters(1);
            parameters.Param[0] = new EncoderParameter(Encoder.Quality, 95L);
            bitmap.Save(path, codec, parameters);
        }
        else
        {
            bitmap.SetResolution(96, 96);
            bitmap.Save(path, format);
        }
        return path;
    }
}
