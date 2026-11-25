using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Sheaf.Core.Editing;
using Sheaf.Platform;
using Sheaf.Ui;

namespace Sheaf.Pages.Document;

/// <summary>
/// Draws a page and the objects on it with GDI+, the same way the saved PDF will look. Works in page points:
/// callers pass the rectangle the turned page occupies on screen, and this sets up the turn and the scale.
/// </summary>
internal static class PagePainter
{
    static readonly Dictionary<(string Family, float Size, FontStyle Style), Font> Fonts = [];
    static readonly Dictionary<(string Family, float Size, FontStyle Style), Font> ScreenFonts = [];
    const float HighlightAlpha = 0.4f;

    /// <summary>Maps page points (before Sheaf's turn) to the screen rectangle <paramref name="target"/>.</summary>
    public static Matrix Transform(PageEntry entry, RectangleF target)
    {
        bool sideways = entry.Rotation is 90 or 270;
        float scale = target.Width / (sideways ? entry.Height : entry.Width);
        var matrix = new Matrix();
        matrix.Translate(target.X, target.Y);
        matrix.Scale(scale, scale);
        switch (entry.Rotation)
        {
            case 90: matrix.Translate(entry.Height, 0); matrix.Rotate(90); break;
            case 180: matrix.Translate(entry.Width, entry.Height); matrix.Rotate(180); break;
            case 270: matrix.Translate(0, entry.Width); matrix.Rotate(270); break;
        }
        return matrix;
    }

    public static SizeF DisplaySize(PageEntry entry) =>
        entry.Rotation is 90 or 270 ? new SizeF(entry.Height, entry.Width) : new SizeF(entry.Width, entry.Height);

    public static void DrawPage(Graphics g, PageEntry entry, Bitmap? page, RectangleF target, Func<PageObject, bool>? include = null, bool previewRedactions = false, bool export = false)
    {
        // A page rendered at exactly the size it shows is copied pixel for pixel: as sharp as the PDF engine drew it.
        bool exact = page is not null && entry.Rotation == 0 && !entry.IsMirrored && g.Transform.IsIdentity
            && Math.Abs(target.Width - page.Width) < 1.01f && target.X == MathF.Round(target.X) && target.Y == MathF.Round(target.Y);
        if (exact)
        {
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(page!, new Rectangle((int)target.X, (int)target.Y, page!.Width, page.Height));
        }
        GraphicsState state = g.Save();
        using Matrix matrix = Transform(entry, target);
        g.MultiplyTransform(matrix);
        var bounds = new RectangleF(0, 0, entry.Width, entry.Height);
        if (!exact) FillPaper(g, entry, bounds, checkered: !export);
        if (page is not null && !exact)
        {
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            GraphicsState flat = g.Save();
            Mirror(g, entry);
            g.DrawImage(page, bounds);
            g.Restore(flat);
        }
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
        foreach (PageObject item in entry.Objects)
        {
            if (item.Hidden || (include is not null && !include(item))) continue;
            if (previewRedactions && item.Kind == MarkKind.Redaction) DrawCover(g, item, page, entry);
            else DrawObject(g, item);
        }
        g.Restore(state);
    }

    /// <summary>Mirrors drawing in page points the way the page's own content is mirrored.</summary>
    /// <summary>The page's paper and its own content, in page space, without objects: what lifting cuts from.</summary>
    public static void DrawPaper(Graphics g, PageEntry entry, Bitmap? page)
    {
        var bounds = new RectangleF(0, 0, entry.Width, entry.Height);
        FillPaper(g, entry, bounds, checkered: false);
        if (page is null) return;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        GraphicsState flat = g.Save();
        Mirror(g, entry);
        g.DrawImage(page, bounds);
        g.Restore(flat);
    }

    /// <summary>Whether a page has no paper: a see-through blank page, which exports to PNG with nothing behind.</summary>
    public static bool IsSeeThrough(PageEntry entry) => entry.IsBlank && entry.Background >> 24 == 0;

