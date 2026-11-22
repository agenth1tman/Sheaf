using System.Drawing;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace Sheaf.Core.Pdf;

public enum PageSizeChoice { FitImage, A4, Letter }

public enum PageOrientationChoice { Auto, Portrait, Landscape }

/// <param name="Margin">Space around the image in points.</param>
public sealed record ImagePageLayout(PageSizeChoice Size, PageOrientationChoice Orientation, double Margin);

/// <summary>Turns images into PDF pages. JPEGs are embedded as they are, without re-encoding.</summary>
public static class ImagePages
{
    const int ExifOrientationTag = 0x0112;
    const double MaxPagePoints = 14400;
    static readonly XSize A4 = new(595.28, 841.89);
    static readonly XSize Letter = new(612, 792);

    public static void AddImagePage(PdfDocument document, string imagePath, ImagePageLayout layout)
    {
        int quarterTurns = ReadQuarterTurns(imagePath);
        using XImage image = LoadImage(imagePath);
        XSize imageSize = PointSize(image);
        if (quarterTurns % 2 == 1) imageSize = new XSize(imageSize.Height, imageSize.Width);

        XSize pageSize = ChoosePageSize(layout, imageSize);
        PdfPage page = document.AddPage();
        page.Width = XUnit.FromPoint(pageSize.Width);
        page.Height = XUnit.FromPoint(pageSize.Height);

        using XGraphics graphics = XGraphics.FromPdfPage(page);
        XRect box = FitInside(imageSize, new XRect(0, 0, pageSize.Width, pageSize.Height), layout.Margin);
        DrawTurned(graphics, image, box, quarterTurns);
    }

    static XImage LoadImage(string path)
    {
        try
        {
            return XImage.FromFile(path);
        }
        catch (Exception ex)
        {
            throw new ToolException($"“{Path.GetFileName(path)}” can’t be read as an image. Use JPG, PNG, BMP, GIF or TIFF.", ex);
        }
    }

    /// <summary>Phones store photos sideways and record the turn in EXIF; this reads it without decoding pixels.</summary>
    static int ReadQuarterTurns(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var image = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: false);
            if (!image.PropertyIdList.Contains(ExifOrientationTag)) return 0;
            byte value = image.GetPropertyItem(ExifOrientationTag)?.Value?[0] ?? 1;
            return value switch { 3 or 4 => 2, 5 or 6 => 1, 7 or 8 => 3, _ => 0 };
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or System.Runtime.InteropServices.ExternalException)
        {
            return 0;
        }
    }

    static XSize PointSize(XImage image)
    {
        double dpi = image.HorizontalResolution is >= 30 and <= 1200 ? image.HorizontalResolution : 96;
        var size = new XSize(image.PixelWidth * 72 / dpi, image.PixelHeight * 72 / dpi);
        double scale = Math.Min(1, MaxPagePoints / Math.Max(size.Width, size.Height));
        return new XSize(size.Width * scale, size.Height * scale);
    }

    static XSize ChoosePageSize(ImagePageLayout layout, XSize image)
    {
        if (layout.Size == PageSizeChoice.FitImage)
            return new XSize(image.Width + 2 * layout.Margin, image.Height + 2 * layout.Margin);

        XSize paper = layout.Size == PageSizeChoice.Letter ? Letter : A4;
        bool landscape = layout.Orientation switch
        {
            PageOrientationChoice.Landscape => true,
            PageOrientationChoice.Portrait => false,
            _ => image.Width > image.Height,
        };
        return landscape ? new XSize(paper.Height, paper.Width) : paper;
    }

    static XRect FitInside(XSize image, XRect page, double margin)
    {
        XRect room = new(page.X + margin, page.Y + margin,
            Math.Max(1, page.Width - 2 * margin), Math.Max(1, page.Height - 2 * margin));
        double scale = Math.Min(room.Width / image.Width, room.Height / image.Height);
        double width = image.Width * scale, height = image.Height * scale;
        return new XRect(room.X + (room.Width - width) / 2, room.Y + (room.Height - height) / 2, width, height);
    }

    static void DrawTurned(XGraphics graphics, XImage image, XRect box, int quarterTurns)
    {
        if (quarterTurns == 0)
        {
            graphics.DrawImage(image, box);
            return;
        }
        var center = new XPoint(box.X + box.Width / 2, box.Y + box.Height / 2);
        graphics.RotateAtTransform(90 * quarterTurns, center);
        XSize drawn = quarterTurns % 2 == 1 ? new XSize(box.Height, box.Width) : box.Size;
        graphics.DrawImage(image, new XRect(center.X - drawn.Width / 2, center.Y - drawn.Height / 2, drawn.Width, drawn.Height));
    }
}
