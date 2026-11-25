using Sheaf.Core.Editing;
using Sheaf.Ui;
using Sheaf.Ui.Controls;

namespace Sheaf.Pages.Document;

/// <summary>
/// One row of an <see cref="ObjectList"/>: an object and the page it is on. A group's row lists its
/// <see cref="Members"/> (its <see cref="Item"/> is the top one); rows of objects inside a group are indented.
/// </summary>
internal sealed record ObjectRow(int Page, PageObject Item, string Title, string Detail, IReadOnlyList<PageObject>? Members = null, bool Indented = false)
{
    public IReadOnlyList<PageObject> Objects => Members ?? [Item];
}

/// <summary>
/// A list of objects (layers, comments, redaction marks): an icon in the object's colour, a name and an
/// optional second line, and one action button at the end of each row (show/hide, or remove). With
/// <see cref="MultiSelect"/> it chooses like File Explorer: Ctrl+click adds or takes out a row, Shift+click (or
/// Shift with the arrow keys) takes a run, Ctrl+A takes all, and dragging from the empty space below the rows
/// takes what the box crosses. With <see cref="Reorderable"/>, dragging chosen rows moves them: between rows, or
/// onto a group's row to put them in it.
/// </summary>
internal sealed class ObjectList : Panel, IItemHost
{
    const int IconSize = 16;
    List<ObjectRow> _rows = [];
    HashSet<Guid> _selected = [];
    int _hover = -1, _anchor = -1, _focus = -1;
    bool _hoverAction, _hoverLock;

    // A press on a row may become a drag; one on the empty space may become a selection box.
    int _pressIndex = -1;
    Point _pressAt;
    bool _leftDown, _dragging, _collapseOnRelease;
    int _dropBefore = -1;
    ObjectRow? _dropInto;
    Rectangle? _band;
    bool _banding;
    HashSet<Guid> _bandKept = [];

