using System.Globalization;
using Sheaf.Platform;

namespace Sheaf.Ui.Controls;

/// <summary>
/// A text field: a borderless native <see cref="TextBox"/> (typing, paste, IME and screen readers work) inside a
/// rounded box whose border lightens on hover, fades to accent when focused and turns red when its field has an
/// error. It can carry an icon on the left, a clear button, and an eye that shows or hides a password.
/// </summary>
internal sealed class InputBox : Panel
{
    const int InnerPadding = 10, ButtonSize = 24;
    readonly Fade _hover;
    readonly Fade _focus;
    readonly Fade _clearShown;
    readonly Fade _reveal;
    readonly Fade _buttonHover;
    bool _hasError;
    double _pulse;

    public InputBox()
    {
        _hover = new Fade(this);
        _focus = new Fade(this, 160);
        _clearShown = new Fade(this, 140);
        _reveal = new Fade(this, 220);
        _buttonHover = new Fade(this, 100);
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Box = new TextBox { BorderStyle = BorderStyle.None, Font = AppFonts.Body, MaxLength = 200 };
        Box.ContextMenuStrip = Menus.ForText(Box);
        Box.GotFocus += (_, _) => _focus.To(1);
        Box.LostFocus += (_, _) => _focus.To(0);
        Box.TextChanged += (_, _) => { if (Clearable) _clearShown.To(Box.TextLength > 0 ? 1 : 0); };
        Box.MouseEnter += (_, _) => _hover.To(1);
        Box.MouseLeave += (_, _) => _hover.To(0);
        Box.KeyDown += OnBoxKeyDown;
        Box.KeyPress += OnBoxKeyPress;
        Box.TextChanged += (_, e) => OnTextChanged(e);
        Controls.Add(Box);
        Height = Theme.ControlHeight;
        Margin = Padding.Empty;
        ApplyColors();
        Theme.Changed += OnThemeChanged;
    }

    public TextBox Box { get; }

    public event EventHandler? EnterPressed;

    public override string Text
    {
        get => Box.Text;
#pragma warning disable CS8765 // Panel.Text allows null; a text box never holds null.
        set => Box.Text = value ?? "";
#pragma warning restore CS8765
    }

    public string Caption
    {
        get => Box.AccessibleName ?? "";
        set => Box.AccessibleName = value;
    }

    public string Placeholder
    {
        get => Box.PlaceholderText;
        set => Box.PlaceholderText = value;
    }

    /// <summary>Hides what is typed, with an eye at the end to show it for a moment.</summary>
    public bool IsPassword
    {
        get => _isPassword;
        set
        {
            _isPassword = value;
            Box.UseSystemPasswordChar = value;
            _reveal.Set(0);
            PerformLayout();
        }
    }

    bool _isPassword;

    /// <summary>Name of an icon in <see cref="Icons"/> drawn at the start; it takes the accent colour while focused.</summary>
    public string? Icon
    {
        get;
        set { field = value; PerformLayout(); Invalidate(); }
    }

    /// <summary>Shows a × at the end once something is typed; it empties the box.</summary>
    public bool Clearable
    {
        get;
        set { field = value; PerformLayout(); Invalidate(); }
    }

    bool HasButton => _isPassword || Clearable;

    Rectangle ButtonBounds
    {
        get
        {
            int size = LogicalToDeviceUnits(ButtonSize);
            return new Rectangle(Width - size - LogicalToDeviceUnits(6), (Height - size) / 2, size, size);
        }
    }

