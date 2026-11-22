using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using WinBitmapDecoder = Windows.Graphics.Imaging.BitmapDecoder;
using WinBitmapEncoder = Windows.Graphics.Imaging.BitmapEncoder;

namespace Sheaf.Core.Editing;

/// <summary>
/// The picture of an image object: how it looks with rounded corners and opacity, and what cropping, turning or
/// flipping it leaves. Edits return new bytes, so undo simply keeps the old ones.
/// </summary>
public static class ImageLook
{
    /// <summary>The most pixels a placed picture keeps along its longer side: about 470 dpi across a whole page.</summary>
    public const int MaxSide = 4000;

    /// <summary>
    /// The picture made no bigger than <paramref name="maxSide"/> pixels along its longer side (photos turned the
    /// way their camera says), or the same bytes when it already fits or can't be read. Windows' decoder scales as it
    /// reads, so a huge picture never sits whole in memory: a 20,000-pixel one would take most of a gigabyte.
    /// </summary>
    public static byte[] Fit(byte[] data, int maxSide = MaxSide)
    {
        try { return Task.Run(() => FitAsync(data, maxSide)).GetAwaiter().GetResult(); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException or IOException)
        {
            return data;
        }
    }

    static async Task<byte[]> FitAsync(byte[] data, int maxSide)
    {
        using IRandomAccessStream input = new MemoryStream(data).AsRandomAccessStream();
        WinBitmapDecoder decoder = await WinBitmapDecoder.CreateAsync(input).AsTask().ConfigureAwait(false);
        uint width = decoder.OrientedPixelWidth, height = decoder.OrientedPixelHeight;
        if (Math.Max(width, height) <= maxSide) return data;
        double scale = maxSide / (double)Math.Max(width, height);
        uint outWidth = (uint)Math.Max(1, Math.Round(width * scale)), outHeight = (uint)Math.Max(1, Math.Round(height * scale));
        // The scale applies before the photo is turned upright, so a sideways photo's sides swap.
        bool turned = width != decoder.PixelWidth;
        var transform = new BitmapTransform
        {
            ScaledWidth = turned ? outHeight : outWidth,
            ScaledHeight = turned ? outWidth : outHeight,
            InterpolationMode = BitmapInterpolationMode.Fant,
        };
        PixelDataProvider pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
            ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb).AsTask().ConfigureAwait(false);
        bool jpeg = decoder.DecoderInformation.CodecId == WinBitmapDecoder.JpegDecoderId;
        using var output = new InMemoryRandomAccessStream();
        WinBitmapEncoder encoder = await WinBitmapEncoder.CreateAsync(jpeg ? WinBitmapEncoder.JpegEncoderId : WinBitmapEncoder.PngEncoderId, output).AsTask().ConfigureAwait(false);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, jpeg ? BitmapAlphaMode.Ignore : BitmapAlphaMode.Premultiplied, outWidth, outHeight, 96, 96, pixels.DetachPixelData());
        await encoder.FlushAsync().AsTask().ConfigureAwait(false);
        using Stream result = output.AsStreamForRead();
        using var copy = new MemoryStream();
        await result.CopyToAsync(copy).ConfigureAwait(false);
        return copy.ToArray();
    }

    /// <summary>True when the picture shows differently from its plain bytes.</summary>
    public static bool IsStyled(PageObject item) => item.CornerRadius > 0 || item.Opacity < 1 || item.Tint >> 24 != 0;

    /// <summary>
    /// The bytes the PDF gets: corners and opacity are made part of the picture as transparency, since that is
    /// the one way PDFsharp can fade or round an image. Unchanged when there is nothing to apply.
    /// </summary>
    public static byte[] Bake(byte[] data, PageObject item)
    {
        if (!IsStyled(item)) return data;
        using var source = Image.FromStream(new MemoryStream(data));
        using Bitmap styled = Render(source, item, int.MaxValue);
        using var png = new MemoryStream();
        styled.Save(png, ImageFormat.Png);
        return png.ToArray();
    }

    /// <summary>
    /// The picture with its corners rounded and its opacity applied, at most <paramref name="maxSide"/> pixels on
    /// its longer side. Corners are drawn with a smoothed edge rather than cut.
    /// </summary>
    public static Bitmap Render(Image source, PageObject item, int maxSide)
    {
        float shrink = Math.Min(1f, maxSide / (float)Math.Max(source.Width, source.Height));
        int width = Math.Max(1, (int)Math.Round(source.Width * shrink)), height = Math.Max(1, (int)Math.Round(source.Height * shrink));
        var result = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        using Graphics g = Graphics.FromImage(result);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        using var fade = new ImageAttributes();
        var colours = new ColorMatrix { Matrix33 = Math.Clamp(item.Opacity, 0, 1) };
        if (item.Tint >> 24 != 0)
        {
            // Every pixel becomes the tint, keeping how see-through it is.
            Color tint = Color.FromArgb(item.Tint);
            colours.Matrix00 = colours.Matrix11 = colours.Matrix22 = 0;
            colours.Matrix40 = tint.R / 255f;
            colours.Matrix41 = tint.G / 255f;
            colours.Matrix42 = tint.B / 255f;
        }
        fade.SetColorMatrix(colours);
        using var brush = new TextureBrush(source, new Rectangle(0, 0, source.Width, source.Height), fade) { WrapMode = WrapMode.Clamp };
        brush.ScaleTransform(width / (float)source.Width, height / (float)source.Height);
        // The radius is in page points; the picture may be stretched, so each corner is an ellipse in pixels.
        float radiusX = item.CornerRadius * width / Math.Max(1f, item.Width), radiusY = item.CornerRadius * height / Math.Max(1f, item.Height);
        using GraphicsPath shape = Rounded(new RectangleF(0, 0, width, height), radiusX, radiusY);
        g.FillPath(brush, shape);
        return result;
    }

    static GraphicsPath Rounded(RectangleF box, float radiusX, float radiusY)
    {
        var path = new GraphicsPath();
        float rx = Math.Min(radiusX, box.Width / 2), ry = Math.Min(radiusY, box.Height / 2);
        if (rx < 0.5f || ry < 0.5f)
        {
            path.AddRectangle(box);
            return path;
        }
        path.AddArc(box.Left, box.Top, 2 * rx, 2 * ry, 180, 90);
        path.AddArc(box.Right - 2 * rx, box.Top, 2 * rx, 2 * ry, 270, 90);
        path.AddArc(box.Right - 2 * rx, box.Bottom - 2 * ry, 2 * rx, 2 * ry, 0, 90);
        path.AddArc(box.Left, box.Bottom - 2 * ry, 2 * rx, 2 * ry, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>The part of the picture inside <paramref name="part"/>, given as fractions of its width and height.</summary>
    public static byte[] Crop(byte[] data, RectangleF part)
    {
        using var source = Image.FromStream(new MemoryStream(data));
        var area = Rectangle.Round(new RectangleF(part.X * source.Width, part.Y * source.Height, part.Width * source.Width, part.Height * source.Height));
        area.Intersect(new Rectangle(0, 0, source.Width, source.Height));
        if (area.Width < 1 || area.Height < 1) return data;
        using var cropped = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppArgb);
        cropped.SetResolution(source.HorizontalResolution, source.VerticalResolution);
        using (Graphics g = Graphics.FromImage(cropped)) g.DrawImage(source, new Rectangle(0, 0, area.Width, area.Height), area, GraphicsUnit.Pixel);
        return Encode(cropped, source.RawFormat);
    }

    /// <summary>The picture turned a quarter clockwise, or anticlockwise.</summary>
    public static byte[] Turn(byte[] data, bool clockwise) => Transform(data, clockwise ? RotateFlipType.Rotate90FlipNone : RotateFlipType.Rotate270FlipNone);

    /// <summary>The picture mirrored left to right, or upside down.</summary>
    public static byte[] Flip(byte[] data, bool leftToRight) => Transform(data, leftToRight ? RotateFlipType.RotateNoneFlipX : RotateFlipType.RotateNoneFlipY);

    static byte[] Transform(byte[] data, RotateFlipType change)
    {
        using var source = Image.FromStream(new MemoryStream(data));
        using var copy = new Bitmap(source);
        copy.RotateFlip(change);
        return Encode(copy, source.RawFormat);
    }

    /// <summary>Photos stay JPEG; anything else becomes PNG so transparency survives.</summary>
    static byte[] Encode(Bitmap bitmap, ImageFormat original)
    {
        using var output = new MemoryStream();
        if (original.Equals(ImageFormat.Jpeg))
        {
            ImageCodecInfo jpeg = ImageCodecInfo.GetImageEncoders().First(codec => codec.FormatID == ImageFormat.Jpeg.Guid);
            using var options = new EncoderParameters(1);
            options.Param[0] = new EncoderParameter(Encoder.Quality, 92L);
            bitmap.Save(output, jpeg, options);
        }
        else bitmap.Save(output, ImageFormat.Png);
        return output.ToArray();
    }
}
