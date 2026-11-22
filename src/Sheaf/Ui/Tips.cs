namespace Sheaf.Ui;

/// <summary>One tooltip component for the whole app, so controls never each create (and leak) their own.</summary>
internal static class Tips
{
    public static readonly ThemedTip Shared = new() { InitialDelay = 500, ReshowDelay = 100 };
}

/// <summary>
/// A tooltip in the app's colours: a raised panel with a hairline edge. A tip with two lines (a tab's name and its
/// file, say) sets the first one stronger. It fades in and out with Windows' own tooltip animation.
/// </summary>
internal sealed class ThemedTip : ToolTip
{
    const int PadX = 10, PadY = 6, MaxWidth = 420;
    string? _shown;

    public ThemedTip()
    {
        OwnerDraw = true;
        UseFading = true;
        UseAnimation = true;
        Popup += (_, e) => e.ToolTipSize = Measure(TextFor(e.AssociatedControl));
        Draw += (_, e) => Paint(e);
    }

    /// <summary>Shows a tip at a point of a control (rather than at the pointer), until <see cref="Hide"/>.</summary>
    public void ShowAt(string text, Control control, Point at)
    {
        _shown = text;
        Show(text, control, at);
    }

    public void Hide(Control control)
    {
        _shown = null;
        base.Hide(control);
    }

    string TextFor(Control? control) => _shown ?? (control is null ? "" : GetToolTip(control) ?? "");

    static (string First, string Second) Split(string text)
    {
        int line = text.IndexOf('\n');
        return line < 0 ? (text, "") : (text[..line], text[(line + 1)..]);
    }

    static Size Measure(string text)
    {
        var (first, rest) = Split(text);
        const TextFormatFlags Flags = TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak;
        Size top = TextRenderer.MeasureText(first, rest.Length > 0 ? AppFonts.SmallStrong : AppFonts.Small, new Size(MaxWidth, 0), Flags);
        Size bottom = rest.Length > 0 ? TextRenderer.MeasureText(rest, AppFonts.Small, new Size(MaxWidth, 0), Flags) : Size.Empty;
        return new Size(Math.Max(top.Width, bottom.Width) + 2 * PadX, top.Height + bottom.Height + 2 * PadY + (rest.Length > 0 ? 2 : 0));
    }

    void Paint(DrawToolTipEventArgs e)
    {
        Palette p = Theme.Current;
        Graphics g = e.Graphics;
        g.Clear(p.Raised);
        using (var edge = new Pen(p.Line2)) g.DrawRectangle(edge, 0, 0, e.Bounds.Width - 1, e.Bounds.Height - 1);
        var (first, rest) = Split(e.ToolTipText is { Length: > 0 } text ? text : _shown ?? "");
        const TextFormatFlags Flags = TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak;
        Font top = rest.Length > 0 ? AppFonts.SmallStrong : AppFonts.Small;
        Size firstSize = TextRenderer.MeasureText(first, top, new Size(MaxWidth, 0), Flags);
        TextRenderer.DrawText(g, first, top, new Rectangle(PadX, PadY, e.Bounds.Width - 2 * PadX, firstSize.Height), p.Text, Flags);
        if (rest.Length > 0)
            TextRenderer.DrawText(g, rest, AppFonts.Small, new Rectangle(PadX, PadY + firstSize.Height + 2, e.Bounds.Width - 2 * PadX, e.Bounds.Height - firstSize.Height - PadY), p.Text2, Flags);
    }
}