    /// <summary>
    /// The paper of a page: white, the colour chosen for a blank page, or, for a see-through one, nothing (or the
    /// grey checks editors show for "nothing here" when <paramref name="checkered"/>).
    /// </summary>
    static void FillPaper(Graphics g, PageEntry entry, RectangleF bounds, bool checkered)
    {
        if (IsSeeThrough(entry))
        {
            if (!checkered) return;
            g.FillRectangle(Brushes.White, bounds);
            using var checks = new System.Drawing.Drawing2D.HatchBrush(HatchStyle.LargeCheckerBoard, Color.FromArgb(225, 225, 228), Color.White);
            GraphicsState state = g.Save();
            // Checks a fixed size on screen, whatever the zoom.
            g.RenderingOrigin = Point.Empty;
            g.FillRectangle(checks, bounds);
            g.Restore(state);
            return;
        }
        using var paper = new SolidBrush(entry.IsBlank ? Color.FromArgb(255, Color.FromArgb(entry.Background)) : Color.White);
        g.FillRectangle(paper, bounds);
    }

    static void Mirror(Graphics g, PageEntry entry)
    {
        if (entry.MirrorX)
        {
            g.TranslateTransform(entry.Width, 0);
            g.ScaleTransform(-1, 1);
        }
        if (entry.MirrorY)
        {
            g.TranslateTransform(0, entry.Height);
            g.ScaleTransform(1, -1);
        }
    }

    /// <summary>Where a box on a mirrored page sits on the page's own, unmirrored content.</summary>
    public static RectangleF Unmirror(PageEntry entry, RectangleF box) => new(
        entry.MirrorX ? entry.Width - box.Right : box.X, entry.MirrorY ? entry.Height - box.Bottom : box.Y, box.Width, box.Height);

    // Objects that failed to draw once, so the log gets one line each rather than one per paint.
    static readonly HashSet<Guid> FailedToDraw = [];

    /// <summary>
    /// Draws an object; one that can't be drawn is skipped and logged, so a single bad object never stops the page
    /// (a paint that throws leaves WinForms showing a red cross instead of the control for good).
    /// </summary>
    public static void DrawObject(Graphics g, PageObject item)
    {
        GraphicsState state = g.Save();
        try
        {
            using (Matrix? turn = ShapeGeometry.Transform(item)) if (turn is not null) g.MultiplyTransform(turn);
            if (item.IsCropped) g.IntersectClip(ShapeGeometry.CropClip(item));
            using (GraphicsPath? erased = ShapeGeometry.ErasedPath(item))
                if (erased is not null)
                    using (var rubbed = new Region(erased)) g.ExcludeClip(rubbed);
            DrawObjectUnguarded(g, item);
        }
        catch (Exception ex) when (ex is ArgumentException or ExternalException or OverflowException or InvalidOperationException)
        {
            if (FailedToDraw.Add(item.Id)) ActivityLog.Error($"drawing a {item.Kind} object", ex);
        }
        finally { g.Restore(state); }
    }

