using Sheaf.Core;
using Sheaf.Core.IO;
using Sheaf.Core.Ocr;
using Sheaf.Core.Scanning;
using Sheaf.Platform;
using Sheaf.Shell;
using Sheaf.Ui;
using Sheaf.Ui.Controls;

namespace Sheaf.Pages.Scan;

/// <summary>
/// Turns paper into a PDF: pages from the camera, a scanner or photos, each straightened and cleaned up, then
/// saved as one PDF, searchable if Windows can read its language. Everything happens on this PC.
/// </summary>
internal sealed class ScanPage : TabContent
{
    enum Source { Camera, Scanner, Photos }
    enum View { Source, Page, Corners }

    const int SteadyMilliseconds = 1200;
    static readonly (string Label, ScanFilter Filter)[] Filters =
        [("Auto", ScanFilter.Auto), ("Colour", ScanFilter.Colour), ("Greyscale", ScanFilter.Greyscale),
         ("Black & White", ScanFilter.BlackAndWhite), ("Whiteboard", ScanFilter.Whiteboard), ("Original", ScanFilter.Original)];

    readonly List<ScannedPage> _pages = [];
    readonly ChoiceGroup<Source> _source = new("Scan with", true, ("Camera", Source.Camera), ("Scanner", Source.Scanner), ("Photos", Source.Photos)) { Large = true };
    readonly ScanView _view = new() { Dock = DockStyle.Fill };
    readonly Surface _message = new(SurfaceRole.Canvas) { Dock = DockStyle.Fill };
    readonly Surface _viewBar = new(SurfaceRole.Canvas) { Dock = DockStyle.Bottom, Height = 64 };
    readonly FlowLayoutPanel _viewLeft = new() { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Left, Margin = Padding.Empty };
    readonly FlowLayoutPanel _viewRight = new() { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Right, Margin = Padding.Empty };
    readonly ScanStrip _strip = new() { Dock = DockStyle.Fill };
    readonly TextLabel _pagesHeading = Make.PanelHeading("Pages");
    readonly TextLabel _pageHeading = Make.PanelHeading("Page");
    readonly TableLayoutPanel _pageSection;
    readonly List<AppButton> _filterButtons = [];
    readonly AppButton _crop = new("Crop and Straighten", ButtonKind.Quiet, "scan") { LogicalHeight = 30 };
    readonly AppButton _rotate = new("Rotate", ButtonKind.Quiet, "rotate") { LogicalHeight = 30 };
    readonly AppButton _retake = new("Retake", ButtonKind.Quiet, "camera") { LogicalHeight = 30 };
    readonly AppButton _delete = new("Delete Page", ButtonKind.Quiet, "trash") { LogicalHeight = 30 };
    readonly Toggle _searchable = new("Make text searchable", TextRecognizer.DefaultLanguage is not null) { SwitchOnRight = true };
    readonly DropDown<string>? _language;
    readonly AppButton _save = new("Save as PDF", ButtonKind.Primary) { Stretch = true };
    readonly TextLabel _summary = new("", Tone.Tertiary) { Font = AppFonts.Small, Margin = new Padding(0, 8, 0, 0) };
    readonly BusyBar _busy = new();
    readonly AppButton _capture = new("Capture", ButtonKind.Primary, "camera");
    readonly Toggle _steady = new("Capture when steady", false);
    View _mode = View.Source;
    CameraFeed? _feed;
    IReadOnlyList<Camera> _cameras = [];
    Bitmap? _frame;
    PointF[]? _found, _steadyCorners;
    long _steadySince, _lastFind;
    bool _finding, _saving, _changed, _autoCaptured;
    string _status = "";