    void PressButton()
    {
        if (_isPassword)
        {
            bool show = Box.UseSystemPasswordChar;
            Box.UseSystemPasswordChar = !show;
            _reveal.To(show ? 1 : 0);
            ScreenReader.Say(this, show ? "Password shown" : "Password hidden");
        }
        else if (Clearable && Box.TextLength > 0)
        {
            Box.Clear();
        }
        Box.Focus();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        bool over = HasButton && ButtonBounds.Contains(e.Location) && (_isPassword || Box.TextLength > 0);
        Cursor = over ? Cursors.Hand : Cursors.IBeam;
        _buttonHover.To(over ? 1 : 0);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left && HasButton && ButtonBounds.Contains(e.Location)) PressButton();
    }

    public bool HasError
    {
        get => _hasError;
        set { _hasError = value; Invalidate(); }
    }

    /// <summary>Digits only, and Up/Down step the value within the range.</summary>
    public (int Minimum, int Maximum)? NumberRange { get; set; }

    /// <summary>Monospaced digits, for numbers that change.</summary>
    public bool Monospace
    {
        set => Box.Font = value ? AppFonts.Mono : AppFonts.Body;
    }

    public int? Number => int.TryParse(Box.Text, NumberStyles.None, CultureInfo.CurrentCulture, out int value) ? value : null;

    public new void Focus() => Box.Focus();

    /// <summary>A glow around the box that fades, to draw the eye to it.</summary>
    public void Pulse() => Motion.Run(900, t =>
    {
        _pulse = 1 - t;
        Invalidate();
    }, Easing.Out);

    void ApplyColors()
    {
        Box.BackColor = Enabled ? Theme.Current.Raised : Theme.Current.Panel;
        Box.ForeColor = Enabled ? Theme.Current.Text : Theme.Current.Text3;
    }

    void OnThemeChanged(object? sender, EventArgs e)
    {
        ApplyColors();
        Invalidate();
    }

    protected override void OnEnabledChanged(EventArgs e) { ApplyColors(); Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnClick(EventArgs e) { Box.Focus(); base.OnClick(e); }
    protected override void OnMouseEnter(EventArgs e) { _hover.To(1); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover.To(0); _buttonHover.To(0); base.OnMouseLeave(e); }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        int padding = LogicalToDeviceUnits(InnerPadding);
        int left = padding + (Icon is null ? 0 : LogicalToDeviceUnits(24));
        int right = HasButton ? LogicalToDeviceUnits(ButtonSize + 10) : padding;
        Box.SetBounds(left, (Height - Box.Height) / 2, Math.Max(1, Width - left - right), Box.Height);
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible) PerformLayout();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Palette p = Theme.Current;
        e.Graphics.Clear(Draw.BackgroundOf(this));
        double focus = _focus.Value;
        Color resting = Theme.Blend(p.Line, p.Line2, _hover.Value);
        Color border = _hasError ? p.Danger : Theme.Blend(resting, p.Accent, focus);
        float width = _hasError ? LogicalToDeviceUnits(2) / 1.5f : 1 + (float)(focus * (LogicalToDeviceUnits(2) / 1.5f - 1));
        width += (float)(_pulse * LogicalToDeviceUnits(3));
        Color fill = Theme.Blend(Theme.Blend(Enabled ? p.Raised : p.Panel, p.Accent, focus * 0.05), p.Accent, _pulse * 0.18);
        Draw.Box(e.Graphics, ClientRectangle, LogicalToDeviceUnits(Theme.Radius), fill, _pulse > 0 ? Theme.Blend(border, p.Accent, _pulse) : border, width);
        Box.BackColor = fill;
        if (Icon is not null)
        {
            int size = LogicalToDeviceUnits(16);
            Icons.Draw(e.Graphics, Icon, new Rectangle(LogicalToDeviceUnits(InnerPadding), (Height - size) / 2, size, size), Theme.Blend(p.Text3, p.AccentText, focus));
        }
        if (_isPassword) DrawEye(e.Graphics, p);
        else if (Clearable && _clearShown.Value > 0.01) DrawClear(e.Graphics, p);
    }

    void DrawClear(Graphics g, Palette p)
    {
        Rectangle box = ButtonBounds;
        double shown = _clearShown.Value;
        if (_buttonHover.Value > 0) Draw.Box(g, box, box.Width / 2f, Color.FromArgb((int)(255 * _buttonHover.Value * shown), p.Hover));
        int glyph = (int)(LogicalToDeviceUnits(10) * (0.6 + 0.4 * shown));
        Icons.Draw(g, "close", new Rectangle(box.X + (box.Width - glyph) / 2, box.Y + (box.Height - glyph) / 2, glyph, glyph), Color.FromArgb((int)(255 * shown), p.Text2), 2.2f);
    }

    /// <summary>The eye: open while the password shows; hidden, a slash draws across it (and retracts to show).</summary>
    void DrawEye(Graphics g, Palette p)
    {
        Rectangle box = ButtonBounds;
        if (_buttonHover.Value > 0) Draw.Box(g, box, box.Width / 2f, Color.FromArgb((int)(255 * _buttonHover.Value), p.Hover));
        int size = LogicalToDeviceUnits(16);
        var icon = new Rectangle(box.X + (box.Width - size) / 2, box.Y + (box.Height - size) / 2, size, size);
        Color ink = Theme.Blend(p.Text2, p.AccentText, _reveal.Value);
        Icons.Draw(g, "eye", icon, ink);
        double slash = 1 - _reveal.Value;
        if (slash <= 0.01) return;
        float scale = size / 24f;
        var from = new PointF(icon.X + 4 * scale, icon.Y + 4 * scale);
        var to = new PointF(from.X + (float)(16 * scale * slash), from.Y + (float)(16 * scale * slash));
        System.Drawing.Drawing2D.SmoothingMode old = g.SmoothingMode;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var gap = new Pen(Box.BackColor, 3.6f * scale + 1)) g.DrawLine(gap, from, to);
        using (var pen = new Pen(ink, 1.75f * scale) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round }) g.DrawLine(pen, from, to);
        g.SmoothingMode = old;
    }

    void OnBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter && !e.Control)
        {
            e.SuppressKeyPress = true;
            EnterPressed?.Invoke(this, EventArgs.Empty);
        }
        else if (NumberRange is { } range && e.KeyCode is Keys.Up or Keys.Down)
        {
            e.SuppressKeyPress = true;
            int next = (Number ?? range.Minimum) + (e.KeyCode == Keys.Up ? 1 : -1);
            Box.Text = Math.Clamp(next, range.Minimum, range.Maximum).ToString(CultureInfo.CurrentCulture);
            Box.SelectAll();
        }
    }

    void OnBoxKeyPress(object? sender, KeyPressEventArgs e)
    {
        if (NumberRange is not null && !char.IsControl(e.KeyChar) && !char.IsAsciiDigit(e.KeyChar)) e.Handled = true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Theme.Changed -= OnThemeChanged;
        base.Dispose(disposing);
    }
}

