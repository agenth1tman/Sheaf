using System.Globalization;
using Sheaf.Core.Editing;
using Sheaf.Ui;
using Sheaf.Ui.Controls;

namespace Sheaf.Pages.Document;

/// <summary>
/// The right-hand panel of the editor. Properties: the selected object's position and size, its comment,
/// how objects are saved, and the layers of the page. Comments: every highlight, underline and note in the file.
/// </summary>
internal sealed class Inspector : Surface
{
    readonly DocumentCanvas _canvas;
    readonly ChoiceGroup<bool> _tabs = new("Panel", true, ("Properties", false), ("Comments", true)) { Dock = DockStyle.Fill };
    readonly Panel _properties = new() { Dock = DockStyle.Fill };
    readonly ObjectList _comments = new() { Dock = DockStyle.Fill, AccessibleName = "Comments", EmptyText = "No comments yet. Highlight, underline or add a note in Comment mode." };
    readonly ObjectList _layers;
    // Groups folded away in the list, so only their own row shows.
    readonly HashSet<Guid> _folded = [];
    // Under the layers, as in Photoshop: group, ungroup, hide or show, duplicate and delete what is chosen.
    readonly AppButton _newGroup = LayerButton("group", "New Group from Selection");
    readonly AppButton _ungroup = LayerButton("ungroup", "Ungroup");
    readonly AppButton _hide = LayerButton("eye", "Hide or Show");
    readonly AppButton _duplicate = LayerButton("copy", "Duplicate");
    readonly AppButton _remove = LayerButton("trash", "Delete");
    readonly TableLayoutPanel _geometry;
    // In the same place when nothing is chosen: the colour of the page in view.
    readonly TableLayoutPanel _pageSection;
    readonly ColorButton _paper = new("Page colour", Color.White) { AllowNone = true, LogicalHeight = 32 };
    readonly WrapLabel _paperHint = Make.Hint("");
    readonly TableLayoutPanel _commentSection;
    readonly TableLayoutPanel _savingSection;
    readonly Control _savingHint;

