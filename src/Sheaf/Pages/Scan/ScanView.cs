using System.Drawing.Drawing2D;
using Sheaf.Ui;
using Sheaf.Ui.Controls;

namespace Sheaf.Pages.Scan;

/// <summary>
/// Shows an image fitted into the space: a live camera frame with the page it found outlined, a photo whose
/// corners can be dragged into place, or a finished page.
/// </summary>
internal sealed class ScanView : Panel
{
    const int ImageMargin = 28, HandleSize = 14;
    int _dragging = -1;
    double _flash;
    Tween? _flashTween;

    public ScanView()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = true;
        AccessibleRole = AccessibleRole.Graphic;
        AccessibleName = "Scan view";
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new DescribedAccessible(this, () =>
        Image is null ? null
        : EditCorners ? "The photo with the page outlined. Use Find Edges or Whole Photo, or drag the corners with the mouse."
        : Status.Length > 0 ? Status
        : "The cleaned-up page.");

    public event EventHandler? CornersChanged;

    /// <summary>The image shown; the view never disposes it.</summary>
    public Bitmap? Image { get; set { field = value; Invalidate(); } }

    /// <summary>The page outline in image pixels: top-left, top-right, bottom-right, bottom-left.</summary>
    public PointF[]? Corners { get; set { field = value; Invalidate(); } }

    /// <summary>Corners can be dragged.</summary>
    public bool EditCorners { get; set { field = value; Cursor = value ? Cursors.Cross : Cursors.Default; Invalidate(); } }

    /// <summary>A line of status shown over the bottom of the image, such as "Page found".</summary>
    public string Status
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            Invalidate();
            AccessibilityNotifyClients(AccessibleEvents.DescriptionChange, -1);
            if (value.Length > 0) ScreenReader.Say(this, value);
        }
    } = "";

    /// <summary>A short white flash, as a camera does when it takes a picture.</summary>
    public void Flash()
    {
        _flashTween?.Cancel();
        if (!Motion.Enabled) return;
        _flashTween = Motion.Run(260, t => { _flash = 1 - t; Invalidate(); }, Easing.Out, () => { _flash = 0; Invalidate(); });
    }

    RectangleF ImageRect()
    {
        if (Image is null) return RectangleF.Empty;
        float margin = LogicalToDeviceUnits(ImageMargin);
        float scale = Math.Min((Width - 2 * margin) / Image.Width, (Height - 2 * margin) / Image.Height);
        float width = Image.Width * scale, height = Image.Height * scale;
        return new RectangleF((Width - width) / 2, (Height - height) / 2, width, height);
    }

    PointF ToScreen(PointF point)
    {
        RectangleF rect = ImageRect();
        return new PointF(rect.X + point.X * rect.Width / Image!.Width, rect.Y + point.Y * rect.Height / Image.Height);
    }

    PointF ToImage(Point point)
    {
        RectangleF rect = ImageRect();
        return new PointF(Math.Clamp((point.X - rect.X) * Image!.Width / rect.Width, 0, Image.Width), Math.Clamp((point.Y - rect.Y) * Image.Height / rect.Height, 0, Image.Height));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(Theme.Current.Canvas);
        if (Image is null) return;
        RectangleF rect = ImageRect();
        using (var shadow = new SolidBrush(Color.FromArgb(Theme.IsDark ? 110 : 40, 0, 0, 0)))
            g.FillRectangle(shadow, rect.X, rect.Y + LogicalToDeviceUnits(2), rect.Width, rect.Height);
        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(Image, rect);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (Corners is { Length: 4 } corners) DrawOutline(g, rect, [.. corners.Select(ToScreen)]);
        if (Status.Length > 0) DrawStatus(g, rect);
        if (_flash > 0)
            using (var white = new SolidBrush(Color.FromArgb((int)(_flash * 200), 255, 255, 255))) g.FillRectangle(white, rect);
    }

    void DrawOutline(Graphics g, RectangleF rect, PointF[] quad)
    {
        Color accent = Theme.Current.Accent;
        if (EditCorners)
        {
            // Dim what will be cut away.
            using var outside = new Region(rect);
            using var page = new GraphicsPath();
            page.AddPolygon(quad);
            outside.Exclude(page);
            using var dim = new SolidBrush(Color.FromArgb(140, 0, 0, 0));
            g.FillRegion(dim, outside);
        }
        else
        {
            using var tint = new SolidBrush(Color.FromArgb(40, accent));
            g.FillPolygon(tint, quad);
        }
        using (var line = new Pen(accent, LogicalToDeviceUnits(2))) g.DrawPolygon(line, quad);
        if (!EditCorners) return;
        float size = LogicalToDeviceUnits(HandleSize);
        using var ring = new Pen(accent, LogicalToDeviceUnits(2));
        foreach (PointF corner in quad)
        {
            var handle = new RectangleF(corner.X - size / 2, corner.Y - size / 2, size, size);
            g.FillEllipse(Brushes.White, handle);
            g.DrawEllipse(ring, handle);
        }
    }

    void DrawStatus(Graphics g, RectangleF rect)
    {
        Size text = Draw.Measure(Status, AppFonts.BodyMedium);
        int padX = LogicalToDeviceUnits(14), height = LogicalToDeviceUnits(32);
        var pill = new Rectangle((int)(rect.X + (rect.Width - text.Width) / 2 - padX), (int)rect.Bottom - height - LogicalToDeviceUnits(16), text.Width + 2 * padX, height);
        Draw.Box(g, pill, height / 2f, Color.FromArgb(200, 14, 14, 16));
        Draw.Text(g, Status, AppFonts.BodyMedium, pill, Color.White, Draw.Centered);
    }

    int HandleAt(Point location)
    {
        if (!EditCorners || Corners is not { Length: 4 } corners || Image is null) return -1;
        float reach = LogicalToDeviceUnits(HandleSize + 8);
        for (int i = 0; i < 4; i++)
        {
            PointF screen = ToScreen(corners[i]);
            if (Math.Abs(screen.X - location.X) <= reach / 2 && Math.Abs(screen.Y - location.Y) <= reach / 2) return i;
        }
        return -1;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        _dragging = HandleAt(e.Location);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging < 0)
        {
            if (EditCorners) Cursor = HandleAt(e.Location) >= 0 ? Cursors.SizeAll : Cursors.Default;
            return;
        }
        PointF[] corners = [.. Corners!];
        corners[_dragging] = ToImage(e.Location);
        Corners = corners;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_dragging < 0) return;
        _dragging = -1;
        CornersChanged?.Invoke(this, EventArgs.Empty);
    }
}
