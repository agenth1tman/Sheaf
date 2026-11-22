using System.Drawing;
using System.Drawing.Imaging;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using Sheaf.Core.Editing;
using Sheaf.Core.Ocr;
using Sheaf.Core.Pdf;
using Sheaf.Core.Rendering;

namespace Sheaf.Core.Scanning;

/// <summary>A cleaned-up page ready to go into a PDF: the image and how it is stored.</summary>
public sealed record PageImage(Bitmap Image, bool BlackAndWhite);

/// <summary>Writes scanned pages into a PDF, optionally with invisible OCR text so it can be searched.</summary>
public static class ScanWriter
{
    const long JpegQuality = 82;
    static readonly (double Width, double Height)[] Papers = [(595.28, 841.89), (612, 792), (612, 1008)];

    /// <summary>The bytes a page takes in the PDF: JPEG for photos and colour, one bit per pixel for black and white.</summary>
    public static byte[] Encode(PageImage page)
    {
        using var stream = new MemoryStream();
        if (page.BlackAndWhite)
        {
            using Bitmap mono = page.Image.Clone(new Rectangle(0, 0, page.Image.Width, page.Image.Height), PixelFormat.Format1bppIndexed);
            mono.Save(stream, ImageFormat.Png);
        }
        else
        {
            ImageCodecInfo codec = ImageCodecInfo.GetImageEncoders().First(encoder => encoder.FormatID == ImageFormat.Jpeg.Guid);
            using var parameters = new EncoderParameters(1);
            parameters.Param[0] = new EncoderParameter(Encoder.Quality, JpegQuality);
            page.Image.Save(stream, codec, parameters);
        }
        return stream.ToArray();
    }

    /// <summary>A4, Letter or Legal when the page's shape matches one (either way round); otherwise A4's width.</summary>
    public static XSize PaperFor(int width, int height)
    {
        bool landscape = width > height;
        double ratio = landscape ? width / (double)height : height / (double)width;
        foreach (var (w, h) in Papers)
            if (Math.Abs(ratio - h / w) < 0.06) return landscape ? new XSize(h, w) : new XSize(w, h);
        return landscape ? new XSize(841.89, 841.89 / ratio) : new XSize(595.28, 595.28 * ratio);
    }

    /// <param name="language">OCR language tag; null writes the images only.</param>
    public static async Task WriteAsync(IReadOnlyList<PageImage> pages, string path, string? language, IProgress<int>? progress = null, CancellationToken cancel = default)
    {
        bool ocr = language is not null;
        var layers = new List<List<PageWord>>();
        if (ocr)
            for (int i = 0; i < pages.Count; i++)
            {
                progress?.Report(i);
                layers.Add(await TextRecognizer.ReadAsync(pages[i].Image, language, cancel).ConfigureAwait(false));
            }
        var encoded = await Task.Run(() => pages.Select(Encode).ToList(), cancel).ConfigureAwait(false);

        await Task.Run(() =>
        {
            var images = new List<XImage>();
            try
            {
                using var document = new PdfDocument();
                document.Info.Creator = "Sheaf";
                for (int i = 0; i < pages.Count; i++)
                {
                    cancel.ThrowIfCancellationRequested();
                    XSize size = PaperFor(pages[i].Image.Width, pages[i].Image.Height);
                    PdfPage page = document.AddPage();
                    page.Width = XUnit.FromPoint(size.Width);
                    page.Height = XUnit.FromPoint(size.Height);
                    XImage image = XImage.FromStream(new MemoryStream(encoded[i]));
                    images.Add(image);
                    using (XGraphics gfx = XGraphics.FromPdfPage(page)) gfx.DrawImage(image, 0, 0, size.Width, size.Height);
                    if (ocr) TextLayer.Add(page, Scale(layers[i], (float)(size.Width / pages[i].Image.Width)));
                }
                PdfFiles.Save(document, path);
            }
            finally
            {
                foreach (XImage image in images) image.Dispose();
            }
        }, cancel).ConfigureAwait(false);
    }

    internal static IEnumerable<PageWord> Scale(IEnumerable<PageWord> words, float scale) =>
        words.Select(word => word with { Bounds = new RectangleF(word.Bounds.X * scale, word.Bounds.Y * scale, word.Bounds.Width * scale, word.Bounds.Height * scale) });
}

/// <summary>Adds an OCR text layer to the pages of a PDF that have no text, such as scans saved by a copier.</summary>
public static class SearchablePdf
{
    const float Dpi = 300;
    const int MinimumWords = 5;

    /// <summary>Returns how many pages were read and how many already had text.</summary>
    public static async Task<(int Read, int Skipped)> MakeAsync(string source, string target, string? language, IProgress<string>? progress = null, CancellationToken cancel = default)
    {
        List<List<PageWord>> existing = await Task.Run(() => Finders.ReadWords(source, cancel), cancel).ConfigureAwait(false);
        var layers = new Dictionary<int, List<PageWord>>();
        using (PdfPreview preview = await PdfPreview.OpenAsync(source).ConfigureAwait(false))
        {
            for (int i = 0; i < preview.PageCount; i++)
            {
                if (i < existing.Count && existing[i].Count >= MinimumWords) continue;
                progress?.Report($"Reading page {i + 1} of {preview.PageCount}…");
                SizeF size = preview.GetPageSize(i);
                int width = (int)Math.Min(4200, size.Width / 72 * Dpi);
                using Bitmap page = await preview.RenderAsync(i, width, cancel).ConfigureAwait(false);
                layers[i] = [.. ScanWriter.Scale(await TextRecognizer.ReadAsync(page, language, cancel).ConfigureAwait(false), size.Width / page.Width)];
            }
        }
        await Task.Run(() =>
        {
            using PdfDocument document = PdfFiles.OpenForModify(source);
            foreach (var (index, words) in layers) TextLayer.Add(document.Pages[index], words);
            PdfFiles.Save(document, target);
        }, cancel).ConfigureAwait(false);
        return (layers.Count, existing.Count(words => words.Count >= MinimumWords));
    }
}
