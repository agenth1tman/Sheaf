using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using Sheaf.Core.IO;
using Sheaf.Core.Rendering;
using Sheaf.Platform;
using Sheaf.Shell;
using Sheaf.Tools;
using Sheaf.Ui;
using Sheaf.Ui.Controls;

namespace Sheaf.Pages;

/// <summary>Home: open or scan, the files you worked on last, and every tool by what it does.</summary>
internal sealed class HomePage : TabContent
{
    const int RecentCount = 6;
    readonly InputBox _search = Make.Input("Find a tool or a recent file", maxLength: 60);
    readonly RecentStrip _recent = new() { ColumnCount = RecentCount, AutoSize = true, Dock = DockStyle.Top, Margin = Padding.Empty };
    readonly TextLabel _recentHeading = Make.Section("Recent", topSpace: 0);
    readonly TextLabel _recentPosition = new("", Tone.Tertiary) { Font = AppFonts.Small, Margin = new Padding(0, 7, 8, 0) };
    readonly AppButton _newer = new("", ButtonKind.Quiet, "chevron-left") { LogicalHeight = 28, AccessibleName = "Newer Files" };
    readonly AppButton _older = new("", ButtonKind.Quiet, "chevron-right") { LogicalHeight = 28, AccessibleName = "Older Files" };
    readonly TableLayoutPanel _recentHeader = new()
    {
        ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top,
        Margin = new Padding(0, 36, 0, 0), Padding = Padding.Empty,
    };
    // Which six of the recent files show: 0 for the newest.
    int _recentPage;
    readonly List<ToolRow> _toolRows = [];
    readonly List<(TextLabel Heading, Control Column)> _groups = [];
    readonly TextLabel _noMatch = Make.Line("", Tone.Tertiary);
    readonly Dictionary<string, Bitmap?> _thumbnails = new(StringComparer.OrdinalIgnoreCase);
    readonly List<TileActor> _actors = [];
    Tween? _animation;
    double _animationMs;
    HistoryEntry? _focusEntry;
    int _focusColumn = -1;

    /// <summary>Takes the keyboard focus when it leaves the search box, so the box can be left without tabbing.</summary>
    readonly Control _focusSink = new() { Size = Size.Empty, TabStop = false, AccessibleName = "Home", AccessibleRole = AccessibleRole.Pane };