    public ObjectList()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        AutoScroll = true;
        TabStop = true;
        AccessibleRole = AccessibleRole.List;
        Theme.Changed += OnThemeChanged;
    }

    public event EventHandler<ObjectRow>? Picked;
    public event EventHandler<ObjectRow>? ActionClicked;

    /// <summary>The lock beside the eye was clicked (layer lists only).</summary>
    public event EventHandler<ObjectRow>? LockClicked;

    /// <summary>With <see cref="MultiSelect"/>: the objects now chosen, after a Ctrl or Shift click.</summary>
    public event EventHandler<IReadOnlyList<PageObject>>? PickedMany;

    /// <summary>A click on the list below its rows: nothing should stay chosen.</summary>
    public event EventHandler? EmptyClicked;

    /// <summary>A right-click on a row, at a point of the list, after choosing the row if it wasn't.</summary>
    public event EventHandler<(ObjectRow Row, Point At)>? MenuRequested;

    public bool MultiSelect { get; init; }

    /// <summary>Rows can be dragged to a new place (layers).</summary>
    public bool Reorderable { get; init; }

    /// <summary>Chosen rows were dropped: before the row with this index (the count for the end), or into a group's row.</summary>
    public event EventHandler<(IReadOnlyList<ObjectRow> Rows, int Before, ObjectRow? Into)>? Dropped;

    /// <summary>Delete was pressed in a list whose action isn't removing (layers): delete what is chosen.</summary>
    public event EventHandler? DeleteRequested;

    public IReadOnlyList<ObjectRow> Rows => _rows;

    /// <summary>Whether a group's row shows its objects under it; null when groups can't fold.</summary>
    public Func<ObjectRow, bool>? IsOpen { get; init; }

    /// <summary>A group's arrow was clicked (or Left or Right pressed on it): fold or unfold it.</summary>
    public event EventHandler<ObjectRow>? FoldToggled;

    Rectangle FoldBounds(Rectangle row) => new(row.X + LogicalToDeviceUnits(12), row.Y + (row.Height - LogicalToDeviceUnits(16)) / 2, LogicalToDeviceUnits(16), LogicalToDeviceUnits(16));

    /// <summary>"eye" for show/hide, "close" for remove, or null for no action.</summary>
    public string? Action { get; init; }

    /// <summary>Text shown when the list is empty.</summary>
    public string EmptyText { get; set; } = "";

    bool TwoLines => _rows.Any(row => row.Detail.Length > 0);

    bool IsSelected(ObjectRow row) => row.Objects.All(item => _selected.Contains(item.Id));

    int Current => _rows.FindIndex(IsSelected);

    int RowHeight => LogicalToDeviceUnits(TwoLines ? 48 : 34);

    public void SetRows(IEnumerable<ObjectRow> rows, IEnumerable<Guid> selected)
    {
        _rows = [.. rows];
        _selected = [.. selected];
        AutoScrollMinSize = new Size(0, _rows.Count * RowHeight + LogicalToDeviceUnits(8));
        AccessibleDescription = _rows.Count == 0 ? EmptyText : null;
        AccessibilityNotifyClients(AccessibleEvents.Reorder, -1);
        Invalidate();
    }

    public override Color BackColor
    {
        get => Theme.Current.Panel;
        set { }
    }

    Rectangle RowBounds(int index) => new(0, LogicalToDeviceUnits(4) + index * RowHeight + AutoScrollPosition.Y, ClientSize.Width, RowHeight);

    Rectangle ActionBounds(Rectangle row)
    {
        int size = LogicalToDeviceUnits(28);
        return new Rectangle(row.Right - size - LogicalToDeviceUnits(8), row.Y + (row.Height - size) / 2, size, size);
    }

    /// <summary>A layer list has a lock beside each eye.</summary>
    bool HasLock => Action == "eye" && LockClicked is not null;

    Rectangle LockBounds(Rectangle row)
    {
        Rectangle eye = ActionBounds(row);
        eye.Offset(-eye.Width - LogicalToDeviceUnits(2), 0);
        return eye;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Palette p = Theme.Current;
        Graphics g = e.Graphics;
        g.Clear(BackColor);
        if (_rows.Count == 0)
        {
            var empty = new Rectangle(LogicalToDeviceUnits(16), LogicalToDeviceUnits(8), ClientSize.Width - LogicalToDeviceUnits(32), LogicalToDeviceUnits(80));
            Draw.Text(g, EmptyText, AppFonts.Small, empty, p.Text3, Draw.Wrapped);
            return;
        }
        int icon = LogicalToDeviceUnits(IconSize), inset = LogicalToDeviceUnits(8);
        for (int i = 0; i < _rows.Count; i++)
        {
            Rectangle row = RowBounds(i);
            if (row.Bottom < 0 || row.Top > ClientSize.Height) continue;
            ObjectRow item = _rows[i];
            bool selected = IsSelected(item);
            bool hidden = item.Objects.All(member => member.Hidden);
            Rectangle box = Rectangle.Inflate(row, -inset, -LogicalToDeviceUnits(1));
            if (selected || i == _hover) Draw.Box(g, box, LogicalToDeviceUnits(Theme.Radius), selected ? p.AccentSoft : p.Hover);
            Color ink = hidden ? p.Text3 : p.Text;
            int x = box.X + LogicalToDeviceUnits(item.Indented ? 26 : 10);
            if (item.Members is not null && IsOpen is not null)
            {
                // The arrow that folds the group: down while its objects show, right while they're tucked away.
                Rectangle fold = FoldBounds(row);
                Icons.Draw(g, IsOpen(item) ? "chevron-down" : "chevron-right", Rectangle.Inflate(fold, -LogicalToDeviceUnits(2), -LogicalToDeviceUnits(2)), p.Text2, 2.2f);
                x += LogicalToDeviceUnits(16);
            }
            if (item.Indented)
                using (var rule = new Pen(p.Line2)) g.DrawLine(rule, box.X + LogicalToDeviceUnits(17), row.Top, box.X + LogicalToDeviceUnits(17), row.Bottom);
            Icons.Draw(g, item.Members is null ? ToolStyles.IconOf(item.Item) : "group", new Rectangle(x, row.Y + (TwoLines ? LogicalToDeviceUnits(8) : (row.Height - icon) / 2), icon, icon),
                hidden ? p.Text3 : item.Members is null ? ToolStyles.SwatchOf(item.Item) : p.Text2);
            x += icon + LogicalToDeviceUnits(10);
            int right = Action is null ? box.Right - inset : (HasLock ? LockBounds(row) : ActionBounds(row)).Left - LogicalToDeviceUnits(4);
            if (TwoLines)
            {
                int line = AppFonts.Body.Height;
                Draw.Text(g, item.Title, AppFonts.Body, new Rectangle(x, row.Y + LogicalToDeviceUnits(6), right - x, line), ink);
                Draw.Text(g, item.Detail, AppFonts.Small, new Rectangle(x, row.Y + LogicalToDeviceUnits(6) + line, right - x, AppFonts.Small.Height + 2), p.Text3);
            }
            else
            {
                Draw.Text(g, item.Title, AppFonts.Body, new Rectangle(x, row.Y, right - x, row.Height), ink);
            }
            if (Action is not null && (i == _hover || selected || (Action == "eye" && hidden)))
            {
                Rectangle action = ActionBounds(row);
                if (i == _hover && _hoverAction) Draw.Box(g, action, LogicalToDeviceUnits(Theme.RadiusSmall), p.Active);
                string name = Action == "eye" ? hidden ? "eye-off" : "eye" : Action;
                Icons.Draw(g, name, Rectangle.Inflate(action, -LogicalToDeviceUnits(6), -LogicalToDeviceUnits(6)), p.Text2);
            }
            // The lock shows on hover and stays in sight while the layer is locked.
            bool locked = item.Objects.All(member => member.Locked);
            if (HasLock && (i == _hover || selected || locked))
            {
                Rectangle padlock = LockBounds(row);
                if (i == _hover && _hoverLock) Draw.Box(g, padlock, LogicalToDeviceUnits(Theme.RadiusSmall), p.Active);
                Icons.Draw(g, locked ? "lock" : "unlock", Rectangle.Inflate(padlock, -LogicalToDeviceUnits(6), -LogicalToDeviceUnits(6)), locked ? p.Text : p.Text3);
            }
        }
        if (Focused && ShowFocusCues && (_focus >= 0 && _focus < _rows.Count ? _focus : Current) is int focus and >= 0)
            Draw.FocusRing(g, Rectangle.Inflate(RowBounds(focus), -LogicalToDeviceUnits(8), -LogicalToDeviceUnits(1)), LogicalToDeviceUnits(Theme.Radius), LogicalToDeviceUnits(2));
        if (_dragging) DrawDropTarget(g);
        if (_band is { } band)
        {
            using var fill = new SolidBrush(Color.FromArgb(28, p.Accent));
            using var edge = new Pen(p.Accent);
            g.FillRectangle(fill, band);
            g.DrawRectangle(edge, band);
        }
    }

    /// <summary>Where a drag will land: a line between rows (indented when it joins a group), or a ring round a group.</summary>
    void DrawDropTarget(Graphics g)
    {
        Palette p = Theme.Current;
        int inset = LogicalToDeviceUnits(8);
        if (_dropInto is { } group && _rows.IndexOf(group) is int index and >= 0)
        {
            Rectangle box = Rectangle.Inflate(RowBounds(index), -inset, -LogicalToDeviceUnits(1));
            using var ring = new Pen(p.Accent, LogicalToDeviceUnits(2));
            using System.Drawing.Drawing2D.GraphicsPath path = Draw.RoundRect(box, LogicalToDeviceUnits(Theme.Radius));
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.DrawPath(ring, path);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
            return;
        }
        if (_dropBefore < 0 || _rows.Count == 0) return;
        int y = _dropBefore < _rows.Count ? RowBounds(_dropBefore).Top : RowBounds(_rows.Count - 1).Bottom;
        bool joins = _dropBefore < _rows.Count && _rows[_dropBefore].Indented;
        int left = inset + LogicalToDeviceUnits(joins ? 20 : 4), thick = LogicalToDeviceUnits(2);
        using var line = new SolidBrush(p.Accent);
        g.FillRectangle(line, left, y - thick / 2, ClientSize.Width - left - inset, thick);
        g.FillEllipse(line, left - thick * 2, y - thick * 2, thick * 4, thick * 4);
    }


    int IndexAt(Point point)
    {
        for (int i = 0; i < _rows.Count; i++)
            if (RowBounds(i).Contains(point)) return i;
        return -1;
    }

    bool PastDragThreshold(Point at) =>
        Math.Abs(at.X - _pressAt.X) > SystemInformation.DragSize.Width || Math.Abs(at.Y - _pressAt.Y) > SystemInformation.DragSize.Height;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_leftDown)
        {
            if (_pressIndex >= 0 && Reorderable && !_dragging && PastDragThreshold(e.Location))
            {
                _dragging = true;
                _collapseOnRelease = false;
                Capture = true;
            }
            if (_dragging)
            {
                ScrollNearEdge(e.Location);
                UpdateDrop(e.Location);
                return;
            }
            if (_banding)
            {
                UpdateBand(e.Location);
                return;
            }
        }
        int index = IndexAt(e.Location);
        bool onAction = index >= 0 && Action is not null && ActionBounds(RowBounds(index)).Contains(e.Location);
        bool onLock = index >= 0 && HasLock && LockBounds(RowBounds(index)).Contains(e.Location);
        if (index == _hover && onAction == _hoverAction && onLock == _hoverLock) return;
        _hover = index;
        _hoverAction = onAction;
        _hoverLock = onLock;
        Tips.Shared.SetToolTip(this, onLock ? (_rows[index].Objects.All(item => item.Locked) ? "Unlock" : "Lock\nA locked layer can't be chosen on the page, moved or erased") : onAction && Action == "eye" ? "Hide or show" : null);
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = -1;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        int index = IndexAt(e.Location);
        Focus();
        _pressAt = e.Location;
        _pressIndex = -1;
        _leftDown = e.Button == MouseButtons.Left;
        if (index < 0)
        {
            if (e.Button != MouseButtons.Left) return;
            // Empty space: a drag from here draws a box that chooses what it crosses; Ctrl keeps what was chosen.
            bool keep = MultiSelect && ModifierKeys.HasFlag(Keys.Control);
            _banding = MultiSelect;
            _bandKept = keep ? [.. _selected] : [];
            if (!keep && _selected.Count > 0)
            {
                _selected.Clear();
                Invalidate();
                EmptyClicked?.Invoke(this, EventArgs.Empty);
            }
            return;
        }
        if (e.Button == MouseButtons.Right)
        {
            if (!IsSelected(_rows[index])) Pick(_rows[index]);
            MenuRequested?.Invoke(this, (_rows[index], e.Location));
            return;
        }
        if (e.Button != MouseButtons.Left) return;
        ObjectRow row = _rows[index];
        if (row.Members is not null && IsOpen is not null && FoldBounds(RowBounds(index)).Contains(e.Location))
        {
            FoldToggled?.Invoke(this, row);
            return;
        }
        if (HasLock && LockBounds(RowBounds(index)).Contains(e.Location))
        {
            LockClicked?.Invoke(this, row);
            return;
        }
        if (Action is not null && ActionBounds(RowBounds(index)).Contains(e.Location))
        {
            ActionClicked?.Invoke(this, row);
            return;
        }
        if (MultiSelect && ModifierKeys.HasFlag(Keys.Shift) && _anchor >= 0) PickRun(_anchor, index);
        else if (MultiSelect && ModifierKeys.HasFlag(Keys.Control)) Toggle(index);
        // A press on a row that is already chosen keeps the others, so they can all be dragged; a click lets them go.
        else if (IsSelected(row) && _selected.Count > row.Objects.Count) _collapseOnRelease = true;
        else Pick(row);
        _pressIndex = index;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _leftDown = false;
        if (_dragging)
        {
            _dragging = false;
            Capture = false;
            var rows = _rows.Where(IsSelected).ToList();
            if (rows.Count > 0 && (_dropInto is not null || _dropBefore >= 0)) Dropped?.Invoke(this, (rows, _dropBefore, _dropInto));
            _dropBefore = -1;
            _dropInto = null;
            Invalidate();
        }
        else if (_collapseOnRelease && _pressIndex >= 0 && _pressIndex < _rows.Count) Pick(_rows[_pressIndex]);
        if (_banding)
        {
            _banding = false;
            if (_band is not null)
            {
                _band = null;
                ChoseMany(Current);
            }
        }
        _collapseOnRelease = false;
        _pressIndex = -1;
    }

    /// <summary>Works out where the dragged rows would land under the pointer.</summary>
    void UpdateDrop(Point at)
    {
        _dropInto = null;
        int index = IndexAt(at);
        if (index < 0) _dropBefore = at.Y < RowBounds(0).Top ? 0 : _rows.Count;
        else
        {
            Rectangle row = RowBounds(index);
            bool movingGroups = _rows.Any(other => other.Members is not null && IsSelected(other));
            // The lower part of a group's row takes the rows into the group; a whole group can't go into another.
            if (_rows[index].Members is not null && !movingGroups && !IsSelected(_rows[index]) && at.Y > row.Top + row.Height / 3)
            {
                _dropInto = _rows[index];
                _dropBefore = -1;
            }
            else _dropBefore = at.Y < row.Top + row.Height / 2 ? index : index + 1;
        }
        Invalidate();
    }

    /// <summary>Scrolls a little when a drag nears the top or bottom edge, so a row can go anywhere in a long list.</summary>
    void ScrollNearEdge(Point at)
    {
        int edge = LogicalToDeviceUnits(20), step = RowHeight / 2;
        if (at.Y < edge) AutoScrollPosition = new Point(0, Math.Max(0, -AutoScrollPosition.Y - step));
        else if (at.Y > ClientSize.Height - edge) AutoScrollPosition = new Point(0, -AutoScrollPosition.Y + step);
    }

    void UpdateBand(Point at)
    {
        if (_band is null && !PastDragThreshold(at)) return;
        var band = Rectangle.FromLTRB(Math.Min(_pressAt.X, at.X), Math.Min(_pressAt.Y, at.Y), Math.Max(_pressAt.X, at.X), Math.Max(_pressAt.Y, at.Y));
        _band = band;
        _selected = [.. _bandKept];
        for (int i = 0; i < _rows.Count; i++)
        {
            Rectangle row = RowBounds(i);
            if (row.Top < band.Bottom && row.Bottom > band.Top) _selected.UnionWith(_rows[i].Objects.Select(item => item.Id));
        }
        Invalidate();
    }

    void Pick(ObjectRow row)
    {
        _selected = [.. row.Objects.Select(item => item.Id)];
        _anchor = _focus = _rows.IndexOf(row);
        Invalidate();
        ItemHostAccessible.ItemFocused(this, _anchor);
        Picked?.Invoke(this, row);
    }

    /// <summary>Ctrl+click: adds the row's objects to the choice, or takes them out.</summary>
    void Toggle(int index)
    {
        ObjectRow row = _rows[index];
        if (IsSelected(row)) _selected.ExceptWith(row.Objects.Select(item => item.Id));
        else _selected.UnionWith(row.Objects.Select(item => item.Id));
        _anchor = _focus = index;
        ChoseMany(index);
    }

    /// <summary>Shift+click: every row from the last one clicked to this one.</summary>
    void PickRun(int from, int to)
    {
        _selected = [.. _rows.Skip(Math.Min(from, to)).Take(Math.Abs(to - from) + 1).SelectMany(row => row.Objects).Select(item => item.Id)];
        _focus = to;
        ChoseMany(to);
    }

    void ChoseMany(int focus)
    {
        Invalidate();
        ItemHostAccessible.ItemFocused(this, focus);
        var chosen = _rows.SelectMany(row => row.Objects).Where(item => _selected.Contains(item.Id)).Distinct().ToList();
        PickedMany?.Invoke(this, chosen);
    }

    protected override bool IsInputKey(Keys keyData) =>
        (keyData & Keys.KeyCode) is Keys.Up or Keys.Down or Keys.Home or Keys.End or Keys.Left or Keys.Right || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_rows.Count == 0) return;
        int current = _focus >= 0 && _focus < _rows.Count ? _focus : Current;
        // Left folds a group, Right unfolds it, as in a tree.
        if (e.KeyCode is Keys.Left or Keys.Right && current >= 0 && _rows[current].Members is not null && IsOpen is not null
            && IsOpen(_rows[current]) == (e.KeyCode == Keys.Left))
        {
            FoldToggled?.Invoke(this, _rows[current]);
            e.Handled = true;
            return;
        }
        int next = e.KeyCode switch { Keys.Up => current - 1, Keys.Down => current + 1, Keys.Home => 0, Keys.End => _rows.Count - 1, _ => int.MinValue };
        if (next != int.MinValue)
        {
            next = Math.Clamp(next, 0, _rows.Count - 1);
            // Shift with the arrows takes a run of rows from the last one picked, as in File Explorer.
            if (MultiSelect && e.Shift && _anchor >= 0) PickRun(_anchor, next);
            else Pick(_rows[next]);
            e.Handled = true;
        }
        else if (MultiSelect && e.KeyData == (Keys.Control | Keys.A))
        {
            _selected = [.. _rows.SelectMany(row => row.Objects).Select(item => item.Id)];
            ChoseMany(current);
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Delete && DeleteRequested is not null && _selected.Count > 0)
        {
            DeleteRequested(this, EventArgs.Empty);
            e.Handled = true;
        }
        else if (e.KeyCode is Keys.Space or Keys.Delete && current >= 0 && Action is not null)
        {
            ActionClicked?.Invoke(this, _rows[current]);
            e.Handled = true;
        }
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new ItemHostAccessible(this, this, AccessibleRole.ListItem);

    int IItemHost.ItemCount => _rows.Count;

    string IItemHost.ItemName(int index)
    {
        ObjectRow row = _rows[index];
        string name = row.Detail.Length > 0 ? $"{row.Title}, {row.Detail}" : row.Title;
        if (row.Indented) name += ", in a group";
        if (row.Objects.All(item => item.Locked)) name += ", locked";
        return row.Objects.All(item => item.Hidden) ? $"{name}, hidden" : name;
    }

    Rectangle IItemHost.ItemBounds(int index) => RowBounds(index);
    bool IItemHost.IsItemSelected(int index) => IsSelected(_rows[index]);
    int IItemHost.FocusedItem => Current;
    void IItemHost.ActivateItem(int index) => Pick(_rows[index]);

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        Invalidate();
        int current = Current;
        if (current < 0 && _rows.Count > 0) ScreenReader.Say(this, $"{AccessibleName}, {_rows.Count} items. Up and Down move, {(Action == "eye" ? "Space hides or shows" : "Delete removes")} the selected one.");
        else ItemHostAccessible.ItemFocused(this, current);
    }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    protected override void OnScroll(ScrollEventArgs se)
    {
        base.OnScroll(se);
        Invalidate();
    }

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
