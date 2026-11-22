using Sheaf.Ui;
using Sheaf.Ui.Controls;

namespace Sheaf.Pages.Scan;

/// <summary>The scanned pages as numbered thumbnails, two to a row. Click to select; drag to reorder.</summary>
internal sealed class ScanStrip : Panel, IItemHost
{
    const int Columns = 2, Gap = 10, LabelHeight = 20;
    IReadOnlyList<ScannedPage> _pages = [];
    int _hover = -1, _pressed = -1, _drop = -1;
    Point _press;
    bool _dragging;

    public ScanStrip()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        AutoScroll = true;
        TabStop = true;
        AccessibleRole = AccessibleRole.List;
        AccessibleName = "Scanned pages";
        Theme.Changed += OnThemeChanged;
    }

    public event EventHandler? SelectionChanged;

    /// <summary>A page was dragged: from its index to the gap before index.</summary>
    public event EventHandler<(int From, int To)>? MoveRequested;

    public event EventHandler<Point>? MenuRequested;

    public int Selected { get; private set; } = -1;

    public override Color BackColor
    {
        get => Theme.Current.Panel;
        set { }
    }

    public void SetPages(IReadOnlyList<ScannedPage> pages, int selected)
    {
        _pages = pages;
        Selected = selected;
        int rows = (pages.Count + Columns - 1) / Columns;
        AutoScrollMinSize = new Size(0, rows * RowHeight + LogicalToDeviceUnits(Gap));
        Invalidate();
    }

    public void Select(int index)
    {
        if (index == Selected) return;
        Selected = index;
        if (index >= 0) Reveal(index);
        Invalidate();
        ItemHostAccessible.ItemFocused(this, index);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    int CellWidth => (ClientSize.Width - LogicalToDeviceUnits(Gap) * (Columns + 1)) / Columns;

    int RowHeight => (int)(CellWidth * 1.41f) + LogicalToDeviceUnits(LabelHeight + Gap);

    Rectangle Cell(int index)
    {
        int gap = LogicalToDeviceUnits(Gap);
        return new Rectangle(gap + index % Columns * (CellWidth + gap), gap + index / Columns * RowHeight + AutoScrollPosition.Y, CellWidth, RowHeight - gap);
    }

    void Reveal(int index)
    {
        Rectangle cell = Cell(index);
        if (cell.Top < 0) AutoScrollPosition = new Point(0, -AutoScrollPosition.Y + cell.Top);
        else if (cell.Bottom > ClientSize.Height) AutoScrollPosition = new Point(0, -AutoScrollPosition.Y + cell.Bottom - ClientSize.Height);
    }

    int IndexAt(Point point)
    {
        for (int i = 0; i < _pages.Count; i++)
            if (Cell(i).Contains(point)) return i;
        return -1;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        Palette p = Theme.Current;
        g.Clear(BackColor);
        for (int i = 0; i < _pages.Count; i++)
        {
            Rectangle cell = Cell(i);
            if (cell.Bottom < 0 || cell.Top > ClientSize.Height) continue;
            bool selected = i == Selected;
            var box = new Rectangle(cell.X, cell.Y, cell.Width, cell.Height - LogicalToDeviceUnits(LabelHeight));
            if (selected || i == _hover) Draw.Box(g, Rectangle.Inflate(box, LogicalToDeviceUnits(4), LogicalToDeviceUnits(4)), LogicalToDeviceUnits(Theme.RadiusLarge), selected ? p.AccentSoft : p.Hover);
            if (_pages[i].Thumbnail is { } thumb)
            {
                float scale = Math.Min(box.Width / (float)thumb.Width, box.Height / (float)thumb.Height);
                var image = new RectangleF(box.X + (box.Width - thumb.Width * scale) / 2, box.Y + (box.Height - thumb.Height * scale) / 2, thumb.Width * scale, thumb.Height * scale);
                g.DrawImage(thumb, image);
                using var edge = new Pen(selected ? p.Accent : p.Line2, selected ? LogicalToDeviceUnits(2) : 1);
                g.DrawRectangle(edge, image.X, image.Y, image.Width, image.Height);
            }
            else
            {
                Draw.Box(g, Rectangle.Inflate(box, -LogicalToDeviceUnits(10), -LogicalToDeviceUnits(14)), LogicalToDeviceUnits(Theme.Radius), p.Raised);
                Draw.Text(g, "Working…", AppFonts.Caption, box, p.Text3, Draw.Centered);
            }
            var label = new Rectangle(cell.X, box.Bottom + LogicalToDeviceUnits(4), cell.Width, LogicalToDeviceUnits(LabelHeight - 4));
            Draw.Text(g, (i + 1).ToString(System.Globalization.CultureInfo.CurrentCulture), AppFonts.MonoSmall, label, selected ? p.Text : p.Text3, Draw.Centered);
        }
        if (_dragging && _drop >= 0)
        {
            Rectangle at = _drop < _pages.Count ? Cell(_drop) : Cell(_pages.Count - 1);
            int x = _drop < _pages.Count ? at.Left - LogicalToDeviceUnits(Gap) / 2 : at.Right + LogicalToDeviceUnits(Gap) / 2;
            using var marker = new SolidBrush(p.Accent);
            g.FillRectangle(marker, x - LogicalToDeviceUnits(2), at.Top, LogicalToDeviceUnits(3), at.Height);
        }
        if (Focused && ShowFocusCues && Selected >= 0 && Selected < _pages.Count)
            Draw.FocusRing(g, Rectangle.Inflate(Cell(Selected), LogicalToDeviceUnits(4), LogicalToDeviceUnits(4)), LogicalToDeviceUnits(Theme.RadiusLarge), LogicalToDeviceUnits(2));
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        int index = IndexAt(e.Location);
        if (index < 0) return;
        Select(index);
        if (e.Button == MouseButtons.Right) { MenuRequested?.Invoke(this, e.Location); return; }
        _pressed = index;
        _press = e.Location;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_pressed >= 0)
        {
            if (!_dragging && (Math.Abs(e.X - _press.X) > SystemInformation.DragSize.Width || Math.Abs(e.Y - _press.Y) > SystemInformation.DragSize.Height)) _dragging = true;
            if (_dragging)
            {
                int over = IndexAt(e.Location);
                _drop = over < 0 ? _pages.Count : e.X > Cell(over).Left + Cell(over).Width / 2 ? over + 1 : over;
                Invalidate();
                return;
            }
        }
        int hover = IndexAt(e.Location);
        if (hover != _hover) { _hover = hover; Invalidate(); }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_dragging && _drop >= 0 && _pressed >= 0 && _drop != _pressed && _drop != _pressed + 1) MoveRequested?.Invoke(this, (_pressed, _drop));
        _dragging = false;
        _pressed = _drop = -1;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = -1; Invalidate(); }

    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_pages.Count == 0) return;
        int next = e.KeyCode switch
        {
            Keys.Left => Selected - 1,
            Keys.Right => Selected + 1,
            Keys.Up => Selected - Columns,
            Keys.Down => Selected + Columns,
            _ => int.MinValue,
        };
        if (next == int.MinValue) return;
        Select(Math.Clamp(next, 0, _pages.Count - 1));
        e.Handled = true;
    }

    protected override void OnScroll(ScrollEventArgs se) { base.OnScroll(se); Invalidate(); }
    protected override AccessibleObject CreateAccessibilityInstance() => new ItemHostAccessible(this, this, AccessibleRole.ListItem);

    int IItemHost.ItemCount => _pages.Count;
    string IItemHost.ItemName(int index) => _pages[index].IsReady ? $"Page {index + 1}" : $"Page {index + 1}, still being cleaned up";
    Rectangle IItemHost.ItemBounds(int index) => Cell(index);
    bool IItemHost.IsItemSelected(int index) => index == Selected;
    int IItemHost.FocusedItem => Selected;
    void IItemHost.ActivateItem(int index) => Select(index);

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); ItemHostAccessible.ItemFocused(this, Selected); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Platform.Native.SetScrollbarTheme(Handle, Theme.IsDark);
    }

    void OnThemeChanged(object? sender, EventArgs e)
    {
        if (IsHandleCreated) Platform.Native.SetScrollbarTheme(Handle, Theme.IsDark);
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Theme.Changed -= OnThemeChanged;
        base.Dispose(disposing);
    }
}
