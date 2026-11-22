using System.Drawing.Drawing2D;
using System.Globalization;

namespace Sheaf.Ui.Controls;

/// <summary>A colour as hue (0–360), saturation and value (0–1).</summary>
internal readonly record struct Hsv(double H, double S, double V)
{
    public static Hsv From(Color color)
    {
        double r = color.R / 255.0, g = color.G / 255.0, b = color.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), delta = max - min;
        double h = delta == 0 ? 0 : max == r ? 60 * ((g - b) / delta % 6) : max == g ? 60 * ((b - r) / delta + 2) : 60 * ((r - g) / delta + 4);
        return new Hsv(h < 0 ? h + 360 : h, max == 0 ? 0 : delta / max, max);
    }

    public Color ToColor()
    {
        double c = V * S, x = c * (1 - Math.Abs(H / 60 % 2 - 1)), m = V - c;
        (double r, double g, double b) = (H % 360) switch
        {
            < 60 => (c, x, 0d),
            < 120 => (x, c, 0d),
            < 180 => (0d, c, x),
            < 240 => (0d, x, c),
            < 300 => (x, 0d, c),
            _ => (c, 0d, x),
        };
        return Color.FromArgb((int)Math.Round((r + m) * 255), (int)Math.Round((g + m) * 255), (int)Math.Round((b + m) * 255));
    }
}

/// <summary>Saturation left to right and brightness bottom to top, for one hue. Drag, click or use the arrow keys.</summary>
internal sealed class SpectrumBox : Panel
{
    Bitmap? _field;
    double _fieldHue = -1;
    Hsv _value;
    bool _dragging;

    public SpectrumBox()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = true;
        Cursor = Cursors.Cross;
        AccessibleRole = AccessibleRole.Graphic;
        AccessibleName = "Saturation and brightness";
    }

    public event EventHandler? ValueChanged;

    public Hsv Value
    {
        get => _value;
        set { _value = value; Invalidate(); AccessibilityNotifyClients(AccessibleEvents.DescriptionChange, -1); }
    }

    protected override AccessibleObject CreateAccessibilityInstance() =>
        new DescribedAccessible(this, () => $"Saturation {Math.Round(_value.S * 100)} %, brightness {Math.Round(_value.V * 100)} %. Arrow keys change them.");

    Bitmap Field()
    {
        if (_field is not null && _fieldHue == _value.H && _field.Size == ClientSize) return _field;
        _field?.Dispose();
        int width = Math.Max(1, ClientSize.Width), height = Math.Max(1, ClientSize.Height);
        var pixels = new int[width * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                pixels[y * width + x] = new Hsv(_value.H, x / (double)Math.Max(1, width - 1), 1 - y / (double)Math.Max(1, height - 1)).ToColor().ToArgb();
        _field = new Core.Scanning.Pixels(width, height, pixels).ToBitmap();
        _fieldHue = _value.H;
        return _field;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(Draw.BackgroundOf(this));
        g.DrawImageUnscaled(Field(), 0, 0);
        float x = (float)(_value.S * (Width - 1)), y = (float)((1 - _value.V) * (Height - 1));
        float ring = LogicalToDeviceUnits(7);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var dark = new Pen(Color.FromArgb(160, 0, 0, 0), 3)) g.DrawEllipse(dark, x - ring, y - ring, ring * 2, ring * 2);
        using (var light = new Pen(Color.White, 1.6f)) g.DrawEllipse(light, x - ring, y - ring, ring * 2, ring * 2);
        if (Focused && ShowFocusCues) Draw.FocusRing(g, ClientRectangle, 0, LogicalToDeviceUnits(2));
    }

    void SetFrom(Point point)
    {
        double s = Math.Clamp(point.X / (double)Math.Max(1, Width - 1), 0, 1), v = 1 - Math.Clamp(point.Y / (double)Math.Max(1, Height - 1), 0, 1);
        Value = _value with { S = s, V = v };
        ValueChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); Focus(); _dragging = true; SetFrom(e.Location); }
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (_dragging) SetFrom(e.Location); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _dragging = false; }
    protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        double step = e.Shift ? 0.1 : 0.02;
        Hsv next = e.KeyCode switch
        {
            Keys.Left => _value with { S = Math.Max(0, _value.S - step) },
            Keys.Right => _value with { S = Math.Min(1, _value.S + step) },
            Keys.Up => _value with { V = Math.Min(1, _value.V + step) },
            Keys.Down => _value with { V = Math.Max(0, _value.V - step) },
            _ => _value,
        };
        if (next == _value) return;
        Value = next;
        ValueChanged?.Invoke(this, EventArgs.Empty);
        ScreenReader.Say(this, $"Saturation {Math.Round(next.S * 100)} %, brightness {Math.Round(next.V * 100)} %");
        e.Handled = true;
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _field?.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>The hues from red round to red. Drag, click or use the arrow keys.</summary>
internal sealed class HueStrip : Panel
{
    double _hue;
    bool _dragging;

