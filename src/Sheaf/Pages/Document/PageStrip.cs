using Sheaf.Core.Editing;
using Sheaf.Ui;
using Sheaf.Ui.Controls;

namespace Sheaf.Pages.Document;

/// <summary>
/// Page thumbnails with their numbers: one column beside the canvas, or a grid in Pages mode. Click selects
/// (Ctrl and Shift add), dragging moves the selected pages, and a right click opens the page menu.
/// </summary>
internal sealed class PageStrip : Panel, IItemHost
{
    const int Pad = 12, LabelHeight = 22, Gap = 8;
    readonly HashSet<int> _selected = [];
    // A box dragged across empty space chooses the pages it touches; its start is in scrolled content coordinates.
    Point? _bandStart;
    Rectangle? _band;
    HashSet<int> _bandKept = [];
    EditDocument? _document;
    PageRenderer? _renderer;
    int _anchor, _focus, _hover = -1, _pressIndex = -1, _dropIndex = -1, _thumbWidth;
    Point _press;
    bool _dragging;

    public PageStrip(int thumbWidth)
    {
        _thumbWidth = thumbWidth;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        AutoScroll = true;
        TabStop = true;
        AllowDrop = false;
        AccessibleRole = AccessibleRole.List;
        AccessibleName = "Pages";
        Theme.Changed += OnThemeChanged;
    }

    /// <summary>A page was clicked: show it.</summary>
    public event EventHandler<int>? PageActivated;

    /// <summary>Pages were opened with a double click (Pages mode switches to the editor).</summary>
    public event EventHandler<int>? PageOpened;

    public event EventHandler? SelectionChanged;

    /// <summary>The selected pages were dragged to a new place: the index they go before, in the current order.</summary>
    public event EventHandler<int>? MoveRequested;

    public event EventHandler<Point>? MenuRequested;

    public event EventHandler? DeleteRequested;

    /// <summary>Logical width of a thumbnail.</summary>
    public int ThumbWidth
    {
        get => _thumbWidth;
        set { _thumbWidth = value; Relayout(); }
    }

    /// <summary>Selected pages in order; the current page when nothing is selected.</summary>
    public IReadOnlyList<int> SelectedPages => [.. _selected.Order()];

    public void Load(EditDocument document, PageRenderer renderer)
    {
        _document = document;
        _renderer = renderer;
        _selected.RemoveWhere(i => i >= document.Pages.Count);
        _anchor = Math.Min(_anchor, Math.Max(0, document.Pages.Count - 1));
        _focus = Math.Min(_focus, Math.Max(0, document.Pages.Count - 1));
        Relayout();
    }

    /// <summary>Marks the page the canvas shows, and scrolls it into view.</summary>
    public void ShowCurrent(int page)
    {
        if (_document is null || page < 0 || page >= _document.Pages.Count) return;
        if (_selected.Count <= 1 && !(_selected.Count == 1 && _selected.Contains(page)))
        {
            _selected.Clear();
            _selected.Add(page);
            _anchor = _focus = page;
        }
        Reveal(page);
        Invalidate();
    }

