using System.Drawing;
using System.Runtime.InteropServices;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using WinPdf = Windows.Data.Pdf;

namespace Sheaf.Core.Rendering;

public enum PageImageFormat { Jpeg, Png }

/// <summary>
/// Renders pages with the PDF engine built into Windows (Windows.Data.Pdf), so Sheaf ships no native renderer.
/// One instance keeps the file open; renders are serialized because the engine renders one page at a time anyway.
/// </summary>
public sealed class PdfPreview : IDisposable
{
    const int WrongPasswordHResult = unchecked((int)0x8007052B);
    const float DipsPerPoint = 96f / 72f;

    readonly FileStream _file;
    readonly WinPdf.PdfDocument _document;
    readonly SemaphoreSlim _gate = new(1, 1);

    PdfPreview(string path, FileStream file, WinPdf.PdfDocument document)
    {
        Path = path;
        _file = file;
        _document = document;
    }

    public string Path { get; }

    public int PageCount => (int)_document.PageCount;

    public static async Task<PdfPreview> OpenAsync(string path, string? password = null)
    {
        string name = System.IO.Path.GetFileName(path);
        FileStream file;
        try
        {
            file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw Pdf.PdfFiles.Describe(ex, name);
        }

        try
        {
            IRandomAccessStream stream = file.AsRandomAccessStream();
            WinPdf.PdfDocument document = password is null
                ? await WinPdf.PdfDocument.LoadFromStreamAsync(stream).AsTask().ConfigureAwait(false)
                : await WinPdf.PdfDocument.LoadFromStreamAsync(stream, password).AsTask().ConfigureAwait(false);
            return new PdfPreview(path, file, document);
        }
        catch (Exception ex)
        {
            await file.DisposeAsync();
            throw ex.HResult == WrongPasswordHResult
                ? Pdf.PdfFiles.PasswordProblem(name, password, ex)
                : new ToolException($"“{name}” can’t be read. It may be damaged or not a PDF.", ex);
        }
    }

    /// <summary>Page size in points as it is displayed, with the page's own rotation applied.</summary>
    public SizeF GetPageSize(int index)
    {
        using WinPdf.PdfPage page = _document.GetPage((uint)index);
        return new SizeF((float)page.Size.Width / DipsPerPoint, (float)page.Size.Height / DipsPerPoint);
    }

    public IReadOnlyList<SizeF> GetPageSizes() => [.. Enumerable.Range(0, PageCount).Select(GetPageSize)];

    public async Task<Bitmap> RenderAsync(int index, int widthPixels, CancellationToken cancel = default)
    {
        using var output = new InMemoryRandomAccessStream();
        await RenderToStreamAsync(index, output, new WinPdf.PdfPageRenderOptions
        {
            DestinationWidth = (uint)Math.Max(1, widthPixels),
            BitmapEncoderId = BitmapEncoder.BmpEncoderId,
        }, cancel).ConfigureAwait(false);

        using Stream stream = output.AsStreamForRead();
        using var decoded = new Bitmap(stream);
        return new Bitmap(decoded);
    }

    public async Task SaveImageAsync(int index, string path, PageImageFormat format, int dpi, CancellationToken cancel = default)
    {
        SizeF points = GetPageSize(index);
        using var output = new InMemoryRandomAccessStream();
        await RenderToStreamAsync(index, output, new WinPdf.PdfPageRenderOptions
        {
            DestinationWidth = (uint)Math.Max(1, Math.Round(points.Width / 72f * dpi)),
            DestinationHeight = (uint)Math.Max(1, Math.Round(points.Height / 72f * dpi)),
            BitmapEncoderId = format == PageImageFormat.Png ? BitmapEncoder.PngEncoderId : BitmapEncoder.JpegEncoderId,
        }, cancel).ConfigureAwait(false);

        using Stream image = output.AsStreamForRead();
        await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        await image.CopyToAsync(file, cancel).ConfigureAwait(false);
    }

    async Task RenderToStreamAsync(int index, IRandomAccessStream output, WinPdf.PdfPageRenderOptions options, CancellationToken cancel)
    {
        await _gate.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            using WinPdf.PdfPage page = _document.GetPage((uint)index);
            await page.RenderToStreamAsync(output, options).AsTask(cancel).ConfigureAwait(false);
            output.Seek(0);
        }
        catch (COMException ex)
        {
            throw new ToolException($"Page {index + 1} of “{System.IO.Path.GetFileName(Path)}” can’t be drawn.", ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _gate.Dispose();
        _file.Dispose();
    }
}