    public HomePage(IShell shell) : base(shell)
    {
        SuspendLayout();
        var scroller = new Surface { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(64, 36, 64, 40) };
        var content = Make.Stack(BuildStartRow(), Make.Hint("Drop PDFs, images or Office files anywhere in this window to open them."),
            _recentHeader, _recent, Make.Section("Tools", topSpace: 36), BuildTools(), _noMatch);
        content.Dock = DockStyle.Top;
        scroller.Controls.Add(content);
        Controls.Add(scroller);
        Controls.Add(_focusSink);
        LeaveSearchOnClick(scroller);

        for (int i = 0; i < RecentCount; i++) _recent.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / RecentCount));
        _recentHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _recentHeader.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var paging = Make.Row(_recentPosition, _newer, _older);
        paging.Anchor = AnchorStyles.Right | AnchorStyles.Top;
        _newer.Margin = new Padding(0, 0, 2, 0);
        _older.Margin = Padding.Empty;
        _recentHeader.Controls.Add(_recentHeading, 0, 0);
        _recentHeader.Controls.Add(paging, 1, 0);
        Tips.Shared.SetToolTip(_newer, "Newer files");
        Tips.Shared.SetToolTip(_older, "Older files");
        _newer.Click += (_, _) => TurnRecentPage(-1);
        _older.Click += (_, _) => TurnRecentPage(1);
        _noMatch.Visible = false;
        _search.Caption = "Find a tool or a recent file";
        _search.Icon = "search";
        _search.Clearable = true;
        _search.TextChanged += (_, _) => Filter(_search.Text);
        _search.EnterPressed += (_, _) => _toolRows.FirstOrDefault(row => row.Visible)?.PerformClick();
        History.Changed += OnHistoryChanged;
        ReloadRecent();
        ResumeLayout(false);
    }

    public override string Title => "Home";

    public override string Icon => "home";

    Control BuildStartRow()
    {
        var open = new AppButton("Open PDF…", ButtonKind.Primary, "folder") { LogicalHeight = 40 };
        var scan = new AppButton("Scan a Document", ButtonKind.Secondary, "scan") { LogicalHeight = 40 };
        var blank = new AppButton("Blank PDF", ButtonKind.Secondary, "file-plus") { LogicalHeight = 40 };
        var image = new AppButton("Image", ButtonKind.Secondary, "image") { LogicalHeight = 40, Chevron = true };
        image.Click += (_, _) => ShowImageMenu(image);
        Tips.Shared.SetToolTip(image, "Edit a picture, or start a blank one\nExport it as an image or a PDF");
        open.Click += (_, _) => { foreach (string file in Shell.PickFiles(FileKind.Pdf, multiple: true)) Shell.OpenDocument(file); };
        scan.Click += (_, _) => Shell.OpenScan();
        blank.Click += (_, _) => Shell.OpenBlankDocument();
        _search.Width = 380;
        _search.Height = 40;

        var row = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = Padding.Empty };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.Controls.Add(Make.Row(open, scan, blank, image), 0, 0);
        _search.Anchor = AnchorStyles.Right;
        row.Controls.Add(_search, 1, 0);
        return row;
    }

    static readonly (string Name, Size Pixels)[] ImageSizes =
    [
        ("Blank 1920 × 1080 (Wide)", new Size(1920, 1080)),
        ("Blank 1080 × 1080 (Square)", new Size(1080, 1080)),
        ("Blank 1080 × 1920 (Tall)", new Size(1080, 1920)),
        ("Blank 1200 × 630 (Link Preview)", new Size(1200, 630)),
        ("Blank A4 at 150 dpi (1240 × 1754)", new Size(1240, 1754)),
    ];

    void ShowImageMenu(AppButton button)
    {
        var menu = Menus.Create();
        menu.Items.Add(Menus.Item("Open a Picture to Edit…", () =>
        {
            if (Shell.PickFiles(FileKind.Image, multiple: false).FirstOrDefault() is { } path) Shell.OpenImageDocument(path, Size.Empty);
        }));
        menu.Items.Add(new ToolStripSeparator());
        foreach (var (name, pixels) in ImageSizes) menu.Items.Add(Menus.Item(name, () => Shell.OpenImageDocument(null, pixels)));
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        Menus.Show(menu, button, Menus.Below(button));
    }

    Control BuildTools()
    {
        var grid = new TableLayoutPanel { ColumnCount = 4, AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = Padding.Empty };
        for (int i = 0; i < 4; i++) grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        foreach (var category in ToolCatalog.All.GroupBy(info => info.Category).OrderBy(group => group.Key))
        {
            var heading = new TextLabel(ToolInfo.CategoryName(category.Key), Tone.Tertiary) { Font = AppFonts.Small, Margin = new Padding(10, 0, 0, 6) };
            var rows = new List<Control> { heading };
            foreach (ToolInfo info in category)
            {
                var row = new ToolRow(info);
                row.Click += (_, _) => Shell.OpenTool(info.Id);
                _toolRows.Add(row);
                rows.Add(row);
            }
            var column = Make.Stack([.. rows]);
            column.Margin = new Padding(0, 0, 24, 0);
            column.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
            grid.Controls.Add(column);
            _groups.Add((heading, column));
        }
        return grid;
    }

    /// <summary>A click on anything that isn't a control of its own (background, headings) leaves the search box.</summary>
    void LeaveSearchOnClick(Control parent)
    {
        parent.MouseDown += (_, _) => { if (_search.Box.Focused) _focusSink.Focus(); };
        foreach (Control child in parent.Controls)
            if (child is not (InputBox or ButtonBase)) LeaveSearchOnClick(child);
    }

    void OnHistoryChanged(object? sender, EventArgs e)
    {
        if (!IsDisposed) ReloadRecent();
    }

    /// <summary>
    /// Brings the Recent row up to date. When Home is on screen the change animates: a removed file fades out, the files
    /// after it glide over and the next one fades in. For that half second the row hides its tiles and draws pictures
    /// of them in one surface, at fractions of a pixel, which moves far more smoothly than moving the tiles' windows.
    /// </summary>
    static RectangleF Shifted(RectangleF rect, float dx) => new(rect.X + dx, rect.Y, rect.Width, rect.Height);

    void TurnRecentPage(int direction)
    {
        int pages = (History.Recent(int.MaxValue).Count + RecentCount - 1) / RecentCount;
        int page = Math.Clamp(_recentPage + direction, 0, Math.Max(0, pages - 1));
        if (page == _recentPage) return;
        _recentPage = page;
        ReloadRecent(direction);
        // The keyboard stays on whichever arrow still leads somewhere.
        if (!(direction < 0 ? _newer : _older).Enabled) (direction < 0 ? _older : _newer).Focus();
    }

    /// <summary>The paging arrows and "7–12 of 20", shown only when there are more files than fit.</summary>
    void UpdateRecentPaging(int total)
    {
        bool paged = total > RecentCount;
        _recentPosition.Visible = _newer.Visible = _older.Visible = paged;
        if (!paged) return;
        int first = _recentPage * RecentCount + 1, last = Math.Min(total, first + RecentCount - 1);
        _recentPosition.Text = $"{first}–{last} of {total}";
        _newer.Enabled = _recentPage > 0;
        _older.Enabled = last < total;
    }

    /// <param name="direction">1 or -1 when paging: the row slides the old files out and the next ones in.</param>
    void ReloadRecent(int direction = 0)
    {
        var all = History.Recent(int.MaxValue);
        if (_recentPage * RecentCount >= all.Count) _recentPage = Math.Max(0, (all.Count - 1) / RecentCount);
        var entries = all.Skip(_recentPage * RecentCount).Take(RecentCount).ToList();
        UpdateRecentPaging(all.Count);
        // Where the pictures are now, if an earlier change is still moving, so this one carries on from there.
        var drawnAt = _actors.Where(actor => actor.Entry is not null).ToDictionary(actor => actor.Entry!, actor => actor.Bounds(_animationMs));
        EndAnimation();
        var tiles = _recent.Controls.OfType<RecentTile>().ToDictionary(tile => tile.Entry);
        bool animate = Motion.Enabled && IsHandleCreated && IsInFront && _recent.Visible && tiles.Count > 0 && _search.Text.Length == 0;
        if (tiles.Values.FirstOrDefault(tile => tile.ContainsFocus) is { } focused)
        {
            (_focusEntry, _focusColumn) = (focused.Entry, _recent.GetColumn(focused));
            _focusSink.Focus();
        }
        int[] widths = _recent.GetColumnWidths();
        RecentTile? first = tiles.Values.FirstOrDefault();
        (int top, int height, Padding margin) = first is null ? (0, 0, Padding.Empty) : (first.Top, first.Height, first.Margin);
        Rectangle Slot(int column) => new(_recent.Padding.Left + widths.Take(column).Sum() + margin.Left, top, widths[column] - margin.Horizontal, height);

        _recent.SuspendLayout();
        // Paging moves the whole row by its width: the old files slide out one side as the next slide in the other.
        int shift = direction * _recent.ClientSize.Width;
        foreach (var (entry, tile) in tiles)
        {
            if (entries.Contains(entry)) continue;
            if (animate)
            {
                RectangleF at = drawnAt.GetValueOrDefault(entry, tile.Bounds);
                _actors.Add(direction == 0
                    ? new TileActor(tile.Snapshot(), null, tile.Bounds, tile.Bounds, TileActor.Kind.Leaving)
                    : new TileActor(tile.Snapshot(), null, at, Shifted(at, -shift), TileActor.Kind.Moving));
            }
            tile.Dispose();
        }
        for (int i = 0; i < entries.Count; i++)
        {
            if (tiles.TryGetValue(entries[i], out RecentTile? tile))
            {
                _recent.SetCellPosition(tile, new TableLayoutPanelCellPosition(i, 0));
                if (animate) _actors.Add(new TileActor(tile.Snapshot(), tile.Entry, drawnAt.GetValueOrDefault(tile.Entry, tile.Bounds), Slot(i), TileActor.Kind.Moving));
                continue;
            }
            tile = NewTile(entries[i]);
            _recent.Controls.Add(tile, i, 0);
            if (!animate) continue;
            tile.Visible = false;
            tile.Bounds = Slot(i);
            _ = tile.Handle; // for its DPI, before it is pictured
            _actors.Add(direction == 0
                ? new TileActor(tile.Snapshot(), tile.Entry, tile.Bounds, tile.Bounds, TileActor.Kind.Arriving)
                : new TileActor(tile.Snapshot(), tile.Entry, Shifted(Slot(i), shift), Slot(i), TileActor.Kind.Moving));
        }
        PrefetchNextThumbnail();
        if (!animate)
        {
            _recentHeader.Visible = _recent.Visible = entries.Count > 0;
            _recent.ResumeLayout(true);
            RestoreFocus();
            return;
        }
        // The table stays out of it until the end, so it doesn't snap the tiles to their places first.
        foreach (RecentTile tile in _recent.Controls.OfType<RecentTile>()) tile.Visible = false;
        _recent.Stage = DrawActors;
        _animation = Motion.Run(TileActor.TotalMs, t =>
        {
            _animationMs = t * TileActor.TotalMs;
            _recent.Invalidate();
        }, Easing.Linear, EndAnimation);
    }

    void DrawActors(Graphics g)
    {
        g.InterpolationMode = InterpolationMode.Bilinear;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.CompositingQuality = CompositingQuality.HighSpeed;
        Color background = Draw.BackgroundOf(_recent);
        // Fading tiles first, so a tile gliding over one passes in front of it.
        foreach (TileActor actor in _actors.OrderBy(actor => actor.Role == TileActor.Kind.Moving)) actor.Draw(g, _animationMs, background);
    }

    /// <summary>Puts the real tiles back in their places, as the last picture showed them.</summary>
    void EndAnimation()
    {
        if (_recent.Stage is null) return;
        _animation?.Cancel();
        _animation = null;
        _recent.Stage = null;
        foreach (TileActor actor in _actors) actor.Dispose();
        _actors.Clear();
        var tiles = _recent.Controls.OfType<RecentTile>().ToList();
        foreach (RecentTile tile in tiles) tile.Visible = true;
        _recentHeader.Visible = _recent.Visible = tiles.Count > 0;
        _recent.ResumeLayout(true);
        _recent.Invalidate();
        if (_search.Text.Length > 0) Filter(_search.Text);
        RestoreFocus();
    }

    /// <summary>Keeps the keyboard in the Recent row: on the same file, or on the one that took the removed one's place.</summary>
    void RestoreFocus()
    {
        if (_focusColumn < 0) return;
        var tiles = _recent.Controls.OfType<RecentTile>().OrderBy(_recent.GetColumn).ToList();
        Control target = tiles.FirstOrDefault(tile => tile.Entry == _focusEntry)
            ?? (tiles.Count > 0 ? tiles[Math.Min(_focusColumn, tiles.Count - 1)] : _focusSink);
        (_focusEntry, _focusColumn) = (null, -1);
        target.Focus();
    }

    RecentTile NewTile(HistoryEntry entry)
    {
        var tile = new RecentTile(entry, _thumbnails.GetValueOrDefault(entry.Path)) { Anchor = AnchorStyles.Left | AnchorStyles.Right };
        tile.Click += (_, _) => OpenRecent(entry.Path);
        tile.RemoveRequested += (_, _) => RemoveRecent(tile);
        tile.MouseUp += (_, e) => { if (e.Button == MouseButtons.Right) ShowRecentMenu(tile, e.Location); };
        if (!_thumbnails.ContainsKey(entry.Path)) _ = LoadThumbnailAsync(entry.Path);
        return tile;
    }

    /// <summary>Loads the first page of the file that would come in next, so it arrives with its picture.</summary>
    void PrefetchNextThumbnail()
    {
        int after = (_recentPage + 1) * RecentCount;
        if (History.Recent(after + 1).ElementAtOrDefault(after) is { } next && !_thumbnails.ContainsKey(next.Path))
            _ = LoadThumbnailAsync(next.Path);
    }

    async Task LoadThumbnailAsync(string path)
    {
        if (FileKinds.Of(path) != FileKind.Pdf) return;
        _thumbnails[path] = null;
        // Once the window has drawn: rendering while it opens would compete with it for the processor.
        await WhenIdle();
        int width = LogicalToDeviceUnits(RecentTile.PageWidth) * 2;
        try
        {
            // Off the UI thread: the first PDF opened also loads Windows' PDF engine, which would hold up the window.
            Bitmap bitmap = await Task.Run(async () =>
            {
                using PdfPreview preview = await PdfPreview.OpenAsync(path);
                return await preview.RenderAsync(0, width);
            });
            _thumbnails[path] = bitmap;
            if (_recent.Controls.OfType<RecentTile>().FirstOrDefault(tile => tile.Entry.Path == path) is { } tile) tile.Thumbnail = bitmap;
        }
        catch (Core.ToolException) { }
    }

    /// <summary>Puts the cursor in the search box and makes it glow for a moment: a new tab starts here.</summary>
    public void Beckon()
    {
        _search.Focus();
        _search.Pulse();
    }

    /// <summary>Completes the next time the app has no messages waiting, such as input or painting.</summary>
    static Task WhenIdle()
    {
        var idle = new TaskCompletionSource();
        void OnIdle(object? sender, EventArgs e)
        {
            Application.Idle -= OnIdle;
            idle.TrySetResult();
        }
        Application.Idle += OnIdle;
        return idle.Task;
    }

    void RemoveRecent(RecentTile tile)
    {
        string path = tile.Entry.Path;
        History.Remove([.. History.Entries.Where(item => string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase))]);
        ScreenReader.Say(this, $"Removed {Path.GetFileName(path)} from Recent");
    }

    void ShowRecentMenu(RecentTile tile, Point location)
    {
        string path = tile.Entry.Path;
        var menu = Menus.Create();
        menu.Items.Add(Menus.Item("Open", () => OpenRecent(path)));
        var folder = Menus.Item("Show in Folder", () => Native.ShowInExplorer(path));
        folder.Enabled = File.Exists(path) || Directory.Exists(path);
        menu.Items.Add(folder);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Menus.Item("Remove from Recent", () => RemoveRecent(tile), "Del"));
        menu.Items.Add(Menus.Item("Clear Recent", ClearRecent));
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        Menus.Show(menu, tile, location);
    }

    void ClearRecent()
    {
        var page = new TaskDialogPage
        {
            Caption = "Sheaf",
            Heading = "Clear the Recent list?",
            Text = "The files themselves stay where they are.",
            Buttons = { new TaskDialogButton("Clear Recent"), TaskDialogButton.Cancel },
            Icon = TaskDialogIcon.None,
        };
        if (TaskDialog.ShowDialog(this, page) != page.Buttons[0]) return;
        History.Clear();
    }

    void OpenRecent(string path)
    {
        if (FileKinds.Of(path) == FileKind.Pdf) Shell.OpenDocument(path);
        else if (Directory.Exists(path) || File.Exists(path)) Native.ShowInExplorer(path);
    }

    void Filter(string text)
    {
        string query = text.Trim();
        SuspendLayout();
        foreach (ToolRow row in _toolRows) row.Visible = query.Length == 0 || row.Matches(query);
        foreach (var (heading, column) in _groups) heading.Visible = column.Controls.OfType<ToolRow>().Any(row => row.Visible);
        foreach (RecentTile tile in _recent.Controls.OfType<RecentTile>()) tile.Visible = query.Length == 0 || tile.Matches(query);
        _noMatch.Text = $"Nothing matches “{query}”.";
        _noMatch.Visible = _toolRows.All(row => !row.Visible) && _recent.Controls.OfType<RecentTile>().All(tile => !tile.Visible);
        ResumeLayout(true);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.K) || keyData == (Keys.Control | Keys.F))
        {
            _search.Focus();
            _search.Box.SelectAll();
            return true;
        }
        if (keyData == Keys.Escape && _search.Box.Focused)
        {
            if (_search.Text.Length > 0) _search.Text = "";
            else _focusSink.Focus();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            History.Changed -= OnHistoryChanged;
            foreach (Bitmap? bitmap in _thumbnails.Values) bitmap?.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>One tool on Home: an icon (or a file-type badge) and its name.</summary>
internal sealed class ToolRow : Button
{
    /// <summary>Small enough for "DOC" to fit the 22 px badge.</summary>
    static readonly Font BadgeFont = new(AppFonts.SemiBoldFamily, 6.5f);

    readonly ToolInfo _tool;
    readonly Fade _hover;
    readonly Fade _press;
    readonly RestPicture _rest = new();

    public ToolRow(ToolInfo tool)
    {
        _tool = tool;
        _hover = new Fade(this);
        _press = new Fade(this, 90);
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Text = tool.Title;
        AccessibleDescription = tool.Summary;
        UseMnemonic = false;
        Height = 36;
        Margin = Padding.Empty;
        Anchor = AnchorStyles.Left | AnchorStyles.Right;
        Tips.Shared.SetToolTip(this, tool.Summary);
    }

    public bool Matches(string query) =>
        _tool.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
        _tool.Summary.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
        ToolInfo.CategoryName(_tool.Category).Contains(query, StringComparison.CurrentCultureIgnoreCase);

    protected override void OnMouseEnter(EventArgs e) { _hover.To(1); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover.To(0); _press.To(0); base.OnMouseLeave(e); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) _press.To(1);
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e) { _press.To(0); base.OnMouseUp(e); }

    protected override void OnPaint(PaintEventArgs e) =>
        _rest.Paint(this, e.Graphics, _hover.Value == 0 && _press.Value == 0 && !(Focused && ShowFocusCues), PaintRow);

    protected override void Dispose(bool disposing)
    {
        if (disposing) _rest.Dispose();
        base.Dispose(disposing);
    }

    void PaintRow(Graphics g)
    {
        Palette p = Theme.Current;
        g.Clear(Draw.BackgroundOf(this));
        int radius = LogicalToDeviceUnits(Theme.Radius);
        Draw.Box(g, ClientRectangle, radius, Theme.Blend(Theme.Blend(Color.FromArgb(0, p.Hover), p.Hover, _hover.Value), p.Active, _press.Value));
        if (Focused && ShowFocusCues) Draw.FocusRing(g, ClientRectangle, radius, LogicalToDeviceUnits(2));

        int pad = LogicalToDeviceUnits(10), badge = LogicalToDeviceUnits(22);
        var badgeBox = new Rectangle(pad, (Height - badge) / 2, badge, badge);
        if (_tool.FileBadge is { } label)
        {
            Draw.Box(g, badgeBox, LogicalToDeviceUnits(5), BadgeColor(label));
            // Smoothed with GDI+, not GDI: GDI's ClearType text drawn into the row's cached picture left coloured
            // fringes on the badge that vanished on hover, when the row is drawn live.
            var state = g.Save();
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using (var white = new SolidBrush(Color.White))
            using (var centred = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
                // The letters' box, not the line's: capitals sit in the middle rather than above it.
                float drop = BadgeFont.SizeInPoints * g.DpiY / 72f * 0.06f;
                g.DrawString(label, BadgeFont, white, new RectangleF(badgeBox.X, badgeBox.Y + drop, badgeBox.Width, badgeBox.Height), centred);
            }
            g.Restore(state);
        }
        else
        {
            Draw.Box(g, badgeBox, LogicalToDeviceUnits(5), p.Raised);
            int icon = LogicalToDeviceUnits(14);
            Icons.Draw(g, _tool.Icon, new Rectangle(badgeBox.X + (badge - icon) / 2, badgeBox.Y + (badge - icon) / 2, icon, icon), p.Text2, 1.9f);
        }
        int textLeft = badgeBox.Right + LogicalToDeviceUnits(12);
        Draw.Text(g, _tool.Title, AppFonts.Body, new Rectangle(textLeft, 0, Width - textLeft - pad, Height), p.Text);
    }

    static Color BadgeColor(string label) => label switch
    {
        "DOC" => Theme.Hex(0x2B5797),
        "XLS" => Theme.Hex(0x1E7145),
        "PPT" => Theme.Hex(0xC2410C),
        "JPG" => Theme.Hex(0xB7791F),
        "ONE" => Theme.Hex(0x7719AA),
        _ => Theme.Hex(0x6B7280),
    };
}

