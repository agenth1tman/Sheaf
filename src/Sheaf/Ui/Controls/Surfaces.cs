using Sheaf.Platform;

namespace Sheaf.Ui.Controls;

internal enum SurfaceRole { Chrome, Panel, Raised, Canvas }

/// <summary>
/// A panel whose background is a theme colour. Children inherit it as their background, so a theme switch
/// is one repaint. Scrolling surfaces get scroll bars that match the theme.
/// </summary>
internal class Surface : Panel
{
    public Surface(SurfaceRole role = SurfaceRole.Panel)
    {
        Role = role;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        Margin = Padding.Empty;
        Theme.Changed += OnThemeChanged;
    }

    public SurfaceRole Role { get; set; }

    /// <summary>Hairlines drawn on these edges, in the theme's line colour.</summary>
    public AnchorStyles Borders { get; set; }

    public override Color BackColor
    {
        get => Role switch
        {
            SurfaceRole.Chrome => Theme.Current.Chrome,
            SurfaceRole.Raised => Theme.Current.Raised,
            SurfaceRole.Canvas => Theme.Current.Canvas,
            _ => Theme.Current.Panel,
        };
        set { }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (Borders == AnchorStyles.None) return;
        using var pen = new Pen(Theme.Current.Line);
        if (Borders.HasFlag(AnchorStyles.Top)) e.Graphics.DrawLine(pen, 0, 0, Width, 0);
        if (Borders.HasFlag(AnchorStyles.Bottom)) e.Graphics.DrawLine(pen, 0, Height - 1, Width, Height - 1);
        if (Borders.HasFlag(AnchorStyles.Left)) e.Graphics.DrawLine(pen, 0, 0, 0, Height);
        if (Borders.HasFlag(AnchorStyles.Right)) e.Graphics.DrawLine(pen, Width - 1, 0, Width - 1, Height);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (AutoScroll) NativeTheme.ScrollBars(this);
    }

    void OnThemeChanged(object? sender, EventArgs e)
    {
        if (AutoScroll) NativeTheme.ScrollBars(this);
        Invalidate(true);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Theme.Changed -= OnThemeChanged;
        base.Dispose(disposing);
    }
}

/// <summary>A one-pixel rule between groups.</summary>
internal sealed class Divider : Control
{
    public Divider(bool vertical = false)
    {
        SetStyle(ControlStyles.Selectable, false);
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
        TabStop = false;
        AccessibleRole = AccessibleRole.Separator;
        if (vertical) { Width = 1; Height = 20; Margin = new Padding(8, 0, 8, 0); }
        else { Height = 1; Anchor = AnchorStyles.Left | AnchorStyles.Right; Margin = new Padding(0, 12, 0, 12); }
    }

    protected override void OnPaint(PaintEventArgs e) => e.Graphics.Clear(Theme.Current.Line);
}

/// <summary>A thin bar that moves along the top of a page while work runs; still when Windows animations are off.</summary>
internal sealed class BusyBar : Panel
{
    const float SegmentShare = 0.28f;
    readonly System.Diagnostics.Stopwatch _clock = new();
    FrameLoop? _loop;
    bool _running;

    public BusyBar()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        SetStyle(ControlStyles.Selectable, false);
        Height = 2;
        Dock = DockStyle.Top;
        TabStop = false;
        AccessibleRole = AccessibleRole.ProgressBar;
        AccessibleName = "Working";
    }

    public bool Running
    {
        get => _running;
        set
        {
            _running = value;
            _clock.Restart();
            _loop?.Stop();
            _loop = value && Motion.Enabled ? Motion.Loop(_ => { if (IsDisposed || !_running) return false; Invalidate(); return true; }) : null;
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(_running ? Theme.Current.Line : Draw.BackgroundOf(this));
        if (!_running) return;
        using var accent = new SolidBrush(Theme.Current.Accent);
        if (_loop is null) { g.FillRectangle(accent, ClientRectangle); return; }
        double cycle = Motion.Ease(Easing.InOut, _clock.Elapsed.TotalMilliseconds % 1400 / 1400);
        float left = (float)((cycle * (1 + SegmentShare) - SegmentShare) * Width);
        g.FillRectangle(accent, left, 0, SegmentShare * Width, Height);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _loop?.Stop();
        base.Dispose(disposing);
    }
}

