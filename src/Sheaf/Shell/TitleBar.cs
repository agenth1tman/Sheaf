using Sheaf.Platform;
using Sheaf.Ui;
using Sheaf.Ui.Controls;

namespace Sheaf.Shell;

/// <summary>A tab in the title bar: Home, a document, a tool or a scan.</summary>
internal sealed class TabItem(string title, string icon, Control page)
{
    public string Title { get; set; } = title;
    public string Icon { get; set; } = icon;
    public Control Page { get; } = page;
    public bool IsDirty { get; set; }
    public bool IsPinned { get; init; }

    /// <summary>A second line for the tab's tip, such as the file's folder.</summary>
    public string? Detail { get; set; }
    public Color? Badge { get; init; }

    /// <summary>0 to 1 while a new tab grows to its width.</summary>
    internal double Grow { get; set; } = 1;
}

/// <summary>A tab that has been closed and is still collapsing out of the strip, where its slot was.</summary>
internal sealed class ClosingTab(TabItem tab, int slot, bool selected)
{
    public TabItem Tab { get; } = tab;

    /// <summary>It sits just before the open tab with this index.</summary>
    public int Slot { get; set; } = slot;

    public bool Selected { get; } = selected;

    /// <summary>0 as it starts to close, 1 when it is gone.</summary>
    public double Progress { get; set; }

    /// <summary>How much of a tab's room it still takes.</summary>
    public double Room => 1 - Motion.Ease(Easing.Drawer, Progress);

    /// <summary>How visible its contents still are: they fade out in the first half.</summary>
    public double Opacity => 1 - Motion.Ease(Easing.Standard, Math.Min(1, Progress * 2));
}

/// <summary>
/// The title bar: the app mark, the tabs, a new-tab button and the window buttons, all painted in one control.
/// Its empty space returns HTTRANSPARENT so the window treats it as caption (drag, double-click to maximize,
/// right-click for the system menu), and its top edge passes through so the window can still be resized from it.
/// </summary>
internal sealed class TitleBar : Panel, IItemHost
{
    const int TabMax = 230, TabMin = 110, HomeTabWidth = 36, TabHeight = 32, CloseSize = 16, ButtonWidth = 46, ResizeEdge = 6;
    const int CloseMs = 260;
    readonly List<TabItem> _tabs = [];
    readonly List<ClosingTab> _closing = [];
    TabItem? _pulsing;
    double _pulse;
    readonly ThemedTip _tip = new();
    int _tipTab = -1;
    long _tipHidden;
    CancellationTokenSource? _tipWait;
    int _selected, _hoverTab = -1, _hoverClose = -1, _hoverButton = -1, _pressedButton = -1;
    int _dragTab = -1, _dragStartX;
    string? _tipText;

    // The mark: it leans when pointed at, its front page hops on a click, and seven quick clicks spin it round.
    readonly Fade _markHover;
    double _riffle, _spin;
    readonly List<long> _markClicks = [];

    public TitleBar()
    {
        _markHover = new Fade(this, 180);
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Dock = DockStyle.Top;
        Height = Theme.TitleBarHeight;
        AccessibleRole = AccessibleRole.PageTabList;
        AccessibleName = "Open tabs";
        Theme.Changed += (_, _) => Invalidate();
    }

    public IReadOnlyList<TabItem> Tabs => _tabs;

    public int SelectedIndex => _selected;

    public TabItem? Selected => _selected < _tabs.Count ? _tabs[_selected] : null;

    public event EventHandler<TabItem>? TabSelected;
    public event EventHandler<TabItem>? CloseRequested;

    /// <summary>A tab was double-clicked: rename what it shows.</summary>
    public event EventHandler<TabItem>? RenameRequested;
    public event EventHandler? NewTabRequested;
    public event EventHandler? SettingsRequested;

    public void Add(TabItem tab, bool select)
    {
        tab.Grow = 0;
        _tabs.Add(tab);
        Motion.Run(180, progress => { tab.Grow = progress; Invalidate(); });
        if (select) Select(_tabs.Count - 1);
        else Invalidate();
    }