/// <summary>A visible caption, the input, and a line for its error, which appears next to the field, never in a dialog.</summary>
internal sealed class FormField : TableLayoutPanel
{
    readonly WrapLabel _error;

    public FormField(string caption, Control input, string? hint = null)
    {
        Input = input;
        ColumnCount = 1;
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Anchor = AnchorStyles.Left | AnchorStyles.Right;
        Margin = new Padding(0, 0, 0, 10);

        if (input is InputBox box) box.Caption = caption;
        else input.AccessibleName ??= caption;
        input.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        input.Margin = new Padding(0, 6, 0, 0);

        Controls.Add(Make.FieldCaption(caption));
        Controls.Add(input);
        if (hint is not null) Controls.Add(Make.Hint(hint));
        _error = new WrapLabel { Font = AppFonts.Small, Tone = Tone.Error, Margin = new Padding(0, 4, 0, 0), Anchor = AnchorStyles.Left | AnchorStyles.Right, Visible = false };
        Controls.Add(_error);
    }

    public Control Input { get; }

    public string? Error
    {
        get => _error.Text.Length == 0 ? null : _error.Text;
        set
        {
            _error.Text = value ?? "";
            _error.Visible = value is not null;
            if (Input is InputBox box)
            {
                box.HasError = value is not null;
                box.Box.AccessibleDescription = value;
            }
        }
    }

    public bool Check(string? error)
    {
        Error = error;
        return error is null;
    }

    public void FocusInput()
    {
        if (Input is InputBox box) box.Focus();
        else Input.Focus();
    }
}

/// <summary>An on/off switch: a real check box, painted as a 30 × 18 track with a sliding knob.</summary>
internal sealed class Toggle : CheckBox
{
    readonly Fade _knob;

    public Toggle(string text, bool on = false)
    {
        _knob = new Fade(this, 160);
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Text = text;
        Checked = on;
        _knob.Set(on ? 1 : 0);
        AutoSize = false;
        UseMnemonic = false;
        Font = AppFonts.Body;
        Height = 28;
        Margin = new Padding(0, 2, 0, 2);
    }

    /// <summary>Text on the left and the switch on the right edge, for settings rows.</summary>
    public bool SwitchOnRight { get; init; }

    public override Size GetPreferredSize(Size proposedSize)
    {
        int width = Draw.Measure(Text, Font).Width + LogicalToDeviceUnits(30 + 12);
        return new Size(SwitchOnRight && proposedSize.Width is > 0 and < int.MaxValue ? proposedSize.Width : width, LogicalToDeviceUnits(28));
    }

