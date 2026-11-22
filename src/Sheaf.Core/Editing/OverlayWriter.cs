using System.Drawing;
using System.Drawing.Drawing2D;
using PdfSharp.Drawing;
using PdfSharp.Drawing.Layout;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Annotations;
using Sheaf.Core.Pdf;

namespace Sheaf.Core.Editing;

/// <summary>Draws added objects onto a PDF page with PDFsharp, in the page's displayed coordinates.</summary>
static class OverlayWriter
{
    public const string AnnotationIdKey = "/SheafId";
    const double HighlightAlpha = 0.4;
    const double ArrowHead = 3.2;

    /// <summary>Draws every visible object except notes and redaction marks; returns images to dispose after saving.</summary>
    public static List<XImage> Draw(PdfPage page, IReadOnlyList<PageObject> objects)
    {
        var images = new List<XImage>();
        var drawable = objects.Where(o => !o.Hidden && o.Kind is not MarkKind.Note and not MarkKind.Redaction).ToList();
        if (drawable.Count > 0)
        {
            using var canvas = PageCanvas.Open(page);
            foreach (PageObject item in drawable) Draw(canvas.Graphics, item, images);
        }
        foreach (PageObject item in objects.Where(o => !o.Hidden && (o.Kind == MarkKind.Note || (o.IsMarkup && o.Comment.Length > 0))))
            AddNote(page, item);
        return images;
    }

    /// <summary>Draws one object, turned and flipped about its middle when it is.</summary>
    static void Draw(XGraphics g, PageObject item, List<XImage> images)
    {
        using Matrix? turn = ShapeGeometry.Transform(item);
        if (turn is null)
        {
            DrawUnturned(g, item, images);
            return;
        }
        XGraphicsState state = g.Save();
        float[] m = turn.Elements;
        g.MultiplyTransform(new XMatrix(m[0], m[1], m[2], m[3], m[4], m[5]), XMatrixOrder.Prepend);
        DrawUnturned(g, item, images);
        g.Restore(state);
    }

    static void DrawUnturned(XGraphics g, PageObject item, List<XImage> images)
    {
        var rect = new XRect(item.X, item.Y, item.Width, item.Height);
        switch (item.Kind)
        {
            case MarkKind.Text: DrawText(g, item, rect); break;
            case var _ when ShapeGeometry.IsClosedShape(item.Kind):
                using (GraphicsPath outline = ShapeGeometry.Outline(item))
                {
                    if (Fill(item) is { } fill) g.DrawPath(fill, ToPdf(outline));
                    if (item.StrokeWidth <= 0) break;
                    // A plain outline has sharp corners where the shape has; any other pen draws it in its own passes.
                    if (item.Pen == PenKind.Pen) g.DrawPath(new XPen(Color(item.StrokeColor, item.Opacity), item.StrokeWidth) { LineJoin = XLineJoin.Miter }, ToPdf(outline));
                    else DrawPieces(g, item, ShapeGeometry.InkPieces(item, ShapeGeometry.PenStrokes(item), []));
                }
                break;
            case var _ when ShapeGeometry.IsLine(item.Kind):
                DrawLine(g, item);
                break;
            case MarkKind.Ink:
                DrawInk(g, item);
                break;
            case MarkKind.Image when item.ImageData is { Length: > 0 } data:
                XImage image = XImage.FromStream(new MemoryStream(ImageLook.Bake(data, item)));
                images.Add(image);
                g.DrawImage(image, rect);
                break;
            case MarkKind.Highlight:
                g.DrawRectangle(new XSolidBrush(Color(item.StrokeColor, item.Opacity * HighlightAlpha)), rect);
                break;
            case MarkKind.Underline when Pen(item) is { } underline:
                g.DrawLine(underline, rect.Left, rect.Bottom - item.StrokeWidth / 2, rect.Right, rect.Bottom - item.StrokeWidth / 2);
                break;
            case MarkKind.Strikeout when Pen(item) is { } strike:
                g.DrawLine(strike, rect.Left, rect.Top + rect.Height / 2, rect.Right, rect.Top + rect.Height / 2);
                break;
        }
    }

