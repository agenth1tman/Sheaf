using System.Globalization;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using Sheaf.Core.Fonts;

namespace Sheaf.Core.Pdf;

public enum StampPosition { TopLeft, TopCenter, TopRight, BottomLeft, BottomCenter, BottomRight }

/// <param name="Format">Text with {n} for the page number and {total} for the last number, e.g. "Page {n} of {total}".</param>
/// <param name="FontFamily">Geist, Geist Medium, Geist SemiBold or a font installed in Windows.</param>
public sealed record PageNumberStyle(StampPosition Position, string Format, int FirstNumber, double FontSize, double Margin,
    string FontFamily = EmbeddedFonts.PdfMediumFamily, System.Drawing.Color Color = default);

/// <param name="Opacity">0 (invisible) to 1 (solid).</param>
/// <param name="ImagePath">An image to stamp instead of <paramref name="Text"/>.</param>
/// <param name="ImageScale">The image's width as a share of the page's width, 0.05 to 1.</param>
public sealed record WatermarkStyle(string Text, double FontSize, System.Drawing.Color Color, double Opacity, bool Diagonal, bool Tiled,
    string? ImagePath = null, double ImageScale = 0.5);

/// <summary>Writes text onto existing pages. The document must be opened for modifying.</summary>
public static class Stamping
{
    const double WatermarkMaxShare = 0.9;
    const double TileGapFactor = 1.5;

    public static void AddPageNumbers(PdfDocument document, IReadOnlyList<int> pageIndexes, PageNumberStyle style)
    {
        var font = new XFont(style.FontFamily, style.FontSize);
        System.Drawing.Color color = style.Color.IsEmpty ? System.Drawing.Color.Black : style.Color;
        var brush = new XSolidBrush(XColor.FromArgb(255, color.R, color.G, color.B));
        int lastNumber = style.FirstNumber + pageIndexes.Count - 1;
        for (int i = 0; i < pageIndexes.Count; i++)
        {
            string text = PageNumberText(style.Format, style.FirstNumber + i, lastNumber);
            using var canvas = PageCanvas.Open(document.Pages[pageIndexes[i]]);
            var area = new XRect(style.Margin, style.Margin,
                Math.Max(1, canvas.Size.Width - 2 * style.Margin), Math.Max(1, canvas.Size.Height - 2 * style.Margin));
            canvas.Graphics.DrawString(text, font, brush, area, Alignment(style.Position));
        }
    }

    public static string PageNumberText(string format, int number, int lastNumber) => format
        .Replace("{n}", number.ToString(CultureInfo.CurrentCulture))
        .Replace("{total}", lastNumber.ToString(CultureInfo.CurrentCulture));

    /// <summary>Stamps text or an image; returns images to dispose once the document is saved.</summary>
    public static IDisposable AddWatermark(PdfDocument document, IReadOnlyList<int> pageIndexes, WatermarkStyle style)
    {
        if (style.ImagePath is { } path) return AddImageWatermark(document, pageIndexes, style, path);
        var brush = new XSolidBrush(XColor.FromArgb(
            (int)Math.Round(Math.Clamp(style.Opacity, 0, 1) * 255), style.Color.R, style.Color.G, style.Color.B));
        foreach (int index in pageIndexes)
        {
            using var canvas = PageCanvas.Open(document.Pages[index]);
            double angle = style.Diagonal ? -Math.Atan2(canvas.Size.Height, canvas.Size.Width) * 180 / Math.PI : 0;
            XFont font = FontThatFits(canvas, style, angle);
            if (style.Tiled) DrawTiled(canvas, style.Text, font, brush, angle);
            else DrawAt(canvas.Graphics, style.Text, font, brush, Center(canvas.Size), angle);
        }
        return new XImageList([]);
    }

    /// <summary>
    /// Stamps an image at the opacity asked for. PDF images can't be drawn half-transparent directly, so the
    /// opacity goes into the image's own alpha channel, which PDFsharp writes as a soft mask.
    /// </summary>
    static XImageList AddImageWatermark(PdfDocument document, IReadOnlyList<int> pageIndexes, WatermarkStyle style, string path)
    {
        XImage image;
        try
        {
            image = XImage.FromStream(new MemoryStream(Faded(path, style.Opacity)));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or OutOfMemoryException or InvalidOperationException)
        {
            throw new ToolException($"“{Path.GetFileName(path)}” can’t be used as a watermark: it isn’t an image Sheaf can read.", ex);
        }
        double aspect = image.PixelHeight / (double)Math.Max(1, image.PixelWidth);
        foreach (int index in pageIndexes)
        {
            using var canvas = PageCanvas.Open(document.Pages[index]);
            double angle = style.Diagonal ? -Math.Atan2(canvas.Size.Height, canvas.Size.Width) * 180 / Math.PI : 0;
            double width = canvas.Size.Width * Math.Clamp(style.ImageScale, 0.05, 1);
            double height = width * aspect;
            if (height > canvas.Size.Height * 0.95)
            {
                height = canvas.Size.Height * 0.95;
                width = height / aspect;
            }
            var size = new XSize(width, height);
            if (style.Tiled)
            {
                double stepX = width * TileGapFactor, stepY = height * TileGapFactor;
                double reach = Math.Max(canvas.Size.Width, canvas.Size.Height);
                for (double y = -reach; y < canvas.Size.Height + reach; y += stepY)
                    for (double x = -reach; x < canvas.Size.Width + reach; x += stepX)
                        if (IsNearPage(x, y, canvas.Size, Math.Max(width, height)))
                            DrawImageAt(canvas.Graphics, image, new XPoint(x, y), size, angle);
            }
            else DrawImageAt(canvas.Graphics, image, Center(canvas.Size), size, angle);
        }
        return new XImageList([image]);
    }

