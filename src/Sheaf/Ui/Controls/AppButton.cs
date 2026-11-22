namespace Sheaf.Ui.Controls;

internal enum ButtonKind
{
    /// <summary>The one main action of a screen. Accent fill.</summary>
    Primary,
    /// <summary>Raised button with a hairline border, for ordinary actions.</summary>
    Secondary,
    /// <summary>No fill at rest; a background on hover. Toolbars, minor actions, icon buttons.</summary>
    Quiet,
}

/// <summary>
/// A real <see cref="Button"/> (keyboard, focus and screen readers work) painted in Sheaf's style: an optional
/// icon, an optional menu chevron, hover that fades in over 120 ms, a darker press, and a selected look that fades
/// in and out.
/// </summary>
internal class AppButton : Button
{
    const int IconSize = 16;
    const TextFormatFlags TextFlags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter;

    readonly Fade _hover;
    readonly Fade _press;
    readonly Fade _select;
    string[] _reserved = [];
    Size? _preferred;
    bool _selected;
    int _height = Theme.ControlHeight;

    public AppButton(string text = "", ButtonKind kind = ButtonKind.Secondary, string? icon = null)
    {
        Kind = kind;
        Icon = icon;
        _hover = new Fade(this);
        _press = new Fade(this, 90);
        _select = new Fade(this, 180);
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        UseMnemonic = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Font = kind == ButtonKind.Primary ? AppFonts.Strong : AppFonts.Body;
        Margin = new Padding(0, 0, 8, 0);
        Text = text;
        if (text.Length == 0 && icon is not null) AccessibleName ??= icon;
    }

    public ButtonKind Kind { get; }

    /// <summary>Name of an icon in <see cref="Icons"/>, drawn left of the text (or alone).</summary>
    public string? Icon { get; set; }

    /// <summary>Marks a button that holds more than it shows (a corner notch), like a rail button for several tools.</summary>
    public bool HasMore { get; set; }

    /// <summary>Draws a small chevron after the text: the button opens a menu.</summary>
    public bool Chevron { get; init; }

    /// <summary>Fills its column instead of hugging its content.</summary>
    public bool Stretch { get; init; }

