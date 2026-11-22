using System.Drawing.Drawing2D;
using Sheaf.Core.Scanning;

namespace Sheaf.Pages.Scan;

/// <summary>
/// One captured page: the photo as taken, where its corners are, how it is cleaned up and turned, and the
/// finished image that goes into the PDF. Bitmaps are only touched on the UI thread; the work runs on copies.
/// </summary>
internal sealed class ScannedPage : IDisposable
{
    const int ThumbnailWidth = 220;
    CancellationTokenSource? _processing;
    bool _disposed;

    public ScannedPage(Bitmap photo, bool fromCamera)
    {
        Photo = photo;
        FromCamera = fromCamera;
        Corners = PageCorners.Find(photo) ?? PageCorners.Whole(photo.Size);
    }

    public Bitmap Photo { get; }
    public bool FromCamera { get; }
    public PointF[] Corners { get; set; }
    public ScanFilter Filter { get; set; } = ScanFilter.Auto;
    public int Rotation { get; set; }

    public Bitmap? Result { get; private set; }
    public Bitmap? Thumbnail { get; private set; }

    /// <summary>About how many bytes the page adds to the PDF.</summary>
    public long Size { get; private set; }

    public bool IsReady => Result is not null;

    /// <summary>Builds the finished image again after a change; a newer change cancels an older run.</summary>
    public async Task ProcessAsync()
    {
        _processing?.Cancel();
        var cancel = new CancellationTokenSource();
        _processing = cancel;
        Pixels photo = Pixels.From(Photo);
        PointF[] corners = Corners;
        ScanFilter filter = Filter;
        int rotation = Rotation;
        var (result, thumbnail, size) = await Task.Run(() =>
        {
            using Bitmap straight = ScanImage.Straighten(photo, corners);
            Bitmap clean = ScanImage.Clean(straight, filter);
            clean.RotateFlip(rotation switch { 90 => RotateFlipType.Rotate90FlipNone, 180 => RotateFlipType.Rotate180FlipNone, 270 => RotateFlipType.Rotate270FlipNone, _ => RotateFlipType.RotateNoneFlipNone });
            long bytes = ScanWriter.Encode(new PageImage(clean, filter == ScanFilter.BlackAndWhite)).LongLength;
            return (clean, Shrink(clean, ThumbnailWidth), bytes);
        });
        if (cancel.IsCancellationRequested || _disposed)
        {
            result.Dispose();
            thumbnail.Dispose();
            return;
        }
        Result?.Dispose();
        Thumbnail?.Dispose();
        (Result, Thumbnail, Size) = (result, thumbnail, size);
    }

    static Bitmap Shrink(Bitmap image, int width)
    {
        int height = Math.Max(1, image.Height * width / Math.Max(1, image.Width));
        var small = new Bitmap(width, height);
        using Graphics g = Graphics.FromImage(small);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(image, 0, 0, width, height);
        return small;
    }

    public void Dispose()
    {
        _disposed = true;
        _processing?.Cancel();
        Photo.Dispose();
        Result?.Dispose();
        Thumbnail?.Dispose();
    }
}