    protected override void OnCheckedChanged(EventArgs e)
    {
        _knob.To(Checked ? 1 : 0);
        base.OnCheckedChanged(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Palette p = Theme.Current;
        Graphics g = e.Graphics;
        g.Clear(Draw.BackgroundOf(this));
        int w = LogicalToDeviceUnits(30), h = LogicalToDeviceUnits(18), knob = LogicalToDeviceUnits(14), inset = LogicalToDeviceUnits(2);
        int trackX = SwitchOnRight ? Width - w : 0;
        var track = new Rectangle(trackX, (Height - h) / 2, w, h);
        Color trackColor = Enabled ? Theme.Blend(p.Line2, p.Accent, _knob.Value) : p.Hover;
        Draw.Box(g, track, h / 2f, trackColor);
        int knobX = track.X + inset + (int)Math.Round((w - knob - 2 * inset) * _knob.Value);
        Draw.Box(g, new Rectangle(knobX, track.Y + inset, knob, knob), knob / 2f, Enabled ? Color.White : p.Text3);
        if (Focused && ShowFocusCues) Draw.FocusRing(g, Rectangle.Inflate(track, inset, inset), h / 2f + inset, LogicalToDeviceUnits(2));

        var text = SwitchOnRight ? new Rectangle(0, 0, track.X - LogicalToDeviceUnits(12), Height) : new Rectangle(track.Right + LogicalToDeviceUnits(10), 0, Width, Height);
        Draw.Text(g, Text, Font, text, Enabled ? p.Text : p.Disabled);
    }
}

/// <summary>A check box painted as a 16 px rounded square with an accent fill and tick when checked.</summary>
internal sealed class Check : CheckBox
{
    public Check(string text, bool isChecked = false)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Text = text;
        Checked = isChecked;
        AutoSize = false;
        UseMnemonic = false;
        Font = AppFonts.Body;
        Margin = new Padding(0, 4, 0, 4);
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        int available = proposedSize.Width is > 0 and < int.MaxValue ? proposedSize.Width : int.MaxValue;
        int textWidth = Math.Min(available - LogicalToDeviceUnits(26), Draw.Measure(Text, Font).Width);
        Size text = TextRenderer.MeasureText(Text, Font, new Size(Math.Max(20, textWidth + 2), int.MaxValue), Draw.Wrapped);
        return new Size(textWidth + LogicalToDeviceUnits(26), Math.Max(LogicalToDeviceUnits(24), text.Height + LogicalToDeviceUnits(4)));
    }

    protected override void OnCheckedChanged(EventArgs e) { Invalidate(); base.OnCheckedChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        Palette p = Theme.Current;
        Graphics g = e.Graphics;
        g.Clear(Draw.BackgroundOf(this));
        int size = LogicalToDeviceUnits(16);
        var box = new Rectangle(0, LogicalToDeviceUnits(3), size, size);
        if (Checked)
        {
            Draw.Box(g, box, LogicalToDeviceUnits(4), Enabled ? p.Accent : p.Line2);
            Icons.Draw(g, "check", Rectangle.Inflate(box, -LogicalToDeviceUnits(2), -LogicalToDeviceUnits(2)), Color.White, 3f);
        }
        else
        {
            Draw.Box(g, box, LogicalToDeviceUnits(4), Color.Transparent, p.Line2, LogicalToDeviceUnits(2) / 1.4f);
        }
        if (Focused && ShowFocusCues) Draw.FocusRing(g, Rectangle.Inflate(box, 2, 2), LogicalToDeviceUnits(5), LogicalToDeviceUnits(2));
        var text = new Rectangle(size + LogicalToDeviceUnits(10), 0, Width - size - LogicalToDeviceUnits(10), Height);
        TextRenderer.DrawText(g, Text, Font, text, Enabled ? p.Text2 : p.Disabled, Draw.Wrapped);
    }
}

/// <summary>One option of a vertical choice: a ring that fills with the accent when selected.</summary>
internal sealed class RadioOption : RadioButton
{
    public RadioOption(string text)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Text = text;
        AutoSize = false;
        UseMnemonic = false;
        Font = AppFonts.Body;
        Margin = new Padding(0, 1, 0, 1);
    }

    public override Size GetPreferredSize(Size proposedSize) =>
        new(Draw.Measure(Text, Font).Width + LogicalToDeviceUnits(28), LogicalToDeviceUnits(28));

    protected override void OnCheckedChanged(EventArgs e) { Invalidate(); base.OnCheckedChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        Palette p = Theme.Current;
        Graphics g = e.Graphics;
        g.Clear(Draw.BackgroundOf(this));
        int size = LogicalToDeviceUnits(16);
        var ring = new Rectangle(0, (Height - size) / 2, size, size);
        Draw.Box(g, ring, size / 2f, Color.Transparent, Checked ? p.Accent : p.Line2, LogicalToDeviceUnits(2) / 1.4f);
        if (Checked)
        {
            int dot = LogicalToDeviceUnits(8);
            Draw.Box(g, new Rectangle(ring.X + (size - dot) / 2, ring.Y + (size - dot) / 2, dot, dot), dot / 2f, p.Accent);
        }
        if (Focused && ShowFocusCues) Draw.FocusRing(g, Rectangle.Inflate(ring, 2, 2), size / 2f + 2, LogicalToDeviceUnits(2));
        Draw.Text(g, Text, Font, new Rectangle(size + LogicalToDeviceUnits(10), 0, Width, Height), Enabled ? (Checked ? p.Text : p.Text2) : p.Disabled);
    }
}

