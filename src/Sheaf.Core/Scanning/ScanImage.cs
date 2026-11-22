using System.Drawing;

namespace Sheaf.Core.Scanning;

/// <summary>How a scanned page is cleaned up.</summary>
public enum ScanFilter
{
    /// <summary>Even white paper, colours kept: the usual choice.</summary>
    Auto,
    /// <summary>The photo's colours with the contrast stretched.</summary>
    Colour,
    Greyscale,
    /// <summary>Pure black on white, for text.</summary>
    BlackAndWhite,
    /// <summary>A whiteboard or blackboard photo: white background, strong marker colours.</summary>
    Whiteboard,
    /// <summary>Straightened only.</summary>
    Original,
}

/// <summary>Straightens a photographed page and cleans it up.</summary>
public static class ScanImage
{
    const int LongestSide = 3000;

    /// <summary>
    /// Maps the four corners (top-left, top-right, bottom-right, bottom-left) to an upright rectangle, as if the
    /// page had been scanned flat.
    /// </summary>
    public static Bitmap Straighten(Bitmap photo, PointF[] corners) => Straighten(Pixels.From(photo), corners);

    public static Bitmap Straighten(Pixels source, PointF[] corners)
    {
        float Distance(PointF a, PointF b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
        float width = Math.Max(Distance(corners[0], corners[1]), Distance(corners[3], corners[2]));
        float height = Math.Max(Distance(corners[0], corners[3]), Distance(corners[1], corners[2]));
        float scale = Math.Min(1f, LongestSide / Math.Max(width, height));
        int w = Math.Max(1, (int)(width * scale)), h = Math.Max(1, (int)(height * scale));

        double[] map = Homography([new(0, 0), new(w, 0), new(w, h), new(0, h)], corners);
        var output = new int[w * h];
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                double px = x + 0.5, py = y + 0.5;
                double d = map[6] * px + map[7] * py + 1;
                double sx = (map[0] * px + map[1] * py + map[2]) / d - 0.5, sy = (map[3] * px + map[4] * py + map[5]) / d - 0.5;
                output[y * w + x] = Sample(source, sx, sy);
            }
        });
        return new Pixels(w, h, output).ToBitmap();
    }

    /// <summary>The 3×3 projective map (last entry 1) that takes each point of <paramref name="from"/> to <paramref name="to"/>.</summary>
    static double[] Homography(PointF[] from, PointF[] to)
    {
        var a = new double[8, 9];
        for (int i = 0; i < 4; i++)
        {
            double x = from[i].X, y = from[i].Y, u = to[i].X, v = to[i].Y;
            double[] rowU = [x, y, 1, 0, 0, 0, -u * x, -u * y, u];
            double[] rowV = [0, 0, 0, x, y, 1, -v * x, -v * y, v];
            for (int j = 0; j < 9; j++) { a[2 * i, j] = rowU[j]; a[2 * i + 1, j] = rowV[j]; }
        }
        // Gaussian elimination with partial pivoting.
        for (int column = 0; column < 8; column++)
        {
            int pivot = column;
            for (int row = column + 1; row < 8; row++)
                if (Math.Abs(a[row, column]) > Math.Abs(a[pivot, column])) pivot = row;
            for (int j = 0; j < 9; j++) (a[column, j], a[pivot, j]) = (a[pivot, j], a[column, j]);
            double lead = a[column, column];
            if (Math.Abs(lead) < 1e-12) throw new ToolException("Those corners don’t outline a page. Drag them to the page’s corners.");
            for (int j = column; j < 9; j++) a[column, j] /= lead;
            for (int row = 0; row < 8; row++)
            {
                if (row == column) continue;
                double factor = a[row, column];
                for (int j = column; j < 9; j++) a[row, j] -= factor * a[column, j];
            }
        }
        return [.. Enumerable.Range(0, 8).Select(i => a[i, 8])];
    }

    static int Sample(Pixels image, double x, double y)
    {
        x = Math.Clamp(x, 0, image.Width - 1);
        y = Math.Clamp(y, 0, image.Height - 1);
        int x0 = (int)x, y0 = (int)y, x1 = Math.Min(x0 + 1, image.Width - 1), y1 = Math.Min(y0 + 1, image.Height - 1);
        double fx = x - x0, fy = y - y0;
        int a = image.Data[y0 * image.Width + x0], b = image.Data[y0 * image.Width + x1];
        int c = image.Data[y1 * image.Width + x0], d = image.Data[y1 * image.Width + x1];
        int Mix(int shift)
        {
            double top = ((a >> shift) & 0xFF) * (1 - fx) + ((b >> shift) & 0xFF) * fx;
            double bottom = ((c >> shift) & 0xFF) * (1 - fx) + ((d >> shift) & 0xFF) * fx;
            return (int)Math.Round(top * (1 - fy) + bottom * fy);
        }
        return Pixels.Rgb(Mix(16), Mix(8), Mix(0));
    }

    /// <summary>A cleaned-up copy of a straightened page.</summary>
    public static Bitmap Clean(Bitmap page, ScanFilter filter)
    {
        Pixels image = Pixels.From(page);
        int[] data = image.Data;
        switch (filter)
        {
            case ScanFilter.Original:
                break;
            case ScanFilter.Colour:
                Stretch(data);
                break;
            case ScanFilter.Greyscale:
                Flatten(image);
                for (int i = 0; i < data.Length; i++) { int l = Pixels.Luma(data[i]); data[i] = Pixels.Rgb(l, l, l); }
                Stretch(data);
                break;
            case ScanFilter.BlackAndWhite:
                Flatten(image);
                for (int i = 0; i < data.Length; i++) { int l = Pixels.Luma(data[i]) < 170 ? 0 : 255; data[i] = Pixels.Rgb(l, l, l); }
                break;
            case ScanFilter.Whiteboard:
                Flatten(image);
                Saturate(data, 1.6, whiten: 225);
                break;
            default:
                Flatten(image);
                Saturate(data, 1.15, whiten: 238);
                break;
        }
        return image.ToBitmap();
    }

    /// <summary>
    /// Evens out the light: estimates the paper's colour everywhere and divides by it, so shadows and a warm lamp
    /// become white paper. The paper colour of each cell is the average of its brighter pixels, leaving out the
    /// very brightest, which on photos are the halos cameras and JPEG put around letters.
    /// </summary>
    static void Flatten(Pixels image)
    {
        int cellSize = Math.Max(24, Math.Max(image.Width, image.Height) / 60);
        int cw = (image.Width + cellSize - 1) / cellSize, ch = (image.Height + cellSize - 1) / cellSize;
        var histograms = new int[cw * ch, 256];
        for (int y = 0; y < image.Height; y++)
            for (int x = 0; x < image.Width; x++)
                histograms[y / cellSize * cw + x / cellSize, Pixels.Luma(image.Data[y * image.Width + x])]++;

        var low = new int[cw * ch];
        var high = new int[cw * ch];
        for (int cell = 0; cell < low.Length; cell++)
        {
            int total = 0;
            for (int v = 0; v < 256; v++) total += histograms[cell, v];
            int seen = 0;
            low[cell] = high[cell] = 255;
            for (int v = 0; v < 256; v++)
            {
                seen += histograms[cell, v];
                if (low[cell] == 255 && seen >= total * 0.8) low[cell] = v;
                if (seen >= total * 0.97) { high[cell] = v; break; }
            }
        }

        var sums = new long[cw * ch, 4];
        for (int y = 0; y < image.Height; y++)
            for (int x = 0; x < image.Width; x++)
            {
                int pixel = image.Data[y * image.Width + x], cell = y / cellSize * cw + x / cellSize, luma = Pixels.Luma(pixel);
                if (luma < low[cell] || luma > high[cell]) continue;
                sums[cell, 0] += (pixel >> 16) & 0xFF;
                sums[cell, 1] += (pixel >> 8) & 0xFF;
                sums[cell, 2] += pixel & 0xFF;
                sums[cell, 3]++;
            }

        var paper = new int[3][];
        for (int channel = 0; channel < 3; channel++)
        {
            var estimate = new int[cw * ch];
            for (int cell = 0; cell < estimate.Length; cell++)
                estimate[cell] = sums[cell, 3] == 0 ? 255 : (int)(sums[cell, channel] / sums[cell, 3]);
            // A cell covered by a picture or a heavy title borrows its neighbours' paper; then everything is softened.
            var spread = new int[estimate.Length];
            for (int y = 0; y < ch; y++)
                for (int x = 0; x < cw; x++)
                {
                    int best = 0;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                            if (x + dx >= 0 && y + dy >= 0 && x + dx < cw && y + dy < ch) best = Math.Max(best, estimate[(y + dy) * cw + x + dx]);
                    spread[y * cw + x] = best;
                }
            var smooth = new int[spread.Length];
            for (int y = 0; y < ch; y++)
                for (int x = 0; x < cw; x++)
                {
                    int sum = 0, n = 0;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                            if (x + dx >= 0 && y + dy >= 0 && x + dx < cw && y + dy < ch) { sum += spread[(y + dy) * cw + x + dx]; n++; }
                    smooth[y * cw + x] = sum / n;
                }
            paper[channel] = smooth;
        }
        int[] data = image.Data;
        Parallel.For(0, image.Height, y =>
        {
            float gy = Math.Clamp((y + 0.5f) / cellSize - 0.5f, 0, ch - 1);
            int y0 = (int)gy, y1 = Math.Min(y0 + 1, ch - 1);
            float fy = gy - y0;
            for (int x = 0; x < image.Width; x++)
            {
                float gx = Math.Clamp((x + 0.5f) / cellSize - 0.5f, 0, cw - 1);
                int x0 = (int)gx, x1 = Math.Min(x0 + 1, cw - 1);
                float fx = gx - x0;
                int pixel = data[y * image.Width + x];
                int Channel(int c)
                {
                    int[] p = paper[c];
                    float bg = (p[y0 * cw + x0] * (1 - fx) + p[y0 * cw + x1] * fx) * (1 - fy) + (p[y1 * cw + x0] * (1 - fx) + p[y1 * cw + x1] * fx) * fy;
                    return (int)(((pixel >> (16 - c * 8)) & 0xFF) * 255f / Math.Max(40f, bg));
                }
                data[y * image.Width + x] = Pixels.Rgb(Channel(0), Channel(1), Channel(2));
            }
        });
    }

    /// <summary>Stretches the levels so the darkest and lightest 1 % become black and white.</summary>
    static void Stretch(int[] data)
    {
        var histogram = new int[256];
        foreach (int pixel in data) histogram[Pixels.Luma(pixel)]++;
        int low = Percentile(histogram, data.Length * 0.01), high = Percentile(histogram, data.Length * 0.99);
        if (high - low < 10) return;
        float scale = 255f / (high - low);
        for (int i = 0; i < data.Length; i++)
        {
            int pixel = data[i];
            data[i] = Pixels.Rgb((int)((((pixel >> 16) & 0xFF) - low) * scale), (int)((((pixel >> 8) & 0xFF) - low) * scale), (int)(((pixel & 0xFF) - low) * scale));
        }
    }

    static int Percentile(int[] histogram, double count)
    {
        long seen = 0;
        for (int i = 0; i < 256; i++)
        {
            seen += histogram[i];
            if (seen >= count) return i;
        }
        return 255;
    }

    /// <summary>Boosts colour and turns near-white into white.</summary>
    static void Saturate(int[] data, double amount, int whiten)
    {
        for (int i = 0; i < data.Length; i++)
        {
            int pixel = data[i], r = (pixel >> 16) & 0xFF, g = (pixel >> 8) & 0xFF, b = pixel & 0xFF;
            if (r >= whiten && g >= whiten && b >= whiten) { data[i] = Pixels.Rgb(255, 255, 255); continue; }
            double l = Pixels.Luma(pixel);
            data[i] = Pixels.Rgb((int)(l + (r - l) * amount), (int)(l + (g - l) * amount), (int)(l + (b - l) * amount));
        }
    }
}