/// <summary>A recently used file: its first page, its name and when it was last used.</summary>
internal sealed class RecentTile : Button
{
    public const int PageWidth = 104;
    const int ThumbHeight = 156;
    readonly HistoryEntry _entry;
    readonly Fade _hover;
    Bitmap? _thumbnail;
    Bitmap? _scaled;
    readonly RestPicture _rest = new();
    bool _overRemove;

    public RecentTile(HistoryEntry entry, Bitmap? thumbnail)
    {
        _entry = entry;
        AccessibleDescription = $"{Meta}. Delete removes it from Recent.";
        _thumbnail = thumbnail;
        _hover = new Fade(this);
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Text = Path.GetFileName(entry.Path);
        UseMnemonic = false;
        Height = ThumbHeight + 54;
        Margin = new Padding(0, 0, 16, 0);
        Tips.Shared.SetToolTip(this, entry.Path);
    }

    public Bitmap? Thumbnail
    {
        set
        {
            _thumbnail = value;
            _scaled?.Dispose();
            _scaled = null;
            _rest.Clear();
            Invalidate();
        }
    }

    public HistoryEntry Entry => _entry;

    /// <summary>The tile as it looks now, for animations that move pictures of tiles instead of the tiles.</summary>
    public Bitmap Snapshot()
    {
        // Drawn on an opaque bitmap first (GDI text needs one), then copied to the format GDI+ draws fastest.
        using var opaque = new Bitmap(Width, Height, PixelFormat.Format32bppRgb);
        using (Graphics g = Graphics.FromImage(opaque)) PaintTile(g);
        var picture = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb);
        using (Graphics g = Graphics.FromImage(picture)) g.DrawImageUnscaled(opaque, 0, 0);
        return picture;
    }

    /// <summary>The tile's × (or Delete) was pressed: take the file off the Recent list.</summary>
    public event EventHandler? RemoveRequested;

    Rectangle RemoveBounds => new(Width - LogicalToDeviceUnits(30), LogicalToDeviceUnits(6), LogicalToDeviceUnits(24), LogicalToDeviceUnits(24));

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        bool over = RemoveBounds.Contains(e.Location);
        if (over == _overRemove) return;
        _overRemove = over;
        Invalidate();
    }

    protected override void OnClick(EventArgs e)
    {
        if (_overRemove) { RemoveRequested?.Invoke(this, EventArgs.Empty); return; }
        base.OnClick(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _scaled?.Dispose();
            _rest.Dispose();
        }
        base.Dispose(disposing);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Delete) RemoveRequested?.Invoke(this, EventArgs.Empty);
    }

    string Meta => $"{_entry.Tool} · {When(_entry.Made)}";

    static string When(DateTime made)
    {
        var culture = CultureInfo.CurrentCulture;
        if (made.Date == DateTime.Today) return "Today, " + made.ToString("HH:mm", culture);
        if (made.Date == DateTime.Today.AddDays(-1)) return "Yesterday";
        return made.ToString("d MMM", culture);
    }

    public bool Matches(string query) => Text.Contains(query, StringComparison.CurrentCultureIgnoreCase);

    protected override void OnMouseEnter(EventArgs e) { _hover.To(1); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover.To(0); _overRemove = false; base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e) => _rest.Paint(this, e.Graphics, _hover.Value == 0 && !(Focused && ShowFocusCues), PaintTile);

    /// <summary>The thumbnail at the size it is shown, scaled once in high quality rather than on every paint.</summary>
    Bitmap Scaled(Bitmap image, Size size)
    {
        if (_scaled?.Size == size) return _scaled;
        _scaled?.Dispose();
        _scaled = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
        using Graphics g = Graphics.FromImage(_scaled);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawImage(image, new Rectangle(Point.Empty, size));
        return _scaled;
    }

    void PaintTile(Graphics g)
    {
        Palette p = Theme.Current;
        g.Clear(Draw.BackgroundOf(this));
        int radius = LogicalToDeviceUnits(Theme.RadiusLarge), thumbHeight = LogicalToDeviceUnits(ThumbHeight);
        var frame = new Rectangle(0, 0, Width, thumbHeight);
        Draw.Box(g, frame, radius, p.Canvas, Theme.Blend(p.Line, p.Line2, _hover.Value));
        if (Focused && ShowFocusCues) Draw.FocusRing(g, frame, radius, LogicalToDeviceUnits(2));

        int pageWidth = LogicalToDeviceUnits(PageWidth), pageHeight = LogicalToDeviceUnits(140);
        var page = new Rectangle((Width - pageWidth) / 2, thumbHeight - pageHeight, pageWidth, pageHeight);
        var state = g.Save();
        g.SetClip(new Rectangle(1, 1, Width - 2, thumbHeight - 2));
        if (_thumbnail is { } image)
        {
            float scale = pageWidth / (float)image.Width;
            page = new Rectangle(page.X, thumbHeight - Math.Min(pageHeight, (int)(image.Height * scale)), pageWidth, (int)(image.Height * scale));
            g.DrawImageUnscaled(Scaled(image, page.Size), page.Location);
        }
        else if (Directory.Exists(_entry.Path))
        {
            int icon = LogicalToDeviceUnits(40);
            Icons.Draw(g, "folder", new Rectangle((Width - icon) / 2, (thumbHeight - icon) / 2, icon, icon), p.Text3);
        }
        else
        {
            g.FillRectangle(Brushes.White, page);
        }
        g.Restore(state);
        if (_hover.Value > 0.5)
        {
            Rectangle remove = RemoveBounds;
            Draw.Box(g, remove, remove.Width / 2f, _overRemove ? p.Active : Color.FromArgb(220, p.Raised), p.Line2);
            int glyph = LogicalToDeviceUnits(12);
            Icons.Draw(g, "close", new Rectangle(remove.X + (remove.Width - glyph) / 2, remove.Y + (remove.Height - glyph) / 2, glyph, glyph), p.Text, 2.2f);
        }

        int textTop = thumbHeight + LogicalToDeviceUnits(10);
        Draw.Text(g, Text, AppFonts.BodyMedium, new Rectangle(2, textTop, Width - 4, AppFonts.BodyMedium.Height + 2), p.Text);
        Draw.Text(g, Meta, AppFonts.Small, new Rectangle(2, textTop + AppFonts.BodyMedium.Height + LogicalToDeviceUnits(4), Width - 4, AppFonts.Small.Height + 2), p.Text3);
    }
}