/// <summary>
/// One choice among a few. Horizontal: a segmented control with a thumb on springs. Pressed, the thumb lifts to
/// fill the track; dragged, it follows the pointer, stretches with speed and squashes against either end; let go,
/// it keeps its speed (a flick carries on to the next choice) and bounces onto the nearest choice. Arrow keys move
/// it too. Vertical: a stack of <see cref="RadioOption"/>s.
/// </summary>
internal sealed class ChoiceGroup<T> : Panel, IItemHost where T : notnull
{
    // Springs: settling onto a choice bounces; following the pointer is tight; the lift pops a little.
    const double SettleResponse = 0.42, SettleDamping = 0.58, FollowResponse = 0.11, FollowDamping = 0.86;
    // How much a moving thumb stretches: by its speed in logical pixels a second, up to a limit.
    const double StretchSpeed = 5000, MaxStretch = 0.22;
    // How far ahead of a released thumb its speed counts when picking where it lands, in seconds.
    const double FlingProjection = 0.1;

    readonly (string Label, T Value)[] _options;
    readonly List<RadioOption> _radios = [];
    readonly bool _horizontal;
    readonly Spring _thumb = new(SettleResponse, SettleDamping);
    readonly Spring _lift = new(0.3, 0.6);
    FrameLoop? _motion;
    int _selected, _hoverIndex = -1;

    // The thumb's centre, in pixels, is placed on the selected segment the first time it is drawn.
    bool _placed, _tracking, _dragging;
    float _grab;
    int _pressX, _pointerX;
    Rectangle[] _segments = [];
    (int Width, int Height, int Dpi) _segmentsFor;