    public ScanPage(IShell shell) : base(shell)
    {
        var languages = TextRecognizer.Languages;
        if (languages.Count > 0)
        {
            _language = new DropDown<string>("Language", [.. languages.Select(language => (language.Name, language.Tag))]) { Stretch = true };
            if (TextRecognizer.DefaultLanguage is { } preferred) _language.Value = preferred.Tag;
        }
        _pageSection = BuildPageSection();
        SuspendLayout();
        BuildLayout();
        WireEvents();
        ResumeLayout(false);
    }

    public override string Title => "Scan";

    public override string Icon => "scan";

    public override bool IsDirty => _changed && _pages.Count > 0;

    public override bool IsBusy => _saving;

    // Layout ------------------------------------------------------------------------------------

    void BuildLayout()
    {
        var header = new Surface { Dock = DockStyle.Top, Height = Theme.HeaderHeight, Borders = AnchorStyles.Bottom };
        var headerLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Padding = new Padding(16, 0, 16, 0) };
        headerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        headerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        headerLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var title = new TextLabel("Scan") { Font = AppFonts.Section, Margin = new Padding(0, 0, 20, 0) };
        var left = Make.Row(title, _source);
        left.Anchor = AnchorStyles.Left;
        var privacy = new TextLabel("Nothing leaves this PC", Tone.Tertiary) { Font = AppFonts.Small, Anchor = AnchorStyles.Right };
        headerLayout.Controls.Add(left, 0, 0);
        headerLayout.Controls.Add(privacy, 1, 0);
        header.Controls.Add(headerLayout);

