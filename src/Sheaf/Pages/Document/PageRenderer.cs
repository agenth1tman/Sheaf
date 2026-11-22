using Sheaf.Core;
using Sheaf.Core.Editing;
using Sheaf.Core.Rendering;

namespace Sheaf.Pages.Document;

/// <summary>
/// Renders the pages of a document's source files with the Windows PDF engine: thumbnails for the page panel
/// and full renders for the canvas, each cached. Blank pages need no render.
/// </summary>
internal sealed class PageRenderer(Func<int, string> sourcePath) : IDisposable
{
    const int CanvasCacheLimit = 10;
    readonly Dictionary<int, Task<PdfPreview>> _previews = [];
    readonly Dictionary<(int Source, int Page), Bitmap> _thumbnails = [];
    readonly HashSet<(int, int)> _thumbnailsQueued = [];
    readonly LinkedList<((int Source, int Page, int Width) Key, Bitmap Bitmap)> _renders = [];
    bool _disposed;

    /// <summary>A thumbnail finished; repaint whatever shows it.</summary>
    public event EventHandler? ThumbnailReady;

    public int ThumbnailWidth { get; set; } = 240;

    Task<PdfPreview> Preview(int source)
    {
        if (!_previews.TryGetValue(source, out Task<PdfPreview>? preview))
            _previews[source] = preview = PdfPreview.OpenAsync(sourcePath(source));
        return preview;
    }

    /// <summary>The cached thumbnail, or null while it renders (the first request queues it).</summary>
    public Bitmap? Thumbnail(PageEntry entry)
    {
        if (entry.IsBlank) return null;
        var key = (entry.Source, entry.SourcePage);
        if (_thumbnails.TryGetValue(key, out Bitmap? bitmap)) return bitmap;
        if (_thumbnailsQueued.Add(key)) _ = RenderThumbnailAsync(key);
        return null;
    }

    async Task RenderThumbnailAsync((int Source, int Page) key)
    {
        try
        {
            PdfPreview preview = await Preview(key.Source);
            Bitmap bitmap = await preview.RenderAsync(key.Page, ThumbnailWidth);
            if (_disposed) { bitmap.Dispose(); return; }
            _thumbnails[key] = bitmap;
            ThumbnailReady?.Invoke(this, EventArgs.Empty);
        }
        catch (ToolException) { }
    }

    /// <summary>The best cached render of a page (for drawing while a sharper one renders).</summary>
    public Bitmap? BestCached(PageEntry entry, int width)
    {
        if (entry.IsBlank) return null;
        Bitmap? best = null;
        foreach (var (key, bitmap) in _renders)
            if (key.Source == entry.Source && key.Page == entry.SourcePage && (best is null || Math.Abs(bitmap.Width - width) < Math.Abs(best.Width - width)))
                best = bitmap;
        return best ?? _thumbnails.GetValueOrDefault((entry.Source, entry.SourcePage));
    }

    public bool HasRender(PageEntry entry, int width) =>
        entry.IsBlank || _renders.Any(r => r.Key == (entry.Source, entry.SourcePage, width));

    public async Task RenderAsync(PageEntry entry, int width, CancellationToken cancel)
    {
        if (entry.IsBlank || HasRender(entry, width)) return;
        PdfPreview preview = await Preview(entry.Source);
        Bitmap bitmap = await preview.RenderAsync(entry.SourcePage, width, cancel);
        if (_disposed) { bitmap.Dispose(); return; }
        _renders.AddFirst(((entry.Source, entry.SourcePage, width), bitmap));
        while (_renders.Count > CanvasCacheLimit)
        {
            _renders.Last!.Value.Bitmap.Dispose();
            _renders.RemoveLast();
        }
    }

    public async Task<PdfPreview> PreviewAsync(int source) => await Preview(source);

    /// <summary>A render that isn't kept: the caller owns it. Null for a blank page or one that won't render.</summary>
    public async Task<Bitmap?> RenderOnceAsync(PageEntry entry, int width)
    {
        if (entry.IsBlank) return null;
        try
        {
            PdfPreview preview = await Preview(entry.Source);
            return await preview.RenderAsync(entry.SourcePage, width);
        }
        catch (ToolException) { return null; }
    }

    public void Dispose()
    {
        _disposed = true;
        foreach (Task<PdfPreview> preview in _previews.Values)
            if (preview.IsCompletedSuccessfully) preview.Result.Dispose();
        foreach (Bitmap bitmap in _thumbnails.Values) bitmap.Dispose();
        foreach (var render in _renders) render.Bitmap.Dispose();
        _renders.Clear();
    }
}
