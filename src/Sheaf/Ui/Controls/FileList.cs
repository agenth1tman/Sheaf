using Sheaf.Tools;

namespace Sheaf.Ui.Controls;

/// <summary>
/// The files added to a tool, with a thumbnail, page count and size. A native list box underneath, so
/// selection, keyboard and screen readers behave as usual. Drag rows to reorder; drop files to add them.
/// </summary>
internal sealed class FileList : ListBox
{
    public const int ThumbnailWidth = 34;
    const int RowHeight = 56, ThumbnailHeight = 44, NumberWidth = 28, Pad = 12;
    const int WM_PAINT = 0x000F;
    const string InternalFormat = "Sheaf.FileListRows";
    static readonly Font ExtensionFont = new(AppFonts.SemiBoldFamily, 6.5f);

    List<SourceFile> _files = [];
    int _dragFrom = -1, _insertAt = -1;
    Point _dragStart;

    public FileList()
    {
        DrawMode = DrawMode.OwnerDrawFixed;
        IntegralHeight = false;
        BorderStyle = BorderStyle.None;
        SelectionMode = SelectionMode.MultiExtended;
        AllowDrop = true;
        Font = AppFonts.Body;
        ItemHeight = RowHeight;
        AccessibleName = "Files";
        ApplyColors();
        Theme.Changed += OnThemeChanged;
    }

    public event EventHandler<string[]>? FilesDropped;
    public event EventHandler? FilesChanged;
    public event EventHandler? RemoveRequested;

    /// <summary>The empty list was clicked: its "Drop files here, or choose Add Files" note works as Add Files.</summary>
    public event EventHandler? EmptyClicked;

    public string EmptyText { get; set; } = "";

    public IReadOnlyList<SourceFile> Files => _files;

    public IReadOnlyList<SourceFile> SelectedFiles => [.. SelectedIndices.Cast<int>().Select(i => _files[i])];

    void ApplyColors()
    {
        BackColor = Theme.Current.Panel;
        ForeColor = Theme.Current.Text;
    }

    void OnThemeChanged(object? sender, EventArgs e)
    {
        ApplyColors();
        NativeTheme.ScrollBars(this);
        Invalidate();
    }

    public void Add(IEnumerable<SourceFile> files)
    {
        _files.AddRange(files);
        Reload([]);
    }

    public void RemoveSelected()
    {
        var removed = SelectedFiles;
        if (removed.Count == 0) return;
        int first = SelectedIndices.Cast<int>().Min();
        foreach (SourceFile file in removed)
        {
            _files.Remove(file);
            file.Dispose();
        }
        Reload([]);
        if (_files.Count > 0) SelectedIndex = Math.Min(first, _files.Count - 1);
    }

    public void Clear()
    {
        foreach (SourceFile file in _files) file.Dispose();
        _files.Clear();
        Reload([]);
    }

    public void MoveSelected(int step)
    {
        var selected = SelectedIndices.Cast<int>().Order().ToList();
        if (selected.Count == 0) return;
        MoveSelectedTo(step < 0 ? Math.Max(0, selected[0] + step) : Math.Min(_files.Count, selected[^1] + 1 + step));
    }

    public void RefreshFile(SourceFile file)
    {
        int index = _files.IndexOf(file);
        if (index >= 0 && index < Items.Count && IsHandleCreated) Invalidate(GetItemRectangle(index));
    }

    void MoveSelectedTo(int insertAt)
    {
        var moving = SelectedFiles;
        int before = SelectedIndices.Cast<int>().Count(i => i < insertAt);
        foreach (SourceFile file in moving) _files.Remove(file);
        _files.InsertRange(Math.Clamp(insertAt - before, 0, _files.Count), moving);
        Reload(moving);
    }

    void Reload(IReadOnlyCollection<SourceFile> select)
    {
        BeginUpdate();
        Items.Clear();
        Items.AddRange([.. _files]);
        ClearSelected();
        for (int i = 0; i < _files.Count; i++)
            if (select.Contains(_files[i])) SetSelected(i, true);
        EndUpdate();
        Invalidate();
        FilesChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ItemHeight = Math.Min(255, LogicalToDeviceUnits(RowHeight));
        NativeTheme.ScrollBars(this);
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        ItemHeight = Math.Min(255, LogicalToDeviceUnits(RowHeight));
    }

