using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Sheaf.Core.Scanning;

/// <summary>The pixels of an image as 32-bit ARGB values in one array, for fast work pixel by pixel.</summary>
public sealed class Pixels(int width, int height, int[] data)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public int[] Data { get; } = data;

    public static Pixels From(Bitmap bitmap)
    {
        var area = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        BitmapData locked = bitmap.LockBits(area, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var data = new int[bitmap.Width * bitmap.Height];
            for (int y = 0; y < bitmap.Height; y++)
                Marshal.Copy(locked.Scan0 + y * locked.Stride, data, y * bitmap.Width, bitmap.Width);
            return new Pixels(bitmap.Width, bitmap.Height, data);
        }
        finally
        {
            bitmap.UnlockBits(locked);
        }
    }

    /// <summary>A smaller copy whose longest side is at most <paramref name="longest"/> pixels, averaged when shrinking.</summary>
    public static Pixels Shrunk(Bitmap bitmap, int longest)
    {
        float scale = Math.Min(1f, longest / (float)Math.Max(bitmap.Width, bitmap.Height));
        if (scale >= 1f) return From(bitmap);
        using var small = new Bitmap(Math.Max(1, (int)(bitmap.Width * scale)), Math.Max(1, (int)(bitmap.Height * scale)), PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(small))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(bitmap, 0, 0, small.Width, small.Height);
        }
        return From(small);
    }

    public Bitmap ToBitmap()
    {
        var bitmap = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
        BitmapData locked = bitmap.LockBits(new Rectangle(0, 0, Width, Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < Height; y++)
                Marshal.Copy(Data, y * Width, locked.Scan0 + y * locked.Stride, Width);
        }
        finally
        {
            bitmap.UnlockBits(locked);
        }
        return bitmap;
    }

    public static int Luma(int argb) => (((argb >> 16) & 0xFF) * 299 + ((argb >> 8) & 0xFF) * 587 + (argb & 0xFF) * 114) / 1000;

    public static int Rgb(int r, int g, int b) =>
        unchecked((int)0xFF000000) | (Math.Clamp(r, 0, 255) << 16) | (Math.Clamp(g, 0, 255) << 8) | Math.Clamp(b, 0, 255);
}
