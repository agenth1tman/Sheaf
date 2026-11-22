using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace Sheaf.Core.Pdf;

/// <summary>
/// Draws on a page in the coordinates a reader sees: origin at the top-left of the visible (crop) box,
/// after the page's /Rotate is applied. PDFsharp's own XGraphics works in the unrotated media box and
/// ignores a media box that doesn't start at 0,0, so a stamp would otherwise land sideways or off the page.
/// </summary>
sealed class PageCanvas : IDisposable
{
    readonly XMatrix _toGraphics;
    readonly double _mediaHeight;

    PageCanvas(XGraphics graphics, XSize size, XMatrix toGraphics, double mediaHeight)
    {
        Graphics = graphics;
        Size = size;
        _toGraphics = toGraphics;
        _mediaHeight = mediaHeight;
    }

    public XGraphics Graphics { get; }

    /// <summary>Width and height of the page as displayed, in points.</summary>
    public XSize Size { get; }

    public static PageCanvas Open(PdfPage page)
    {
        XMatrix toGraphics = VisualToGraphics(page, out XSize size, out double mediaHeight);
        var graphics = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
        graphics.MultiplyTransform(toGraphics);
        return new PageCanvas(graphics, size, toGraphics, mediaHeight);
    }

    /// <summary>Size of the page as displayed (crop box, after /Rotate), in points.</summary>
    public static XSize VisualSize(PdfPage page)
    {
        VisualToGraphics(page, out XSize size, out _);
        return size;
    }

    /// <summary>A point in the displayed page, in PDF user space (for annotation rectangles).</summary>
    public static XPoint ToPdf(PdfPage page, XPoint visual)
    {
        XMatrix toGraphics = VisualToGraphics(page, out _, out double mediaHeight);
        XPoint graphics = toGraphics.Transform(visual);
        return new XPoint(graphics.X, mediaHeight - graphics.Y);
    }

    /// <summary>A point in PDF user space, in the displayed page's coordinates (for annotation and field rectangles).</summary>
    public static XPoint FromPdf(PdfPage page, XPoint pdf)
    {
        XMatrix toGraphics = VisualToGraphics(page, out _, out double mediaHeight);
        toGraphics.Invert();
        return toGraphics.Transform(new XPoint(pdf.X, mediaHeight - pdf.Y));
    }

    public XPoint ToPdf(XPoint visual)
    {
        XPoint graphics = _toGraphics.Transform(visual);
        return new XPoint(graphics.X, _mediaHeight - graphics.Y);
    }

    static XMatrix VisualToGraphics(PdfPage page, out XSize size, out double mediaHeight)
    {
        PdfRectangle media = page.MediaBox;
        PdfRectangle visible = page.Elements.ContainsKey("/CropBox") ? page.CropBox : media;
        double width = visible.Width, height = visible.Height;
        int rotation = PageOps.NormalizeRotation(page.Rotate);

        XMatrix toUnrotated = rotation switch
        {
            90 => new XMatrix(0, -1, 1, 0, 0, height),
            180 => new XMatrix(-1, 0, 0, -1, width, height),
            270 => new XMatrix(0, 1, -1, 0, width, 0),
            _ => XMatrix.Identity,
        };
        toUnrotated.OffsetX += visible.X1;
        toUnrotated.OffsetY += media.Height - visible.Y2;
        size = rotation is 90 or 270 ? new XSize(height, width) : new XSize(width, height);
        mediaHeight = media.Height;
        return toUnrotated;
    }

    public void Dispose() => Graphics.Dispose();
}
