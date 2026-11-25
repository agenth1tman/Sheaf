using System.Drawing.Drawing2D;

namespace Sheaf.Ui;

/// <summary>Shared drawing helpers: rounded boxes, text, focus rings. (Named Draw because every control has a Paint event.)</summary>
internal static class Draw
{
    public const TextFormatFlags Line = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter;
    public const TextFormatFlags Centered = Line | TextFormatFlags.HorizontalCenter;
    public const TextFormatFlags Wrapped = TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl;

    public static GraphicsPath RoundRect(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        if (d <= 0.5f)
        {
            path.AddRectangle(bounds);
            return path;
        }
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>A filled rounded box at a fractional position, for something that moves smoothly (a sliding thumb).</summary>
    public static void Box(Graphics g, RectangleF bounds, float radius, Color fill)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0 || fill.A == 0) return;
        SmoothingMode old = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using GraphicsPath path = RoundRect(bounds, radius);
        using var brush = new SolidBrush(fill);
        g.FillPath(brush, path);
        g.SmoothingMode = old;
    }

    /// <summary>A rounded box on the pixel grid; a border of width w sits fully inside the bounds.</summary>
    public static void Box(Graphics g, Rectangle bounds, float radius, Color fill, Color border = default, float borderWidth = 1)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        SmoothingMode old = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (fill.A > 0)
        {
            using GraphicsPath path = RoundRect(bounds, radius);
            using var brush = new SolidBrush(fill);
            g.FillPath(brush, path);
        }
        if (border.A > 0 && borderWidth > 0)
        {
            float inset = borderWidth / 2;
            using GraphicsPath path = RoundRect(new RectangleF(bounds.X + inset, bounds.Y + inset, bounds.Width - borderWidth, bounds.Height - borderWidth), Math.Max(0, radius - inset));
            using var pen = new Pen(border, borderWidth);
            g.DrawPath(pen, path);
        }
        g.SmoothingMode = old;
    }

    /// <summary>The nearest opaque background behind a control, for painting outside its rounded corners.</summary>
    public static Color BackgroundOf(Control control)
    {
        for (Control? parent = control.Parent; parent is not null; parent = parent.Parent)
            if (parent.BackColor.A == 255) return parent.BackColor;
        return Theme.Current.Panel;
    }

    public static void Text(Graphics g, string text, Font font, Rectangle bounds, Color color, TextFormatFlags flags = Line) =>
        TextRenderer.DrawText(g, text, font, bounds, color, flags);

    /// <summary>
    /// Text on a known solid background. Needed for text drawn into a picture rather than on screen: there, GDI blends
    /// the app's own fonts against black unless told what is behind them, which smears dark text on a light theme.
    /// </summary>
    public static void Text(Graphics g, string text, Font font, Rectangle bounds, Color color, Color back, TextFormatFlags flags = Line) =>
        TextRenderer.DrawText(g, text, font, bounds, color, back, flags);

    public static Size Measure(string text, Font font) =>
        TextRenderer.MeasureText(text.Length == 0 ? " " : text, font, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);

    /// <summary>
    /// Sheaf's mark, as in the app icon: a blue sheet behind a white one, with a fine edge on light themes. The back
    /// sheet leans <paramref name="tilt"/> degrees.
    /// </summary>
    /// <summary>
    /// The app's mark: a blue sheet behind, turned by <paramref name="tilt"/>, and a white one in front, raised by
    /// <paramref name="lift"/> (24ths of the size); the whole mark turned by <paramref name="spin"/>.
    /// </summary>
    public static void AppMark(Graphics g, Rectangle bounds, float tilt = -12, float lift = 0, float spin = 0)
    {
        float s = Math.Min(bounds.Width, bounds.Height) / 24f;
        GraphicsState state = g.Save();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TranslateTransform(bounds.X, bounds.Y);
        g.ScaleTransform(s, s);
        if (spin != 0)
        {
            // About the middle of the mark.
            g.TranslateTransform(12, 12);
            g.RotateTransform(spin);
            g.TranslateTransform(-12, -12);
        }
        using (GraphicsPath back = RoundRect(new RectangleF(3, 5, 12, 16), 2))
        using (var accent = new SolidBrush(Theme.Current.Accent))
        using (var rotate = new Matrix())
        {
            rotate.RotateAt(tilt, new PointF(9, 13));
            back.Transform(rotate);
            g.FillPath(accent, back);
        }
        using (GraphicsPath front = RoundRect(new RectangleF(8, 3 - lift, 12, 16), 2))
        using (var paper = new SolidBrush(Theme.Hex(0xF4F4F5)))
        {
            g.FillPath(paper, front);
            if (!Theme.IsDark)
                using (var edge = new Pen(Theme.Hex(0x18181B), 1.2f)) g.DrawPath(edge, front);
        }
        g.Restore(state);
    }

    public static void FocusRing(Graphics g, Rectangle bounds, float radius, int thickness) =>
        Box(g, bounds, radius, Color.Transparent, Theme.Current.Accent, thickness);
}