    public void SelectPages(IEnumerable<int> pages)
    {
        _selected.Clear();
        foreach (int page in pages) _selected.Add(page);
        if (_selected.Count > 0) _anchor = _focus = _selected.Min();
        Invalidate();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    // Layout ------------------------------------------------------------------------------------

    int Px(int logical) => LogicalToDeviceUnits(logical);

    int CellWidth => Px(_thumbWidth) + 2 * Px(Gap);

    int Columns => Math.Max(1, (ClientSize.Width - 2 * Px(Pad) + Px(Gap)) / CellWidth);

    int RowHeight => (int)(Px(_thumbWidth) * 1.42f) + Px(LabelHeight) + 2 * Px(Gap);

    void Relayout()
    {
        int count = _document?.Pages.Count ?? 0;
        int rows = (count + Columns - 1) / Columns;
        AutoScrollMinSize = new Size(0, rows * RowHeight + 2 * Px(Pad));
        Invalidate();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Relayout();
    }

    protected override void OnScroll(ScrollEventArgs se)
    {
        base.OnScroll(se);
        Invalidate();
    }

    /// <summary>The cell of a page in client coordinates.</summary>
    Rectangle Cell(int index)
    {
        int columns = Columns;
        int used = columns * CellWidth;
        int left = Math.Max(Px(Pad), (ClientSize.Width - used) / 2);
        return new Rectangle(left + index % columns * CellWidth, Px(Pad) + index / columns * RowHeight + AutoScrollPosition.Y, CellWidth, RowHeight);
    }

    /// <summary>Where the thumbnail of a page sits inside its cell: fitted, centred, bottom-aligned above the number.</summary>
    RectangleF ThumbRect(int index)
    {
        Rectangle cell = Cell(index);
        SizeF size = PagePainter.DisplaySize(_document!.Pages[index]);
        float boxWidth = Px(_thumbWidth), boxHeight = cell.Height - Px(LabelHeight) - 2 * Px(Gap);
        float scale = Math.Min(boxWidth / size.Width, boxHeight / size.Height);
        float width = size.Width * scale, height = size.Height * scale;
        return new RectangleF(cell.X + (cell.Width - width) / 2, cell.Y + Px(Gap) + boxHeight - height, width, height);
    }

    int IndexAt(Point point)
    {
        if (_document is null) return -1;
        for (int i = 0; i < _document.Pages.Count; i++)
            if (Cell(i).Contains(point)) return i;
        return -1;
    }

    /// <summary>The gap a dragged page would land in: the index it goes before.</summary>
    int DropIndexAt(Point point)
    {
        if (_document is null) return -1;
        int best = _document.Pages.Count;
        double bestDistance = double.MaxValue;
        for (int i = 0; i <= _document.Pages.Count; i++)
        {
            Rectangle cell = i < _document.Pages.Count ? Cell(i) : Cell(i - 1) with { X = Cell(i - 1).Right };
            if (i == _document.Pages.Count && Columns == 1) cell = Cell(i - 1) with { Y = Cell(i - 1).Bottom };
            Point gap = Columns == 1 ? new Point(cell.X + cell.Width / 2, cell.Y) : new Point(cell.X, cell.Y + cell.Height / 2);
            double distance = Math.Pow(point.X - gap.X, 2) + Math.Pow(point.Y - gap.Y, 2);
            if (distance < bestDistance) { bestDistance = distance; best = i; }
        }
        return best;
    }

    void Reveal(int index)
    {
        Rectangle cell = Cell(index);
        if (cell.Top < 0) AutoScrollPosition = new Point(0, -AutoScrollPosition.Y + cell.Top - Px(Pad));
        else if (cell.Bottom > ClientSize.Height) AutoScrollPosition = new Point(0, -AutoScrollPosition.Y + cell.Bottom - ClientSize.Height + Px(Pad));
    }

    // Painting ----------------------------------------------------------------------------------

    protected override void OnPaint(PaintEventArgs e)
    {
        Palette p = Theme.Current;
        Graphics g = e.Graphics;
        g.Clear(BackColor);
        if (_document is null || _renderer is null) return;
        for (int i = 0; i < _document.Pages.Count; i++)
        {
            Rectangle cell = Cell(i);
            if (cell.Bottom < 0 || cell.Top > ClientSize.Height) continue;
            PageEntry entry = _document.Pages[i];
            RectangleF thumb = ThumbRect(i);
            bool selected = _selected.Contains(i);
            if (i == _hover || selected)
                Draw.Box(g, Rectangle.Inflate(cell, -Px(2), -Px(2)), Px(Theme.RadiusLarge), selected ? p.AccentSoft : p.Hover);
            PagePainter.DrawPage(g, entry, _renderer.Thumbnail(entry), thumb);
            using (var edge = new Pen(selected ? p.Accent : p.Line2, selected ? Px(2) : 1))
                g.DrawRectangle(edge, thumb.X, thumb.Y, thumb.Width, thumb.Height);
            var label = new Rectangle(cell.X, (int)thumb.Bottom + Px(4), cell.Width, Px(LabelHeight) - Px(4));
            string number = (i + 1).ToString(System.Globalization.CultureInfo.CurrentCulture);
            Draw.Text(g, number, AppFonts.MonoSmall, label, selected ? p.Text : p.Text3, Draw.Centered);
            int badge = cell.Right - Px(Gap) - Px(16);
            if (entry.Rotation != 0)
            {
                Icons.Draw(g, "rotate", new Rectangle(badge, label.Y + Px(2), Px(14), Px(14)), p.Text3);
                badge -= Px(18);
            }
            if (entry.IsMirrored)
                Icons.Draw(g, entry.MirrorX ? "mirror" : "flip-vertical", new Rectangle(badge, label.Y + Px(2), Px(14), Px(14)), p.Text3);
        }
        if (_dragging && _dropIndex >= 0) DrawDropMarker(g);
        if (_band is { } band)
        {
            using var fill = new SolidBrush(Color.FromArgb(30, p.Accent));
            using var edge = new Pen(p.Accent);
            g.FillRectangle(fill, band);
            g.DrawRectangle(edge, band);
        }
        if (Focused && ShowFocusCues && _focus < _document.Pages.Count)
            Draw.FocusRing(g, Rectangle.Inflate(Cell(_focus), -Px(1), -Px(1)), Px(Theme.RadiusLarge), Px(2));
    }

    void DrawDropMarker(Graphics g)
    {
        int count = _document!.Pages.Count;
        Rectangle reference = _dropIndex < count ? Cell(_dropIndex) : Cell(count - 1);
        using var brush = new SolidBrush(Theme.Current.Accent);
        int thickness = Px(3);
        if (Columns == 1)
        {
            int y = _dropIndex < count ? reference.Top : reference.Bottom;
            g.FillRectangle(brush, reference.X + Px(Gap), y - thickness / 2, reference.Width - 2 * Px(Gap), thickness);
        }
        else
        {
            int x = _dropIndex < count ? reference.Left : reference.Right;
            g.FillRectangle(brush, x - thickness / 2, reference.Y + Px(Gap), thickness, reference.Height - 2 * Px(Gap));
        }
    }

    public override Color BackColor
    {
        get => Theme.Current.Panel;
        set { }
    }

    // Mouse and keyboard ------------------------------------------------------------------------

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        int index = IndexAt(e.Location);
        if (index < 0)
        {
            if (e.Button != MouseButtons.Left) return;
            // Empty space: a click lets go of every page, a drag draws a box that chooses pages.
            _bandKept = ModifierKeys.HasFlag(Keys.Control) ? [.. _selected] : [];
            if (_bandKept.Count == 0 && _selected.Count > 0)
            {
                _selected.Clear();
                Invalidate();
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
            _bandStart = new Point(e.X, e.Y - AutoScrollPosition.Y);
            return;
        }
        if (e.Button == MouseButtons.Right)
        {
            if (!_selected.Contains(index)) ClickPage(index, Keys.None);
            MenuRequested?.Invoke(this, e.Location);
            return;
        }
        if (e.Button != MouseButtons.Left) return;
        if (e.Clicks == 2) { PageOpened?.Invoke(this, index); return; }
        _press = e.Location;
        _pressIndex = index;
        if (!_selected.Contains(index) || ModifierKeys != Keys.None) ClickPage(index, ModifierKeys);
    }

    void ClickPage(int index, Keys modifiers)
    {
        if (modifiers.HasFlag(Keys.Shift))
        {
            _selected.Clear();
            for (int i = Math.Min(_anchor, index); i <= Math.Max(_anchor, index); i++) _selected.Add(i);
            _focus = index;
        }
        else if (modifiers.HasFlag(Keys.Control))
        {
            if (!_selected.Remove(index)) _selected.Add(index);
            _anchor = _focus = index;
        }
        else
        {
            _selected.Clear();
            _selected.Add(index);
            _anchor = _focus = index;
        }
        Invalidate();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        ItemHostAccessible.ItemFocused(this, _focus);
        if (!modifiers.HasFlag(Keys.Control) && !modifiers.HasFlag(Keys.Shift)) PageActivated?.Invoke(this, index);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_bandStart is { } start && e.Button == MouseButtons.Left)
        {
            DragBand(start, e.Location);
            return;
        }
        if (_pressIndex >= 0)
        {
            if (!_dragging && (Math.Abs(e.X - _press.X) > SystemInformation.DragSize.Width || Math.Abs(e.Y - _press.Y) > SystemInformation.DragSize.Height))
                _dragging = true;
            if (_dragging)
            {
                _dropIndex = DropIndexAt(e.Location);
                if (e.Y < Px(Pad)) AutoScrollPosition = new Point(0, -AutoScrollPosition.Y - Px(16));
                else if (e.Y > ClientSize.Height - Px(Pad)) AutoScrollPosition = new Point(0, -AutoScrollPosition.Y + Px(16));
                Cursor = Cursors.SizeAll;
                Invalidate();
                return;
            }
        }
        int hover = IndexAt(e.Location);
        if (hover != _hover) { _hover = hover; Invalidate(); }
    }

