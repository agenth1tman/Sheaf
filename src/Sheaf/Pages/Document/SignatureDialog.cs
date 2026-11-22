using System.Drawing.Drawing2D;
using Sheaf.Core.Editing;
using Sheaf.Core.Fonts;
using Sheaf.Core.IO;
using Sheaf.Ui;
using Sheaf.Ui.Controls;

namespace Sheaf.Pages.Document;

/// <summary>Draws a page object scaled to fit a white card: the signature in the dialog and the Fill &amp; Sign panel.</summary>
internal sealed class SignaturePreview : Panel
{
    public SignaturePreview()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        AccessibleRole = AccessibleRole.Graphic;
    }

    public PageObject? Signature
    {
        get => field;
        set { field = value; AccessibleName = value is null ? "No signature" : "Your signature"; Invalidate(); }
    }

    public string EmptyText { get; set; } = "";

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(Draw.BackgroundOf(this));
        Draw.Box(g, ClientRectangle, LogicalToDeviceUnits(Theme.Radius), Color.White, Theme.Current.Line2);
        if (Signature is not { } item)
        {
            Draw.Text(g, EmptyText, AppFonts.Small, ClientRectangle, Color.FromArgb(0x71, 0x71, 0x7A), Draw.Centered);
            return;
        }
        int pad = LogicalToDeviceUnits(10);
        float scale = Math.Min((Width - 2 * pad) / Math.Max(1, item.Width), (Height - 2 * pad) / Math.Max(1, item.Height));
        GraphicsState state = g.Save();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        g.TranslateTransform((Width - item.Width * scale) / 2 - item.X * scale, (Height - item.Height * scale) / 2 - item.Y * scale);
        g.ScaleTransform(scale, scale);
        PagePainter.DrawObject(g, item);
        g.Restore(state);
    }
}

/// <summary>A white pad to sign on with the mouse, a pen or a finger.</summary>
internal sealed class InkPad : Panel
{
    readonly List<List<PointF>> _strokes = [];
    bool _drawing;

    public InkPad()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = true;
        Cursor = Cursors.Cross;
        AccessibleRole = AccessibleRole.Graphic;
        AccessibleName = "Signature pad";
        AccessibleDescription = "Draw with the mouse, a pen or a finger. To sign without drawing, choose Type or Image above.";
    }

    public event EventHandler? Changed;

    public Color Ink { get; set { field = value; Invalidate(); } } = Color.Black;

    public IReadOnlyList<List<PointF>> Strokes => _strokes;

    public void Clear()
    {
        _strokes.Clear();
        Invalidate();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        Focus();
        _drawing = true;
        _strokes.Add([e.Location]);
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_drawing) return;
        _strokes[^1].Add(e.Location);
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_drawing) return;
        _drawing = false;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(Draw.BackgroundOf(this));
        Draw.Box(g, ClientRectangle, LogicalToDeviceUnits(Theme.Radius), Color.White, Focused ? Theme.Current.Accent : Theme.Current.Line2);
        int baseline = Height - LogicalToDeviceUnits(36), inset = LogicalToDeviceUnits(24);
        using (var line = new Pen(Color.FromArgb(0xD4, 0xD4, 0xD8)))
            g.DrawLine(line, inset, baseline, Width - inset, baseline);
        if (_strokes.Count == 0)
            Draw.Text(g, "Sign above the line", AppFonts.Small, new Rectangle(0, baseline + LogicalToDeviceUnits(6), Width, LogicalToDeviceUnits(20)), Color.FromArgb(0x71, 0x71, 0x7A), Draw.Centered);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(Ink, LogicalToDeviceUnits(3)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        foreach (List<PointF> stroke in _strokes)
        {
            if (stroke.Count > 1) g.DrawLines(pen, [.. stroke]);
            else
            {
                using var dot = new SolidBrush(Ink);
                g.FillEllipse(dot, stroke[0].X - pen.Width / 2, stroke[0].Y - pen.Width / 2, pen.Width, pen.Width);
            }
        }
    }
}

/// <summary>Creates the signature: drawn on a pad, typed in a handwriting font, or an image of a real one.</summary>
internal sealed class SignatureDialog : DialogForm
{
    const float SignatureWidth = 180, SignatureHeight = 60;
    enum Way { Draw, Type, Image }

    readonly ChoiceGroup<Way> _way = new("How to sign", true, ("Draw", Way.Draw), ("Type", Way.Type), ("Image", Way.Image));
    readonly ChoiceGroup<int> _ink = new("Ink", true, ("Black", unchecked((int)0xFF18181B)), ("Blue", unchecked((int)0xFF1D4ED8)));
    readonly InkPad _pad = new() { Dock = DockStyle.Fill };
    readonly InputBox _name = new() { Placeholder = "Your name", Caption = "Your name", Dock = DockStyle.Top };
    readonly SignaturePreview _typed = new() { Dock = DockStyle.Fill, EmptyText = "Type your name above" };
    readonly SignaturePreview _image = new() { Dock = DockStyle.Fill, EmptyText = "Choose a photo or scan of your signature" };
    readonly Panel _content = new() { Height = 170 };
    readonly Panel _drawPanel = new() { Dock = DockStyle.Fill };
    readonly Panel _typePanel = new() { Dock = DockStyle.Fill, Visible = false };
    readonly Panel _imagePanel = new() { Dock = DockStyle.Fill, Visible = false };
    readonly TextLabel _error = new("", Tone.Error) { Font = AppFonts.Small };
    readonly Func<string?> _pickImage;
    PageObject? _picked;