    /// <summary>On a short window the explanation under "When saving" goes, leaving the layers more room.</summary>
    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (_savingHint is null) return;
        int height = (int)Math.Round(Height * 96f / Math.Max(96, DeviceDpi));
        _savingHint.Visible = height <= 0 || height >= 560;
        // Under 700 the page colour's note goes too (its words stay in the colour's tooltip), leaving the layers room.
        _paperHint.Visible = height <= 0 || height >= 700;
    }
    readonly TextLabel _layersHeading = Make.PanelHeading("Layers");
    readonly InputBox _x = Coordinate("X"), _y = Coordinate("Y"), _w = Coordinate("Width"), _h = Coordinate("Height");
    readonly TextBox _comment = new() { Multiline = true, BorderStyle = BorderStyle.None, Height = 72, Dock = DockStyle.Fill, AccessibleName = "Comment", ScrollBars = ScrollBars.Vertical };
    readonly ChoiceGroup<bool> _keepEditable = new("When saving", true, ("Keep editable", true), ("Flatten", false)) { Anchor = AnchorStyles.Left | AnchorStyles.Right };
    PageObject? _commentTarget;
    bool _syncing;

    public Inspector(DocumentCanvas canvas) : base(SurfaceRole.Panel)
    {
        _canvas = canvas;
        _layers = new ObjectList
        {
            Dock = DockStyle.Fill, AccessibleName = "Layers", Action = "eye", MultiSelect = true, Reorderable = true,
            EmptyText = "Nothing added to this page yet.", IsOpen = row => row.Item.Group is not { } group || !_folded.Contains(group),
        };
        Borders = AnchorStyles.Left;
        AccessibleRole = AccessibleRole.Pane;
        AccessibleName = "Properties";

        _comment.ContextMenuStrip = Menus.ForText(_comment);
        _geometry = Make.PanelSection("Position and size", GeometryGrid());
        _paper.Anchor = AnchorStyles.Left;
        _pageSection = Make.PanelSection("Page colour", _paper, _paperHint);
        _pageSection.Dock = DockStyle.Bottom;
        _pageSection.Visible = false;
        Tips.Shared.SetToolTip(_paper, "The colour of this blank page\nNone leaves an image see-through");
        _paper.Changed += (_, _) => { if (!_syncing) PaperChosen?.Invoke(this, _paper.Color); };
        _commentSection = Make.PanelSection("Comment", CommentBox());
        _savingHint = Make.Hint("Keep editable stores what you added so Sheaf can change it later. Every app shows it either way.");
        _savingSection = Make.PanelSection("When saving", _keepEditable, _savingHint);
        var layersHeader = Make.Stack(_layersHeading);
        layersHeader.Dock = DockStyle.Top;
        layersHeader.Padding = new Padding(16, 14, 16, 0);

        // Nothing above the layers ever comes or goes, so choosing or letting go of an object never moves them:
        // what belongs to the selection sits below, in a strip that keeps its height.
        _geometry.Dock = DockStyle.Bottom;
        _commentSection.Dock = DockStyle.Bottom;
        var layerBar = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Height = 40, Padding = new Padding(12, 4, 12, 4) };
        layerBar.Controls.AddRange([_remove, _duplicate, _hide, _ungroup, _newGroup]);
        _properties.Controls.Add(_layers);
        _properties.Controls.Add(layerBar);
        _properties.Controls.Add(Rule(DockStyle.Bottom));
        _properties.Controls.Add(_commentSection);
        _properties.Controls.Add(_geometry);
        _properties.Controls.Add(_pageSection);
        _properties.Controls.Add(layersHeader);
        _properties.Controls.Add(Rule());
        _properties.Controls.Add(_savingSection);
        _comments.Visible = false;
        // On a short window the layers keep room for a few rows and the panel scrolls instead of squeezing them.
        _properties.AutoScroll = true;
        _properties.Layout += (_, _) => QueueLayersRoom();
        _properties.HandleCreated += (_, _) => Platform.Native.SetScrollbarTheme(_properties.Handle, Theme.IsDark);
        Theme.Changed += (_, _) => { if (_properties.IsHandleCreated) Platform.Native.SetScrollbarTheme(_properties.Handle, Theme.IsDark); };

        var tabsHost = new Panel { Dock = DockStyle.Top, Height = 50, Padding = new Padding(12, 10, 12, 6) };
        tabsHost.Controls.Add(_tabs);
        Controls.Add(_properties);
        Controls.Add(_comments);
        Controls.Add(tabsHost);

        _tabs.Changed += (_, _) => ShowComments(_tabs.Value);
        // A row inside a group chooses just that object; the group's own row chooses all of it.
        _layers.Picked += (_, row) => _canvas.Select(row.Page, row.Objects, withGroups: !row.Indented);
        _layers.PickedMany += (_, items) => _canvas.Select(items.Count == 0 ? -1 : LayersPage, items, withGroups: false);
        _layers.EmptyClicked += (_, _) => _canvas.ClearSelection();
        _layers.MenuRequested += (_, request) => ShowLayerMenu(request.Row, request.At);
        _layers.ActionClicked += (_, row) => ToggleHidden(row);
        _layers.LockClicked += (_, row) =>
        {
            _canvas.SetLocked(row.Objects, !row.Objects.All(item => item.Locked));
            Sync();
        };
        _layers.Dropped += (_, drop) => DropLayers(drop.Rows, drop.Before, drop.Into);
        _layers.FoldToggled += (_, row) =>
        {
            if (row.Item.Group is not { } group) return;
            if (!_folded.Remove(group)) _folded.Add(group);
            Sync();
        };
        _layers.DeleteRequested += (_, _) => _canvas.DeleteSelection();
        _layers.Renamed += (_, rename) => RenameLayer(rename.Row, rename.Name);
        Tips.Shared.SetToolTip(_newGroup, "New group from selection (Ctrl+G)\nChoose two or more layers first: Ctrl+click or Shift+click them");
        Tips.Shared.SetToolTip(_ungroup, "Ungroup (Ctrl+Shift+G)\nChoose a group first");
        Tips.Shared.SetToolTip(_hide, "Hide or show\nChoose a layer first");
        Tips.Shared.SetToolTip(_duplicate, "Duplicate (Ctrl+D)\nChoose a layer first");
        Tips.Shared.SetToolTip(_remove, "Delete (Del)\nChoose a layer first");
        _newGroup.Click += (_, _) => _canvas.GroupSelection();
        _ungroup.Click += (_, _) => _canvas.UngroupSelection();
        _hide.Click += (_, _) => { if (_canvas.Selection.Count > 0) ToggleHidden(new ObjectRow(LayersPage, _canvas.Selection[0], "", "", [.. _canvas.Selection])); };
        _duplicate.Click += (_, _) => _canvas.DuplicateSelection();
        _remove.Click += (_, _) => _canvas.DeleteSelection();
        _comments.Picked += (_, row) => { _canvas.Reveal(row.Page, row.Item); };
        foreach (InputBox input in new[] { _x, _y, _w, _h })
        {
            input.EnterPressed += (_, _) => ApplyGeometry();
            input.Box.Leave += (_, _) => ApplyGeometry();
        }
        _comment.Leave += (_, _) => ApplyComment();
        _keepEditable.Changed += (_, _) => KeepEditableChanged?.Invoke(this, EventArgs.Empty);
        Theme.Changed += OnThemeChanged;
        ApplyColors();
    }

    public event EventHandler? KeepEditableChanged;

    public bool KeepEditable
    {
        get => _keepEditable.Value;
        set => _keepEditable.Value = value;
    }

    static Divider Rule(DockStyle dock = DockStyle.Top) => new() { Dock = dock, Margin = Padding.Empty };

    static AppButton LayerButton(string icon, string name) => new("", ButtonKind.Quiet, icon) { LogicalHeight = 30, AccessibleName = name, Margin = new Padding(2, 0, 0, 0) };

    static InputBox Coordinate(string name)
    {
        var input = new InputBox { NumberRange = (-14400, 14400), Monospace = true, Caption = name, Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0, 0, 8, 6) };
        input.Box.MaxLength = 6;
        return input;
    }

    TableLayoutPanel GeometryGrid()
    {
        var grid = new TableLayoutPanel { ColumnCount = 4, RowCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = Padding.Empty };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        (string Caption, InputBox Input)[] cells = [("X", _x), ("Y", _y), ("W", _w), ("H", _h)];
        for (int i = 0; i < cells.Length; i++)
        {
            var caption = new TextLabel(cells[i].Caption, Tone.Tertiary) { Font = AppFonts.MonoSmall, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 6, 6) };
            grid.Controls.Add(caption, i % 2 * 2, i / 2);
            grid.Controls.Add(cells[i].Input, i % 2 * 2 + 1, i / 2);
        }
        return grid;
    }

    Control CommentBox()
    {
        var frame = new Surface(SurfaceRole.Raised) { Height = 84, Padding = new Padding(10, 8, 6, 8), Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = Padding.Empty };
        frame.Controls.Add(_comment);
        return frame;
    }

    void ApplyColors()
    {
        _comment.BackColor = Theme.Current.Raised;
        _comment.ForeColor = Theme.Current.Text;
    }

    void OnThemeChanged(object? sender, EventArgs e) => ApplyColors();

    /// <summary>Shows the comment box of the selected note and puts the cursor in it.</summary>
    public void EditComment()
    {
        ShowComments(false);
        if (_commentSection.Visible) _comment.Focus();
    }

    public void ShowComments(bool comments)
    {
        _tabs.Value = comments;
        _comments.Visible = comments;
        _properties.Visible = !comments;
        Sync();
    }

    /// <summary>Refreshes everything from the canvas: the selection, the page it shows and the document.</summary>
    /// <summary>A colour was chosen for the page in view (none: see-through).</summary>
    public event EventHandler<Color>? PaperChosen;

    /// <summary>
    /// The height the panel needs for its sections and about five layer rows; when the window is shorter, the panel
    /// scrolls rather than leave the layers a sliver.
    /// </summary>
    void QueueLayersRoom()
    {
        // Measured once the change in hand is laid out: sections come and go during one, and measuring each step
        // flashed a scroll bar that wasn't needed.
        if (_roomQueued || !IsHandleCreated) return;
        _roomQueued = true;
        BeginInvoke(KeepLayersRoom);
    }

    bool _roomQueued;

    void KeepLayersRoom()
    {
        _roomQueued = false;
        int sections = _properties.Controls.Cast<Control>().Where(part => part.Visible && part != _layers).Sum(part => part.Height);
        var needed = new Size(0, sections + LogicalToDeviceUnits(5 * 34 + 8));
        if (_properties.AutoScrollMinSize != needed) _properties.AutoScrollMinSize = needed;
    }

    public void Sync()
    {
        if (_canvas.Document is not { } document) return;
        _syncing = true;
        SuspendLayout();
        _properties.SuspendLayout();
        IReadOnlyList<PageObject> selection = _canvas.Selection;
        PageObject? single = selection.Count == 1 ? selection[0] : null;

        // With nothing chosen, the strip under the layers holds the page's colour instead of a position.
        bool nothing = selection.Count == 0 && _canvas.CurrentPage < document.Pages.Count;
        if (nothing)
        {
            PageEntry entry = document.Pages[_canvas.CurrentPage];
            _paper.Enabled = entry.IsBlank;
            _paper.Color = entry.IsBlank ? Color.FromArgb(entry.Background) : Color.White;
            _paperHint.Text = entry.IsBlank ? $"Page {_canvas.CurrentPage + 1}. Right-click pages in the strip to colour several."
                : "Pages from a PDF keep their own paper. Add a blank page (the + by Pages) to choose its colour.";
            Tips.Shared.SetToolTip(_paper, _paperHint.Text);
        }
        if (_geometry.Height > 0) _pageSection.MinimumSize = new Size(0, _geometry.Height);
        _pageSection.Visible = nothing;
        _geometry.Visible = !nothing;

        // Always there, so the layers above never move; usable when one object (not a note) is chosen.
        bool placeable = single is not null && single.Kind != MarkKind.Note;
        foreach (InputBox input in new[] { _x, _y, _w, _h }) input.Enabled = placeable;
        if (placeable)
        {
            SetNumber(_x, single!.X);
            SetNumber(_y, single.Y);
            SetNumber(_w, single.Width);
            SetNumber(_h, single.Height);
        }
        else foreach (InputBox input in new[] { _x, _y, _w, _h }) input.Text = "";
        _newGroup.Enabled = _canvas.CanGroup;
        _ungroup.Enabled = _canvas.CanUngroup;
        _hide.Enabled = _duplicate.Enabled = _remove.Enabled = selection.Count > 0;
        bool commentable = single is not null && (single.IsMarkup || single.Kind == MarkKind.Note);
        _commentSection.Visible = commentable;
        _commentTarget = commentable ? single : null;
        if (commentable && !_comment.Focused) _comment.Text = single!.Comment.Replace("\n", Environment.NewLine);

        int page = _canvas.SelectionPage >= 0 ? _canvas.SelectionPage : _canvas.CurrentPage;
        if (page < document.Pages.Count)
        {
            _layersHeading.Text = $"Layers · Page {page + 1}";
            _layers.SetRows(LayerRows(document.Pages[page].Objects, page, _folded), selection.Select(item => item.Id));
        }
        _comments.SetRows(CommentRows(document), single is null ? [] : [single.Id]);
        _properties.ResumeLayout(true);
        ResumeLayout(true);
        _syncing = false;
    }

    int LayersPage => _canvas.SelectionPage >= 0 ? _canvas.SelectionPage : _canvas.CurrentPage;

    /// <summary>The page's objects, top first; a group shows as one row with its objects indented under it.</summary>
    static IEnumerable<ObjectRow> LayerRows(List<PageObject> objects, int page, IReadOnlySet<Guid>? folded = null)
    {
        var shown = new HashSet<Guid>();
        foreach (PageObject item in objects.AsEnumerable().Reverse())
        {
            if (item.Group is not { } group)
            {
                yield return new ObjectRow(page, item, ToolStyles.Describe(item), "");
                continue;
            }
            if (!shown.Add(group)) continue;
            var members = objects.Where(other => other.Group == group).Reverse().ToList();
            yield return new ObjectRow(page, item, ToolStyles.GroupTitle(members), "", members);
            if (folded?.Contains(group) == true) continue;
            foreach (PageObject member in members) yield return new ObjectRow(page, member, ToolStyles.Describe(member), "", Indented: true);
        }
    }

    static IEnumerable<ObjectRow> CommentRows(EditDocument document) =>
        document.Pages.SelectMany((entry, page) => entry.Objects
            .Where(item => item.IsMarkup || item.Kind == MarkKind.Note)
            .Select(item => new ObjectRow(page, item, item.Comment.Length > 0 ? FirstLine(item.Comment) : ToolStyles.Describe(item),
                $"Page {page + 1} · {ToolStyles.Describe(item)}")));

    static string FirstLine(string text) => text.Split('\n')[0] is { Length: > 60 } line ? line[..60] + "…" : text.Split('\n')[0];

    static void SetNumber(InputBox input, float value)
    {
        if (!input.Box.Focused) input.Text = Math.Round(value).ToString(CultureInfo.CurrentCulture);
    }

    void ApplyGeometry()
    {
        if (_syncing || _canvas.Selection.Count != 1) return;
        PageObject item = _canvas.Selection[0];
        if (_x.Number is not { } x || _y.Number is not { } y || _w.Number is not { } w || _h.Number is not { } h || w < 1 || h < 1) return;
        var target = new RectangleF(x, y, w, h);
        if (Rectangle.Round(item.Bounds) == Rectangle.Round(target)) return;
        _canvas.ChangeSelection(selected =>
        {
            if (selected.Width != target.Width || selected.Height != target.Height) selected.Resize(new RectangleF(selected.X, selected.Y, target.Width, target.Height));
            selected.Offset(target.X - selected.X, target.Y - selected.Y);
        });
    }

    void ApplyComment()
    {
        if (_syncing || _commentTarget is not { } target) return;
        string text = _comment.Text.Replace("\r\n", "\n").TrimEnd();
        if (text == target.Comment) return;
        _canvas.ChangeObject(target, item => item.Comment = text);
    }

    /// <summary>
    /// Layers dragged in the list move to where they were dropped, joining the group they land in or leaving the
    /// one they were in. A group dragged by its own row moves whole, and lands beside other groups, not inside.
    /// </summary>
    void DropLayers(IReadOnlyList<ObjectRow> dragged, int before, ObjectRow? into)
    {
        int page = LayersPage;
        IReadOnlyList<ObjectRow> rows = _layers.Rows;
        var moving = dragged.SelectMany(row => row.Objects).Distinct().ToList();
        if (moving.Count == 0 || _canvas.Document is not { } document) return;
        var wholeGroups = dragged.Where(row => row.Members is not null).Select(row => row.Item.Group).ToHashSet();
        // Every object once, top first, folded groups included (a group's row stands for the rows under it).
        var layers = LayerRows(document.Pages[page].Objects, page).Where(row => row.Members is null).Select(row => row.Item).ToList();
        int from = into is not null ? rows.ToList().IndexOf(into) + (_folded.Contains(into.Item.Group ?? Guid.Empty) ? 0 : 1) : Math.Max(0, before);
        Guid? group = into is not null ? into.Item.Group : before >= 0 && before < rows.Count && rows[before].Indented ? rows[before].Item.Group : null;
        // They go just above the first layer at the drop point that isn't itself moving; a folded group's row
        // stands for its objects.
        PageObject? anchor = rows.Skip(from).SelectMany(row => row.Members is null ? [row.Item] : _folded.Contains(row.Item.Group ?? Guid.Empty) ? row.Members : [])
            .FirstOrDefault(item => !moving.Contains(item));
        if (wholeGroups.Count > 0 && group is { } target && !wholeGroups.Contains(target))
        {
            anchor = layers.FirstOrDefault(item => item.Group == target && !moving.Contains(item));
            group = null;
        }
        var order = layers.Where(item => !moving.Contains(item)).ToList();
        order.InsertRange(anchor is null ? order.Count : order.IndexOf(anchor), layers.Where(moving.Contains));
        _canvas.Restack(page, [.. order.Select(item => (item, moving.Contains(item) && !wholeGroups.Contains(item.Group) ? group : item.Group))]);
        _canvas.Select(page, moving, withGroups: false);
    }

    void ShowLayerMenu(ObjectRow row, Point at)
    {
        int count = _canvas.Selection.Count;
        bool hidden = row.Objects.All(item => item.Hidden);
        var menu = Menus.Create();
        menu.ShowImageMargin = false;
        bool locked = row.Objects.All(item => item.Locked);
        menu.Items.Add(Menus.Item(row.Members is null ? "Rename" : "Rename Group", () => _layers.BeginRename(row), "F2"));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Menus.Item(hidden ? "Show" : "Hide", () => ToggleHidden(row)));
        menu.Items.Add(Menus.Item(locked ? "Unlock" : "Lock", () => { _canvas.SetLocked(row.Objects, !locked); Sync(); }));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Menus.Item("Cut", _canvas.Cut, "Ctrl+X"));
        menu.Items.Add(Menus.Item("Copy", _canvas.Copy, "Ctrl+C"));
        menu.Items.Add(Menus.Item(count > 1 ? $"Duplicate {count} Objects" : "Duplicate", _canvas.DuplicateSelection, "Ctrl+D"));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Menus.Item("Bring to Front", () => _canvas.Arrange(toFront: true)));
        menu.Items.Add(Menus.Item("Bring Forward", () => _canvas.ArrangeStep(forward: true)));
        menu.Items.Add(Menus.Item("Send Backward", () => _canvas.ArrangeStep(forward: false)));
        menu.Items.Add(Menus.Item("Send to Back", () => _canvas.Arrange(toFront: false)));
        if (_canvas.CanGroup || _canvas.CanUngroup) menu.Items.Add(new ToolStripSeparator());
        if (_canvas.CanGroup) menu.Items.Add(Menus.Item("Group", _canvas.GroupSelection, "Ctrl+G"));
        if (_canvas.CanUngroup) menu.Items.Add(Menus.Item("Ungroup", _canvas.UngroupSelection, "Ctrl+Shift+G"));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Menus.Item(count > 1 ? $"Delete {count} Objects" : "Delete", _canvas.DeleteSelection, "Del"));
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        Menus.Show(menu, _layers, at);
    }

    /// <summary>
    /// Names a layer, or a group (the name kept on each of its objects). An empty name goes back to the one Sheaf gives:
    /// what the object is, or how many a group has.
    /// </summary>
    void RenameLayer(ObjectRow row, string name)
    {
        name = name.Trim();
        if (row.Members is { } members)
        {
            if (members.All(member => member.GroupName == name)) return;
            _canvas.ChangeObjects(members, member => member.GroupName = name);
        }
        else
        {
            if (row.Item.Name == name) return;
            _canvas.ChangeObject(row.Item, item => item.Name = name);
        }
        Sync();
    }

    /// <summary>Hides a layer, or a whole group, or shows it again.</summary>
    void ToggleHidden(ObjectRow row)
    {
        bool hide = row.Objects.Any(item => !item.Hidden);
        _canvas.ChangeObjects(row.Objects, item => item.Hidden = hide);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Theme.Changed -= OnThemeChanged;
        base.Dispose(disposing);
    }
}