    public ChoiceGroup(string name, bool horizontal, params (string Label, T Value)[] options)
    {
        _options = options;
        _horizontal = horizontal;
        AccessibleName = name;
        Margin = Padding.Empty;
        AutoSize = true;
        if (horizontal)
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            AccessibleRole = AccessibleRole.PageTabList;
            TabStop = true;
            Font = AppFonts.Body;
        }
        else
        {
            AccessibleRole = AccessibleRole.Grouping;
            for (int i = 0; i < options.Length; i++)
            {
                int index = i;
                var radio = new RadioOption(options[i].Label) { Checked = i == 0 };
                radio.CheckedChanged += (_, _) => { if (radio.Checked) Select(index, notify: true); };
                _radios.Add(radio);
                Controls.Add(radio);
            }
        }
    }

    public event EventHandler? Changed;

    /// <summary>Large segments for a page header (28 px high inside a 34 px track).</summary>
    public bool Large { get; init; }

    public T Value
    {
        get => _options[_selected].Value;
        set
        {
            int index = Array.FindIndex(_options, option => EqualityComparer<T>.Default.Equals(option.Value, value));
            if (index >= 0) Select(index, notify: false);
        }
    }

    void Select(int index, bool notify)
    {
        if (index == _selected) return;
        _selected = index;
        if (!_horizontal) _radios[index].Checked = true;
        else ItemHostAccessible.ItemFocused(this, index);
        // Whatever the choice changes happens first, so the time it takes isn't lost from the thumb's motion.
        if (notify) Changed?.Invoke(this, EventArgs.Empty);
        if (_horizontal) Animate();
        Invalidate();
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        if (!_horizontal)
        {
            int width = _radios.Count == 0 ? 0 : _radios.Max(radio => radio.GetPreferredSize(Size.Empty).Width);
            return new Size(width, _radios.Sum(radio => radio.GetPreferredSize(Size.Empty).Height + radio.Margin.Vertical));
        }
        int pad = LogicalToDeviceUnits(Large ? 14 : 10);
        int segments = _options.Sum(option => Draw.Measure(option.Label, Font).Width + 2 * pad);
        return new Size(segments + LogicalToDeviceUnits(6), LogicalToDeviceUnits(Large ? 34 : 32));
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        if (_horizontal) return;
        int y = 0;
        foreach (RadioOption radio in _radios)
        {
            Size size = radio.GetPreferredSize(Size.Empty);
            radio.SetBounds(0, y + radio.Margin.Top, Math.Max(size.Width, Width), size.Height);
            y += size.Height + radio.Margin.Vertical;
        }
    }

    Rectangle[] Segments()
    {
        if (_segmentsFor == (Width, Height, DeviceDpi) && _segments.Length == _options.Length) return _segments;
        int inset = LogicalToDeviceUnits(3), pad = LogicalToDeviceUnits(Large ? 14 : 10);
        int[] natural = [.. _options.Select(option => Draw.Measure(option.Label, Font).Width + 2 * pad)];
        int available = Width - 2 * inset, total = natural.Sum();
        var segments = new Rectangle[_options.Length];
        int x = inset;
        for (int i = 0; i < natural.Length; i++)
        {
            int width = total <= 0 ? 0 : (int)Math.Round(natural[i] * (double)available / total);
            if (i == natural.Length - 1) width = Width - inset - x;
            segments[i] = new Rectangle(x, inset, width, Height - 2 * inset);
            x += width;
        }
        (_segments, _segmentsFor) = (segments, (Width, Height, DeviceDpi));
        return segments;
    }

    static float Center(Rectangle segment) => segment.X + segment.Width / 2f;

    /// <summary>Runs the springs until the thumb comes to rest; without animations it jumps there.</summary>
    void Animate()
    {
        if (!_placed || !IsHandleCreated || !Visible)
        {
            _placed = false;
            Invalidate();
            return;
        }
        if (!Motion.Enabled)
        {
            Step(0);
            _thumb.Snap();
            _lift.Snap();
            Invalidate();
            return;
        }
        if (!(_motion?.IsRunning ?? false)) _motion = Motion.Loop(Step);
    }

    bool Step(double seconds)
    {
        if (IsDisposed) return false;
        Rectangle[] segments = Segments();
        _thumb.Target = _dragging ? DragTarget(segments) : Center(segments[_selected]);
        _thumb.Step(seconds);
        _lift.Step(seconds);
        Invalidate();
        if (_dragging || !_thumb.IsSettled(0.1) || !_lift.IsSettled(0.002)) return true;
        _thumb.Snap();
        _lift.Snap();
        return false;
    }

    /// <summary>Where the pointer is pulling the thumb's centre. Past the first or last choice it pulls less and less.</summary>
    float DragTarget(Rectangle[] segments)
    {
        float wanted = _pointerX - _grab, first = Center(segments[0]), last = Center(segments[^1]);
        if (wanted < first) return first - RubberBand(first - wanted, segments[0].Width * 0.35f);
        if (wanted > last) return last + RubberBand(wanted - last, segments[^1].Width * 0.35f);
        return wanted;
    }

    static float RubberBand(float distance, float limit) => (1 - 1 / (distance * 0.55f / limit + 1)) * limit;

    /// <summary>The thumb's width over a point of the track: between the widths of the segments either side.</summary>
    static float WidthAt(float center, Rectangle[] segments)
    {
        float Mid(Rectangle r) => r.X + r.Width / 2f;
        if (center <= Mid(segments[0])) return segments[0].Width;
        for (int i = 0; i < segments.Length - 1; i++)
        {
            float a = Mid(segments[i]), b = Mid(segments[i + 1]);
            if (center <= b) return segments[i].Width + (segments[i + 1].Width - segments[i].Width) * (center - a) / Math.Max(1, b - a);
        }
        return segments[^1].Width;
    }

    /// <summary>The thumb as drawn: lifted while held, stretched by its speed, squashed where it meets either end.</summary>
    RectangleF Thumb(Rectangle[] segments)
    {
        if (!_placed)
        {
            _thumb.Target = Center(segments[_selected]);
            _thumb.Snap();
            _placed = true;
        }
        float center = (float)_thumb.Value, inset = segments[0].Y, lift = (float)Math.Max(0, _lift.Value);
        float grow = inset * Math.Min(lift, 1.3f);
        float width = WidthAt(center, segments) + 2 * grow, height = segments[0].Height + 2 * Math.Min(grow, inset);
        float stretch = (float)Math.Min(MaxStretch, Math.Abs(_thumb.Velocity) / (DeviceDpi / 96.0) / StretchSpeed);
        width *= 1 + stretch;
        height *= 1 - stretch * 0.3f;
        // Carried past either end, it stays on the track and squashes a little against the end instead.
        float wall = inset * (1 - Math.Min(lift, 1)), left = center - width / 2, right = center + width / 2;
        if (left < wall) (left, right) = (wall, wall + width - RubberBand(wall - left, width * 0.2f));
        else if (right > Width - wall) (left, right) = (Width - wall - width + RubberBand(right - (Width - wall), width * 0.2f), Width - wall);
        return new RectangleF(left, (Height - height) / 2, right - left, height);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (!_horizontal) { base.OnPaint(e); return; }
        Palette p = Theme.Current;
        Graphics g = e.Graphics;
        g.Clear(Draw.BackgroundOf(this));
        Draw.Box(g, ClientRectangle, LogicalToDeviceUnits(Theme.RadiusLarge), p.Raised);
        Rectangle[] segments = Segments();
        RectangleF thumb = Thumb(segments);
        double lift = Math.Clamp(_lift.Value, 0, 1);
        float radius = (float)(LogicalToDeviceUnits(Theme.Radius) + (LogicalToDeviceUnits(Theme.RadiusLarge) - LogicalToDeviceUnits(Theme.Radius)) * lift);
        Draw.Box(g, thumb, radius, Enabled ? Theme.Blend(p.Active, p.Line2, lift * 0.5) : p.Hover);
        for (int i = 0; i < segments.Length; i++)
        {
            // A label brightens as the thumb covers it, so dragging reads as moving the highlight.
            float covered = Math.Max(0, Math.Min(thumb.Right, segments[i].Right) - Math.Max(thumb.Left, segments[i].Left)) / Math.Max(1, segments[i].Width);
            Color ink = !Enabled ? p.Disabled : i == _hoverIndex && !_dragging ? p.Text : Theme.Blend(p.Text2, p.Text, Math.Min(1, covered * 1.4));
            Draw.Text(g, _options[i].Label, covered > 0.5f ? AppFonts.BodyMedium : Font, segments[i], ink, Draw.Centered);
        }
        if (Focused && ShowFocusCues) Draw.FocusRing(g, Rectangle.Round(thumb), LogicalToDeviceUnits(Theme.Radius), LogicalToDeviceUnits(2));
    }

    protected override void OnResize(EventArgs eventargs)
    {
        base.OnResize(eventargs);
        if (_horizontal && !(_motion?.IsRunning ?? false)) _placed = false;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_horizontal) return;
        if (_tracking)
        {
            _pointerX = e.X;
            if (!_dragging && Math.Abs(e.X - _pressX) > SystemInformation.DragSize.Width)
            {
                _dragging = true;
                (_thumb.Response, _thumb.Damping) = (FollowResponse, FollowDamping);
            }
            if (_dragging)
            {
                Animate();
                return;
            }
        }
        int index = Array.FindIndex(Segments(), segment => segment.Contains(e.Location));
        if (index != _hoverIndex) { _hoverIndex = index; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hoverIndex = -1; Invalidate(); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!_horizontal || !Enabled || e.Button != MouseButtons.Left) return;
        Rectangle[] segments = Segments();
        int index = Array.FindIndex(segments, segment => segment.Contains(e.Location));
        if (index < 0) return;
        Focus();
        Capture = true;
        _tracking = true;
        _pressX = _pointerX = e.X;
        _lift.Target = 1;
        // Grabbing the thumb keeps it under the pointer; pressing elsewhere sends it there first, then it follows.
        bool onThumb = _placed && Thumb(segments).Contains(e.X, Height / 2f);
        if (!onThumb) Select(index, notify: true);
        _grab = e.X - (onThumb ? (float)_thumb.Value : Center(segments[index]));
        Animate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        EndDrag();
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (!Capture) EndDrag();
    }

    /// <summary>Lets go: the thumb lands on the choice its speed carries it nearest to, and bounces there.</summary>
    void EndDrag()
    {
        if (!_tracking) return;
        _tracking = false;
        _lift.Target = 0;
        if (Capture) Capture = false;
        if (_dragging)
        {
            _dragging = false;
            (_thumb.Response, _thumb.Damping) = (SettleResponse, SettleDamping);
            Rectangle[] segments = Segments();
            double landing = _thumb.Value + _thumb.Velocity * FlingProjection;
            int nearest = Enumerable.Range(0, segments.Length).MinBy(i => Math.Abs(Center(segments[i]) - landing));
            if (nearest != _selected) Select(nearest, notify: true);
        }
        Animate();
    }

    protected override bool IsInputKey(Keys keyData) => _horizontal && keyData is Keys.Left or Keys.Right || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!_horizontal) return;
        if (e.KeyCode == Keys.Left && _selected > 0) Select(_selected - 1, notify: true);
        else if (e.KeyCode == Keys.Right && _selected < _options.Length - 1) Select(_selected + 1, notify: true);
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); if (_horizontal) ItemHostAccessible.ItemFocused(this, _selected); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); foreach (RadioOption radio in _radios) radio.Enabled = Enabled; }

    protected override AccessibleObject CreateAccessibilityInstance() =>
        _horizontal ? new ItemHostAccessible(this, this, AccessibleRole.RadioButton) : base.CreateAccessibilityInstance();

    int IItemHost.ItemCount => _horizontal ? _options.Length : 0;
    string IItemHost.ItemName(int index) => _options[index].Label;
    Rectangle IItemHost.ItemBounds(int index) => Segments()[index];
    bool IItemHost.IsItemSelected(int index) => index == _selected;
    int IItemHost.FocusedItem => _selected;
    void IItemHost.ActivateItem(int index) { if (Enabled) Select(index, notify: true); }
}