/// <summary>
/// A slim line that shows how far a tool run has got. It moves to the share of work reported done and, between
/// reports, creeps toward the end of the current step, slower the nearer it gets, so a long file never looks stuck.
/// It never goes backwards. When the run ends it fills (or stays, if stopped) and fades out.
/// </summary>
internal sealed class ProgressLine : Control
{
    const double CreepShare = 0.9, CreepSeconds = 2.5;
    double _done, _next, _shown, _opacity;
    bool _running;
    FrameLoop? _loop;
    Tween? _fade;

    public ProgressLine()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false;
        Height = 3;
        AccessibleRole = AccessibleRole.ProgressBar;
        AccessibleName = "Progress";
    }

    public void Start()
    {
        _fade?.Cancel();
        (_done, _next, _shown) = (0, 1, 0);
        _running = true;
        _fade = Motion.Run(160, t => { _opacity = t; Invalidate(); }, Easing.Out);
        Animate();
    }

    /// <summary>The share of the work that is <paramref name="done"/>, and the share it reaches when the current step ends.</summary>
    public void Report(double done, double next)
    {
        if (!_running) return;
        _done = Math.Max(_done, done);
        _next = Math.Max(_done, next);
        if (!Motion.Enabled) { _shown = Math.Max(_shown, _done); Invalidate(); }
    }

    /// <summary>Ends the run: a finished one fills the line first. Either way it fades out.</summary>
    public void Finish(bool completed)
    {
        _running = false;
        _loop?.Stop();
        double from = _shown, to = completed ? 1 : _shown;
        _fade?.Cancel();
        _fade = Motion.Run(completed ? 260 : 0, t => { _shown = from + (to - from) * t; Invalidate(); }, Easing.Out, done: () =>
            _fade = Motion.Run(320, t => { _opacity = 1 - t; Invalidate(); }, Easing.Standard));
    }

    void Animate()
    {
        _loop?.Stop();
        if (!Motion.Enabled) { _opacity = 1; Invalidate(); return; }
        _loop = Motion.Loop(seconds =>
        {
            if (IsDisposed || !_running) return false;
            double ceiling = _done + (_next - _done) * CreepShare;
            // Reported progress is caught up with quickly; past that, the line only creeps.
            if (_shown < _done) _shown += (_done - _shown) * (1 - Math.Exp(-seconds / 0.12));
            if (_shown < ceiling) _shown += (ceiling - _shown) * (1 - Math.Exp(-seconds / CreepSeconds));
            Invalidate();
            return true;
        });
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        Color background = Draw.BackgroundOf(this);
        g.Clear(background);
        if (_opacity <= 0) return;
        Palette p = Theme.Current;
        float radius = Height / 2f;
        Draw.Box(g, new RectangleF(0, 0, Width, Height), radius, Theme.Blend(background, p.Line, _opacity));
        float filled = (float)(Width * Math.Clamp(_shown, 0, 1));
        if (filled >= 1) Draw.Box(g, new RectangleF(0, 0, Math.Max(filled, Height), Height), radius, Theme.Blend(background, p.Accent, _opacity));
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new LineAccessible(this);

    sealed class LineAccessible(ProgressLine line) : ControlAccessibleObject(line)
    {
        public override string Value => line._running ? $"{Math.Round(line._done * 100)} %" : "";
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        Height = LogicalToDeviceUnits(3);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Height = LogicalToDeviceUnits(3);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _loop?.Stop();
            _fade?.Cancel();
        }
        base.Dispose(disposing);
    }
}