        var viewLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Padding = new Padding(24, 0, 24, 0) };
        viewLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        viewLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        viewLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        viewLayout.Controls.Add(_viewLeft, 0, 0);
        viewLayout.Controls.Add(_viewRight, 1, 0);
        _viewBar.Controls.Add(viewLayout);
        var center = new Surface(SurfaceRole.Canvas) { Dock = DockStyle.Fill };
        center.Controls.Add(_view);
        center.Controls.Add(_message);
        center.Controls.Add(_viewBar);

        var side = new Surface { Dock = DockStyle.Right, Width = Theme.SidePanelWidth, Borders = AnchorStyles.Left };
        var pagesHeader = Make.Stack(_pagesHeading);
        pagesHeader.Dock = DockStyle.Top;
        pagesHeader.Padding = new Padding(16, 14, 16, 0);
        side.Controls.Add(_strip);
        side.Controls.Add(pagesHeader);
        side.Controls.Add(_pageSection);
        side.Controls.Add(BuildOutputSection());

        var body = new Surface(SurfaceRole.Canvas) { Dock = DockStyle.Fill };
        body.Controls.Add(center);
        body.Controls.Add(side);
        Controls.Add(body);
        Controls.Add(_busy);
        Controls.Add(header);
    }

    TableLayoutPanel BuildPageSection()
    {
        var filters = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0, 0, 0, 8) };
        for (int i = 0; i < 2; i++) filters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        foreach (var (label, filter) in Filters)
        {
            var button = new AppButton(label, ButtonKind.Quiet) { LogicalHeight = 28, Stretch = true, Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0, 0, 4, 4) };
            button.Click += (_, _) => ChangePage(page => page.Filter = filter);
            _filterButtons.Add(button);
            filters.Controls.Add(button);
        }
        foreach (AppButton action in new[] { _crop, _rotate, _retake, _delete }) action.Margin = new Padding(0, 0, 0, 2);
        var section = Make.PanelSection(null, _pageHeading, filters, _crop, _rotate, _retake, _delete);
        section.Dock = DockStyle.Bottom;
        return section;
    }

    Control BuildOutputSection()
    {
        var controls = new List<Control> { _searchable };
        _searchable.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        if (_language is not null)
        {
            controls.Add(new FormField("Language", _language));
            controls.Add(Make.Hint("Uses the text recognition built into Windows."));
        }
        else
        {
            _searchable.Enabled = false;
            controls.Add(Make.Hint("Windows has no text recognition installed. Add a language with “Optical character recognition” in Windows Settings."));
        }
        _save.Margin = new Padding(0, 14, 0, 0);
        _save.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _save.ReserveWidthFor("Saving…");
        controls.Add(_save);
        controls.Add(_summary);
        var section = Make.PanelSection(null, [.. controls]);
        section.Dock = DockStyle.Bottom;
        var holder = new Surface { Dock = DockStyle.Bottom, AutoSize = true, Borders = AnchorStyles.Top };
        holder.Controls.Add(section);
        return holder;
    }

    void WireEvents()
    {
        _source.Changed += (_, _) => ShowSource();
        _strip.SelectionChanged += (_, _) => { if (_strip.Selected >= 0) ShowPage(View.Page); UpdatePageSection(); };
        _strip.MoveRequested += (_, move) => MovePage(move.From, move.To);
        _strip.MenuRequested += (_, point) => ShowPageMenu(point);
        _crop.Click += (_, _) => ShowPage(View.Corners);
        _rotate.Click += (_, _) => ChangePage(page => page.Rotation = (page.Rotation + 90) % 360);
        _retake.Click += (_, _) => Retake();
        _delete.Click += (_, _) => DeletePage();
        _save.Click += (_, _) => _ = SaveAsync();
        _capture.Click += (_, _) => TakePicture();
        _searchable.CheckedChanged += (_, _) => { if (_language is not null) _language.Enabled = _searchable.Checked; };
        Tips.Shared.SetToolTip(_capture, "Capture (Space)");
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        ShowSource();
        Refresh(selected: -1);
    }

    // Sources -----------------------------------------------------------------------------------

    void ShowSource()
    {
        _mode = View.Source;
        _view.EditCorners = false;
        if (_source.Value != Source.Camera) _ = StopCameraAsync();
        switch (_source.Value)
        {
            case Source.Camera when _feed is not null:
                ShowView();
                _view.Image = _frame;
                _view.Corners = _found;
                _view.Status = _status;
                SetViewBar([_steady], [Hint("Space to capture"), _capture]);
                break;
            case Source.Camera:
                _ = ShowCameraStartAsync();
                break;
            case Source.Scanner:
                var scan = new AppButton("Scan a Page…", ButtonKind.Primary, "scan");
                scan.Click += (_, _) => ScanWithScanner();
                ShowMessage("Scan from a Scanner", "Uses the scanner driver in Windows. Each scan becomes one page here; scan as many as you need.", scan);
                break;
            default:
                var choose = new AppButton("Choose Photos…", ButtonKind.Primary, "image");
                choose.Click += (_, _) => AddPhotos(Shell.PickFiles(FileKind.Image, multiple: true));
                ShowMessage("Use Photos of Pages", "Choose photos you took of documents, receipts or a whiteboard, or drop them here. Sheaf finds each page’s edges and straightens it.", choose);
                break;
        }
    }

    async Task ShowCameraStartAsync()
    {
        try { _cameras = await CameraFeed.FindAsync(); }
        catch (Exception ex) { ActivityLog.Error("listing cameras", ex); _cameras = []; }
        if (IsDisposed || _source.Value != Source.Camera || _feed is not null) return;
        if (_cameras.Count == 0)
        {
            var photos = new AppButton("Use Photos Instead", ButtonKind.Secondary);
            photos.Click += (_, _) => { _source.Value = Source.Photos; ShowSource(); };
            ShowMessage("No Camera Found", "Connect a webcam, or scan from a scanner or photos instead.", photos);
            return;
        }
        var start = new AppButton("Start Camera", ButtonKind.Primary, "camera");
        var picker = new DropDown<string>("Camera", [.. _cameras.Select(camera => (camera.Name, camera.Id))]);
        start.Click += (_, _) => _ = StartCameraAsync(_cameras.First(camera => camera.Id == picker.Value));
        Control[] extra = _cameras.Count > 1 ? [picker, start] : [start];
        ShowMessage("Scan with Your Camera", "Hold a page flat in good light. Sheaf outlines it, and takes the picture when you press Capture, or by itself when the page is steady.", extra);
    }

    async Task StartCameraAsync(Camera camera)
    {
        SetBusy(true);
        try
        {
            _feed = await CameraFeed.StartAsync(camera);
            _feed.Frame += OnFrame;
            ActivityLog.Write($"Camera started: {camera.Name}");
            _status = "Looking for a page…";
            if (_source.Value == Source.Camera) ShowSource();
        }
        catch (ToolException ex)
        {
            ShowMessage("The Camera Didn’t Start", ex.Message, RetryButton());
        }
        finally
        {
            SetBusy(false);
        }
    }

    AppButton RetryButton()
    {
        var retry = new AppButton("Try Again", ButtonKind.Secondary);
        retry.Click += (_, _) => ShowSource();
        return retry;
    }

    async Task StopCameraAsync()
    {
        if (_feed is not { } feed) return;
        _feed = null;
        feed.Frame -= OnFrame;
        if (_view.Image == _frame) _view.Image = null;
        _frame?.Dispose();
        _frame = null;
        _found = null;
        await feed.DisposeAsync();
    }

    void OnFrame(object? sender, Bitmap frame)
    {
        if (IsDisposed || !IsHandleCreated) { frame.Dispose(); return; }
        try
        {
            BeginInvoke(() =>
            {
                if (_feed is null || IsDisposed) { frame.Dispose(); return; }
                Bitmap? old = _frame;
                _frame = frame;
                if (_mode == View.Source && _source.Value == Source.Camera) _view.Image = _frame;
                old?.Dispose();
                FindPageInFrame();
            });
        }
        catch (InvalidOperationException)
        {
            frame.Dispose();
        }
    }

    /// <summary>Looks for the page in the latest frame a few times a second, and captures it once it holds still.</summary>
    void FindPageInFrame()
    {
        long now = Environment.TickCount64;
        if (_finding || _frame is null || now - _lastFind < 200) return;
        _finding = true;
        _lastFind = now;
        var copy = new Bitmap(_frame);
        _ = Task.Run(() =>
        {
            using (copy) return PageCorners.Find(copy);
        }).ContinueWith(task =>
        {
            _finding = false;
            if (IsDisposed || _feed is null) return;
            _found = task.IsCompletedSuccessfully ? task.Result : null;
            UpdateSteadiness();
            if (_mode == View.Source && _source.Value == Source.Camera)
            {
                _view.Corners = _found;
                _view.Status = _status;
            }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    void UpdateSteadiness()
    {
        if (_found is null)
        {
            _steadyCorners = null;
            _autoCaptured = false;
            _status = "Looking for a page…";
            return;
        }
        long now = Environment.TickCount64;
        float tolerance = _frame is null ? 0 : (_frame.Width + _frame.Height) * 0.012f;
        bool still = _steadyCorners is not null && _found.Zip(_steadyCorners).All(pair => Math.Abs(pair.First.X - pair.Second.X) < tolerance && Math.Abs(pair.First.Y - pair.Second.Y) < tolerance);
        if (!still)
        {
            _steadyCorners = _found;
            _steadySince = now;
            _autoCaptured = false;
        }
        _status = _steady.Checked && !_autoCaptured ? "Page found. Hold still to capture." : "Page found";
        if (_steady.Checked && still && !_autoCaptured && now - _steadySince > SteadyMilliseconds)
        {
            _autoCaptured = true;
            TakePicture();
        }
    }

    void TakePicture()
    {
        if (_frame is null || _feed is null) return;
        var page = new ScannedPage(new Bitmap(_frame), fromCamera: true);
        if (_found is not null) page.Corners = _found;
        _view.Flash();
        AddPage(page, select: false);
        _status = $"Captured page {_pages.Count}";
    }

    void ScanWithScanner()
    {
        try
        {
            string? path = ScannerDevice.ScanPage(Path.Combine(AppPaths.WorkFolder, "scans"));
            if (path is null) return;
            AddPhotos([path]);
            TryDelete(path);
        }
        catch (ToolException ex)
        {
            var retry = new AppButton("Scan a Page…", ButtonKind.Primary, "scan");
            retry.Click += (_, _) => ScanWithScanner();
            ShowMessage("Scanning Didn’t Work", ex.Message, retry);
        }
    }

    void AddPhotos(IEnumerable<string> paths)
    {
        int added = 0;
        foreach (string path in paths)
        {
            try
            {
                using var file = new Bitmap(path);
                var photo = new Bitmap(file);
                AddPage(new ScannedPage(photo, fromCamera: false), select: true);
                added++;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or OutOfMemoryException)
            {
                ActivityLog.Error($"reading {path}", ex);
            }
        }
        if (added == 0 && paths.Any()) _summary.Text = "Those files couldn’t be read as photos.";
    }

    public override bool AcceptDrop(string[] paths)
    {
        var photos = paths.Where(path => FileKinds.Of(path) == FileKind.Image).ToList();
        if (photos.Count == 0) return false;
        AddPhotos(photos);
        return true;
    }

    // Pages -------------------------------------------------------------------------------------

    void AddPage(ScannedPage page, bool select)
    {
        _pages.Add(page);
        _changed = true;
        Refresh(select ? _pages.Count - 1 : _strip.Selected);
        if (select) ShowPage(View.Page);
        _ = ProcessAsync(page);
    }

    async Task ProcessAsync(ScannedPage page)
    {
        try
        {
            await page.ProcessAsync();
        }
        catch (Exception ex) when (ex is ToolException or ArgumentException or InvalidOperationException)
        {
            ActivityLog.Error("cleaning up a scanned page", ex);
            _summary.Text = ex is ToolException ? ex.Message : "A page couldn’t be cleaned up. Try Crop and Straighten.";
        }
        if (IsDisposed || !_pages.Contains(page)) return;
        Refresh(_strip.Selected);
        if (_mode == View.Page && Current == page) _view.Image = page.Result ?? page.Photo;
    }

    ScannedPage? Current => _strip.Selected >= 0 && _strip.Selected < _pages.Count ? _pages[_strip.Selected] : null;

    void ChangePage(Action<ScannedPage> change)
    {
        if (Current is not { } page) return;
        if (_mode == View.Corners) page.Corners = _view.Corners ?? page.Corners;
        change(page);
        _changed = true;
        ShowPage(View.Page);
        UpdatePageSection();
        _ = ProcessAsync(page);
    }

    void ShowPage(View mode)
    {
        if (Current is not { } page) { ShowSource(); return; }
        _mode = mode;
        ShowView();
        _view.Status = "";
        if (mode == View.Corners)
        {
            _view.Image = page.Photo;
            _view.Corners = page.Corners;
            _view.EditCorners = true;
            var detect = new AppButton("Find Edges", ButtonKind.Secondary);
            detect.Click += (_, _) => _view.Corners = PageCorners.Find(page.Photo) ?? PageCorners.Whole(page.Photo.Size);
            var whole = new AppButton("Whole Photo", ButtonKind.Secondary);
            whole.Click += (_, _) => _view.Corners = PageCorners.Whole(page.Photo.Size);
            var cancel = new AppButton("Cancel", ButtonKind.Secondary);
            cancel.Click += (_, _) => ShowPage(View.Page);
            var apply = new AppButton("Apply", ButtonKind.Primary);
            apply.Click += (_, _) => ChangePage(selected => selected.Corners = _view.Corners ?? selected.Corners);
            SetViewBar([detect, whole, Hint("Drag the corners to the page’s corners.")], [cancel, apply]);
            return;
        }
        _view.EditCorners = false;
        _view.Corners = null;
        _view.Image = page.Result ?? page.Photo;
        var back = new AppButton(_source.Value switch { Source.Camera => "Back to Camera", Source.Scanner => "Scan Another", _ => "Add More Photos" }, ButtonKind.Secondary);
        back.Click += (_, _) => { _strip.Select(-1); ShowSource(); };
        SetViewBar([back, Hint("Pick a look on the right, or crop and straighten the page.")], []);
    }

    void Retake()
    {
        DeletePage();
        _source.Value = Source.Camera;
        ShowSource();
    }

    void DeletePage()
    {
        if (Current is not { } page) return;
        int index = _pages.IndexOf(page);
        _pages.RemoveAt(index);
        page.Dispose();
        _changed = true;
        int next = _pages.Count == 0 ? -1 : Math.Min(index, _pages.Count - 1);
        Refresh(next);
        if (next < 0) ShowSource(); else ShowPage(View.Page);
    }

    void MovePage(int from, int to)
    {
        ScannedPage page = _pages[from];
        _pages.RemoveAt(from);
        int target = to > from ? to - 1 : to;
        _pages.Insert(target, page);
        _changed = true;
        Refresh(target);
    }

    void ShowPageMenu(Point point)
    {
        int index = _strip.Selected;
        if (index < 0) return;
        var menu = Menus.Create();
        menu.ShowImageMargin = false;
        var earlier = Menus.Item("Move Earlier", () => MovePage(index, index - 1));
        earlier.Enabled = index > 0;
        var later = Menus.Item("Move Later", () => MovePage(index, index + 2));
        later.Enabled = index < _pages.Count - 1;
        menu.Items.AddRange([earlier, later, new ToolStripSeparator(), Menus.Item("Rotate", () => ChangePage(page => page.Rotation = (page.Rotation + 90) % 360)), Menus.Item("Delete Page", DeletePage)]);
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        Menus.Show(menu, _strip, point);
    }

    void Refresh(int selected)
    {
        _strip.SetPages(_pages, selected);
        _pagesHeading.Text = _pages.Count == 0 ? "Pages" : $"Pages · {_pages.Count}";
        long size = _pages.Sum(page => page.Size);
        _summary.Text = _pages.Count == 0 ? "Scanned pages appear here." : $"{Words.Pages(_pages.Count)}, about {Words.Size(size)}";
        _save.Enabled = _pages.Count > 0 && !_saving;
        UpdatePageSection();
        Shell.PageChanged(this);
    }

    void UpdatePageSection()
    {
        ScannedPage? page = Current;
        _pageSection.Visible = page is not null;
        if (page is null) return;
        _pageHeading.Text = $"Page {_pages.IndexOf(page) + 1}";
        for (int i = 0; i < Filters.Length; i++) _filterButtons[i].Selected = Filters[i].Filter == page.Filter;
        _retake.Visible = page.FromCamera;
    }

    // Saving ------------------------------------------------------------------------------------

    async Task SaveAsync()
    {
        if (_pages.Count == 0 || _saving) return;
        string folder = Directory.Exists(Shell.Settings.LastOpenFolder) ? Shell.Settings.LastOpenFolder : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        string? path = Shell.PickSavePath(Path.Combine(folder, $"Scan {DateTime.Now:yyyy-MM-dd HHmm}.pdf"), FileDialogs.Filter(FileKind.Pdf));
        if (path is null) return;
        _saving = true;
        _save.Text = "Saving…";
        SetBusy(true);
        Refresh(_strip.Selected);
        try
        {
            foreach (ScannedPage page in _pages.Where(page => !page.IsReady).ToList()) await page.ProcessAsync();
            var pages = _pages.Select(page => new PageImage(page.Result!, page.Filter == ScanFilter.BlackAndWhite)).ToList();
            string? language = _searchable.Checked && _searchable.Enabled ? _language?.Value : null;
            var progress = new Progress<int>(index => _summary.Text = $"Reading the text of page {index + 1} of {pages.Count}…");
            await ScanWriter.WriteAsync(pages, path, language, progress);
            ActivityLog.Write($"Scanned {Words.Pages(pages.Count)} to {path}{(language is null ? "" : $", searchable ({language})")}");
            History.Add([path], "Scan");
            _changed = false;
            Shell.OpenDocument(path);
        }
        catch (Exception ex)
        {
            ActivityLog.Error($"saving the scan to {path}", ex);
            _summary.Text = $"Couldn’t save: {(ex is ToolException or IOException or UnauthorizedAccessException ? ex.Message : "something went wrong while writing the PDF.")}";
            return;
        }
        finally
        {
            _saving = false;
            _save.Text = "Save as PDF";
            SetBusy(false);
        }
        Refresh(_strip.Selected);
    }

    public override bool ConfirmClose()
    {
        if (_saving) return false;
        if (!IsDirty) return true;
        var discard = new TaskDialogButton("Discard Pages");
        var page = new TaskDialogPage
        {
            Caption = "Sheaf",
            Heading = $"Discard {Words.Pages(_pages.Count)}?",
            Text = "They haven’t been saved as a PDF yet.",
            Buttons = { discard, TaskDialogButton.Cancel },
            DefaultButton = TaskDialogButton.Cancel,
            Icon = TaskDialogIcon.Warning,
        };
        return TaskDialog.ShowDialog(this, page) == discard;
    }

    // View helpers ------------------------------------------------------------------------------

    void ShowView()
    {
        _message.Visible = false;
        _view.Visible = true;
        _viewBar.Visible = true;
    }

    void ShowMessage(string title, string text, params Control[] actions)
    {
        _view.Visible = false;
        _viewBar.Visible = false;
        foreach (Control control in _message.Controls.Cast<Control>().ToList()) control.Dispose();
        foreach (Control action in actions) { action.Margin = new Padding(0, 0, 8, 0); }
        var row = Make.Row(actions);
        row.Margin = new Padding(0, 18, 0, 0);
        var stack = Make.Stack(Make.Title(title), Make.Paragraph(text), row);
        stack.Anchor = AnchorStyles.None;
        stack.Width = 440;
        stack.MaximumSize = new Size(440, 0);
        var center = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 1 };
        center.Controls.Add(stack);
        _message.Controls.Add(center);
        _message.Visible = true;
    }

    /// <summary>Puts controls under the view: <paramref name="left"/> at the start, <paramref name="right"/> at the end.</summary>
    void SetViewBar(Control[] left, Control[] right)
    {
        foreach (FlowLayoutPanel side in new[] { _viewLeft, _viewRight })
        {
            foreach (Control control in side.Controls.Cast<Control>().Where(control => control != _capture && control != _steady).ToList()) control.Dispose();
            side.Controls.Clear();
        }
        foreach (Control control in left.Concat(right))
        {
            if (control is AppButton) control.Margin = new Padding(0, 0, 8, 0);
            control.Anchor = AnchorStyles.Left;
        }
        _steady.Margin = new Padding(0, 6, 8, 0);
        _viewLeft.Controls.AddRange(left);
        _viewRight.Controls.AddRange(right);
    }

    static TextLabel Hint(string text) => new(text, Tone.Tertiary) { Font = AppFonts.Small, Margin = new Padding(8, 9, 8, 0) };

    void SetBusy(bool busy) => _busy.Running = busy;

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Space && _feed is not null && _mode == View.Source && _source.Value == Source.Camera && ActiveControl is not TextBoxBase)
        {
            TakePicture();
            return true;
        }
        if (keyData == Keys.Delete && _strip.Focused) { DeletePage(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _ = StopCameraAsync();
            foreach (ScannedPage page in _pages) page.Dispose();
            _pages.Clear();
        }
        base.Dispose(disposing);
    }
}