    public HueStrip()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = true;
        AccessibleRole = AccessibleRole.Slider;
        AccessibleName = "Hue";
    }

    public event EventHandler? HueChanged;

    public double Hue
    {
        get => _hue;
        set { _hue = value; Invalidate(); AccessibilityNotifyClients(AccessibleEvents.DescriptionChange, -1); }
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new DescribedAccessible(this, () => $"{Math.Round(_hue)} degrees");

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(Draw.BackgroundOf(this));
        int inset = LogicalToDeviceUnits(4);
        var track = new Rectangle(0, inset, Width, Height - 2 * inset);
        using (var brush = new LinearGradientBrush(track, Color.Red, Color.Red, 0f))
        {
            brush.InterpolationColors = new ColorBlend
            {
                Colors = [.. Enumerable.Range(0, 7).Select(i => new Hsv(i * 60 % 360, 1, 1).ToColor())],
                Positions = [0f, 1 / 6f, 2 / 6f, 3 / 6f, 4 / 6f, 5 / 6f, 1f],
            };
            using GraphicsPath shape = Draw.RoundRect(track, track.Height / 2f);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.FillPath(brush, shape);
        }
        float knob = Height - 2;
        float x = (float)(Travel.Start + _hue / 360 * Travel.Length);
        using (var fill = new SolidBrush(new Hsv(_hue, 1, 1).ToColor())) g.FillEllipse(fill, x - knob / 2, 1, knob, knob);
        using (var ring = new Pen(Color.White, 2)) g.DrawEllipse(ring, x - knob / 2, 1, knob, knob);
        if (Focused && ShowFocusCues) Draw.FocusRing(g, ClientRectangle, Height / 2f, LogicalToDeviceUnits(2));
    }

    /// <summary>Where the knob's centre can go: inset by half the knob so it never hangs off either end.</summary>
    (float Start, float Length) Travel => (Height / 2f, Math.Max(1, Width - Height));

    void SetFrom(int x)
    {
        Hue = Math.Clamp((x - Travel.Start) / Travel.Length, 0, 1) * 359.9;
        HueChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); Focus(); _dragging = true; SetFrom(e.X); }
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (_dragging) SetFrom(e.X); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _dragging = false; }
    protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) is Keys.Left or Keys.Right || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is not (Keys.Left or Keys.Right)) return;
        double step = (e.Shift ? 15 : 3) * (e.KeyCode == Keys.Left ? -1 : 1);
        Hue = (_hue + step + 360) % 360;
        HueChanged?.Invoke(this, EventArgs.Empty);
        ScreenReader.Say(this, $"{Math.Round(_hue)} degrees");
        e.Handled = true;
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
}

/// <summary>Chooses any colour: a saturation and brightness square, a hue strip, and a hex code.</summary>
internal sealed class ColorPickerDialog : DialogForm
{
    readonly SpectrumBox _spectrum = new() { Height = 180 };
    readonly HueStrip _hue = new() { Height = 22 };
    readonly InputBox _hex = new() { Caption = "Hex code", Width = 120, Monospace = true };
    readonly Panel _swatches = new() { Width = 96, Height = 32, Margin = new Padding(16, 0, 0, 0) };
    readonly Color _before;
    bool _syncing;

    ColorPickerDialog(Color start) : base("Choose a Colour", null, 360)
    {
        _before = Color.FromArgb(255, start);
        _hex.Box.MaxLength = 7;
        AddContent(_spectrum);
        AddContent(_hue, 12);
        var row = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Margin = Padding.Empty };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.Controls.Add(new FormField("Hex code", _hex), 0, 0);
        _swatches.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
        _swatches.Margin = new Padding(16, 0, 0, 12);
        row.Controls.Add(_swatches, 1, 0);
        AddContent(row, 14);
        AddButtons("Use Colour", () => Parse(_hex.Text) is not null);

        _swatches.AccessibleName = "Before and after";
        _swatches.Paint += (_, e) => PaintSwatches(e.Graphics);
        _spectrum.ValueChanged += (_, _) => Changed(_spectrum.Value);
        _hue.HueChanged += (_, _) => Changed(_spectrum.Value with { H = _hue.Hue });
        _hex.Box.TextChanged += (_, _) => { if (!_syncing && Parse(_hex.Text) is { } color) Show(Hsv.From(color), updateHex: false); };
        Show(Hsv.From(_before), updateHex: true);
        Shown += (_, _) => _spectrum.Focus();
    }

    Color Current => _spectrum.Value.ToColor();

    void Changed(Hsv value) => Show(value, updateHex: true);

    void Show(Hsv value, bool updateHex)
    {
        _syncing = true;
        _spectrum.Value = value;
        _hue.Hue = value.H;
        if (updateHex) _hex.Text = Hex(value.ToColor());
        _swatches.Invalidate();
        _syncing = false;
    }

    void PaintSwatches(Graphics g)
    {
        g.Clear(Draw.BackgroundOf(_swatches));
        int half = _swatches.Width / 2;
        using (var before = new SolidBrush(_before)) g.FillRectangle(before, 0, 0, half, _swatches.Height);
        using (var after = new SolidBrush(Current)) g.FillRectangle(after, half, 0, _swatches.Width - half, _swatches.Height);
        using var edge = new Pen(Theme.Current.Line2);
        g.DrawRectangle(edge, 0, 0, _swatches.Width - 1, _swatches.Height - 1);
    }

    static string Hex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    static Color? Parse(string text)
    {
        string digits = text.Trim().TrimStart('#');
        if (digits.Length == 3) digits = string.Concat(digits.Select(c => $"{c}{c}"));
        return digits.Length == 6 && int.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb)
            ? Color.FromArgb(255, Color.FromArgb(rgb))
            : null;
    }

    /// <summary>Asks for a colour; null when cancelled.</summary>
    public static Color? Pick(IWin32Window? owner, Color start)
    {
        using var dialog = new ColorPickerDialog(start.A == 0 ? Color.Black : start);
        return dialog.ShowDialog(owner) == DialogResult.OK ? Parse(dialog._hex.Text) : null;
    }
}