    static void DrawObjectUnguarded(Graphics g, PageObject item)
    {
        var rect = item.Bounds;
        switch (item.Kind)
        {
            case MarkKind.Text: DrawText(g, item, rect); break;
            case var _ when ShapeGeometry.IsClosedShape(item.Kind):
                using (GraphicsPath outline = ShapeGeometry.Outline(item))
                {
                    using (Brush? fill = Fill(item)) if (fill is not null) g.FillPath(fill, outline);
                    if (item.StrokeWidth <= 0) break;
                    // A plain outline has sharp corners where the shape has; any other pen draws it in its own passes.
                    if (item.Pen == PenKind.Pen)
                        using (Pen pen = Pen(item)) { pen.LineJoin = LineJoin.Miter; g.DrawPath(pen, outline); }
                    else DrawPieces(g, item, ShapeGeometry.InkPieces(item, ShapeGeometry.PenStrokes(item), []));
                }
                break;
            case var _ when ShapeGeometry.IsLine(item.Kind):
                DrawLine(g, item);
                break;
            case MarkKind.Ink:
                DrawInk(g, item);
                break;
            case MarkKind.Image:
                if (ImageCache.Get(item) is { } image)
                {
                    Image shown = ImageLook.IsStyled(item) ? ImageCache.Styled(item, image) : image;
                    // From a copy about the size it shows at: scaling a big photo down on every paint made the page crawl.
                    float[] m = g.Transform.Elements;
                    float scale = MathF.Sqrt(m[0] * m[0] + m[1] * m[1]);
                    g.DrawImage(ImageCache.Sized(shown, (int)Math.Ceiling(rect.Width * scale), (int)Math.Ceiling(rect.Height * scale)), rect);
                }
                break;
            case MarkKind.Highlight:
                using (var brush = new SolidBrush(WithAlpha(item.StrokeColor, item.Opacity * HighlightAlpha))) g.FillRectangle(brush, rect);
                break;
            case MarkKind.Underline:
                using (Pen pen = Pen(item)) g.DrawLine(pen, rect.Left, rect.Bottom - item.StrokeWidth / 2, rect.Right, rect.Bottom - item.StrokeWidth / 2);
                break;
            case MarkKind.Strikeout:
                using (Pen pen = Pen(item)) g.DrawLine(pen, rect.Left, rect.Top + rect.Height / 2, rect.Right, rect.Top + rect.Height / 2);
                break;
            case MarkKind.Note:
                DrawNote(g, item, rect);
                break;
            case MarkKind.Redaction:
                DrawRedaction(g, item);
                break;
        }
    }

    public static Font FontFor(PageObject item) => FontFor(item, item.Bold, item.Italic);

    public static Font FontFor(PageObject item, bool bold, bool italic)
    {
        FontStyle style = (bold ? FontStyle.Bold : 0) | (italic ? FontStyle.Italic : 0);
        var key = (item.FontFamily, Math.Max(1, item.FontSize), style);
        if (Fonts.TryGetValue(key, out Font? font)) return font;
        FontFamily family = item.FontFamily switch
        {
            "Geist" => AppFonts.RegularFamily,
            "Geist Mono" => AppFonts.Mono.FontFamily,
            "Sacramento" => AppFonts.SignatureFamily,
            _ => TrySystemFamily(item.FontFamily),
        };
        font = new Font(family, key.Item2, family.IsStyleAvailable(style) ? style : FontStyle.Regular, GraphicsUnit.Pixel);
        Fonts[key] = font;
        return font;
    }

    static FontFamily TrySystemFamily(string name)
    {
        if (AppFonts.AddedFamily(name) is { } added) return added;
        try { return new FontFamily(name); }
        catch (ArgumentException) { return AppFonts.RegularFamily; }
    }

