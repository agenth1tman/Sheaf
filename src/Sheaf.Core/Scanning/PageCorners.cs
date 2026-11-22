using System.Drawing;

namespace Sheaf.Core.Scanning;

/// <summary>
/// Finds the four corners of a sheet of paper in a photo: the largest bright area (paper is lighter than
/// what it lies on), and its outermost points towards each corner of the photo.
/// </summary>
public static class PageCorners
{
    const int WorkSize = 360;

    /// <summary>Corners in photo pixels, top-left, top-right, bottom-right, bottom-left; null when no page stands out.</summary>
    public static PointF[]? Find(Bitmap photo)
    {
        Pixels small = Pixels.Shrunk(photo, WorkSize);
        int width = small.Width, height = small.Height, count = width * height;
        byte[] luma = Blur(small, 2);
        int threshold = Otsu(luma);

        // Largest four-connected bright region.
        var label = new int[count];
        int bestLabel = 0, bestArea = 0, next = 0;
        var stack = new Stack<int>();
        for (int start = 0; start < count; start++)
        {
            if (label[start] != 0 || luma[start] <= threshold) continue;
            next++;
            int area = 0;
            label[start] = next;
            stack.Push(start);
            while (stack.Count > 0)
            {
                int i = stack.Pop();
                area++;
                int x = i % width, y = i / width;
                if (x > 0) Visit(i - 1);
                if (x < width - 1) Visit(i + 1);
                if (y > 0) Visit(i - width);
                if (y < height - 1) Visit(i + width);
            }
            if (area > bestArea) { bestArea = area; bestLabel = next; }

            void Visit(int j)
            {
                if (label[j] != 0 || luma[j] <= threshold) return;
                label[j] = next;
                stack.Push(j);
            }
        }
        if (bestArea < count * 0.12 || bestArea > count * 0.985) return null;

        PointF topLeft = default, topRight = default, bottomRight = default, bottomLeft = default;
        float minSum = float.MaxValue, maxSum = float.MinValue, minDiff = float.MaxValue, maxDiff = float.MinValue;
        for (int i = 0; i < count; i++)
        {
            if (label[i] != bestLabel) continue;
            float x = i % width, y = i / width, sum = x + y, diff = x - y;
            if (sum < minSum) { minSum = sum; topLeft = new(x, y); }
            if (sum > maxSum) { maxSum = sum; bottomRight = new(x, y); }
            if (diff > maxDiff) { maxDiff = diff; topRight = new(x, y); }
            if (diff < minDiff) { minDiff = diff; bottomLeft = new(x, y); }
        }
        PointF[] corners = [topLeft, topRight, bottomRight, bottomLeft];
        if (Area(corners) < count * 0.1) return null;
        float scaleX = photo.Width / (float)width, scaleY = photo.Height / (float)height;
        return [.. corners.Select(p => new PointF((p.X + 0.5f) * scaleX, (p.Y + 0.5f) * scaleY))];
    }

    /// <summary>The whole photo, for when no page stands out.</summary>
    public static PointF[] Whole(Size photo) => [new(0, 0), new(photo.Width, 0), new(photo.Width, photo.Height), new(0, photo.Height)];

    public static float Area(PointF[] quad)
    {
        float area = 0;
        for (int i = 0; i < quad.Length; i++)
        {
            PointF a = quad[i], b = quad[(i + 1) % quad.Length];
            area += a.X * b.Y - b.X * a.Y;
        }
        return Math.Abs(area) / 2;
    }

    static byte[] Blur(Pixels image, int passes)
    {
        int width = image.Width, height = image.Height;
        var luma = new byte[width * height];
        for (int i = 0; i < luma.Length; i++) luma[i] = (byte)Pixels.Luma(image.Data[i]);
        var temp = new byte[luma.Length];
        for (int pass = 0; pass < passes; pass++)
        {
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int sum = 0, n = 0;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int xx = x + dx, yy = y + dy;
                            if (xx < 0 || yy < 0 || xx >= width || yy >= height) continue;
                            sum += luma[yy * width + xx];
                            n++;
                        }
                    temp[y * width + x] = (byte)(sum / n);
                }
            (luma, temp) = (temp, luma);
        }
        return luma;
    }

    static int Otsu(byte[] luma)
    {
        var histogram = new int[256];
        foreach (byte value in luma) histogram[value]++;
        long total = luma.Length, sumAll = 0;
        for (int i = 0; i < 256; i++) sumAll += (long)i * histogram[i];
        long sumBack = 0, weightBack = 0;
        double best = -1;
        int threshold = 128;
        for (int t = 0; t < 256; t++)
        {
            weightBack += histogram[t];
            if (weightBack == 0) continue;
            long weightFore = total - weightBack;
            if (weightFore == 0) break;
            sumBack += (long)t * histogram[t];
            double meanBack = sumBack / (double)weightBack, meanFore = (sumAll - sumBack) / (double)weightFore;
            double between = (double)weightBack * weightFore * (meanBack - meanFore) * (meanBack - meanFore);
            if (between > best) { best = between; threshold = t; }
        }
        return threshold;
    }
}
