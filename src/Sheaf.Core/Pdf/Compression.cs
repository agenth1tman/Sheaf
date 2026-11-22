using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.Filters;

namespace Sheaf.Core.Pdf;

public enum CompressionLevel
{
    /// <summary>Recompresses streams and drops unused objects; images are untouched.</summary>
    Light,
    /// <summary>Also re-encodes JPEG photos at a moderate size and quality.</summary>
    Recommended,
    /// <summary>Also turns other full-colour images into JPEG, smaller and at lower quality.</summary>
    Strong,
}

/// <summary>Makes PDFs smaller. Only images whose colour data Sheaf fully understands are re-encoded.</summary>
public static class Compression
{
    const double WorthReplacingRatio = 0.9;
    const int MinimumPixels = 64 * 64;

    sealed record ImageBudget(int MaxSide, long Quality, bool IncludeLosslessImages);

    /// <summary>Compresses a document opened for modifying. Returns the number of images re-encoded.</summary>
    public static int Compress(PdfDocument document, CompressionLevel level, CancellationToken cancel = default)
    {
        document.Options.NoCompression = false;
        document.Options.CompressContentStreams = true;
        document.Options.FlateEncodeMode = PdfFlateEncodeMode.BestCompression;
        document.Options.UseFlateDecoderForJpegImages = PdfUseFlateDecoderForJpegImages.Never;

        ImageBudget? budget = level switch
        {
            CompressionLevel.Recommended => new ImageBudget(MaxSide: 2000, Quality: 72, IncludeLosslessImages: false),
            CompressionLevel.Strong => new ImageBudget(MaxSide: 1400, Quality: 50, IncludeLosslessImages: true),
            _ => null,
        };
        if (budget is null) return 0;

        int reencoded = 0;
        foreach (PdfObject item in document.Internals.GetAllObjects())
        {
            cancel.ThrowIfCancellationRequested();
            if (item is PdfDictionary image && IsImage(image) && TryReencode(image, budget))
                reencoded++;
        }
        return reencoded;
    }

    static bool IsImage(PdfDictionary dictionary) =>
        dictionary.Stream is not null && dictionary.Elements.GetName("/Subtype") == "/Image";

    static bool TryReencode(PdfDictionary image, ImageBudget budget)
    {
        if (!HasPlainColorData(image, out int components)) return false;

        string? filter = SingleFilter(image);
        using Bitmap? bitmap = filter switch
        {
            "/DCTDecode" => DecodeJpeg(image.Stream!.Value),
            "/FlateDecode" when budget.IncludeLosslessImages => DecodeFlate(image, components),
            _ => null,
        };
        if (bitmap is null || bitmap.Width * bitmap.Height < MinimumPixels) return false;

        byte[] jpeg = EncodeJpeg(bitmap, budget, out Size size);
        if (jpeg.Length >= image.Stream!.Value.Length * WorthReplacingRatio) return false;

        image.Stream.Value = jpeg;
        image.Elements.SetName("/Filter", "/DCTDecode");
        image.Elements.Remove("/DecodeParms");
        image.Elements.SetInteger("/Width", size.Width);
        image.Elements.SetInteger("/Height", size.Height);
        image.Elements.SetInteger("/BitsPerComponent", 8);
        image.Elements.SetName("/ColorSpace", "/DeviceRGB");
        image.Elements.SetInteger("/Length", jpeg.Length);
        return true;
    }

    /// <summary>8-bit grey or RGB without masks or decode arrays: anything else is left alone.</summary>
    static bool HasPlainColorData(PdfDictionary image, out int components)
    {
        components = 0;
        if (image.Elements.GetBoolean("/ImageMask") || image.Elements.ContainsKey("/Decode")) return false;
        if (image.Elements.GetInteger("/BitsPerComponent") != 8) return false;
        components = ColorComponents(image.Elements.GetValue("/ColorSpace"));
        return components is 1 or 3;
    }

    static int ColorComponents(PdfItem? colorSpace)
    {
        if (colorSpace is PdfReference reference) colorSpace = reference.Value;
        if (colorSpace is PdfName name)
            return name.Value switch { "/DeviceRGB" => 3, "/DeviceGray" => 1, _ => 0 };
        if (colorSpace is PdfArray { Elements.Count: 2 } array && array.Elements.GetName(0) == "/ICCBased")
            return (array.Elements.GetObject(1) as PdfDictionary)?.Elements.GetInteger("/N") ?? 0;
        return 0;
    }

    static string? SingleFilter(PdfDictionary image)
    {
        PdfItem? filter = image.Elements.GetValue("/Filter");
        if (filter is PdfName name) return name.Value;
        if (filter is PdfArray { Elements.Count: 1 } array) return array.Elements.GetName(0);
        return null;
    }

    static Bitmap? DecodeJpeg(byte[] data)
    {
        try
        {
            using var stream = new MemoryStream(data);
            using var decoded = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: true);
            return new Bitmap(decoded);
        }
        catch (Exception ex) when (ex is ArgumentException or ExternalException or OutOfMemoryException)
        {
            return null;
        }
    }

    static Bitmap? DecodeFlate(PdfDictionary image, int components)
    {
        int width = image.Elements.GetInteger("/Width"), height = image.Elements.GetInteger("/Height");
        byte[]? raw = Filtering.Decode(image.Stream!.Value, image.Elements.GetValue("/Filter")!, image.Elements.GetValue("/DecodeParms"));
        if (raw is null || width <= 0 || height <= 0 || raw.Length != (long)width * height * components) return null;

        var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        BitmapData data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        try
        {
            var row = new byte[data.Stride];
            for (int y = 0; y < height; y++)
            {
                FillBgrRow(raw, y * width * components, width, components, row);
                Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, row.Length);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
        return bitmap;
    }

    static void FillBgrRow(byte[] source, int offset, int width, int components, byte[] row)
    {
        for (int x = 0; x < width; x++)
        {
            int s = offset + x * components;
            row[x * 3 + 2] = source[s];
            row[x * 3 + 1] = components == 3 ? source[s + 1] : source[s];
            row[x * 3] = components == 3 ? source[s + 2] : source[s];
        }
    }

    static byte[] EncodeJpeg(Bitmap source, ImageBudget budget, out Size size)
    {
        double scale = Math.Min(1.0, budget.MaxSide / (double)Math.Max(source.Width, source.Height));
        size = new Size(Math.Max(1, (int)Math.Round(source.Width * scale)), Math.Max(1, (int)Math.Round(source.Height * scale)));

        using var resized = new Bitmap(size.Width, size.Height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(resized))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.DrawImage(source, new Rectangle(Point.Empty, size));
        }

        using var output = new MemoryStream();
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(Encoder.Quality, budget.Quality);
        resized.Save(output, JpegCodec, parameters);
        return output.ToArray();
    }

    static readonly ImageCodecInfo JpegCodec =
        ImageCodecInfo.GetImageEncoders().First(codec => codec.FormatID == ImageFormat.Jpeg.Guid);
}