/// <summary>The Recent row: a table of tiles that, while it animates, draws pictures of them itself.</summary>
internal sealed class RecentStrip : TableLayoutPanel
{
    public RecentStrip() => DoubleBuffered = true;

    /// <summary>Drawn over the row's background while its tiles are hidden for an animation.</summary>
    public Action<Graphics>? Stage { get; set; }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Stage?.Invoke(e.Graphics);
    }
}

/// <summary>A picture of a Recent tile that leaves, moves or arrives while the row animates.</summary>
internal sealed class TileActor(Bitmap picture, HistoryEntry? entry, RectangleF from, RectangleF to, TileActor.Kind role) : IDisposable
{
    public enum Kind { Leaving, Moving, Arriving }

    // A leaving tile fades out first; the others start gliding while it goes; a newcomer fades in as they settle.
    const double LeaveMs = 200, MoveStartMs = 70, MoveMs = 380, ArriveStartMs = 200, ArriveMs = 260;
    public const int TotalMs = 460;

    public HistoryEntry? Entry { get; } = entry;
    public Kind Role { get; } = role;

    public RectangleF Bounds(double ms) => Role == Kind.Moving ? Lerp(from, to, Part(ms, MoveStartMs, MoveMs, Easing.Drawer)) : from;

    public void Draw(Graphics g, double ms, Color background)
    {
        (double opacity, double scale) = Role switch
        {
            Kind.Leaving => (1 - Part(ms, 0, LeaveMs, Easing.Out), 1 - 0.08 * Part(ms, 0, LeaveMs, Easing.Out)),
            Kind.Arriving => (Part(ms, ArriveStartMs, ArriveMs, Easing.Out), 0.94 + 0.06 * Part(ms, ArriveStartMs, ArriveMs, Easing.Out)),
            _ => (1.0, 1.0),
        };
        if (opacity <= 0.004) return;
        RectangleF at = Bounds(ms);
        if (opacity >= 1 && scale >= 1)
        {
            // A plain copy is several times cheaper than smoothing, and at these frame rates each step is a pixel or two.
            g.DrawImageUnscaled(picture, (int)Math.Round(at.X), (int)Math.Round(at.Y));
            return;
        }
        float width = (float)(at.Width * scale), height = (float)(at.Height * scale);
        at = new RectangleF(at.X + (at.Width - width) / 2, at.Y + (at.Height - height) / 2, width, height);
        g.DrawImage(picture, at);
        if (opacity >= 1) return;
        // The row's background is one colour, so laying it over the picture fades the tile as alpha would, far faster.
        using var veil = new SolidBrush(Color.FromArgb((int)Math.Round(255 * (1 - opacity)), background));
        g.FillRectangle(veil, RectangleF.Inflate(at, 1, 1));
    }

    static double Part(double ms, double start, double length, Easing easing) => Motion.Ease(easing, Math.Clamp((ms - start) / length, 0, 1));

    static RectangleF Lerp(RectangleF a, RectangleF b, double t) =>
        new((float)(a.X + (b.X - a.X) * t), (float)(a.Y + (b.Y - a.Y) * t), (float)(a.Width + (b.Width - a.Width) * t), (float)(a.Height + (b.Height - a.Height) * t));

    public void Dispose() => picture.Dispose();
}