    /// <summary>Takes a tab off the strip; <paramref name="gone"/> runs once it has finished collapsing out of sight.</summary>
    public void Remove(TabItem tab, Action? gone = null)
    {
        int index = _tabs.IndexOf(tab);
        if (index < 0) return;
        bool wasSelected = index == _selected;
        _tabs.RemoveAt(index);
        if (index < _selected) _selected--;
        _selected = Math.Clamp(_selected, 0, Math.Max(0, _tabs.Count - 1));
        _hoverTab = _hoverClose = -1;
        if (Motion.Enabled && IsHandleCreated && Visible) Collapse(tab, index, wasSelected, gone);
        else gone?.Invoke();
        Invalidate();
        if (wasSelected && _tabs.Count > 0) TabSelected?.Invoke(this, _tabs[_selected]);
    }

    /// <summary>Leaves the closed tab in its place as a ghost that narrows to nothing while the tabs after it close up.</summary>
    void Collapse(TabItem tab, int index, bool selected, Action? gone)
    {
        foreach (ClosingTab other in _closing)
            if (other.Slot > index) other.Slot--;
        var ghost = new ClosingTab(tab, index, selected);
        _closing.Add(ghost);
        // It starts once the window has drawn the tab it switched to (Home takes a moment), so no frame is lost to that.
        void Start(object? sender, EventArgs e)
        {
            Application.Idle -= Start;
            Motion.Run(CloseMs, t =>
            {
                ghost.Progress = t;
                if (!IsDisposed) Invalidate();
            }, Easing.Linear, () =>
            {
                _closing.Remove(ghost);
                if (!IsDisposed) Invalidate();
                gone?.Invoke();
            });
        }
        Application.Idle += Start;
    }

    /// <summary>Lights a tab up for a moment, to show where something went.</summary>
    public void Pulse(int index)
    {
        if (index < 0 || index >= _tabs.Count) return;
        TabItem tab = _tabs[index];
        _pulsing = tab;
        Motion.Run(700, t =>
        {
            _pulse = 1 - t;
            if (t >= 1 && _pulsing == tab) _pulsing = null;
            Invalidate();
        }, Easing.Out);
    }

    public void Select(int index)
    {
        if (index < 0 || index >= _tabs.Count) return;
        _selected = index;
        Invalidate();
        ItemHostAccessible.ItemFocused(this, index);
        TabSelected?.Invoke(this, _tabs[index]);
    }

    public void Select(TabItem tab) => Select(_tabs.IndexOf(tab));

    public void RepaintTab() => Invalidate();

    protected override AccessibleObject CreateAccessibilityInstance() => new ItemHostAccessible(this, this, AccessibleRole.PageTab);

    int IItemHost.ItemCount => _tabs.Count;
    string IItemHost.ItemName(int index) => _tabs[index].IsDirty ? $"{_tabs[index].Title}, unsaved changes" : _tabs[index].Title;
    Rectangle IItemHost.ItemBounds(int index) => TabBounds()[index];
    bool IItemHost.IsItemSelected(int index) => index == _selected;
    int IItemHost.FocusedItem => _selected;
    void IItemHost.ActivateItem(int index) => Select(index);

    /// <summary>"Report.pdf, tab 2 of 3", for screen readers after a tab switch.</summary>
    public string Describe(int index) => $"{((IItemHost)this).ItemName(index)}, tab {index + 1} of {_tabs.Count}";

    // Layout ------------------------------------------------------------------------------------

    int Px(int logical) => LogicalToDeviceUnits(logical);

    Rectangle MarkBounds => new(Px(12), 0, Px(28), Height);

    Rectangle WindowButton(int i) => new(Width - Px(ButtonWidth) * (3 - i), 0, Px(ButtonWidth), Height);

    Rectangle SettingsButton => new(Width - Px(ButtonWidth) * 3 - Px(40), (Height - Px(28)) / 2, Px(28), Px(28));

    /// <summary>Where each open tab goes, where closing tabs still show, and where the strip ends.</summary>
    readonly record struct StripLayout(Rectangle[] Tabs, int TabWidth, (ClosingTab Ghost, Rectangle Bounds)[] Ghosts, int End);

