using System.Drawing.Drawing2D;
using Sheaf.Core;
using Sheaf.Core.IO;
using Sheaf.Core.Rendering;

namespace Sheaf.Tools;

/// <summary>A file added to a tool. Page count and thumbnail arrive later from <see cref="Inspect"/>.</summary>
internal sealed class SourceFile : IDisposable
{
    static readonly SemaphoreSlim InspectGate = new(2);

    public SourceFile(string path)
    {
        Path = path;
        Name = System.IO.Path.GetFileName(path);
        BaseName = System.IO.Path.GetFileNameWithoutExtension(path);
        Folder = System.IO.Path.GetDirectoryName(path) ?? "";
        Kind = FileKinds.Of(path);
        try { Size = new FileInfo(path).Length; }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public string Path { get; }
    public string Name { get; }
    public string BaseName { get; }
    public string Folder { get; }
    public FileKind Kind { get; }
    public long Size { get; }
    public int? PageCount { get; private set; }
    public Bitmap? Thumbnail { get; private set; }

    /// <summary>Why this file may not work, in a few words ("Password-protected").</summary>
    public string? Problem { get; private set; }

    public bool IsPasswordProtected { get; private set; }

    public string Details
    {
        get
        {
            var parts = new List<string>(3);
            if (PageCount is int pages) parts.Add(Words.Pages(pages));
            parts.Add(Words.Size(Size));
            parts.Add(Folder);
            return string.Join(" · ", parts);
        }
    }

    public override string ToString() => Problem is null ? $"{Name}, {Details}" : $"{Name}, {Problem}, {Details}";

    /// <summary>Reads the page count and draws a thumbnail <paramref name="thumbnailWidth"/> pixels wide.</summary>
    public async Task Inspect(int thumbnailWidth, CancellationToken cancel)
    {
        await InspectGate.WaitAsync(cancel);
        try
        {
            if (Kind == FileKind.Pdf) await InspectPdf(thumbnailWidth, cancel);
            else if (Kind == FileKind.Image) await Task.Run(() => InspectImage(thumbnailWidth), cancel);
        }
        finally
        {
            InspectGate.Release();
        }
    }

    async Task InspectPdf(int thumbnailWidth, CancellationToken cancel)
    {
        try
        {
            using PdfPreview preview = await PdfPreview.OpenAsync(Path);
            PageCount = preview.PageCount;
            if (preview.PageCount > 0) SetThumbnail(await preview.RenderAsync(0, thumbnailWidth, cancel));
        }
        catch (PasswordException)
        {
            IsPasswordProtected = true;
            Problem = "Password-protected";
        }
        catch (ToolException)
        {
            Problem = "Can’t be read";
        }
    }

    void InspectImage(int thumbnailWidth)
    {
        try
        {
            using var image = Image.FromFile(Path);
            float scale = thumbnailWidth / (float)Math.Max(image.Width, image.Height);
            var size = new Size(Math.Max(1, (int)(image.Width * scale)), Math.Max(1, (int)(image.Height * scale)));
            var thumbnail = new Bitmap(size.Width, size.Height);
            using (var g = Graphics.FromImage(thumbnail))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(image, new Rectangle(Point.Empty, size));
            }
            SetThumbnail(thumbnail);
        }
        catch (Exception ex) when (ex is OutOfMemoryException or ArgumentException or IOException)
        {
            Problem = "Not an image Sheaf can read";
        }
    }

    void SetThumbnail(Bitmap bitmap)
    {
        Bitmap? old = Thumbnail;
        Thumbnail = bitmap;
        old?.Dispose();
    }

    public void Dispose() => Thumbnail?.Dispose();
}