    static void DrawText(XGraphics g, PageObject item, XRect rect)
    {
        if (Fill(item) is { } background)
        {
            if (item.CornerRadius > 0) g.DrawRoundedRectangle(background, rect, Corner(item, rect));
            else g.DrawRectangle(background, rect);
        }
        if (string.IsNullOrEmpty(item.Text)) return;
        var style = (item.Bold, item.Italic) switch
        {
            (true, true) => XFontStyleEx.BoldItalic,
            (true, false) => XFontStyleEx.Bold,
            (false, true) => XFontStyleEx.Italic,
            _ => XFontStyleEx.Regular,
        };
        var font = new XFont(item.FontFamily, item.FontSize, style);
        var formatter = new XTextFormatter(g)
        {
            Alignment = item.Align switch
            {
                TextAlign.Center => XParagraphAlignment.Center,
                TextAlign.Right => XParagraphAlignment.Right,
                TextAlign.Justify => XParagraphAlignment.Justify,
                _ => XParagraphAlignment.Left,
            },
        };
        const double Inset = 2;
        double width = Math.Max(1, rect.Width - 2 * Inset);
        // Middle or bottom: the box's spare height goes above the text.
        double spare = Math.Max(0, rect.Height - 2 * Inset - TextHeight(g, item.Text, font, width));
        double shift = item.Anchor switch { TextAnchor.Middle => spare / 2, TextAnchor.Bottom => spare, _ => 0 };
        var area = new XRect(rect.X + Inset, rect.Y + Inset + shift, width, Math.Max(1, rect.Height - 2 * Inset) + item.FontSize);
        formatter.DrawString(item.Text, font, new XSolidBrush(Color(item.StrokeColor, item.Opacity)), area, XStringFormats.TopLeft);
    }

    /// <summary>The height of text wrapped to a width the way <see cref="XTextFormatter"/> wraps it, word by word.</summary>
    static double TextHeight(XGraphics g, string text, XFont font, double width)
    {
        int lines = 0;
        foreach (string paragraph in text.Split('\n'))
        {
            lines++;
            double line = 0, space = g.MeasureString(" ", font).Width;
            foreach (string word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                double size = g.MeasureString(word, font).Width;
                if (line > 0 && line + space + size > width) { lines++; line = size; }
                else line += (line > 0 ? space : 0) + size;
            }
        }
        return lines * font.GetHeight();
    }

    static void DrawLine(XGraphics g, PageObject item)
    {
        if (Pen(item) is not { } pen) return;
        if (item.Pen == PenKind.Pen)
            using (GraphicsPath line = ShapeGeometry.Stroke(item)) g.DrawPath(pen, ToPdf(line));
        else DrawPieces(g, item, ShapeGeometry.InkPieces(item, ShapeGeometry.PenStrokes(item), []));
        var solid = new XSolidBrush(Color(item.StrokeColor, item.Opacity));
        foreach (EndMark end in ShapeGeometry.EndMarks(item))
        {
            if (item.Pen != PenKind.Pen)
            {
                // The ends in the same pen as the line.
                if (end.Filled)
                    g.DrawPolygon(new XSolidBrush(Color(item.StrokeColor, item.Opacity * ShapeGeometry.FillAlpha(item.Pen))),
                        [.. ShapeGeometry.EndMarkStrokes(end)[0].Select(p => new XPoint(p.X, p.Y))], XFillMode.Winding);
                DrawPieces(g, item, ShapeGeometry.InkPieces(item, ShapeGeometry.EndMarkStrokes(end), []));
                continue;
            }
            XPoint[] points = [.. end.Points.Select(p => new XPoint(p.X, p.Y))];
            if (ShapeGeometry.IsDot(end)) g.DrawEllipse(solid, new XRect(points[0], points[1]));
            else if (end.Filled) g.DrawPolygon(solid, points, XFillMode.Winding);
            else g.DrawLines(pen, points);
        }
    }

    /// <summary>Pen strokes, in the passes their pen makes.</summary>
    static void DrawInk(XGraphics g, PageObject item) => DrawPieces(g, item, ShapeGeometry.InkPieces(item));

