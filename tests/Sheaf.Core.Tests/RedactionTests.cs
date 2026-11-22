using System.Drawing;
using System.Drawing.Imaging;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using Sheaf.Core.Editing;
using Sheaf.Core.Fonts;
using ZXing;
using ZXing.Rendering;
using Pig = UglyToad.PdfPig;

namespace Sheaf.Core.Tests;

public class RedactionTests
{
    static string Invoice(string path)
    {
        using var document = new PdfDocument();
        var font = new XFont(EmbeddedFonts.PdfFamily, 12);
        string[] lines =
        [
            "Invoice 20931",
            "Contact jane.doe@example.com",
            "Phone +44 20 7946 0958",
            "Pay to DE89 3704 0044 0532 0130 00",
            "Secret code 1234",
        ];
        PdfPage first = document.AddPage();
        using (XGraphics gfx = XGraphics.FromPdfPage(first))
            for (int i = 0; i < lines.Length; i++) gfx.DrawString(lines[i], font, XBrushes.Black, 60, 100 + i * 30);
        PdfPage second = document.AddPage();
        using (XGraphics gfx = XGraphics.FromPdfPage(second)) gfx.DrawString("Nothing to hide here", font, XBrushes.Black, 60, 100);
        document.Save(path);
        return path;
    }

    [Fact]
    public void FindsTextAndPatterns()
    {
        using var temp = new TempFolder();
        var words = Finders.ReadWords(Invoice(temp.File("invoice.pdf")));

        Found secret = Assert.Single(Finders.FindText(words, "secret  CODE"));
        Assert.Equal(0, secret.SourcePage);
        Assert.InRange(secret.Bounds.Y, 205, 225);
        Assert.Single(Finders.FindPattern(words, FindKind.Emails));
        Assert.Single(Finders.FindPattern(words, FindKind.PhoneNumbers));
        Assert.Contains(Finders.FindPattern(words, FindKind.BankAndCardNumbers), found => found.Text.StartsWith("DE89", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RedactedPagesLoseTheirTextAndOthersKeepIt()
    {
        using var temp = new TempFolder();
        EditDocument document = DocumentStore.Open(Invoice(temp.File("invoice.pdf")), temp.File("work"), null);
        var words = Finders.ReadWords(document.Sources[0]);
        foreach (Found found in Finders.FindText(words, "secret code"))
            document.Pages[found.SourcePage].Objects.Add(new PageObject { Kind = MarkKind.Redaction, Bounds = found.Bounds, Cover = RedactCover.Black });

        int applied = await Redactor.ApplyAsync(document, temp.File("redacted.pdf"), temp.File("work"), clean: true);

        Assert.Equal(1, applied);
        using var result = Pig.PdfDocument.Open(temp.File("redacted.pdf"));
        Assert.Equal(2, result.NumberOfPages);
        Assert.DoesNotContain("Secret", result.GetPage(1).Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Nothing to hide", result.GetPage(2).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TextMarksTakeOutOnlyTheirWords()
    {
        using var temp = new TempFolder();
        EditDocument document = DocumentStore.Open(Invoice(temp.File("invoice.pdf")), temp.File("work"), null);
        var words = Finders.ReadWords(document.Sources[0]);
        RectangleF before = words[0].Single(word => word.Text == "1234").Bounds;
        foreach (Found found in Finders.FindText(words, "secret code"))
            document.Pages[found.SourcePage].Objects.Add(new PageObject { Kind = MarkKind.Redaction, RedactShape = RedactShape.Text, Bounds = found.Bounds, Cover = RedactCover.Black });

        int applied = await Redactor.ApplyAsync(document, temp.File("redacted.pdf"), temp.File("work"), clean: true);

        Assert.Equal(1, applied);
        using var result = Pig.PdfDocument.Open(temp.File("redacted.pdf"));
        Pig.Content.Page page = result.GetPage(1);
        Assert.DoesNotContain("Secret", page.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("code", page.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Invoice 20931", page.Text, StringComparison.Ordinal);
        // The page is still text, not a picture of itself, and the words after the gap haven't moved.
        Assert.Empty(page.GetImages());
        RectangleF after = Finders.ReadWords(temp.File("redacted.pdf"))[0].Single(word => word.Text == "1234").Bounds;
        Assert.Equal(before.X, after.X, 0.5f);
        Assert.Equal(before.Y, after.Y, 0.5f);
    }

    [Fact]
    public async Task FindsQrCodes()
    {
        using var temp = new TempFolder();
        string qr = temp.File("qr.png");
        var writer = new BarcodeWriterPixelData { Format = BarcodeFormat.QR_CODE, Options = new ZXing.Common.EncodingOptions { Width = 300, Height = 300, Margin = 1 } };
        PixelData pixels = writer.Write("https://example.com/pay/20931");
        using (var bitmap = new Bitmap(pixels.Width, pixels.Height, PixelFormat.Format32bppRgb))
        {
            BitmapData data = bitmap.LockBits(new Rectangle(0, 0, pixels.Width, pixels.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
            System.Runtime.InteropServices.Marshal.Copy(pixels.Pixels, 0, data.Scan0, pixels.Pixels.Length);
            bitmap.UnlockBits(data);
            bitmap.Save(qr, ImageFormat.Png);
        }
        string pdf = temp.File("qr.pdf");
        using (var document = new PdfDocument())
        {
            PdfPage page = document.AddPage();
            using XGraphics gfx = XGraphics.FromPdfPage(page);
            using XImage image = XImage.FromFile(qr);
            gfx.DrawImage(image, 300, 500, 120, 120);
            document.Save(pdf);
        }

        Found code = Assert.Single(await Finders.FindCodesAsync(pdf, [0]));
        Assert.Equal("QR code", code.Text);
        Assert.True(code.Bounds.Contains(new RectangleF(305, 505, 110, 110)), $"The mark {code.Bounds} doesn't cover the code.");
    }
}