    void DragBand(Point start, Point at)
    {
        if (e_InEdge(at)) AutoScrollPosition = new Point(0, -AutoScrollPosition.Y + (at.Y < Px(Pad) ? -Px(16) : Px(16)));
        var top = new Point(start.X, start.Y + AutoScrollPosition.Y);
        _band = Rectangle.FromLTRB(Math.Min(top.X, at.X), Math.Min(top.Y, at.Y), Math.Max(top.X, at.X), Math.Max(top.Y, at.Y));
        _selected.Clear();
        _selected.UnionWith(_bandKept);
        for (int i = 0; i < (_document?.Pages.Count ?? 0); i++)
            if (Cell(i).IntersectsWith(_band.Value)) _selected.Add(i);
        if (_selected.Count > 0) _anchor = _focus = _selected.Min();
        Invalidate();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    bool e_InEdge(Point at) => at.Y < Px(Pad) || at.Y > ClientSize.Height - Px(Pad);

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_bandStart is not null)
        {
            _bandStart = null;
            _band = null;
            Invalidate();
            return;
        }
        bool dropped = _dragging && _dropIndex >= 0;
        int target = _dropIndex, pressed = _pressIndex;
        _dragging = false;
        _pressIndex = _dropIndex = -1;
        Cursor = Cursors.Default;
        Invalidate();
        if (dropped) MoveRequested?.Invoke(this, target);
        else if (pressed >= 0 && ModifierKeys == Keys.None && _selected.Count > 1) ClickPage(pressed, Keys.None);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = -1;
        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        Invalidate();
    }

    protected override bool IsInputKey(Keys keyData) =>
        (keyData & Keys.KeyCode) is Keys.Up or Keys.Down or Keys.Left or Keys.Right or Keys.Home or Keys.End || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_document is null || _document.Pages.Count == 0) return;
        int count = _document.Pages.Count, columns = Columns, next = _focus;
        switch (e.KeyCode)
        {
            case Keys.Up: next -= columns; break;
            case Keys.Down: next += columns; break;
            case Keys.Left: next -= 1; break;
            case Keys.Right: next += 1; break;
            case Keys.Home: next = 0; break;
            case Keys.End: next = count - 1; break;
            case Keys.Delete: DeleteRequested?.Invoke(this, EventArgs.Empty); e.Handled = true; return;
            case Keys.Enter: PageOpened?.Invoke(this, _focus); e.Handled = true; return;
            case Keys.Apps: MenuRequested?.Invoke(this, Cell(_focus).Location + new Size(Px(24), Px(24))); e.Handled = true; return;
            case Keys.A when e.Control: SelectPages(Enumerable.Range(0, count)); e.Handled = true; return;
            default: return;
        }
        e.Handled = true;
        next = Math.Clamp(next, 0, count - 1);
        ClickPage(next, e.Shift ? Keys.Shift : Keys.None);
        Reveal(next);
    }

    // Screen readers ---------------------------------------------------------------------------

    protected override AccessibleObject CreateAccessibilityInstance() => new ItemHostAccessible(this, this, AccessibleRole.ListItem);

    int IItemHost.ItemCount => _document?.Pages.Count ?? 0;

    string IItemHost.ItemName(int index)
    {
        PageEntry entry = _document!.Pages[index];
        string turned = (entry.Rotation == 0 ? "" : $", turned {entry.Rotation}°")
            + (entry.MirrorX ? ", mirrored" : "") + (entry.MirrorY ? ", upside down" : "");
        string blank = entry.IsBlank ? ", blank" : "";
        int added = entry.Objects.Count;
        return $"Page {index + 1}{blank}{turned}{(added == 0 ? "" : $", {Core.Words.Count(added, "addition", "additions")}")}";
    }

    Rectangle IItemHost.ItemBounds(int index) => Cell(index);

    bool IItemHost.IsItemSelected(int index) => _selected.Contains(index);

    int IItemHost.FocusedItem => _document is null ? -1 : _focus;

    void IItemHost.ActivateItem(int index) => ClickPage(index, Keys.None);

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); ItemHostAccessible.ItemFocused(this, _focus); }
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