    static void DrawPieces(XGraphics g, PageObject item, List<InkPiece> pieces)
    {
        foreach (InkPiece piece in pieces)
        {
            var pen = new XPen(Color(item.StrokeColor, item.Opacity * piece.Alpha), piece.Width)
            {
                LineCap = piece.Square ? XLineCap.Square : XLineCap.Round,
                LineJoin = XLineJoin.Round,
            };
            XPoint[] points = [.. piece.Points.Select(p => new XPoint(p.X, p.Y))];
            if (points.Length == 2) g.DrawLine(pen, points[0], points[1]);
            else g.DrawLines(pen, points);
        }
    }

    /// <summary>A GDI+ path (lines and curves) as the same path in the PDF.</summary>
    static XGraphicsPath ToPdf(GraphicsPath path)
    {
        var result = new XGraphicsPath();
        if (path.PointCount == 0) return result;
        PointF[] points = path.PathPoints;
        byte[] types = path.PathTypes;
        PointF current = points[0];
        for (int i = 0; i < points.Length;)
        {
            int type = types[i] & 0x7;
            int last = i;
            if (type == 0)
            {
                result.StartFigure();
                current = points[i++];
                continue;
            }
            if (type == 3 && i + 2 < points.Length)
            {
                result.AddBezier(new XPoint(current.X, current.Y), new XPoint(points[i].X, points[i].Y), new XPoint(points[i + 1].X, points[i + 1].Y), new XPoint(points[i + 2].X, points[i + 2].Y));
                current = points[i + 2];
                last = i + 2;
                i += 3;
            }
            else
            {
                result.AddLine(new XPoint(current.X, current.Y), new XPoint(points[i].X, points[i].Y));
                current = points[i++];
            }
            if ((types[last] & 0x80) != 0) result.CloseFigure();
        }
        return result;
    }

    /// <summary>
    /// Notes, and highlights or other markup with a comment, become real PDF comments, so every reader lists
    /// them. A markup's comment sits as a small note at its top-right corner.
    /// </summary>
    static void AddNote(PdfPage page, PageObject note)
    {
        const double MarkupNoteSize = 16;
        var box = note.Kind == MarkKind.Note
            ? new XRect(note.X, note.Y, note.Width, note.Height)
            : new XRect(note.X + note.Width - MarkupNoteSize / 2, note.Y - MarkupNoteSize / 2, MarkupNoteSize, MarkupNoteSize);
        XPoint a = PageCanvas.ToPdf(page, box.TopLeft), b = PageCanvas.ToPdf(page, box.BottomRight);
        var annotation = new PdfTextAnnotation
        {
            Title = "Sheaf",
            Contents = note.Comment,
            Icon = PdfTextAnnotationIcon.Note,
            Color = XColor.FromArgb(note.StrokeColor),
            Rectangle = new PdfRectangle(new XPoint(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y)), new XPoint(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y))),
        };
        annotation.Elements.SetString(AnnotationIdKey, note.Id.ToString("N"));
        page.Annotations.Add(annotation);
    }

    /// <summary>The size of the ellipse that rounds each corner, never more than half the box.</summary>
    static XSize Corner(PageObject item, XRect rect)
    {
        double diameter = Math.Min(item.CornerRadius * 2, Math.Min(rect.Width, rect.Height));
        return new XSize(diameter, diameter);
    }

    /// <summary>The outline of an object, or null for a shape whose width is set to none.</summary>
    static XPen? Pen(PageObject item) => item.StrokeWidth <= 0 ? null : new(Color(item.StrokeColor, item.Opacity), item.StrokeWidth)
    {
        LineCap = XLineCap.Round,
        LineJoin = XLineJoin.Round,
    };

    static XBrush? Fill(PageObject item) => item.FillColor >> 24 == 0 ? null : new XSolidBrush(Color(item.FillColor, item.Opacity));

    static XColor Color(int argb, double opacity)
    {
        var color = System.Drawing.Color.FromArgb(argb);
        return XColor.FromArgb((int)Math.Round(color.A * Math.Clamp(opacity, 0, 1)), color.R, color.G, color.B);
    }
}