    /// <summary>Accent-tinted look for a tool or option that is on.</summary>
    public bool Selected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            if (IsHandleCreated && Visible) _select.To(value ? 1 : 0);
            else _select.Set(value ? 1 : 0);
            AccessibleDescription = value ? "Selected" : null;
            Invalidate();
        }
    }

    /// <summary>Selected shows a neutral raised background instead of the accent tint (B, I, U and similar).</summary>
    public bool NeutralSelection { get; init; }

    /// <summary>Logical height; 32 by default, 28 in dense bars.</summary>
    public int LogicalHeight
    {
        get => _height;
        set { _height = value; _preferred = null; }
    }

    /// <summary>Width for a fixed set of labels, so swapping "Merge PDF" for "Cancel" moves nothing.</summary>
    public void ReserveWidthFor(params string[] labels)
    {
        _reserved = labels;
        _preferred = null;
        PerformLayout();
    }

    bool IconOnly => Text.Length == 0 && Icon is not null;

    public override Size GetPreferredSize(Size proposedSize)
    {
        _preferred ??= Measure();
        if (Stretch && proposedSize.Width is > 0 and < int.MaxValue)
            return new Size(Math.Max(proposedSize.Width, _preferred.Value.Width), _preferred.Value.Height);
        return _preferred.Value;
    }

    Size Measure()
    {
        int height = LogicalToDeviceUnits(_height);
        if (IconOnly) return new Size(height, height);
        int text = _reserved.Append(Text).Max(label => Draw.Measure(label, Font).Width);
        int padding = LogicalToDeviceUnits(Kind == ButtonKind.Quiet ? 10 : 14);
        int extras = (Icon is null ? 0 : LogicalToDeviceUnits(IconSize + 8)) + (Chevron ? LogicalToDeviceUnits(18) : 0);
        return new Size(text + extras + 2 * padding, height);
    }

    protected override void OnTextChanged(EventArgs e) { _preferred = null; base.OnTextChanged(e); AccessibilityNotifyClients(AccessibleEvents.NameChange, -1); }
    protected override void OnFontChanged(EventArgs e) { _preferred = null; base.OnFontChanged(e); }
    protected override void OnDpiChangedAfterParent(EventArgs e) { _preferred = null; base.OnDpiChangedAfterParent(e); }
    protected override void OnMouseEnter(EventArgs e) { _hover.To(1); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover.To(0); Release(); base.OnMouseLeave(e); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && Enabled)
        {
            _press.To(1);
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e) { Release(); base.OnMouseUp(e); }

    void Release() => _press.To(0);

    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(Draw.BackgroundOf(this));
        var (fill, border, ink) = Colors();
        int radius = LogicalToDeviceUnits(Theme.Radius);
        Draw.Box(g, ClientRectangle, radius, fill, border);
        DrawContent(g, ink);
        if (HasMore)
        {
            int size = LogicalToDeviceUnits(4), inset = LogicalToDeviceUnits(4);
            Point corner = new(Width - inset, Height - inset);
            using var notch = new SolidBrush(Color.FromArgb(150, ink));
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.FillPolygon(notch, [new(corner.X, corner.Y - size), corner, new(corner.X - size, corner.Y)]);
        }
        if (Focused && ShowFocusCues)
            Draw.FocusRing(g, ClientRectangle, radius, LogicalToDeviceUnits(2));
    }

    void DrawContent(Graphics g, Color ink)
    {
        int icon = LogicalToDeviceUnits(IconSize);
        if (IconOnly)
        {
            Icons.Draw(g, Icon!, new Rectangle((Width - icon) / 2, (Height - icon) / 2, icon, icon), ink);
            return;
        }
        int padding = LogicalToDeviceUnits(Kind == ButtonKind.Quiet ? 10 : 14);
        int contentWidth = Draw.Measure(Text, Font).Width + (Icon is null ? 0 : icon + LogicalToDeviceUnits(8)) + (Chevron ? LogicalToDeviceUnits(18) : 0);
        // A stretched drop-down reads like a select box, so its text starts at the left; other buttons centre theirs.
        bool centred = (Stretch && !Chevron) || _reserved.Length > 0;
        int x = centred ? Math.Max(padding, (Width - contentWidth) / 2) : padding;
        if (Icon is not null)
        {
            Icons.Draw(g, Icon, new Rectangle(x, (Height - icon) / 2, icon, icon), ink);
            x += icon + LogicalToDeviceUnits(8);
        }
        int textRight = Width - padding - (Chevron ? LogicalToDeviceUnits(18) : 0);
        TextRenderer.DrawText(g, Text, Font, new Rectangle(x, 0, Math.Max(1, textRight - x), Height), ink, TextFlags | TextFormatFlags.EndEllipsis);
        if (Chevron)
        {
            int size = LogicalToDeviceUnits(12);
            Icons.Draw(g, "chevron-down", new Rectangle(Width - padding - size, (Height - size) / 2, size, size), ink, 2.2f);
        }
    }

    (Color Fill, Color Border, Color Ink) Colors()
    {
        Palette p = Theme.Current;
        double hover = _hover.Value;
        if (!Enabled)
            return Kind switch
            {
                ButtonKind.Primary => (p.Hover, Color.Transparent, p.Disabled),
                ButtonKind.Secondary => (Color.Transparent, p.Line, p.Disabled),
                _ => (Color.Transparent, Color.Transparent, p.Disabled),
            };
        double press = _press.Value;
        var (fill, border, ink) = Kind switch
        {
            ButtonKind.Primary => (Theme.Blend(Theme.Blend(p.Accent, p.AccentHover, hover), p.AccentPressed, press), Color.Transparent, Color.White),
            ButtonKind.Secondary => (Theme.Blend(Theme.Blend(p.Raised, p.Hover, hover), p.Active, press), p.Line2, p.Text),
            _ => (Theme.Blend(Theme.Blend(Color.FromArgb(0, p.Hover), p.Hover, hover), p.Active, press), Color.Transparent, Theme.Blend(p.Text2, p.Text, hover)),
        };
        double selected = _select.Value;
        if (selected <= 0) return (fill, border, ink);
        // On, or fading between on and off.
        var on = NeutralSelection
            ? (Fill: Theme.Blend(p.Active, p.Hover, press), Border: Color.Transparent, Ink: p.Text)
            : (Fill: Theme.Blend(p.AccentSoft, p.Active, press * 0.4), Border: Color.Transparent, Ink: p.AccentText);
        return (Theme.Blend(fill, on.Fill, selected), Theme.Blend(border, on.Border, selected), Theme.Blend(ink, on.Ink, selected));
    }
}