    SignatureDialog(Func<string?> pickImage)
        : base("Create Your Signature", "Sheaf keeps it on this computer only, ready for the next PDF you sign.", 460)
    {
        _pickImage = pickImage;
        var clear = new ActionLink("Clear", () => _pad.Clear()) { Dock = DockStyle.Bottom, Font = AppFonts.Small };
        _drawPanel.Controls.Add(_pad);
        _drawPanel.Controls.Add(clear);
        var spacer = new Panel { Dock = DockStyle.Top, Height = 10 };
        _typePanel.Controls.Add(_typed);
        _typePanel.Controls.Add(spacer);
        _typePanel.Controls.Add(_name);
        var choose = new AppButton("Choose Image…", ButtonKind.Secondary, "image") { Dock = DockStyle.Top };
        var gap = new Panel { Dock = DockStyle.Top, Height = 10 };
        _imagePanel.Controls.Add(_image);
        _imagePanel.Controls.Add(gap);
        _imagePanel.Controls.Add(choose);
        _content.Controls.AddRange([_drawPanel, _typePanel, _imagePanel]);

        AddContent(_way);
        _way.Anchor = AnchorStyles.Left;
        AddContent(_content, 12);
        AddContent(Make.Row(new TextLabel("Ink", Tone.Secondary) { Font = AppFonts.SmallStrong, Margin = new Padding(0, 9, 10, 0) }, _ink), 12);
        AddContent(_error, 8);
        AddButtons("Save Signature", Accept);

        _way.Changed += (_, _) => ShowWay();
        _ink.Changed += (_, _) => { _pad.Ink = Color.FromArgb(_ink.Value); UpdateTyped(); };
        _name.Box.TextChanged += (_, _) => UpdateTyped();
        choose.Click += (_, _) => PickImage();
        _pad.Changed += (_, _) => _error.Text = "";
    }

    void ShowWay()
    {
        _drawPanel.Visible = _way.Value == Way.Draw;
        _typePanel.Visible = _way.Value == Way.Type;
        _imagePanel.Visible = _way.Value == Way.Image;
        _error.Text = "";
        if (_way.Value == Way.Type) _name.Focus();
    }

    void UpdateTyped() => _typed.Signature = Typed();

    PageObject? Typed()
    {
        string name = _name.Text.Trim();
        if (name.Length == 0) return null;
        var item = new PageObject { Kind = MarkKind.Text, Text = name, FontFamily = EmbeddedFonts.PdfSignatureFamily, FontSize = 36, StrokeColor = _ink.Value };
        item.Width = PagePainter.TextWidth(item) + 12;
        item.Height = item.FontSize * 1.5f;
        return item;
    }

    PageObject? Drawn()
    {
        var strokes = _pad.Strokes.Where(stroke => stroke.Count > 0).ToList();
        if (strokes.Count == 0) return null;
        var item = new PageObject { Kind = MarkKind.Ink, StrokeColor = _ink.Value, Strokes = [.. strokes.Select(stroke => stroke.ToList())] };
        item.FitToStrokes();
        // Scale the drawing to a signature's size on paper, keeping its shape.
        float scale = Math.Min(SignatureWidth / Math.Max(1, item.Width), SignatureHeight / Math.Max(1, item.Height));
        item.StrokeWidth = Math.Max(1, LogicalToDeviceUnits(3) * scale);
        item.Resize(new RectangleF(0, 0, item.Width * scale, item.Height * scale));
        item.FitToStrokes(item.StrokeWidth);
        item.Offset(-item.X, -item.Y);
        return item;
    }

    void PickImage()
    {
        if (_pickImage() is not { } path) return;
        try
        {
            var item = new PageObject { Kind = MarkKind.Image, ImageData = File.ReadAllBytes(path) };
            if (ImageCache.Get(item) is not { } image)
            {
                _error.Text = "That file isn’t an image Sheaf can read.";
                return;
            }
            item.Width = SignatureWidth;
            item.Height = SignatureWidth * image.Height / Math.Max(1, image.Width);
            _picked = item;
            _image.Signature = item;
            _error.Text = "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _error.Text = $"Couldn’t read the image: {ex.Message}";
        }
    }

    PageObject? Result => _way.Value switch
    {
        Way.Draw => Drawn(),
        Way.Type => Typed(),
        _ => _picked,
    };

    bool Accept()
    {
        if (Result is not null) return true;
        _error.Text = _way.Value switch
        {
            Way.Draw => "Sign on the pad first.",
            Way.Type => "Type your name first.",
            _ => "Choose an image first.",
        };
        return false;
    }

    /// <summary>Asks for a new signature; returns it, or null when cancelled.</summary>
    public static PageObject? Create(IWin32Window? owner)
    {
        using var dialog = new SignatureDialog(() =>
        {
            using var open = new OpenFileDialog { Filter = Shell.FileDialogs.Filter(FileKind.Image), Title = "Choose your signature" };
            return open.ShowDialog(owner) == DialogResult.OK ? open.FileName : null;
        });
        return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.Result : null;
    }
}