    StripLayout LayOut()
    {
        int x = Px(46), top = Height - Px(TabHeight), gap = Px(2);
        int available = SettingsButton.Left - Px(44) - x;
        // A closing tab gives its room back as it narrows, so squeezed tabs widen smoothly rather than all at once.
        double unpinned = _tabs.Count(tab => !tab.IsPinned) + _closing.Sum(ghost => ghost.Room);
        int pinnedWidth = _tabs.Where(tab => tab.IsPinned).Sum(_ => Px(HomeTabWidth) + gap);
        int each = unpinned <= 0 ? 0 : Math.Clamp((int)((available - pinnedWidth) / unpinned) - gap, Px(TabMin), Px(TabMax));
        var tabs = new Rectangle[_tabs.Count];
        var ghosts = new List<(ClosingTab, Rectangle)>();
        for (int i = 0; i <= _tabs.Count; i++)
        {
            foreach (ClosingTab ghost in _closing.Where(ghost => Math.Min(ghost.Slot, _tabs.Count) == i))
            {
                int room = (int)Math.Round((each + gap) * ghost.Room);
                ghosts.Add((ghost, new Rectangle(x, top, Math.Max(0, room - gap), Px(TabHeight))));
                x += room;
            }
            if (i == _tabs.Count) break;
            int width = (int)((_tabs[i].IsPinned ? Px(HomeTabWidth) : each) * _tabs[i].Grow);
            tabs[i] = new Rectangle(x, top, width, Px(TabHeight));
            x += width + gap;
        }
        return new StripLayout(tabs, each, [.. ghosts], x);
    }

    Rectangle[] TabBounds() => LayOut().Tabs;

    Rectangle NewTabButton()
    {
        StripLayout strip = LayOut();
        int x = strip.Tabs.Length == 0 && strip.Ghosts.Length == 0 ? Px(46) : strip.End + Px(2);
        return new Rectangle(x, Height - Px(TabHeight) + Px(2), Px(28), Px(28));
    }

    Rectangle CloseBounds(Rectangle tab) => new(tab.Right - Px(8) - Px(CloseSize), tab.Y + (tab.Height - Px(CloseSize)) / 2, Px(CloseSize), Px(CloseSize));

    // Painting ----------------------------------------------------------------------------------

    protected override void OnPaint(PaintEventArgs e)
    {
        Palette p = Theme.Current;
        Graphics g = e.Graphics;
        g.Clear(p.Chrome);
        DrawMark(g);
        StripLayout strip = LayOut();
        foreach (var (ghost, bounds) in strip.Ghosts) DrawClosing(g, ghost, bounds, strip.TabWidth);
        for (int i = 0; i < _tabs.Count; i++)
        {
            Rectangle bounds = strip.Tabs[i];
            // Laid out at its full width and cut to the width it has so far, so a growing tab's title never re-wraps.
            var full = new Rectangle(bounds.X, bounds.Y, _tabs[i].IsPinned ? Px(HomeTabWidth) : strip.TabWidth, bounds.Height);
            DrawTab(g, _tabs[i], full, bounds, i == _selected, i == _hoverTab, i == _hoverClose);
        }
        DrawNewTab(g);
        DrawIconButton(g, SettingsButton, "settings", _hoverButton == 3);
        for (int i = 0; i < 3; i++) DrawWindowButton(g, i);
    }

    void DrawMark(Graphics g)
    {
        Rectangle box = MarkBounds;
        int size = Px(18);
        // Leaning like the mark in Settings; a riffle swings the back page out and lifts the front one for a moment.
        float tilt = (float)(-12 - 10 * _markHover.Value - 26 * _riffle), lift = (float)(2.5 * _riffle);
        Draw.AppMark(g, new Rectangle(box.X, (Height - size) / 2, size, size), tilt, lift, (float)(360 * _spin));
    }

    Rectangle MarkHitBounds => new(MarkBounds.X - Px(4), (Height - Px(28)) / 2, MarkBounds.Width + Px(4), Px(28));

    /// <summary>A click on the mark riffles its pages; the seventh within a few seconds sends it spinning, with a note.</summary>
    void PokeMark()
    {
        long now = Environment.TickCount64;
        _markClicks.RemoveAll(time => now - time > 3000);
        _markClicks.Add(now);
        if (_markClicks.Count >= 7)
        {
            _markClicks.Clear();
            Motion.Run(1100, t => { _spin = t; Invalidate(MarkHitBounds); }, Easing.Spring, () => { _spin = 0; Invalidate(MarkHitBounds); });
            _tip.ShowAt("You found the shuffle\nA sheaf is a bundle of pages tied together. That's all this is.", this, new Point(MarkBounds.X, Height + Px(4)));
            return;
        }
        // Out quickly, back with a little bounce.
        Motion.Run(140, t => { _riffle = t; Invalidate(MarkHitBounds); }, Easing.Out,
            () => Motion.Run(420, t => { _riffle = 1 - t; Invalidate(MarkHitBounds); }, Easing.Spring));
    }

