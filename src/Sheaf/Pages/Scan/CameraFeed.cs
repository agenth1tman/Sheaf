using System.Drawing.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using Sheaf.Core;
using Windows.Devices.Enumeration;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;

namespace Sheaf.Pages.Scan;

/// <summary>A camera on this PC.</summary>
internal sealed record Camera(string Id, string Name);

/// <summary>
/// Live frames from a camera through Windows' own capture API. Frames stay on this PC; nothing is recorded
/// or sent anywhere. <see cref="Frame"/> is raised on a worker thread with a new bitmap the receiver owns.
/// </summary>
internal sealed class CameraFeed : IAsyncDisposable
{
    const uint PreferredWidth = 1920;
    readonly MediaCapture _capture;
    readonly MediaFrameReader _reader;
    long _lastFrame;

    CameraFeed(MediaCapture capture, MediaFrameReader reader)
    {
        _capture = capture;
        _reader = reader;
        _reader.FrameArrived += OnFrameArrived;
    }

    public event EventHandler<Bitmap>? Frame;

    public static async Task<IReadOnlyList<Camera>> FindAsync()
    {
        DeviceInformationCollection devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
        return [.. devices.Where(device => device.IsEnabled).Select(device => new Camera(device.Id, device.Name))];
    }

    public static async Task<CameraFeed> StartAsync(Camera camera)
    {
        var capture = new MediaCapture();
        try
        {
            await capture.InitializeAsync(new MediaCaptureInitializationSettings
            {
                VideoDeviceId = camera.Id,
                StreamingCaptureMode = StreamingCaptureMode.Video,
                MemoryPreference = MediaCaptureMemoryPreference.Cpu,
                SharingMode = MediaCaptureSharingMode.ExclusiveControl,
            });
            MediaFrameSource source = capture.FrameSources.Values.FirstOrDefault(s => s.Info.SourceKind == MediaFrameSourceKind.Color && s.Info.MediaStreamType == Windows.Media.Capture.MediaStreamType.VideoRecord)
                ?? capture.FrameSources.Values.FirstOrDefault(s => s.Info.SourceKind == MediaFrameSourceKind.Color)
                ?? throw new ToolException($"{camera.Name} doesn’t give colour pictures Sheaf can use.");
            MediaFrameFormat? format = source.SupportedFormats
                .Where(f => f.VideoFormat.Width <= PreferredWidth)
                .OrderByDescending(f => f.VideoFormat.Width * f.VideoFormat.Height)
                .ThenByDescending(f => f.FrameRate.Denominator == 0 ? 0 : f.FrameRate.Numerator / (double)f.FrameRate.Denominator)
                .FirstOrDefault();
            if (format is not null) await source.SetFormatAsync(format);
            MediaFrameReader reader = await capture.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8);
            var feed = new CameraFeed(capture, reader);
            MediaFrameReaderStartStatus status = await reader.StartAsync();
            if (status != MediaFrameReaderStartStatus.Success)
            {
                await feed.DisposeAsync();
                throw new ToolException(status == MediaFrameReaderStartStatus.DeviceNotAvailable
                    ? $"{camera.Name} is in use by another app. Close it there, then try again."
                    : $"{camera.Name} didn’t start.");
            }
            return feed;
        }
        catch (UnauthorizedAccessException ex)
        {
            capture.Dispose();
            throw new ToolException("Windows is blocking the camera. Turn on “Let desktop apps access your camera” in Windows Settings › Privacy › Camera.", ex);
        }
        catch (Exception ex) when (ex is not ToolException)
        {
            capture.Dispose();
            throw new ToolException($"{camera.Name} couldn’t be started: {ex.Message}", ex);
        }
    }

    void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        // About 20 frames a second is plenty for a preview and for finding the page.
        long now = Environment.TickCount64;
        if (now - _lastFrame < 50) return;
        _lastFrame = now;
        using MediaFrameReference? reference = sender.TryAcquireLatestFrame();
        SoftwareBitmap? frame = reference?.VideoMediaFrame?.SoftwareBitmap;
        if (frame is null) return;
        using SoftwareBitmap bgra = frame.BitmapPixelFormat == BitmapPixelFormat.Bgra8
            ? SoftwareBitmap.Copy(frame)
            : SoftwareBitmap.Convert(frame, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        Frame?.Invoke(this, ToBitmap(bgra));
    }

    static Bitmap ToBitmap(SoftwareBitmap frame)
    {
        int width = frame.PixelWidth, height = frame.PixelHeight;
        var bytes = new byte[width * height * 4];
        frame.CopyToBuffer(bytes.AsBuffer());
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppRgb);
        BitmapData data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
        try
        {
            for (int y = 0; y < height; y++)
                System.Runtime.InteropServices.Marshal.Copy(bytes, y * width * 4, data.Scan0 + y * data.Stride, width * 4);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
        return bitmap;
    }

    public async ValueTask DisposeAsync()
    {
        _reader.FrameArrived -= OnFrameArrived;
        try { await _reader.StopAsync(); }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException) { }
        _reader.Dispose();
        _capture.Dispose();
    }
}