    int Px(int logical) => LogicalToDeviceUnits(logical);

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _files.Count) return;
        Palette p = Theme.Current;
        SourceFile file = _files[e.Index];
        Graphics g = e.Graphics;
        Rectangle row = e.Bounds;
        bool selected = (e.State & DrawItemState.Selected) != 0;

        using (var back = new SolidBrush(p.Panel)) g.FillRectangle(back, row);
        if (selected) Draw.Box(g, Rectangle.Inflate(row, -Px(4), -Px(2)), Px(Theme.Radius), p.AccentSoft);
        using (var rule = new Pen(p.Line)) g.DrawLine(rule, row.Left + Px(12), row.Bottom - 1, row.Right - Px(12), row.Bottom - 1);

        var number = new Rectangle(row.Left, row.Top, Px(NumberWidth), row.Height);
        Draw.Text(g, (e.Index + 1).ToString(), AppFonts.MonoSmall, number, p.Text3, Draw.Line | TextFormatFlags.Right);

        var thumb = new Rectangle(number.Right + Px(Pad), row.Top + (row.Height - Px(ThumbnailHeight)) / 2, Px(ThumbnailWidth), Px(ThumbnailHeight));
        DrawThumbnail(g, file, thumb);

        int textLeft = thumb.Right + Px(Pad);
        int problemWidth = file.Problem is null ? 0 : Px(170);
        int textWidth = Math.Max(1, row.Right - textLeft - Px(Pad) - problemWidth);
        int lineHeight = AppFonts.BodyMedium.Height;
        int top = row.Top + (row.Height - lineHeight - AppFonts.Small.Height - Px(2)) / 2;
        Draw.Text(g, file.Name, AppFonts.BodyMedium, new Rectangle(textLeft, top, textWidth, lineHeight), p.Text);
        Draw.Text(g, file.Details, AppFonts.Small, new Rectangle(textLeft, top + lineHeight + Px(2), textWidth, AppFonts.Small.Height), p.Text3,
            Draw.Line | TextFormatFlags.PathEllipsis);
        if (file.Problem is not null)
            Draw.Text(g, file.Problem, AppFonts.SmallMedium, new Rectangle(row.Right - Px(Pad) - problemWidth, row.Top, problemWidth, row.Height), p.Warning,
                Draw.Line | TextFormatFlags.Right);

        if ((e.State & DrawItemState.Focus) != 0 && ShowFocusCues)
            Draw.FocusRing(g, Rectangle.Inflate(row, -Px(4), -Px(2)), Px(Theme.Radius), Px(2));
        if (_insertAt == e.Index || (_insertAt == _files.Count && e.Index == _files.Count - 1))
        {
            int y = _insertAt == _files.Count ? row.Bottom - Px(2) : row.Top;
            using var accent = new SolidBrush(p.Accent);
            g.FillRectangle(accent, row.Left + Px(8), y, row.Width - Px(16), Px(2));
        }
    }

    void DrawThumbnail(Graphics g, SourceFile file, Rectangle box)
    {
        if (file.Thumbnail is { } image)
        {
            float scale = Math.Min(box.Width / (float)image.Width, box.Height / (float)image.Height);
            var size = new Size((int)(image.Width * scale), (int)(image.Height * scale));
            var target = new Rectangle(box.X + (box.Width - size.Width) / 2, box.Y + (box.Height - size.Height) / 2, size.Width, size.Height);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(image, target);
            using var border = new Pen(Theme.Current.Line2);
            g.DrawRectangle(border, target.X, target.Y, target.Width - 1, target.Height - 1);
            return;
        }
        Draw.Box(g, box, Px(3), Theme.Current.Raised, Theme.Current.Line2);
        string extension = Path.GetExtension(file.Name).TrimStart('.').ToUpperInvariant();
        Draw.Text(g, extension, ExtensionFont, box, Theme.Current.Text2, Draw.Centered);
    }

    /// <summary>The list box is native, so its empty state is painted after Windows has drawn the background.</summary>
    protected override void WndProc(ref Message m)
    {
        const int WM_LBUTTONDOWN = 0x0201, WM_RBUTTONDOWN = 0x0204;
        if (m.Msg is WM_LBUTTONDOWN or WM_RBUTTONDOWN && Items.Count > 0)
        {
            // A click on the empty space below the files lets go of the selection (a list box would pick the last row).
            var point = new Point(unchecked((short)(long)m.LParam), unchecked((short)((long)m.LParam >> 16)));
            int index = IndexFromPoint(point);
            if (index < 0 || !GetItemRectangle(index).Contains(point))
            {
                Focus();
                ClearSelected();
                return;
            }
        }
        base.WndProc(ref m);
        if (m.Msg != WM_PAINT || Items.Count > 0 || EmptyText.Length == 0) return;
        using Graphics g = CreateGraphics();
        var area = Rectangle.Inflate(ClientRectangle, -Px(24), -Px(24));
        int icon = Px(28);
        Icons.Draw(g, "download", new Rectangle(area.X + (area.Width - icon) / 2, area.Y + area.Height / 2 - icon - Px(10), icon, icon), Theme.Current.Text3);
        TextRenderer.DrawText(g, EmptyText, AppFonts.Body, new Rectangle(area.X, area.Y + area.Height / 2, area.Width, area.Height / 2), Theme.Current.Text2,
            TextFormatFlags.NoPrefix | TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.WordBreak);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (Items.Count == 0) Invalidate();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Delete) { RemoveRequested?.Invoke(this, EventArgs.Empty); e.Handled = true; }
        else if (e.KeyCode == Keys.Escape && SelectedIndices.Count > 0) { ClearSelected(); e.Handled = true; }
        else if (e.Alt && e.KeyCode is Keys.Up or Keys.Down) { MoveSelected(e.KeyCode == Keys.Up ? -1 : 1); e.Handled = true; }
        else if (e.Control && e.KeyCode == Keys.A) { for (int i = 0; i < Items.Count; i++) SetSelected(i, true); e.Handled = true; }
        base.OnKeyDown(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (Items.Count == 0 && e.Button == MouseButtons.Left)
        {
            EmptyClicked?.Invoke(this, EventArgs.Empty);
            return;
        }
        _dragFrom = e.Button == MouseButtons.Left ? IndexFromPoint(e.Location) : -1;
        _dragStart = e.Location;
    }

    protected override void OnMouseMove(MouseEventArgs e) => MoveOrHover(e);

    void MoveOrHover(MouseEventArgs e)
    {
        Cursor = Items.Count == 0 ? Cursors.Hand : Cursors.Default;
        OnDragMove(e);
    }

    void OnDragMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragFrom < 0 || e.Button != MouseButtons.Left) return;
        Size drag = SystemInformation.DragSize;
        if (Math.Abs(e.X - _dragStart.X) < drag.Width && Math.Abs(e.Y - _dragStart.Y) < drag.Height) return;
        if (!GetSelected(_dragFrom)) SetSelected(_dragFrom, true);
        _dragFrom = -1;
        DoDragDrop(new DataObject(InternalFormat, true), DragDropEffects.Move);
        SetInsertionPoint(-1);
    }

    protected override void OnMouseUp(MouseEventArgs e) { _dragFrom = -1; base.OnMouseUp(e); }

    protected override void OnDragOver(DragEventArgs e)
    {
        base.OnDragOver(e);
        if (e.Data?.GetDataPresent(InternalFormat) == true)
        {
            e.Effect = DragDropEffects.Move;
            SetInsertionPoint(InsertionIndex(PointToClient(new Point(e.X, e.Y))));
        }
        else e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
    }

    protected override void OnDragLeave(EventArgs e) { base.OnDragLeave(e); SetInsertionPoint(-1); }

    protected override void OnDragDrop(DragEventArgs e)
    {
        base.OnDragDrop(e);
        if (e.Data?.GetDataPresent(InternalFormat) == true)
        {
            int target = _insertAt;
            SetInsertionPoint(-1);
            if (target >= 0) MoveSelectedTo(target);
        }
        else if (e.Data?.GetData(DataFormats.FileDrop) is string[] paths) FilesDropped?.Invoke(this, paths);
    }

    int InsertionIndex(Point point)
    {
        if (_files.Count == 0) return 0;
        int index = IndexFromPoint(point);
        if (index < 0) return point.Y < 0 ? 0 : _files.Count;
        Rectangle row = GetItemRectangle(index);
        return point.Y > row.Top + row.Height / 2 ? index + 1 : index;
    }

    void SetInsertionPoint(int index)
    {
        if (_insertAt == index) return;
        _insertAt = index;
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Theme.Changed -= OnThemeChanged;
        base.Dispose(disposing);
    }
}