    /// <summary>A closed tab on its way out: as it was, cut to its shrinking width, its contents fading into the bar.</summary>
    void DrawClosing(Graphics g, ClosingTab ghost, Rectangle bounds, int tabWidth)
    {
        if (bounds.Width < 1) return;
        var full = new Rectangle(bounds.X, bounds.Y, ghost.Tab.IsPinned ? Px(HomeTabWidth) : tabWidth, bounds.Height);
        DrawTab(g, ghost.Tab, full, bounds, ghost.Selected, hover: false, hoverClose: false, minimum: 1);
        using var veil = new SolidBrush(Color.FromArgb((int)Math.Round(255 * (1 - ghost.Opacity)), Theme.Current.Chrome));
        g.FillRectangle(veil, Rectangle.Inflate(bounds, 1, 0));
    }

    /// <summary>Draws a tab laid out in <paramref name="full"/> but only as much of it as <paramref name="visible"/> shows.</summary>
    void DrawTab(Graphics g, TabItem tab, Rectangle full, Rectangle visible, bool selected, bool hover, bool hoverClose, int minimum = 8)
    {
        if (visible.Width < Px(minimum)) return;
        Palette p = Theme.Current;
        Rectangle bounds = full;
        var state = g.Save();
        g.SetClip(visible);
        if (tab == _pulsing && _pulse > 0)
        {
            using var glow = TopRounded(new Rectangle(visible.X, visible.Y, Math.Max(visible.Width, Px(16)), visible.Height), Px(8));
            using var tint = new SolidBrush(Theme.Blend(selected ? p.Panel : p.Chrome, p.Accent, _pulse * 0.45));
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.FillPath(tint, glow);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
        }
        else if (selected || hover)
        {
            using var path = TopRounded(new Rectangle(visible.X, visible.Y, Math.Max(visible.Width, Px(16)), visible.Height), Px(8));
            using var brush = new SolidBrush(selected ? p.Panel : p.Raised);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.FillPath(brush, path);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
        }
        Color ink = selected ? p.Text : p.Text2;
        int icon = Px(16);
        if (tab.IsPinned)
        {
            Icons.Draw(g, tab.Icon, new Rectangle(bounds.X + (bounds.Width - icon) / 2, bounds.Y + (bounds.Height - icon) / 2, icon, icon), ink);
            g.Restore(state);
            return;
        }
        int x = bounds.X + Px(12);
        if (tab.Badge is { } badge)
        {
            var file = new Rectangle(x, bounds.Y + (bounds.Height - Px(16)) / 2, Px(13), Px(16));
            Draw.Box(g, file, Px(2), selected ? badge : Color.FromArgb(180, badge));
        }
        else
        {
            Icons.Draw(g, tab.Icon, new Rectangle(x, bounds.Y + (bounds.Height - Px(14)) / 2, Px(14), Px(14)), ink);
        }
        x += Px(22);
        Rectangle close = CloseBounds(bounds);
        var text = new Rectangle(x, bounds.Y, Math.Max(0, close.Left - Px(6) - x), bounds.Height);
        // GDI text ignores the clip unless told to keep it; without this a closing tab's title spills past its edge.
        Draw.Text(g, tab.Title, selected ? AppFonts.BodyMedium : AppFonts.Body, text, ink, Draw.Line | TextFormatFlags.PreserveGraphicsClipping);

        bool showClose = selected || hover || !tab.IsDirty;
        if (tab.IsDirty && !(hover && hoverClose))
        {
            int dot = Px(8);
            Draw.Box(g, new Rectangle(close.X + (close.Width - dot) / 2, close.Y + (close.Height - dot) / 2, dot, dot), dot / 2f, p.Text2);
        }
        else if (showClose)
        {
            if (hoverClose) Draw.Box(g, Rectangle.Inflate(close, Px(2), Px(2)), Px(4), p.Hover);
            Icons.Draw(g, "close", close, ink, 2f);
        }
        g.Restore(state);
    }

    void DrawNewTab(Graphics g)
    {
        Rectangle box = NewTabButton();
        DrawIconButton(g, box, "plus", _hoverButton == 4);
    }