/// <summary>A horizontal slider from <see cref="Minimum"/> to <see cref="Maximum"/>; arrows and Page keys step it.</summary>
internal sealed class Slider : Panel
{
    int _value = 100;
    bool _dragging;

    public Slider()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = true;
        Height = 24;
        AccessibleRole = AccessibleRole.Slider;
    }

    public int Minimum { get; init; }
    public int Maximum { get; init; } = 100;

    public event EventHandler? ValueChanged;

    public int Value
    {
        get => _value;
        set
        {
            int clamped = Math.Clamp(value, Minimum, Maximum);
            if (clamped == _value) return;
            _value = clamped;
            AccessibilityNotifyClients(AccessibleEvents.DescriptionChange, -1);
            if (Focused) ScreenReader.Say(this, value.ToString(System.Globalization.CultureInfo.CurrentCulture));
            Invalidate();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    int Knob => LogicalToDeviceUnits(14);

    protected override AccessibleObject CreateAccessibilityInstance() =>
        new DescribedAccessible(this, () => $"{_value} (from {Minimum} to {Maximum})");

    protected override void OnPaint(PaintEventArgs e)
    {
        Palette p = Theme.Current;
        Graphics g = e.Graphics;
        g.Clear(Draw.BackgroundOf(this));
        int knob = Knob, trackHeight = LogicalToDeviceUnits(4);
        int left = knob / 2, right = Width - knob / 2;
        int x = left + (int)Math.Round((right - left) * (_value - Minimum) / (double)Math.Max(1, Maximum - Minimum));
        int y = Height / 2;
        Draw.Box(g, new Rectangle(left, y - trackHeight / 2, right - left, trackHeight), trackHeight / 2f, p.Line2);
        Draw.Box(g, new Rectangle(left, y - trackHeight / 2, x - left, trackHeight), trackHeight / 2f, Enabled ? p.Accent : p.Text3);
        var knobBox = new Rectangle(x - knob / 2, y - knob / 2, knob, knob);
        Draw.Box(g, knobBox, knob / 2f, Color.White, p.Line2);
        if (Focused && ShowFocusCues) Draw.FocusRing(g, Rectangle.Inflate(knobBox, 3, 3), knob / 2f + 3, LogicalToDeviceUnits(2));
    }

    void SetFromX(int x)
    {
        int knob = Knob;
        double t = (x - knob / 2.0) / Math.Max(1, Width - knob);
        Value = Minimum + (int)Math.Round(Math.Clamp(t, 0, 1) * (Maximum - Minimum));
    }

    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); Focus(); _dragging = true; SetFromX(e.X); }
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (_dragging) SetFromX(e.X); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _dragging = false; }
    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        int step = Math.Max(1, (Maximum - Minimum) / 20);
        if (e.KeyCode is Keys.Left or Keys.Down) Value -= step;
        else if (e.KeyCode is Keys.Right or Keys.Up) Value += step;
        else if (e.KeyCode == Keys.Home) Value = Minimum;
        else if (e.KeyCode == Keys.End) Value = Maximum;
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
}

internal static class NativeTheme
{
    /// <summary>Dark or light scroll bars for a native scrolling control, matching the theme.</summary>
    public static void ScrollBars(Control control)
    {
        if (!control.IsHandleCreated) return;
        Native.SetScrollbarTheme(control.Handle, Theme.IsDark);
    }
}