    /// <summary>The image as a PNG whose alpha is multiplied by <paramref name="opacity"/>.</summary>
    static byte[] Faded(string path, double opacity)
    {
        using var source = new System.Drawing.Bitmap(path);
        using var faded = new System.Drawing.Bitmap(source.Width, source.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = System.Drawing.Graphics.FromImage(faded))
        using (var attributes = new System.Drawing.Imaging.ImageAttributes())
        {
            attributes.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix { Matrix33 = (float)Math.Clamp(opacity, 0, 1) });
            g.DrawImage(source, new System.Drawing.Rectangle(0, 0, source.Width, source.Height), 0, 0, source.Width, source.Height, System.Drawing.GraphicsUnit.Pixel, attributes);
        }
        using var stream = new MemoryStream();
        faded.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
        return stream.ToArray();
    }

    static void DrawImageAt(XGraphics graphics, XImage image, XPoint center, XSize size, double angle)
    {
        XGraphicsState state = graphics.Save();
        graphics.RotateAtTransform(angle, center);
        graphics.DrawImage(image, center.X - size.Width / 2, center.Y - size.Height / 2, size.Width, size.Height);
        graphics.Restore(state);
    }

    /// <summary>Images a stamped document still needs while it is saved; dispose after saving.</summary>
    sealed class XImageList(List<XImage> images) : IDisposable
    {
        public void Dispose()
        {
            foreach (XImage image in images) image.Dispose();
        }
    }

    static XFont FontThatFits(PageCanvas canvas, WatermarkStyle style, double angle)
    {
        var font = new XFont(EmbeddedFonts.PdfFamily, style.FontSize, XFontStyleEx.Bold);
        double room = angle == 0
            ? canvas.Size.Width
            : Math.Sqrt(canvas.Size.Width * canvas.Size.Width + canvas.Size.Height * canvas.Size.Height);
        double textWidth = canvas.Graphics.MeasureString(style.Text, font).Width;
        double scale = room * WatermarkMaxShare / Math.Max(1, textWidth);
        return scale >= 1 ? font : new XFont(EmbeddedFonts.PdfFamily, style.FontSize * scale, XFontStyleEx.Bold);
    }

    static void DrawTiled(PageCanvas canvas, string text, XFont font, XBrush brush, double angle)
    {
        XSize textSize = canvas.Graphics.MeasureString(text, font);
        double stepX = textSize.Width * TileGapFactor, stepY = textSize.Height * TileGapFactor * 2;
        double reach = Math.Max(canvas.Size.Width, canvas.Size.Height);
        for (double y = -reach; y < canvas.Size.Height + reach; y += stepY)
            for (double x = -reach; x < canvas.Size.Width + reach; x += stepX)
                if (IsNearPage(x, y, canvas.Size, textSize.Width))
                    DrawAt(canvas.Graphics, text, font, brush, new XPoint(x, y), angle);
    }

    static bool IsNearPage(double x, double y, XSize page, double margin) =>
        x > -margin && y > -margin && x < page.Width + margin && y < page.Height + margin;

    static void DrawAt(XGraphics graphics, string text, XFont font, XBrush brush, XPoint center, double angle)
    {
        XGraphicsState state = graphics.Save();
        graphics.RotateAtTransform(angle, center);
        graphics.DrawString(text, font, brush, center, XStringFormats.Center);
        graphics.Restore(state);
    }

    static XPoint Center(XSize size) => new(size.Width / 2, size.Height / 2);

    static XStringFormat Alignment(StampPosition position) => position switch
    {
        StampPosition.TopLeft => XStringFormats.TopLeft,
        StampPosition.TopCenter => XStringFormats.TopCenter,
        StampPosition.TopRight => XStringFormats.TopRight,
        StampPosition.BottomLeft => XStringFormats.BottomLeft,
        StampPosition.BottomRight => XStringFormats.BottomRight,
        _ => XStringFormats.BottomCenter,
    };
}