    void DrawIconButton(Graphics g, Rectangle box, string icon, bool hover)
    {
        if (hover) Draw.Box(g, box, Px(6), Theme.Current.Hover);
        int size = Px(16);
        Icons.Draw(g, icon, new Rectangle(box.X + (box.Width - size) / 2, box.Y + (box.Height - size) / 2, size, size), hover ? Theme.Current.Text : Theme.Current.Text2);
    }

    void DrawWindowButton(Graphics g, int i)
    {
        Rectangle box = WindowButton(i);
        bool hover = _hoverButton == i, pressed = _pressedButton == i;
        if (hover || pressed)
        {
            Color fill = i == 2 ? (pressed ? Color.FromArgb(0xF1, 0x70, 0x7A) : Color.FromArgb(0xE8, 0x11, 0x23)) : pressed ? Theme.Current.Active : Theme.Current.Hover;
            using var brush = new SolidBrush(fill);
            g.FillRectangle(brush, box);
        }
        Color ink = hover && i == 2 ? Color.White : Theme.Current.Text2;
        int size = Px(10);
        int cx = box.X + (box.Width - size) / 2, cy = box.Y + (box.Height - size) / 2;
        using var pen = new Pen(ink, 1f);
        bool maximized = FindForm()?.WindowState == FormWindowState.Maximized;
        switch (i)
        {
            case 0:
                g.DrawLine(pen, cx, cy + size / 2, cx + size, cy + size / 2);
                break;
            case 1 when maximized:
                g.DrawRectangle(pen, cx, cy + Px(2), size - Px(2), size - Px(2));
                g.DrawLines(pen, [new Point(cx + Px(2), cy + Px(2)), new Point(cx + Px(2), cy), new Point(cx + size, cy), new Point(cx + size, cy + size - Px(2)), new Point(cx + size - Px(2), cy + size - Px(2))]);
                break;
            case 1:
                g.DrawRectangle(pen, cx, cy, size, size);
                break;
            default:
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.DrawLine(pen, cx, cy, cx + size, cy + size);
                g.DrawLine(pen, cx + size, cy, cx, cy + size);
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
                break;
        }
    }