    static void DrawText(Graphics g, PageObject item, RectangleF rect)
    {
        using (Brush? fill = Fill(item))
            if (fill is not null)
            {
                if (item.CornerRadius > 0)
                    using (GraphicsPath rounded = Draw.RoundRect(rect, item.CornerRadius)) g.FillPath(fill, rounded);
                else g.FillRectangle(fill, rect);
            }
        if (string.IsNullOrEmpty(item.Text)) return;
        if (item.Spans.Count > 0)
        {
            DrawStyledText(g, item, rect);
            return;
        }
        using var format = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = item.Align switch { TextAlign.Center => StringAlignment.Center, TextAlign.Right => StringAlignment.Far, _ => StringAlignment.Near },
            Trimming = StringTrimming.None,
        };
        format.FormatFlags &= ~StringFormatFlags.NoWrap;
        format.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
        using var brush = new SolidBrush(WithAlpha(item.StrokeColor, item.Opacity));
        const float Inset = 2;
        Font font = FontFor(item);
        // Middle or bottom: the box's spare height goes above the text.
        float spare = Math.Max(0, rect.Height - (TextHeight(g, item) - 6) - 2 * Inset);
        float shift = item.Anchor switch { TextAnchor.Middle => spare / 2, TextAnchor.Bottom => spare, _ => 0 };
        var area = new RectangleF(rect.X + Inset, rect.Y + Inset + shift, Math.Max(1, rect.Width - 2 * Inset), rect.Height + item.FontSize);
        float slantY = rect.Y + shift + item.FontSize;
        GraphicsState state = g.Save();
        float[] m;
        using (Matrix current = g.Transform) m = current.Elements;
        if (Math.Abs(m[1]) < 0.0001f && Math.Abs(m[2]) < 0.0001f && m[0] > 0 && Math.Abs(m[0] - m[3]) < 0.0001f)
        {
            // An upright page: draw the text at the size it has on screen instead of scaling small text up, which
            // turns its edges jagged. (Turned pages keep the transform.)
            float scale = m[0];
            g.ResetTransform();
            font = ScreenFont(font, scale);
            area = new RectangleF(area.X * scale + m[4], area.Y * scale + m[5], area.Width * scale, area.Height * scale);
            slantY = slantY * scale + m[5];
        }
        if (item.Italic && !font.Italic)
        {
            // The PDF slants a font that has no italic; slant it here the same way.
            g.TranslateTransform(area.X, slantY);
            g.MultiplyTransform(new Matrix(1, 0, -0.2f, 1, 0, 0));
            g.TranslateTransform(-area.X, -slantY);
        }
        if (item.Align == TextAlign.Justify) DrawJustified(g, item.Text, font, brush, area);
        else g.DrawString(item.Text, font, brush, area, format);
        g.Restore(state);
    }

    static readonly StringFormat Measuring = new(StringFormat.GenericTypographic) { FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces };

    /// <summary>A text box whose words have styles of their own, laid out as the saved PDF lays it out.</summary>
    static List<TextLine> StyledLines(Graphics g, PageObject item, float width) =>
        TextLayout.Lay(item, width, (text, style) => g.MeasureString(text, FontFor(item, style.Bold, style.Italic), PointF.Empty, Measuring).Width,
            style => FontFor(item, style.Bold, style.Italic).GetHeight(g));

    static void DrawStyledText(Graphics g, PageObject item, RectangleF rect)
    {
        const float Inset = 2;
        List<TextLine> lines;
        using (var bitmap = new Bitmap(1, 1))
        using (Graphics measure = Graphics.FromImage(bitmap)) lines = StyledLines(measure, item, Math.Max(1, rect.Width - 2 * Inset));
        float spare = Math.Max(0, rect.Height - lines.Sum(line => line.Height) - 2 * Inset);
        float shift = item.Anchor switch { TextAnchor.Middle => spare / 2, TextAnchor.Bottom => spare, _ => 0 };
        GraphicsState state = g.Save();
        float[] m;
        using (Matrix current = g.Transform) m = current.Elements;
        // On an upright page each piece is drawn at its size on screen, as plain text is, so small text stays crisp.
        bool upright = Math.Abs(m[1]) < 0.0001f && Math.Abs(m[2]) < 0.0001f && m[0] > 0 && Math.Abs(m[0] - m[3]) < 0.0001f;
        float scale = upright ? m[0] : 1, dx = upright ? m[4] : 0, dy = upright ? m[5] : 0;
        if (upright) g.ResetTransform();
        foreach (TextLine line in lines)
            foreach (TextPiece piece in line.Pieces)
            {
                Font font = FontFor(item, piece.Style.Bold, piece.Style.Italic);
                if (upright) font = ScreenFont(font, scale);
                float x = (rect.X + Inset + piece.X) * scale + dx, y = (rect.Y + Inset + shift + line.Top) * scale + dy;
                using var brush = new SolidBrush(WithAlpha(piece.Style.Color, item.Opacity));
                GraphicsState slanted = g.Save();
                if (piece.Style.Italic && !font.Italic)
                {
                    float baseline = y + item.FontSize * scale;
                    g.TranslateTransform(x, baseline);
                    g.MultiplyTransform(new Matrix(1, 0, -0.2f, 1, 0, 0));
                    g.TranslateTransform(-x, -baseline);
                }
                if (piece.Text.Trim().Length > 0) g.DrawString(piece.Text, font, brush, x, y, Measuring);
                g.Restore(slanted);
                if (!piece.Style.Underline) continue;
                float thickness = Math.Max(0.5f, item.FontSize * 0.06f) * scale, under = y + line.Height * 0.8f * scale + thickness * 1.5f;
                using var pen = new Pen(WithAlpha(piece.Style.Color, item.Opacity), thickness);
                g.DrawLine(pen, x, under, x + piece.Width * scale, under);
            }
        g.Restore(state);
    }

    /// <summary>
    /// Justified text: each wrapped line but a paragraph's last is spread to fill the width, word by word, as the
    /// saved PDF lays it out.
    /// </summary>
    static void DrawJustified(Graphics g, string text, Font font, Brush brush, RectangleF area)
    {
        using var format = new StringFormat(StringFormat.GenericTypographic) { FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces };
        float Width(string word) => g.MeasureString(word, font, PointF.Empty, format).Width;
        float space = Width("a b") - Width("ab"), lineHeight = font.GetHeight(g), y = area.Y;
        foreach (string paragraph in text.Split('\n'))
        {
            var words = paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(word => (Word: word, Width: Width(word))).ToList();
            int start = 0;
            if (words.Count == 0) y += lineHeight;
            while (start < words.Count)
            {
                int end = start;
                float used = words[start].Width;
                while (end + 1 < words.Count && used + space + words[end + 1].Width <= area.Width) used += space + words[++end].Width;
                bool last = end == words.Count - 1;
                float gap = !last && end > start ? (area.Width - (used - space * (end - start))) / (end - start) : space;
                float x = area.X;
                for (int i = start; i <= end; i++)
                {
                    g.DrawString(words[i].Word, font, brush, x, y, format);
                    x += words[i].Width + gap;
                }
                y += lineHeight;
                start = end + 1;
            }
        }
    }

    /// <summary>A page font at its size on screen, kept for reuse (zooming creates a few sizes).</summary>
    static Font ScreenFont(Font font, float scale)
    {
        var key = (font.FontFamily.Name, MathF.Round(font.Size * scale * 4) / 4, font.Style);
        if (ScreenFonts.TryGetValue(key, out Font? screen)) return screen;
        if (ScreenFonts.Count > 96)
        {
            foreach (Font old in ScreenFonts.Values) old.Dispose();
            ScreenFonts.Clear();
        }
        screen = new Font(font.FontFamily, Math.Max(0.5f, key.Item2), font.Style, GraphicsUnit.Pixel);
        ScreenFonts[key] = screen;
        return screen;
    }

    /// <summary>Width of a text object's longest line, in points.</summary>
    public static float TextWidth(PageObject item)
    {
        using var bitmap = new Bitmap(1, 1);
        using Graphics g = Graphics.FromImage(bitmap);
        using var format = new StringFormat(StringFormat.GenericTypographic);
        format.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
        return item.Text.Split('\n').Max(line => g.MeasureString(line.Length == 0 ? " " : line, FontFor(item), PointF.Empty, format).Width);
    }

    /// <summary>Height a text box needs for its text at its width, in points.</summary>
    public static float TextHeight(Graphics g, PageObject item)
    {
        if (item.Spans.Count > 0 && item.Text.Length > 0)
        {
            string own = item.Text;
            if (own.EndsWith('\n')) item.Text += "Ag";
            try { return StyledLines(g, item, Math.Max(1, item.Width - 4)).Sum(line => line.Height) + 6; }
            finally { item.Text = own; }
        }
        using var format = new StringFormat(StringFormat.GenericTypographic);
        string text = string.IsNullOrEmpty(item.Text) ? "Ag" : item.Text.EndsWith('\n') ? item.Text + "Ag" : item.Text;
        SizeF size = g.MeasureString(text, FontFor(item), new SizeF(Math.Max(1, item.Width - 4), 100000), format);
        return size.Height + 6;
    }

    static void DrawLine(Graphics g, PageObject item)
    {
        using Pen pen = Pen(item);
        if (item.Pen == PenKind.Pen)
            using (GraphicsPath line = ShapeGeometry.Stroke(item)) g.DrawPath(pen, line);
        else DrawPieces(g, item, ShapeGeometry.InkPieces(item, ShapeGeometry.PenStrokes(item), []));
        using var solid = new SolidBrush(WithAlpha(item.StrokeColor, item.Opacity));
        foreach (EndMark end in ShapeGeometry.EndMarks(item))
        {
            if (item.Pen != PenKind.Pen)
            {
                // The ends in the same pen as the line: a pencil arrow has a pencil head.
                if (end.Filled)
                    using (var fill = new SolidBrush(WithAlpha(item.StrokeColor, item.Opacity * ShapeGeometry.FillAlpha(item.Pen))))
                        g.FillPolygon(fill, [.. ShapeGeometry.EndMarkStrokes(end)[0]]);
                DrawPieces(g, item, ShapeGeometry.InkPieces(item, ShapeGeometry.EndMarkStrokes(end), []));
                continue;
            }
            if (ShapeGeometry.IsDot(end)) g.FillEllipse(solid, RectangleF.FromLTRB(end.Points[0].X, end.Points[0].Y, end.Points[1].X, end.Points[1].Y));
            else if (end.Filled) g.FillPolygon(solid, end.Points);
            else g.DrawLines(pen, end.Points);
        }
    }

    /// <summary>Pen strokes as the saved PDF draws them, in the passes their pen makes.</summary>
    static void DrawInk(Graphics g, PageObject item) => DrawPieces(g, item, ShapeGeometry.InkPieces(item));

    public static void DrawPieces(Graphics g, PageObject item, List<InkPiece> pieces)
    {
        foreach (InkPiece piece in pieces)
        {
            LineCap cap = piece.Square ? LineCap.Square : LineCap.Round;
            using var pen = new Pen(WithAlpha(item.StrokeColor, item.Opacity * piece.Alpha), piece.Width) { StartCap = cap, EndCap = cap, LineJoin = LineJoin.Round };
            if (piece.Points.Length == 2) g.DrawLine(pen, piece.Points[0], piece.Points[1]);
            else g.DrawLines(pen, piece.Points);
        }
    }

    static void DrawNote(Graphics g, PageObject item, RectangleF rect)
    {
        using var path = new GraphicsPath();
        float fold = rect.Width * 0.3f;
        path.AddLines([new PointF(rect.Left, rect.Top), new PointF(rect.Right, rect.Top), new PointF(rect.Right, rect.Bottom - fold), new PointF(rect.Right - fold, rect.Bottom), new PointF(rect.Left, rect.Bottom)]);
        path.CloseFigure();
        using var fill = new SolidBrush(Color.FromArgb(item.StrokeColor));
        using var edge = new Pen(Color.FromArgb(90, 0, 0, 0), 0.75f);
        g.FillPath(fill, path);
        g.DrawPath(edge, path);
    }

    /// <summary>
    /// A form field on the page: tinted while filling in, and its new value drawn over the old appearance once
    /// it changes (the saved file gets a real appearance). Radio choices show in the panel only.
    /// </summary>
    public static void DrawField(Graphics g, FormField field, string value, bool changed, bool highlight)
    {
        RectangleF r = field.Bounds;
        bool draws = changed && field.Kind != FieldKind.Radio;
        if (draws) g.FillRectangle(Brushes.White, r);
        if (highlight)
        {
            using var tint = new SolidBrush(Color.FromArgb(36, 37, 99, 235));
            using var edge = new Pen(Color.FromArgb(140, 37, 99, 235), 0.75f);
            g.FillRectangle(tint, r);
            g.DrawRectangle(edge, r.X, r.Y, r.Width, r.Height);
        }
        if (!draws) return;
        if (field.Kind == FieldKind.CheckBox)
        {
            if (value != "true") return;
            using var pen = new Pen(Color.FromArgb(0x18, 0x18, 0x1B), Math.Max(1, r.Height * 0.12f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            g.DrawLines(pen, [new PointF(r.Left + r.Width * 0.2f, r.Top + r.Height * 0.55f), new PointF(r.Left + r.Width * 0.42f, r.Top + r.Height * 0.78f), new PointF(r.Left + r.Width * 0.8f, r.Top + r.Height * 0.25f)]);
            return;
        }
        float size = field.Multiline ? 10 : Math.Clamp(r.Height * 0.62f, 6, 12);
        using var font = new Font(AppFonts.RegularFamily, size, FontStyle.Regular, GraphicsUnit.Pixel);
        using var format = new StringFormat(StringFormat.GenericTypographic) { LineAlignment = field.Multiline ? StringAlignment.Near : StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter };
        if (!field.Multiline) format.FormatFlags |= StringFormatFlags.NoWrap;
        g.DrawString(value, font, Brushes.Black, new RectangleF(r.X + 2, r.Y + (field.Multiline ? 2 : 0), Math.Max(1, r.Width - 4), r.Height), format);
    }

    /// <summary>Unapplied redaction marks: a red outline over diagonal hatching, so the page stays readable.</summary>
    static void DrawRedaction(Graphics g, PageObject item)
    {
        using GraphicsPath shape = RedactionPath(item);
        using var hatch = new HatchBrush(HatchStyle.WideUpwardDiagonal, Color.FromArgb(110, 229, 72, 77), Color.FromArgb(30, 229, 72, 77));
        using var edge = new Pen(Color.FromArgb(229, 72, 77), 1.2f);
        g.FillPath(hatch, shape);
        g.DrawPath(edge, shape);
    }

    /// <summary>
    /// A redaction as it will be applied. Pixelate and blur work on the page in view the way applying does (the
    /// area shrunk to a few cells and stretched back), so the preview shows the real blocks or smear.
    /// </summary>
    static void DrawCover(Graphics g, PageObject item, Bitmap? page, PageEntry entry)
    {
        using GraphicsPath shape = RedactionPath(item);
        if (item.Cover is RedactCover.Pixelate or RedactCover.Blur && page is not null && Scrambled(item, page, entry) is { } cover)
        {
            GraphicsState state = g.Save();
            g.SetClip(shape, CombineMode.Intersect);
            Mirror(g, entry);
            g.InterpolationMode = cover.Stretch;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(cover.Small, cover.At);
            g.Restore(state);
            return;
        }
        using Brush brush = item.Cover switch
        {
            RedactCover.White => new SolidBrush(Color.White),
            RedactCover.Color => new SolidBrush(Color.FromArgb(255, Color.FromArgb(item.FillColor))),
            RedactCover.Pixelate or RedactCover.Blur => new SolidBrush(Color.FromArgb(0xA1, 0xA1, 0xAA)),
            _ => new SolidBrush(Color.Black),
        };
        g.FillPath(brush, shape);
    }

    sealed record ScrambledCover(Bitmap Page, RectangleF Mark, RedactCover Cover, Bitmap Small, InterpolationMode Stretch, RectangleF At);

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<PageObject, ScrambledCover> Covers = [];

    /// <summary>
    /// The page area behind a pixelate or blur mark, shrunk, and where it goes in the page's own (unmirrored)
    /// points. Kept until the page image, the mark or its cover changes.
    /// </summary>
    static ScrambledCover? Scrambled(PageObject item, Bitmap page, PageEntry entry)
    {
        using GraphicsPath shape = RedactionPath(item);
        RectangleF mark = Unmirror(entry, shape.GetBounds());
        if (Covers.TryGetValue(item, out ScrambledCover? kept) && kept.Page == page && kept.Mark == mark && kept.Cover == item.Cover) return kept;
        float scale = page.Width / Math.Max(1f, entry.Width);
        var area = Rectangle.Ceiling(new RectangleF(mark.X * scale, mark.Y * scale, mark.Width * scale, mark.Height * scale));
        area.Intersect(new Rectangle(0, 0, page.Width, page.Height));
        if (area.Width <= 0 || area.Height <= 0) return null;
        Bitmap small = Redactor.Shrink(page, area, item.Cover, out InterpolationMode stretch);
        kept?.Small.Dispose();
        var cover = new ScrambledCover(page, mark, item.Cover, small, stretch, new RectangleF(area.X / scale, area.Y / scale, area.Width / scale, area.Height / scale));
        Covers.AddOrUpdate(item, cover);
        return cover;
    }

    public static GraphicsPath RedactionPath(PageObject item)
    {
        var path = new GraphicsPath();
        switch (item.RedactShape)
        {
            case RedactShape.Ellipse: path.AddEllipse(item.Bounds); break;
            case RedactShape.Freeform when item.Strokes.FirstOrDefault() is { Count: > 2 } points:
                path.AddPolygon([.. points]);
                break;
            default: path.AddRectangle(item.Bounds); break;
        }
        return path;
    }

    static Pen Pen(PageObject item) => new(WithAlpha(item.StrokeColor, item.Opacity), Math.Max(0.1f, item.StrokeWidth))
    {
        StartCap = LineCap.Round,
        EndCap = LineCap.Round,
        LineJoin = LineJoin.Round,
    };

    static Brush? Fill(PageObject item) => item.FillColor >> 24 == 0 ? null : new SolidBrush(WithAlpha(item.FillColor, item.Opacity));

    public static Color WithAlpha(int argb, float opacity)
    {
        var color = Color.FromArgb(argb);
        return Color.FromArgb((int)Math.Round(color.A * Math.Clamp(opacity, 0, 1)), color);
    }
}

/// <summary>Decoded images of image objects, kept while their bytes stay the same.</summary>
internal static class ImageCache
{
    static readonly Dictionary<byte[], Image> Images = new(ReferenceEqualityComparer.Instance);

    static readonly Dictionary<byte[], (float Radius, float Opacity, int Tint, float Width, float Height, Bitmap Picture)> StyledImages = new(ReferenceEqualityComparer.Instance);

    /// <summary>The picture with its rounded corners and opacity, drawn at screen size and kept until they change.</summary>
    public static Image Styled(PageObject item, Image image)
    {
        byte[] data = item.ImageData!;
        if (StyledImages.TryGetValue(data, out var kept) && kept.Radius == item.CornerRadius && kept.Opacity == item.Opacity && kept.Tint == item.Tint
            && kept.Width == item.Width && kept.Height == item.Height)
            return kept.Picture;
        kept.Picture?.Dispose();
        Bitmap picture = ImageLook.Render(image, item, 1200);
        StyledImages[data] = (item.CornerRadius, item.Opacity, item.Tint, item.Width, item.Height, picture);
        return picture;
    }

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Image, Bitmap> SizedCopies = [];

    /// <summary>
    /// The picture at about the size it is drawn: itself when it's near that size already, or a smoothly scaled copy,
    /// kept until a zoom needs a size more than twice as large or small.
    /// </summary>
    public static Image Sized(Image source, int width, int height)
    {
        if (width <= 0 || height <= 0 || source.Width <= width * 3 / 2) return source;
        if (SizedCopies.TryGetValue(source, out Bitmap? kept) && kept.Width >= width && kept.Width <= width * 2) return kept;
        kept?.Dispose();
        int w = Math.Min(source.Width, (int)(width * 1.25)), h = Math.Max(1, (int)((long)source.Height * w / source.Width));
        var copy = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using (Graphics g = Graphics.FromImage(copy))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(source, new Rectangle(0, 0, w, h));
        }
        SizedCopies.AddOrUpdate(source, copy);
        return copy;
    }

    public static Image? Get(PageObject item)
    {
        if (item.ImageData is not { Length: > 0 } data) return null;
        if (Images.TryGetValue(data, out Image? image)) return image;
        try
        {
            // A picture saved before pictures were fitted on the way in is fitted for the screen here, the same way.
            byte[] shown = ImageLook.Fit(data, ImageLook.MaxSide);
            image = Image.FromStream(new MemoryStream(shown));
            Images[data] = image;
            return image;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