    static System.Drawing.Drawing2D.GraphicsPath TopRounded(Rectangle r, int radius)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        int d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddLine(r.Right, r.Bottom, r.X, r.Bottom);
        path.CloseFigure();
        return path;
    }

    // Input -------------------------------------------------------------------------------------

    int HitTab(Point point)
    {
        Rectangle[] bounds = TabBounds();
        for (int i = 0; i < bounds.Length; i++) if (bounds[i].Contains(point)) return i;
        return -1;
    }

    int HitButton(Point point)
    {
        for (int i = 0; i < 3; i++) if (WindowButton(i).Contains(point)) return i;
        if (SettingsButton.Contains(point)) return 3;
        if (NewTabButton().Contains(point)) return 4;
        return -1;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_NCHITTEST)
        {
            Point point = PointToClient(new Point(unchecked((short)(long)m.LParam), unchecked((short)((long)m.LParam >> 16))));
            bool onSomething = HitTab(point) >= 0 || HitButton(point) >= 0 || MarkHitBounds.Contains(point);
            bool topEdge = point.Y < Px(ResizeEdge) && FindForm()?.WindowState != FormWindowState.Maximized;
            m.Result = (IntPtr)(onSomething && !topEdge ? Native.HTCLIENT : Native.HTTRANSPARENT);
            return;
        }
        base.WndProc(ref m);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragTab >= 0 && e.Button == MouseButtons.Left) DragTab(e.X);
        int tab = HitTab(e.Location), button = HitButton(e.Location);
        int close = tab >= 0 && !_tabs[tab].IsPinned && CloseBounds(TabBounds()[tab]).Contains(e.Location) ? tab : -1;
        bool onMark = MarkHitBounds.Contains(e.Location);
        if (onMark != _markHover.Value > 0.5) _markHover.To(onMark ? 1 : 0);
        Cursor = onMark ? Cursors.Hand : Cursors.Default;
        if (tab != _hoverTab || button != _hoverButton || close != _hoverClose)
        {
            _hoverTab = tab; _hoverButton = button; _hoverClose = close;
            Invalidate();
        }
        if (tab != _tipTab) _ = TipFor(tab);
        string? tip = tab >= 0 ? null : button switch { 0 => "Minimize", 1 => "Maximize", 2 => "Close", 3 => "Settings (Ctrl+,)", 4 => "New tab (Ctrl+T)", _ => null };
        if (tip != _tipText) { _tipText = tip; _tip.SetToolTip(this, tip); }
    }

    /// <summary>
    /// A tab's tip sits just under the tab, not wherever the pointer is: after a short wait the first time, and at
    /// once while moving from tab to tab.
    /// </summary>
    async Task TipFor(int tab)
    {
        _tipWait?.Cancel();
        if (_tipTab >= 0) { _tip.Hide(this); _tipHidden = Environment.TickCount64; }
        _tipTab = tab;
        if (tab < 0) return;
        var wait = _tipWait = new CancellationTokenSource();
        bool moving = Environment.TickCount64 - _tipHidden < 600;
        try { await Task.Delay(moving ? 60 : 550, wait.Token); }
        catch (TaskCanceledException) { return; }
        if (IsDisposed || _tipTab != tab || tab >= _tabs.Count) return;
        Rectangle bounds = TabBounds()[tab];
        TabItem item = _tabs[tab];
        string text = item.Detail is { Length: > 0 } detail ? $"{item.Title}\n{detail}" : item.Title;
        _tip.ShowAt(text, this, new Point(bounds.X, bounds.Bottom + Px(6)));
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hoverTab = _hoverButton = _hoverClose = -1;
        _markHover.To(0);
        Cursor = Cursors.Default;
        _ = TipFor(-1);
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        _ = TipFor(-1);
        if (e.Button == MouseButtons.Left && MarkHitBounds.Contains(e.Location)) { PokeMark(); return; }
        int button = HitButton(e.Location);
        if (button >= 0 && e.Button == MouseButtons.Left) { _pressedButton = button; Invalidate(); return; }
        int tab = HitTab(e.Location);
        if (tab < 0) return;
        if (e.Button == MouseButtons.Middle && !_tabs[tab].IsPinned) { CloseRequested?.Invoke(this, _tabs[tab]); return; }
        if (e.Button != MouseButtons.Left) return;
        if (!_tabs[tab].IsPinned && CloseBounds(TabBounds()[tab]).Contains(e.Location)) { CloseRequested?.Invoke(this, _tabs[tab]); return; }
        Select(tab);
        if (e.Clicks == 2 && !_tabs[tab].IsPinned)
        {
            _dragTab = -1;
            RenameRequested?.Invoke(this, _tabs[tab]);
            return;
        }
        if (!_tabs[tab].IsPinned) { _dragTab = tab; _dragStartX = e.X; }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragTab = -1;
        int pressed = _pressedButton;
        _pressedButton = -1;
        Invalidate();
        if (pressed < 0 || HitButton(e.Location) != pressed) return;
        Form? form = FindForm();
        switch (pressed)
        {
            case 0: if (form is not null) form.WindowState = FormWindowState.Minimized; break;
            case 1: if (form is not null) form.WindowState = form.WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized; break;
            case 2: form?.Close(); break;
            case 3: SettingsRequested?.Invoke(this, EventArgs.Empty); break;
            case 4: NewTabRequested?.Invoke(this, EventArgs.Empty); break;
        }
    }

    /// <summary>Dragging a tab past half of its neighbour swaps them.</summary>
    void DragTab(int x)
    {
        Rectangle[] bounds = TabBounds();
        int delta = x - _dragStartX;
        int target = _dragTab + (delta > 0 && _dragTab + 1 < _tabs.Count && x > bounds[_dragTab + 1].X + bounds[_dragTab + 1].Width / 2 ? 1
            : delta < 0 && _dragTab - 1 >= 0 && !_tabs[_dragTab - 1].IsPinned && x < bounds[_dragTab - 1].Right - bounds[_dragTab - 1].Width / 2 ? -1 : 0);
        if (target == _dragTab) return;
        (_tabs[_dragTab], _tabs[target]) = (_tabs[target], _tabs[_dragTab]);
        if (_selected == _dragTab) _selected = target;
        else if (_selected == target) _selected = _dragTab;
        _dragTab = target;
        _dragStartX = x;
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tip.Dispose();
        base.Dispose(disposing);
    }
}
