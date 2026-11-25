using System.Globalization;
using Sheaf.Core;
using Sheaf.Core.Editing;
using Sheaf.Core.IO;
using Sheaf.Core.Rendering;
using Sheaf.Platform;
using Sheaf.Shell;
using Sheaf.Ui;
using Sheaf.Ui.Controls;
using FormField = Sheaf.Core.Editing.FormField;

namespace Sheaf.Pages.Document;

/// <summary>A picture to edit (its path), or a blank image of a size in pixels when the path is null.</summary>
internal sealed record ImageStart(string? Path, Size Pixels, int Background = unchecked((int)0xFFFFFFFF));

/// <summary>
/// A PDF in its own tab. One working document (a private copy of the file plus everything added to it) is
/// shown in five modes: Edit, Comment, Fill &amp; Sign, Redact and Pages. Every change is an undo step, and
/// nothing touches the file until Save.
/// </summary>
internal sealed class DocumentPage : TabContent
{
    const int UndoLimit = 100;
    static readonly Color PdfBadge = Theme.Hex(0xE5484D);

    readonly string _workFolder = AppPaths.NewWorkFolder();
    readonly ToolStyles _styles = new();
    readonly DocumentCanvas _canvas = new() { Dock = DockStyle.Fill };
    readonly PageStrip _strip = new(112) { Dock = DockStyle.Fill };
    readonly PageStrip _grid = new(168) { Dock = DockStyle.Fill, Visible = false };
    readonly ToolRail _rail = new() { Dock = DockStyle.Left, Width = Theme.RailWidth };
    readonly Surface _pagesPanel = new() { Dock = DockStyle.Left, Width = Theme.PagesPanelWidth, Borders = AnchorStyles.Right };
    readonly TextLabel _pagesHeading = new("Pages", Tone.Secondary) { Font = AppFonts.SmallStrong };
    readonly AppButton _addPages = new("", ButtonKind.Quiet, "plus") { LogicalHeight = 28, AccessibleName = "Add Pages" };
    readonly Inspector _inspector;
    readonly SignPanel _signPanel = new() { Dock = DockStyle.Right, Width = Theme.InspectorWidth, Visible = false };
    readonly RedactPanel _redactPanel = new() { Dock = DockStyle.Right, Width = Theme.InspectorWidth, Visible = false };
    readonly Surface _redactBar = new() { Dock = DockStyle.Top, Height = Theme.ContextBarHeight, Borders = AnchorStyles.Bottom, Visible = false };
    readonly ColorButton _pagePaper = new("Page colour", Color.White) { AllowNone = true, LogicalHeight = 28 };
    readonly AppButton _preview = new("Preview Result", ButtonKind.Quiet, "eye") { LogicalHeight = 28, Margin = new Padding(0, 2, 4, 0) };
    readonly Dictionary<int, Task<List<List<PageWord>>>> _words = [];
    readonly StyleBar _styleBar;
    readonly Surface _pageBar = new() { Dock = DockStyle.Top, Height = Theme.ContextBarHeight, Borders = AnchorStyles.Bottom, Visible = false };
    readonly ChoiceGroup<DocumentMode> _modes = new("Mode", true, EditorModes.Choices) { Large = true };
    readonly AppButton _file = new("File", ButtonKind.Quiet) { Chevron = true };
    readonly AppButton _undo = new("", ButtonKind.Quiet, "undo") { AccessibleName = "Undo" };
    readonly AppButton _redo = new("", ButtonKind.Quiet, "redo") { AccessibleName = "Redo" };
    readonly AppButton _history = new("", ButtonKind.Quiet, "history") { AccessibleName = "History" };
    readonly AppButton _export = new("Export", ButtonKind.Secondary) { Chevron = true };
    // Show or hide the page thumbnails and the panel on the right; on a narrow window they start hidden.
    readonly AppButton _pagesToggle = new("", ButtonKind.Quiet, "panel-left") { AccessibleName = "Page Thumbnails", NeutralSelection = true };
    readonly AppButton _sideToggle = new("", ButtonKind.Quiet, "panel-right") { AccessibleName = "Side Panel", NeutralSelection = true };
    readonly List<(AppButton Button, string Text)> _pageBarButtons = [];
    TextLabel? _redactHint;
    bool? _showPages, _showSide;
    bool _narrowPages, _narrowSide, _narrowBars, _narrowHeader;
    readonly AppButton _save = new("Save", ButtonKind.Primary);
    readonly TextLabel _status = new("", Tone.Secondary) { Font = AppFonts.Small, Dock = DockStyle.Fill, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft };
    readonly InputBox _zoomBox = new() { NumberRange = (DocumentCanvas.MinimumZoom, DocumentCanvas.MaximumZoom), Caption = "Zoom, in per cent", Width = 50 };
    readonly TextLabel _zoomUnit = new("%", Tone.Secondary) { Font = AppFonts.MonoSmall, Margin = new Padding(4, 5, 4, 0) };
    readonly AppButton _zoomOut = new("", ButtonKind.Quiet, "minus") { LogicalHeight = 24, AccessibleName = "Zoom Out" };
    readonly AppButton _zoomIn = new("", ButtonKind.Quiet, "plus") { LogicalHeight = 24, AccessibleName = "Zoom In" };
    readonly AppButton _fit = new("Fit Width", ButtonKind.Quiet) { LogicalHeight = 24 };
    readonly BusyBar _busy = new();
    readonly Surface _body = new(SurfaceRole.Canvas) { Dock = DockStyle.Fill };
    FlowLayoutPanel _zoomBar = new();
    readonly Surface _message = new(SurfaceRole.Canvas) { Dock = DockStyle.Fill, Visible = false };
    readonly List<(EditDocument Document, long State)> _undoStack = [];
    readonly List<(EditDocument Document, long State)> _redoStack = [];
    EditDocument? _document;
    PageRenderer? _renderer;
    string? _path;
    DocumentMode _mode;
    readonly bool _modeChosen;
    long _state, _savedState, _nextState;
    bool _saving, _loaded, _failed;
    string _notice = "";

    public DocumentPage(IShell shell, string? path, string? mode, ImageStart? image = null, BlankChoice? blank = null) : base(shell)
    {
        _path = path;
        _image = image;
        _blank = blank;
        _mode = EditorModes.Parse(mode);
        _modeChosen = mode is not null;
        _inspector = new Inspector(_canvas) { Dock = DockStyle.Right, Width = Theme.InspectorWidth };
        _styleBar = new StyleBar(_canvas, _styles) { Dock = DockStyle.Top, Height = Theme.ContextBarHeight };
        SuspendLayout();
        BuildLayout();
        WireEvents();
        ResumeLayout(false);
    }

    public string? FilePath => _path;

    /// <summary>The mode in view, as <see cref="SetMode(string)"/> takes it ("edit", "pages"…).</summary>
    public string ModeName => _mode.ToString().ToLowerInvariant();

    public override string Title => _path is not null ? Path.GetFileName(_path)
        : _name is not null ? _name + (IsImage ? Path.GetExtension(_imageTarget ?? ".png") : ".pdf")
        : _image?.Path is { } picture ? Path.GetFileName(picture)
        : _image is not null ? "Untitled image" : "Untitled";

    /// <summary>A name given to a document that isn't saved as a file yet (or an image not exported yet).</summary>
    string? _name;

    public override bool CanRename => _document is not null && !_saving;

    /// <summary>
    /// Renames the document: a saved PDF (or an exported image) is renamed in its folder, anything else takes the name
    /// for when it is saved. A PDF's title, which browsers show, is set too.
    /// </summary>
    public override void Rename()
    {
        if (_document is null || _saving) return;
        _canvas.CommitTextEdit();
        string? file = IsImage ? _imageTarget : _path;
        bool saved = file is not null && File.Exists(file);
        string extension = file is not null ? Path.GetExtension(file) : IsImage ? ".png" : ".pdf";
        string current = saved ? Path.GetFileNameWithoutExtension(file!) : _name ?? BaseName;
        if (RenameDialog.Ask(FindForm(), current, extension, _document.Title, saved, pdf: !IsImage) is not var (name, title)) return;
        if (saved && name != current)
        {
            string target = Path.Combine(Path.GetDirectoryName(file!)!, name + extension);
            bool sameFile = string.Equals(target, file, StringComparison.OrdinalIgnoreCase);
            if (!sameFile && File.Exists(target))
            {
                Notify($"There’s already a file called {name}{extension} in that folder. Choose another name.");
                return;
            }
            try { File.Move(file!, target); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Notify($"Couldn’t rename {Path.GetFileName(file)}: {(ex is IOException ? "it is open in another app." : ex.Message)}");
                return;
            }
            History.Remove(History.Entries.Where(entry => string.Equals(entry.Path, file, StringComparison.OrdinalIgnoreCase)).ToList());
            History.Remember(target);
            if (IsImage) _imageTarget = target; else _path = target;
            ActivityLog.Write($"Renamed {file} to {target}");
            Notify($"Renamed to {name}{extension}.", target);
        }
        else if (!saved) _name = name;
        // Cleared, a title the file had is taken out when it is saved; none at all is left alone.
        string? newTitle = title is null ? _document.Title : title.Length > 0 ? title : _document.Title is null ? null : "";
        if (!IsImage && newTitle != _document.Title)
        {
            PushUndo();
            _document.Title = newTitle;
            AfterChange();
        }
        UpdateState();
    }

    /// <summary>What this document was started from when it is an image to edit, not a PDF.</summary>
    readonly ImageStart? _image;

    /// <summary>The size and paper of a new blank PDF; null for A4 and white.</summary>
    readonly BlankChoice? _blank;

    /// <summary>Where the edited image was last exported, so Ctrl+S exports there again.</summary>
    string? _imageTarget;

    bool IsImage => _image is not null && _path is null;

    public override Color? Badge => PdfBadge;

    public override bool IsDirty => _state != _savedState;

    public override bool IsBusy => _saving;

    // Layout ------------------------------------------------------------------------------------

    void BuildLayout()
    {
        var body = _body;
        body.Controls.Add(_canvas);
        body.Controls.Add(_grid);
        body.Controls.Add(_message);
        body.Controls.Add(_inspector);
        body.Controls.Add(_signPanel);
        body.Controls.Add(_redactPanel);
        body.Controls.Add(_pagesPanel);
        body.Controls.Add(_rail);

        var pagesHeader = new TableLayoutPanel { Dock = DockStyle.Top, Height = 44, ColumnCount = 2, RowCount = 1, Padding = new Padding(14, 8, 8, 4) };
        pagesHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        pagesHeader.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _pagesHeading.Anchor = AnchorStyles.Left;
        _addPages.Anchor = AnchorStyles.Right;
        _addPages.Margin = Padding.Empty;
        pagesHeader.Controls.Add(_pagesHeading, 0, 0);
        pagesHeader.Controls.Add(_addPages, 1, 0);
        _pagesPanel.Controls.Add(_strip);
        _pagesPanel.Controls.Add(pagesHeader);
        Tips.Shared.SetToolTip(_addPages, "Add pages");

        BuildPageBar();
        BuildRedactBar();
        Controls.Add(body);
        Controls.Add(_redactBar);
        Controls.Add(_pageBar);
        Controls.Add(_styleBar);
        Controls.Add(BuildHeader());
        Controls.Add(_busy);
        Controls.Add(BuildStatusBar());
    }

    Control BuildHeader()
    {
        var header = new Surface { Dock = DockStyle.Top, Height = Theme.HeaderHeight, Borders = AnchorStyles.Bottom };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Padding = new Padding(10, 0, 12, 0) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _file.Margin = new Padding(0, 0, 12, 0);
        _pagesToggle.Margin = new Padding(0, 0, 4, 0);
        _sideToggle.Margin = new Padding(8, 0, 0, 0);
        _pagesToggle.Click += (_, _) => { _showPages = !PagesOpen; ApplyPanels(); };
        _sideToggle.Click += (_, _) => { _showSide = !SideOpen; ApplyPanels(); };
        Tips.Shared.SetToolTip(_pagesToggle, "Show or hide the page thumbnails");
        Tips.Shared.SetToolTip(_sideToggle, "Show or hide the panel on the right\nLayers, page colour, signing and redaction");
        var left = Make.Row(_pagesToggle, _file, _modes);
        left.Anchor = AnchorStyles.Left;
        _undo.Margin = new Padding(0, 0, 2, 0);
        _redo.Margin = new Padding(0, 0, 2, 0);
        _history.Margin = new Padding(0, 0, 12, 0);
        _save.ReserveWidthFor("Saving…");
        _save.Margin = Padding.Empty;
        var right = Make.Row(_undo, _redo, _history, _export, _save, _sideToggle);
        right.Anchor = AnchorStyles.Right;
        layout.Controls.Add(left, 0, 0);
        layout.Controls.Add(right, 2, 0);
        header.Controls.Add(layout);
        Tips.Shared.SetToolTip(_undo, "Undo (Ctrl+Z)");
        Tips.Shared.SetToolTip(_redo, "Redo (Ctrl+Y)");
        Tips.Shared.SetToolTip(_history, "History: go back to any step");
        Tips.Shared.SetToolTip(_save, "Save (Ctrl+S)");
        return header;
    }

    void BuildPageBar()
    {
        AppButton Action(string text, string icon, Action run, string? tip = null)
        {
            var button = new AppButton(text, ButtonKind.Quiet, icon) { LogicalHeight = 28, Margin = new Padding(0, 2, 4, 0), AccessibleName = text };
            button.Click += (_, _) => run();
            Tips.Shared.SetToolTip(button, tip is null ? text : $"{text}\n{tip}");
            _pageBarButtons.Add((button, text));
            return button;
        }
        _pagePaper.Margin = new Padding(0, 2, 4, 0);
        _pagePaper.Changed += (_, _) => PaintPages(_pagePaper.Color);
        Tips.Shared.SetToolTip(_pagePaper, "Colours the blank pages chosen\nPages from a PDF keep their own paper");
        var size = new Slider { Minimum = 96, Maximum = 280, Width = 120, Margin = new Padding(8, 6, 0, 0), AccessibleName = "Thumbnail size" };
        size.Value = _grid.ThumbWidth;
        size.ValueChanged += (_, _) => _grid.ThumbWidth = size.Value;
        var row = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(12, 4, 12, 0) };
        row.Controls.AddRange([
            Action("Turn Left", "rotate-left", () => RotatePages(270)),
            Action("Turn Right", "rotate", () => RotatePages(90)),
            Action("Mirror", "mirror", () => MirrorPages(leftToRight: true), "Mirror left to right"),
            Action("Flip", "flip-vertical", () => MirrorPages(leftToRight: false), "Flip upside down"),
            Action("Duplicate", "copy", DuplicatePages),
            Action("Delete", "trash", DeletePages, "Delete (Del)"),
            new Divider(vertical: true) { Margin = new Padding(8, 8, 8, 0) },
            Action("Insert Blank Page", "file-plus", InsertBlankPage),
            Action("Insert from File…", "folder", () => _ = InsertFromFileAsync()),
            Action("Extract…", "extract", () => _ = ExtractPagesAsync()),
            new Divider(vertical: true) { Margin = new Padding(8, 8, 8, 0) },
            new TextLabel("Page colour", Tone.Tertiary) { Font = AppFonts.Small, Margin = new Padding(0, 9, 6, 0) },
            _pagePaper,
            new TextLabel("Size", Tone.Tertiary) { Font = AppFonts.Small, Margin = new Padding(16, 9, 0, 0) },
            size,
        ]);
        _pageBar.Controls.Add(row);
    }

    void BuildRedactBar()
    {
        var clear = new AppButton("Clear Marks", ButtonKind.Quiet, "trash") { LogicalHeight = 28, Margin = new Padding(0, 2, 4, 0) };
        clear.Click += (_, _) => ClearMarks();
        _preview.Click += (_, _) =>
        {
            _preview.Selected = !_preview.Selected;
            _canvas.PreviewRedactions = _preview.Selected;
        };
        var hint = new TextLabel("Marks stay see-through until you apply them. Preview shows how the copy will look.", Tone.Tertiary) { Font = AppFonts.Small, Margin = new Padding(12, 9, 0, 0) };
        _redactHint = hint;
        var row = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(12, 4, 12, 0) };
        row.Controls.AddRange([_preview, clear, new Divider(vertical: true) { Margin = new Padding(8, 8, 0, 0) }, hint]);
        _redactBar.Controls.Add(row);
    }

    Control BuildStatusBar()
    {
        var bar = new Surface { Dock = DockStyle.Bottom, Height = Theme.StatusBarHeight, Borders = AnchorStyles.Top };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Padding = new Padding(14, 0, 8, 0) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _zoomBox.Height = 24;
        _zoomBox.Box.Font = AppFonts.MonoSmall;
        _zoomBox.Box.TextAlign = HorizontalAlignment.Center;
        _zoomBox.Box.MaxLength = 3;
        foreach (Control control in new Control[] { _zoomOut, _zoomBox, _zoomIn, _fit }) control.Margin = Padding.Empty;
        _zoomBox.Margin = new Padding(4, 0, 0, 0);
        _zoomBar = Make.Row(_zoomOut, _zoomBox, _zoomUnit, _zoomIn, _fit);
        _zoomBar.Anchor = AnchorStyles.Right;
        layout.Controls.Add(_status, 0, 0);
        layout.Controls.Add(_zoomBar, 1, 0);
        bar.Controls.Add(layout);
        Tips.Shared.SetToolTip(_zoomOut, "Zoom out (Ctrl+−)");
        Tips.Shared.SetToolTip(_zoomIn, "Zoom in (Ctrl++)");
        Tips.Shared.SetToolTip(_fit, "Fit the page width (Ctrl+0)");
        Tips.Shared.SetToolTip(_zoomBox, "Type a zoom and press Enter");
        _zoomBox.EnterPressed += (_, _) => ApplyZoom();
        _zoomBox.Box.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Escape) return;
            e.SuppressKeyPress = true;
            _canvas.Focus();
        };
        _zoomBox.Box.GotFocus += (_, _) => _zoomBox.Box.BeginInvoke(_zoomBox.Box.SelectAll);
        _zoomBox.Box.Leave += (_, _) => UpdateStatus();
        return bar;
    }

    /// <summary>Zooms to the percentage typed in the status bar (25 to 400).</summary>
    void ApplyZoom()
    {
        if (_zoomBox.Number is { } percent) _canvas.ZoomTo(percent);
        _canvas.Focus();
        UpdateStatus();
    }

    void WireEvents()
    {
        _canvas.NewObject = _styles.Create;
        _canvas.EditingTools = _styleBar;
        _canvas.EditorSelectionChanged += (_, _) => _styleBar.SyncEditing();
        _canvas.PickImage = PickImage;
        _canvas.CurrentSignature = CurrentSignature;
        _canvas.Placed += (_, item) => { if (item.Kind == MarkKind.Note) _inspector.EditComment(); };
        _canvas.FieldClicked += (_, field) => ClickField(field);
        _canvas.SelectText = SelectText;
        _canvas.TextBetween = TextBetween;
        _canvas.IsOverText = IsOverText;
        _canvas.RenderForLift = (entry, width) => _renderer?.RenderOnceAsync(entry, width) ?? Task.FromResult<Bitmap?>(null);
        _styleBar.Notice += (_, text) => Notify(text);
        _redactPanel.CoverChanged += (_, _) => ChangeCover();
        _redactPanel.FindRequested += (_, request) => _ = FindAsync(request);
        _redactPanel.MarkPicked += (_, row) => _canvas.Reveal(row.Page, row.Item);
        _redactPanel.MarkRemoved += (_, row) => { _canvas.Select(row.Page, [row.Item]); _canvas.DeleteSelection(); };
        _redactPanel.ApplyRequested += (_, _) => _ = ApplyRedactionsAsync();
        _signPanel.FieldChanged += (_, pair) => SetField(pair.Key, pair.Value);
        _canvas.BeforeChange += (_, _) => PushUndo();
        _canvas.Changed += (_, _) => AfterChange();
        _canvas.SelectionChanged += (_, _) => SyncPanels();
        _canvas.ViewChanged += (_, _) => { _strip.ShowCurrent(_canvas.CurrentPage); UpdateStatus(); _inspector.Sync(); };
        _canvas.ToolFinished += (_, _) => { _rail.Show(_canvas.Tool); _styleBar.Sync(); };
        _rail.ToolChosen += (_, tool) => ChooseTool(tool, announce: true);
        _modes.Changed += (_, _) => SetMode(_modes.Value);
        _inspector.KeepEditableChanged += (_, _) => _canvas.Focus();
        _inspector.PaperChosen += (_, color) => PaintPage(_canvas.CurrentPage, color);

        foreach (PageStrip strip in new[] { _strip, _grid })
        {
            strip.MoveRequested += (_, target) => MovePages(strip.SelectedPages, target);
            strip.MenuRequested += (_, point) => ShowPageMenu(strip, point);
            strip.DeleteRequested += (_, _) => DeletePages();
        }
        _strip.PageActivated += (_, page) => _canvas.GoToPage(page);
        _grid.PageOpened += (_, page) => { SetMode(DocumentMode.Edit); _canvas.GoToPage(page); };
        _addPages.Click += (_, _) => ShowAddPagesMenu();

        _file.Click += (_, _) => ShowFileMenu();
        _export.Click += (_, _) => ShowExportMenu();
        _save.Click += (_, _) => _ = SaveAsync();
        _undo.Click += (_, _) => Undo();
        _redo.Click += (_, _) => Redo();
        _history.Click += (_, _) => ShowHistory();
        _zoomIn.Click += (_, _) => _canvas.ZoomIn();
        _zoomOut.Click += (_, _) => _canvas.ZoomOut();
        _fit.Click += (_, _) => _canvas.FitWidth();
        _status.Click += (_, _) => { if (_status.Tag is string file && File.Exists(file)) Native.ShowInExplorer(file); };
    }

    // Loading -----------------------------------------------------------------------------------

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        SetMode(_mode);
        UpdateState();
        _ = LoadAsync();
    }

    async Task LoadAsync()
    {
        _failed = false;
        _message.Visible = false;
        SetBusy(true, _path is null ? "" : "Opening…");
        string? password = null;
        bool retry = false;
        while (true)
        {
            try
            {
                string? path = _path;
                string folder = _workFolder;
                string? attempt = password;
                ImageStart? image = _image;
                EditDocument document = await Task.Run(() => path is not null ? DocumentStore.Open(path, folder, attempt)
                    : image is not null ? ImageDocument(image, folder)
                    : NewBlank(_blank, folder));
                if (IsDisposed) return;
                Show(document);
                break;
            }
            catch (PasswordException)
            {
                password = PasswordDialog.Ask(FindForm(), Title, retry);
                retry = true;
                if (password is not null) continue;
                ShowMessage("This PDF Is Locked", "Sheaf needs its password to open it.", "Enter Password…");
                break;
            }
            catch (Exception ex)
            {
                // Damaged files fail deep inside the PDF reader in many ways; all of them end here.
                ActivityLog.Error($"opening {_path}", ex);
                ShowMessage("Sheaf Couldn’t Open This File", ex is ToolException or IOException ? ex.Message : "The file looks damaged, or it isn’t a PDF.", "Try Again");
                break;
            }
        }
        if (!IsDisposed) SetBusy(false, "");
    }

    void Show(EditDocument document)
    {
        _document = document;
        _renderer?.Dispose();
        _renderer = new PageRenderer(source => _document!.Sources[source]) { ThumbnailWidth = 360 };
        _renderer.ThumbnailReady += (_, _) => { _strip.Invalidate(); _grid.Invalidate(); };
        _canvas.Load(document, _renderer);
        _strip.Load(document, _renderer);
        _grid.Load(document, _renderer);
        _signPanel.Sync(document);
        _loaded = true;
        if (document.Fields.Count > 0 && !_modeChosen) _mode = DocumentMode.Sign;
        SetMode(_mode);
        SyncPanels();
        UpdateState();
        if (_path is not null) ActivityLog.Write($"Opened {_path} ({Words.Pages(document.Pages.Count)}, {Words.Count(document.ObjectCount, "object", "objects")} kept editable)");
    }

    void ShowMessage(string title, string text, string action)
    {
        _message.Controls.Clear();
        var retry = new AppButton(action, ButtonKind.Secondary);
        retry.Click += (_, _) => _ = LoadAsync();
        var stack = Make.Stack(Make.Title(title), Make.Paragraph(text), retry);
        retry.Margin = new Padding(0, 16, 0, 0);
        retry.Anchor = AnchorStyles.Left;
        stack.Anchor = AnchorStyles.None;
        stack.Width = 420;
        stack.MaximumSize = new Size(420, 0);
        var center = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 1 };
        center.Controls.Add(stack);
        _message.Controls.Add(center);
        _failed = true;
        _canvas.Visible = _grid.Visible = false;
        _message.Visible = true;
        _message.BringToFront();
    }

    // Modes and tools ---------------------------------------------------------------------------

    public void SetMode(string mode) => SetMode(EditorModes.Parse(mode));

    void SetMode(DocumentMode mode)
    {
        _mode = mode;
        _canvas.CommitTextEdit();
        // The panels change in one go: nothing below the header draws until the new mode is laid out, so the
        // switch shows as one clean frame while the mode bar's highlight keeps moving.
        Control[] changing = [_body, _styleBar, _redactBar, _pageBar];
        bool frozen = IsHandleCreated && Visible;
        if (frozen) foreach (Control part in changing) Native.PauseDrawing(part);
        SuspendLayout();
        _modes.Value = mode;
        bool pages = mode == DocumentMode.Pages;
        bool ready = !_failed;
        _rail.Visible = !pages;
        ApplyPanels();
        _redactBar.Visible = mode == DocumentMode.Redact;
        _canvas.ShowFields = mode == DocumentMode.Sign;
        if (mode != DocumentMode.Redact && _preview.Selected) { _preview.Selected = false; _canvas.PreviewRedactions = false; }
        if (mode is DocumentMode.Edit or DocumentMode.Comment or DocumentMode.Redact) ReadWordsInBackground();
        _styleBar.Visible = !pages && mode != DocumentMode.Redact;
        _pageBar.Visible = pages;
        _canvas.Visible = ready && !pages;
        _grid.Visible = ready && pages;
        _zoomBar.Visible = !pages;
        _rail.SetTools(EditorModes.ToolsOf(mode));
        _inspector.ShowComments(mode == DocumentMode.Comment);
        _styleBar.IdleHint = mode switch
        {
            DocumentMode.Comment => "Highlight, underline or strike through text, or add a note. Notes become comments every PDF app shows.",
            DocumentMode.Sign => "Place your signature, text, the date or a tick where the form needs them.",
            DocumentMode.Redact => "Mark what to hide with the tools on the left.",
            _ => "Pick a tool on the left, or select something on the page to change it.",
        };
        ResumeLayout(true);
        ChooseTool(EditTool.Select);
        if (pages)
        {
            _grid.SelectPages([_canvas.CurrentPage]);
            _grid.Focus();
        }
        else if (_loaded) _canvas.Focus();
        if (frozen) foreach (Control part in changing) if (part.Visible) Native.ResumeDrawing(part); else Native.EnableDrawing(part);
    }

    bool PagesOpen => _showPages ?? !_narrowPages;

    bool SideOpen => _showSide ?? !_narrowSide;

    /// <summary>Shows the side panels the mode has, unless the window is too narrow for them or they were hidden.</summary>
    void ApplyPanels()
    {
        bool pages = _mode == DocumentMode.Pages;
        SuspendLayout();
        _pagesPanel.Visible = !pages && PagesOpen;
        _inspector.Visible = !pages && _mode is not (DocumentMode.Sign or DocumentMode.Redact) && SideOpen;
        _signPanel.Visible = _mode == DocumentMode.Sign && SideOpen;
        _redactPanel.Visible = _mode == DocumentMode.Redact && SideOpen;
        _pagesToggle.Selected = PagesOpen;
        _sideToggle.Selected = SideOpen;
        // Greyed rather than hidden in Pages: hiding them would slide the mode bar along under the pointer.
        _pagesToggle.Enabled = _sideToggle.Enabled = !pages;
        ResumeLayout(true);
    }

    /// <summary>
    /// Fits the editor to the window: as it narrows, the thumbnails and then the right-hand panel fold away (the
    /// buttons at the ends of the header bring them back), the Pages bar shows icons only, and the header drops
    /// the buttons that have keys (Ctrl+Z, Ctrl+Y; History stays in the File menu).
    /// </summary>
    void FitToWidth()
    {
        int width = (int)Math.Round(Width * 96f / Math.Max(96, DeviceDpi));
        if (width <= 0) return;
        bool narrowPages = width < 1150, narrowSide = width < 940, narrowBars = width < 1120, narrowHeader = width < 900;
        if (narrowPages != _narrowPages || narrowSide != _narrowSide)
        {
            _narrowPages = narrowPages;
            _narrowSide = narrowSide;
            ApplyPanels();
        }
        if (narrowBars != _narrowBars)
        {
            _narrowBars = narrowBars;
            foreach (var (button, text) in _pageBarButtons) button.Text = narrowBars ? "" : text;
            if (_redactHint is not null) _redactHint.Visible = !narrowBars;
        }
        if (narrowHeader != _narrowHeader)
        {
            _narrowHeader = narrowHeader;
            _history.Visible = !narrowHeader;
        }
        _undo.Visible = _redo.Visible = width >= 840;
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        FitToWidth();
    }

    void ChooseTool(EditTool tool, bool announce = false)
    {
        _canvas.Tool = tool;
        _rail.Show(tool);
        _styleBar.Sync();
        if (_loaded && _canvas.Visible) _canvas.Focus();
        if (announce && _rail.Tools.FirstOrDefault(railTool => railTool.Tool == tool) is { } chosen)
            ScreenReader.Say(_canvas, tool is EditTool.Select or EditTool.Hand ? $"{chosen.Name} tool" : $"{chosen.Name} tool. Drag on the page, or press Enter to place one in the middle.");
    }

    byte[]? PickImage()
    {
        string? path = Shell.PickFiles(FileKind.Image, multiple: false).FirstOrDefault();
        if (path is null) return null;
        try
        {
            return ImageLook.Fit(File.ReadAllBytes(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Notify($"Couldn’t read {Path.GetFileName(path)}: {ex.Message}");
            return null;
        }
    }

    /// <summary>The saved signature, ready to place; asks for one the first time.</summary>
    PageObject? CurrentSignature()
    {
        PageObject? signature = SignatureStore.Load() ?? _signPanel.CreateSignature();
        if (signature is null) return null;
        PageObject copy = signature.Clone();
        copy.Id = Guid.NewGuid();
        return copy;
    }

    void ClickField(FormField field)
    {
        if (field.Kind == FieldKind.CheckBox)
            SetField(field.Name, _document!.FieldValues.GetValueOrDefault(field.Name, field.Value) == "true" ? "false" : "true");
        else
            _signPanel.FocusField(field.Name);
    }

    void SetField(string name, string value)
    {
        if (_document is null || _document.FieldValues.GetValueOrDefault(name) == value) return;
        PushUndo();
        _document.FieldValues[name] = value;
        _signPanel.Sync(_document);
        AfterChange();
    }

    public override bool AcceptDrop(string[] paths)
    {
        if (_document is null || _mode == DocumentMode.Pages) return false;
        var images = paths.Where(path => FileKinds.Of(path) == FileKind.Image).ToList();
        if (images.Count == 0) return false;
        PlaceImages(images);
        return true;
    }

    /// <summary>Puts dropped images in the middle of the page in view, one slightly below the other.</summary>
    void PlaceImages(IEnumerable<string> paths)
    {
        int page = _canvas.CurrentPage;
        PageEntry entry = _document!.Pages[page];
        var added = new List<PageObject>();
        PushUndo();
        foreach (string path in paths)
        {
            PageObject item = _styles.Create(EditTool.Image);
            try { item.ImageData = ImageLook.Fit(File.ReadAllBytes(path)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            if (ImageCache.Get(item) is not { } image) continue;
            float width = Math.Min(entry.Width * 0.6f, image.Width * 72f / Math.Max(1, image.HorizontalResolution));
            float height = width * image.Height / image.Width;
            item.Bounds = new RectangleF((entry.Width - width) / 2, (entry.Height - height) / 2 + added.Count * 16, width, height);
            entry.Objects.Add(item);
            added.Add(item);
        }
        if (added.Count == 0) { DropUndo(); return; }
        AfterChange();
        SetMode(_mode == DocumentMode.Edit ? _mode : DocumentMode.Edit);
        _canvas.Select(page, added);
    }

    // Undo --------------------------------------------------------------------------------------

    void PushUndo()
    {
        if (_document is null) return;
        _undoStack.Add((_document.Clone(), _state));
        if (_undoStack.Count > UndoLimit) _undoStack.RemoveAt(0);
        _redoStack.Clear();
        _state = ++_nextState;
    }

    /// <summary>Takes back an undo step that ended up changing nothing.</summary>
    void DropUndo()
    {
        if (_undoStack.Count == 0) return;
        _state = _undoStack[^1].State;
        _undoStack.RemoveAt(_undoStack.Count - 1);
    }

    void Undo()
    {
        _canvas.CommitTextEdit();
        if (_undoStack.Count == 0 || _document is null) return;
        _redoStack.Add((_document.Clone(), _state));
        var (document, state) = _undoStack[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);
        Restore(document, state);
    }

    void Redo()
    {
        _canvas.CommitTextEdit();
        if (_redoStack.Count == 0 || _document is null) return;
        _undoStack.Add((_document.Clone(), _state));
        var (document, state) = _redoStack[^1];
        _redoStack.RemoveAt(_redoStack.Count - 1);
        Restore(document, state);
    }

    /// <summary>Every step that can be undone or redone, newest at the top; picking one goes straight to it.</summary>
    void ShowHistory()
    {
        if (_document is null) return;
        _canvas.CommitTextEdit();
        // Oldest first: what undo would bring back, then now, then what redo would.
        var states = _undoStack.Select(step => step.Document).Append(_document).Concat(Enumerable.Reverse(_redoStack).Select(step => step.Document)).ToList();
        int now = _undoStack.Count;
        var menu = Menus.Create();
        for (int step = states.Count - 1; step >= 1; step--)
        {
            int target = step;
            ToolStripMenuItem item = Menus.Item(StepName(states[step - 1], states[step]), () => GoToStep(target), isChecked: step == now);
            // Undone steps stay greyed until they are redone or something new replaces them.
            if (step > now) item.ForeColor = Theme.Current.Text3;
            menu.Items.Add(item);
            if (menu.Items.Count == 30) break;
        }
        if (menu.Items.Count > 0) menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Menus.Item(_undoStack.Count + _redoStack.Count >= UndoLimit ? "Earliest Kept Step" : "As Opened", () => GoToStep(0), isChecked: now == 0));
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        Menus.Show(menu, _history, Menus.Below(_history));
    }

    /// <summary>Undoes or redoes as many steps as it takes to reach <paramref name="target"/>, redrawing once.</summary>
    void GoToStep(int target)
    {
        if (_document is null || target == _undoStack.Count) return;
        EditDocument document = _document.Clone();
        long state = _state;
        while (_undoStack.Count > target)
        {
            _redoStack.Add((document, state));
            (document, state) = _undoStack[^1];
            _undoStack.RemoveAt(_undoStack.Count - 1);
        }
        while (_undoStack.Count < target && _redoStack.Count > 0)
        {
            _undoStack.Add((document, state));
            (document, state) = _redoStack[^1];
            _redoStack.RemoveAt(_redoStack.Count - 1);
        }
        Restore(document, state);
    }

    /// <summary>A name for what changed between two versions, worked out by comparing them.</summary>
    static string StepName(EditDocument before, EditDocument after)
    {
        static string Title(string text) => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(text);
        if (before.Pages.Count != after.Pages.Count)
        {
            int difference = after.Pages.Count - before.Pages.Count;
            return Title($"{(difference > 0 ? "Added" : "Removed")} {Words.Pages(Math.Abs(difference))}");
        }
        if (!before.Pages.Select(page => page.Id).SequenceEqual(after.Pages.Select(page => page.Id))) return "Moved Pages";
        var pairs = before.Pages.Zip(after.Pages).ToList();
        if (pairs.Any(pair => pair.First.Rotation != pair.Second.Rotation)) return "Turned Pages";
        if (pairs.Any(pair => pair.First.MirrorX != pair.Second.MirrorX || pair.First.MirrorY != pair.Second.MirrorY)) return "Mirrored Pages";
        if (pairs.Any(pair => pair.First.Background != pair.Second.Background)) return "Paper Colour";

        var old = before.Pages.SelectMany(page => page.Objects).ToDictionary(item => item.Id);
        var now = after.Pages.SelectMany(page => page.Objects).ToDictionary(item => item.Id);
        var added = now.Values.Where(item => !old.ContainsKey(item.Id)).ToList();
        var removed = old.Values.Where(item => !now.ContainsKey(item.Id)).ToList();
        string Name(PageObject item) => item.Kind == MarkKind.Text ? "Text" : Title(ToolStyles.Describe(item));
        string Several(List<PageObject> items) => items.Count == 1 ? Name(items[0]) : $"{items.Count} Objects";
        if (added.Any(item => item.Comment == "Lifted area")) return "Lifted an Area";
        if (added.Count > 0 && removed.Count == 0) return $"Added {Several(added)}";
        if (removed.Count > 0 && added.Count == 0) return $"Deleted {Several(removed)}";
        if (added.Count > 0) return "Replaced Objects";

        var changed = now.Values.Where(item => old.TryGetValue(item.Id, out PageObject? earlier) && !Same(earlier, item)).ToList();
        if (changed.Count > 0)
        {
            PageObject first = changed[0], earlier = old[first.Id];
            string what = changed.Count == 1 ? Name(first) : $"{changed.Count} Objects";
            if (first.Text != earlier.Text) return "Edited Text";
            if (first.Group != earlier.Group) return first.Group is null ? "Ungrouped" : "Grouped";
            if (first.Hidden != earlier.Hidden) return first.Hidden ? $"Hid {what}" : $"Showed {what}";
            if (first.Bounds.Size != earlier.Bounds.Size) return $"Resized {what}";
            if (first.Bounds != earlier.Bounds) return $"Moved {what}";
            return $"Restyled {what}";
        }
        bool reordered = before.Pages.Zip(after.Pages).Any(pair => !pair.First.Objects.Select(item => item.Id).SequenceEqual(pair.Second.Objects.Select(item => item.Id)));
        if (reordered) return "Arranged Objects";
        if (!before.FieldValues.OrderBy(pair => pair.Key).SequenceEqual(after.FieldValues.OrderBy(pair => pair.Key))) return "Filled In the Form";
        return "Change";
    }

    static bool Same(PageObject a, PageObject b) =>
        a.Bounds == b.Bounds && a.Text == b.Text && a.StrokeColor == b.StrokeColor && a.FillColor == b.FillColor && a.StrokeWidth == b.StrokeWidth
        && a.Opacity == b.Opacity && a.CornerRadius == b.CornerRadius && a.FontFamily == b.FontFamily && a.FontSize == b.FontSize && a.Bold == b.Bold
        && a.Italic == b.Italic && a.Align == b.Align && a.Anchor == b.Anchor && a.Hidden == b.Hidden && a.Group == b.Group && a.Pen == b.Pen
        && a.StartEnd == b.StartEnd && a.EndEnd == b.EndEnd && a.Sweep == b.Sweep && a.Kind == b.Kind && a.Comment == b.Comment
        && a.Cover == b.Cover && (a.ImageData?.Length ?? 0) == (b.ImageData?.Length ?? 0)
        && a.Strokes.Count == b.Strokes.Count && a.Strokes.Zip(b.Strokes).All(pair => pair.First.SequenceEqual(pair.Second));

    /// <summary>Shows an earlier or later version. Source files only ever get added, so the newest list is kept.</summary>
    void Restore(EditDocument document, long state)
    {
        if (document.Sources.Count < _document!.Sources.Count) document.Sources = _document.Sources;
        _document = document;
        _state = state;
        ReloadViews();
        UpdateState();
    }

    void ReloadViews()
    {
        _canvas.Reload(_document!);
        _strip.Load(_document!, _renderer!);
        _grid.Load(_document!, _renderer!);
        _signPanel.Sync(_document!);
        SyncPanels();
    }

    void AfterChange()
    {
        _strip.Invalidate();
        _grid.Invalidate();
        _canvas.Invalidate();
        SyncPanels();
        UpdateState();
    }

    void SyncPanels()
    {
        _styleBar.Sync();
        _inspector.Sync();
        if (_document is not null) _redactPanel.Sync(_document, _canvas.Selection.Count == 1 ? _canvas.Selection[0].Id : null);
    }

    // Text and redaction ------------------------------------------------------------------------

    /// <summary>The words of a source file, read once in the background.</summary>
    Task<List<List<PageWord>>> WordsAsync(int source)
    {
        if (_words.TryGetValue(source, out var words)) return words;
        string path = _document!.Sources[source];
        return _words[source] = Task.Run(() =>
        {
            try { return Finders.ReadWords(path); }
            catch (Exception ex)
            {
                // PdfPig can't read some files' text; marking by hand still works for them.
                ActivityLog.Error($"reading the text of {Path.GetFileName(path)}", ex);
                return new List<List<PageWord>>();
            }
        });
    }

    void ReadWordsInBackground()
    {
        if (_document is null) return;
        foreach (int source in _document.Pages.Where(page => !page.IsBlank).Select(page => page.Source).Distinct()) _ = WordsAsync(source);
    }

    /// <summary>The words between two points of a page, in reading order, as one box per line.</summary>
    IReadOnlyList<RectangleF>? SelectText(int page, PointF from, PointF to)
    {
        if (WordsBetween(page, from, to) is not { } words) return null;
        PageEntry entry = _document!.Pages[page];
        return [.. Finders.LineBoxes(words).Select(box => PagePainter.Unmirror(entry, box))];
    }

    /// <summary>The text between two points of a page: words joined by spaces, lines by line breaks.</summary>
    string? TextBetween(int page, PointF from, PointF to)
    {
        if (WordsBetween(page, from, to) is not { } words) return null;
        var text = new System.Text.StringBuilder();
        PageWord? previous = null;
        foreach (PageWord word in words)
        {
            if (previous is not null)
            {
                float middle = word.Bounds.Top + word.Bounds.Height / 2, before = previous.Bounds.Top + previous.Bounds.Height / 2;
                text.Append(Math.Abs(middle - before) > Math.Min(word.Bounds.Height, previous.Bounds.Height) / 2 ? Environment.NewLine : " ");
            }
            text.Append(word.Text);
            previous = word;
        }
        return text.ToString();
    }

    /// <summary>Whether a point is on the page's text: on a word, or in the space between two words of a line.</summary>
    bool IsOverText(int page, PointF point)
    {
        PageEntry entry = _document!.Pages[page];
        if (entry.IsBlank || !_words.TryGetValue(entry.Source, out var task) || !task.IsCompletedSuccessfully || entry.SourcePage >= task.Result.Count) return false;
        PointF at = PagePainter.Unmirror(entry, new RectangleF(point, SizeF.Empty)).Location;
        return task.Result[entry.SourcePage].Any(word => RectangleF.Inflate(word.Bounds, 4, 1).Contains(at));
    }

    /// <summary>The words from the one nearest <paramref name="from"/> to the one nearest <paramref name="to"/>, in reading order.</summary>
    List<PageWord>? WordsBetween(int page, PointF from, PointF to)
    {
        PageEntry entry = _document!.Pages[page];
        if (entry.IsBlank || !_words.TryGetValue(entry.Source, out var task) || !task.IsCompletedSuccessfully || entry.SourcePage >= task.Result.Count) return null;
        List<PageWord> words = task.Result[entry.SourcePage];
        // The words are where the page's own content has them; on a mirrored page, that is the other way round.
        PointF Unmirrored(PointF point) => PagePainter.Unmirror(entry, new RectangleF(point, SizeF.Empty)).Location;
        int first = NearestWord(words, Unmirrored(from)), last = NearestWord(words, Unmirrored(to));
        if (first < 0 || last < 0) return null;
        if (first > last) (first, last) = (last, first);
        return words.GetRange(first, last - first + 1);
    }

    static int NearestWord(List<PageWord> words, PointF point)
    {
        const float Reach = 14;
        int best = -1;
        double bestDistance = Reach;
        for (int i = 0; i < words.Count; i++)
        {
            RectangleF box = words[i].Bounds;
            double dx = Math.Max(0, Math.Max(box.Left - point.X, point.X - box.Right));
            double dy = Math.Max(0, Math.Max(box.Top - point.Y, point.Y - box.Bottom)) * 2;
            double distance = Math.Sqrt(dx * dx + dy * dy);
            if (distance < bestDistance) { bestDistance = distance; best = i; }
            if (distance == 0) break;
        }
        return best;
    }

    async Task FindAsync(FindRequest request)
    {
        if (_document is null) return;
        SetBusy(true, "Looking…");
        try
        {
            var found = new List<(int Source, Found Item)>();
            foreach (int source in _document.Pages.Where(page => !page.IsBlank).Select(page => page.Source).Distinct().ToList())
            {
                if (request is FindRequest.Codes)
                {
                    var pages = _document.Pages.Where(page => page.Source == source && !page.IsBlank).Select(page => page.SourcePage).ToList();
                    found.AddRange((await Finders.FindCodesAsync(_document.Sources[source], pages)).Select(item => (source, item)));
                    continue;
                }
                List<List<PageWord>> words = await WordsAsync(source);
                var hits = request is FindRequest.Phrase phrase ? Finders.FindText(words, phrase.Text) : Finders.FindPattern(words, ((FindRequest.Pattern)request).Kind);
                found.AddRange(hits.Select(item => (source, item)));
            }
            if (IsDisposed || _document is null) return;
            int marked = MarkFound(found, words: request is not FindRequest.Codes);
            string what = request switch
            {
                FindRequest.Codes => "QR codes or barcodes",
                FindRequest.Pattern { Kind: FindKind.Emails } => "email addresses",
                FindRequest.Pattern { Kind: FindKind.PhoneNumbers } => "phone numbers",
                FindRequest.Pattern => "bank or card numbers",
                _ => "matches",
            };
            string result = found.Count == 0 ? $"No {what} found."
                : marked == found.Count ? $"Marked {Words.Count(marked, "match", "matches")}."
                : $"Found {found.Count}; {found.Count - marked} were already marked.";
            _redactPanel.ShowResult(result);
            Notify(result);
        }
        catch (Exception ex) when (ex is ToolException or IOException)
        {
            Notify($"Couldn’t look through the file: {ex.Message}");
        }
        finally
        {
            if (!IsDisposed) SetBusy(false, "");
        }
    }

    /// <summary>
    /// Marks each find on every page that shows its source page, skipping places already marked. Found
    /// <paramref name="words"/> get text marks, which take just the letters out; codes get area marks.
    /// </summary>
    int MarkFound(List<(int Source, Found Item)> found, bool words)
    {
        var added = new List<(int Page, PageObject Mark)>();
        PushUndo();
        for (int page = 0; page < _document!.Pages.Count; page++)
        {
            PageEntry entry = _document.Pages[page];
            if (entry.IsBlank) continue;
            foreach (var (_, item) in found.Where(hit => hit.Source == entry.Source && hit.Item.SourcePage == entry.SourcePage))
            {
                RectangleF bounds = PagePainter.Unmirror(entry, item.Bounds);
                if (entry.Objects.Any(mark => mark.Kind == MarkKind.Redaction && RectangleF.Inflate(mark.Bounds, 1, 1).Contains(bounds))) continue;
                PageObject mark = _styles.Create(words ? EditTool.RedactText : EditTool.RedactRectangle);
                mark.Bounds = bounds;
                mark.Comment = item.Text;
                entry.Objects.Add(mark);
                added.Add((page, mark));
            }
        }
        if (added.Count == 0)
        {
            DropUndo();
            return 0;
        }
        AfterChange();
        _canvas.Reveal(added[0].Page, added[0].Mark);
        return added.Count;
    }

    void ChangeCover()
    {
        _styles.Cover = _redactPanel.Cover;
        _styles.CoverColor = _redactPanel.CoverColor;
        var marks = _document?.Pages.SelectMany(page => page.Objects).Where(item => item.Kind == MarkKind.Redaction).ToList() ?? [];
        if (marks.Count == 0) return;
        PushUndo();
        foreach (PageObject mark in marks)
        {
            mark.Cover = _styles.Cover;
            mark.FillColor = _styles.CoverColor;
        }
        AfterChange();
    }

    void ClearMarks()
    {
        if (_document is null || !_document.Pages.Any(page => page.Objects.Any(item => item.Kind == MarkKind.Redaction))) return;
        PushUndo();
        foreach (PageEntry page in _document.Pages) page.Objects.RemoveAll(item => item.Kind == MarkKind.Redaction);
        _canvas.Reload(_document);
        AfterChange();
        Notify("Cleared every mark.");
    }

    async Task ApplyRedactionsAsync()
    {
        if (_document is null || _saving) return;
        _canvas.CommitTextEdit();
        string? target = Shell.PickSavePath(Path.Combine(SuggestedFolder, BaseName + " (redacted).pdf"), FileDialogs.Filter(FileKind.Pdf));
        if (target is null) return;
        if (_path is not null && string.Equals(Path.GetFullPath(target), _path, StringComparison.OrdinalIgnoreCase))
        {
            Notify("Choose a new name for the redacted copy, so this file stays as it is.");
            return;
        }
        _saving = true;
        SetBusy(true, "Redacting…");
        _redactPanel.SetBusy(true);
        UpdateState();
        try
        {
            int applied = await Redactor.ApplyAsync(_document.Clone(), target, _workFolder, _redactPanel.Clean, _redactPanel.Searchable);
            ActivityLog.Write($"Redacted {target} ({Words.Count(applied, "mark", "marks")})");
            History.Add([target], "Redact");
            Notify($"Applied {Words.Count(applied, "redaction", "redactions")} to {Path.GetFileName(target)}.", target);
            Shell.OpenDocument(target);
        }
        catch (Exception ex)
        {
            ActivityLog.Error($"redacting to {target}", ex);
            Notify($"Couldn’t redact: {(ex is ToolException or IOException ? ex.Message : "something in the file stopped it.")}");
        }
        finally
        {
            _saving = false;
            if (!IsDisposed)
            {
                SetBusy(false, "");
                _redactPanel.SetBusy(false);
                SyncPanels();
                UpdateState();
            }
        }
    }

    // Pages -------------------------------------------------------------------------------------

    IReadOnlyList<int> SelectedPages => _mode == DocumentMode.Pages ? _grid.SelectedPages : _strip.SelectedPages;

    /// <summary>Changes the page list as one undo step, then selects <paramref name="select"/>.</summary>
    void ChangePages(Action<List<PageEntry>> change, Func<IReadOnlyList<int>>? select = null)
    {
        if (_document is null) return;
        _canvas.CommitTextEdit();
        PushUndo();
        change(_document.Pages);
        ReloadViews();
        if (select?.Invoke() is { Count: > 0 } pages)
        {
            _strip.SelectPages(pages);
            _grid.SelectPages(pages);
            if (_mode != DocumentMode.Pages) _canvas.GoToPage(pages[0]);
        }
        UpdateState();
    }

    /// <summary>The pages a page action works on; with none selected it says so and gives nothing.</summary>
    IReadOnlyList<int> PagesToChange()
    {
        var pages = SelectedPages;
        if (pages.Count == 0) Notify("Select a page first: click it, or drag a box around several.");
        return pages;
    }

    void RotatePages(int degrees)
    {
        var pages = PagesToChange();
        if (pages.Count == 0) return;
        ChangePages(list => { foreach (int i in pages) list[i].Rotation = (list[i].Rotation + degrees) % 360; }, () => pages);
    }

    /// <summary>Mirrors the selected pages left to right, or flips them upside down; doing it again undoes it.</summary>
    void MirrorPages(bool leftToRight)
    {
        var pages = PagesToChange();
        if (pages.Count == 0) return;
        ChangePages(list =>
        {
            foreach (int i in pages)
            {
                if (leftToRight) list[i].MirrorX = !list[i].MirrorX;
                else list[i].MirrorY = !list[i].MirrorY;
            }
        }, () => pages);
        Notify(leftToRight ? $"Mirrored {Words.Pages(pages.Count)}." : $"Flipped {Words.Pages(pages.Count)}.");
    }

    void DeletePages()
    {
        if (_document is null) return;
        var pages = PagesToChange();
        if (pages.Count == 0) return;
        if (pages.Count >= _document.Pages.Count)
        {
            Notify("A PDF needs at least one page. Add a page before deleting this one.");
            return;
        }
        int first = pages.Min();
        ChangePages(list => { foreach (int i in pages.OrderDescending()) list.RemoveAt(i); },
            () => [Math.Min(first, _document.Pages.Count - 1)]);
        Notify($"Deleted {Words.Pages(pages.Count)}.");
    }

    void DuplicatePages()
    {
        var pages = PagesToChange();
        if (pages.Count == 0) return;
        int at = pages.Max() + 1;
        ChangePages(list => list.InsertRange(at, pages.Select(i => CopyOf(list[i]))), () => [.. Enumerable.Range(at, pages.Count)]);
    }

    static PageEntry CopyOf(PageEntry entry)
    {
        PageEntry copy = entry.Clone();
        copy.Id = Guid.NewGuid();
        foreach (PageObject item in copy.Objects) item.Id = Guid.NewGuid();
        return copy;
    }

    void MovePages(IReadOnlyList<int> pages, int target)
    {
        if (_document is null || pages.Count == 0) return;
        int before = target - pages.Count(i => i < target);
        if (pages.Count == 1 && (pages[0] == before)) return;
        ChangePages(list =>
        {
            var moving = pages.Select(i => list[i]).ToList();
            foreach (int i in pages.OrderDescending()) list.RemoveAt(i);
            list.InsertRange(before, moving);
        }, () => [.. Enumerable.Range(before, pages.Count)]);
    }

    int InsertionPoint => _document is null ? 0 : _mode == DocumentMode.Pages || _strip.Focused
        ? (SelectedPages.Count > 0 ? SelectedPages.Max() + 1 : _document.Pages.Count)
        : _canvas.CurrentPage + 1;

    void InsertBlankPage()
    {
        if (_document is null) return;
        int at = Math.Min(InsertionPoint, _document.Pages.Count);
        PageEntry reference = _document.Pages[Math.Max(0, at - 1)];
        SizeF size = PagePainter.DisplaySize(reference);
        ChangePages(list => list.Insert(at, new PageEntry { SourcePage = -1, Width = size.Width, Height = size.Height }), () => [at]);
    }

    async Task InsertFromFileAsync()
    {
        // One at a time: a click while the last one is still asking does nothing.
        if (_document is null || _inserting) return;
        _inserting = true;
        string? path = null;
        bool busy = false;
        try
        {
            path = Shell.PickFiles(FileKind.Pdf, multiple: false).FirstOrDefault();
            if (path is null) return;
            int at = Math.Min(InsertionPoint, _document.Pages.Count);
            var probe = new EditDocument { Sources = [.. _document.Sources] };
            // Which of its pages to add, as a print dialog asks which to print; a one-page file is asked about too.
            int count = await Task.Run(() => Core.Pdf.PdfFiles.PageCount(path));
            if (IsDisposed || FindForm() is not { } owner) return;
            IReadOnlyList<int>? pages = PagePickDialog.Ask(owner, Path.GetFileName(path), count);
            if (pages is not { Count: > 0 } || _document is null) return;
            SetBusy(busy = true, "Adding pages…");
            IReadOnlyList<PageEntry> all = await Task.Run(() => DocumentStore.AddSource(probe, path, _workFolder));
            if (IsDisposed) return;
            var entries = pages.Where(page => page < all.Count).Select(page => all[page]).ToList();
            _document.Sources.Add(probe.Sources[^1]);
            ChangePages(list => list.InsertRange(at, entries), () => [.. Enumerable.Range(at, entries.Count)]);
            Notify($"Added {Words.Pages(entries.Count)} from {Path.GetFileName(path)}.");
        }
        catch (PasswordException)
        {
            Notify($"{Path.GetFileName(path)} has a password. Unlock it with Unlock PDF first, then add its pages.");
        }
        catch (Exception ex) when (ex is ToolException or IOException or UnauthorizedAccessException)
        {
            Notify($"Couldn’t add pages from {Path.GetFileName(path)}: {ex.Message}");
        }
        finally
        {
            _inserting = false;
            if (busy && !IsDisposed) SetBusy(false, "");
        }
    }

    bool _inserting;

    async Task ExtractPagesAsync()
    {
        if (_document is null) return;
        var pages = PagesToChange();
        if (pages.Count == 0) return;
        string name = $"{BaseName} (page{(pages.Count == 1 ? "" : "s")} {PageList(pages)}).pdf";
        if (Shell.PickSavePath(Path.Combine(SuggestedFolder, name), FileDialogs.Filter(FileKind.Pdf)) is not { } target) return;
        EditDocument copy = _document.Clone();
        copy.Pages = [.. pages.Select(i => copy.Pages[i])];
        if (await WriteAsync(copy, target, _inspector.KeepEditable, "Extracting…"))
        {
            History.Add([target], "Extract pages");
            Notify($"Saved {Words.Pages(pages.Count)} to {Path.GetFileName(target)}.", target);
        }
    }

    static string PageList(IReadOnlyList<int> pages)
    {
        var parts = new List<string>();
        for (int i = 0; i < pages.Count; i++)
        {
            int start = pages[i];
            while (i + 1 < pages.Count && pages[i + 1] == pages[i] + 1) i++;
            parts.Add(start == pages[i] ? $"{start + 1}" : $"{start + 1}-{pages[i] + 1}");
        }
        return string.Join(",", parts);
    }

    void ShowPageMenu(PageStrip strip, Point point)
    {
        int count = SelectedPages.Count;
        string pages = count == 1 ? "Page" : $"{count} Pages";
        var menu = Menus.Create();
        menu.Items.Add(Menus.Item("Turn Right", () => RotatePages(90)));
        menu.Items.Add(Menus.Item("Turn Left", () => RotatePages(270)));
        menu.Items.Add(Menus.Item("Mirror", () => MirrorPages(leftToRight: true)));
        menu.Items.Add(Menus.Item("Flip Upside Down", () => MirrorPages(leftToRight: false)));
        menu.Items.Add(Menus.Item($"Duplicate {pages}", DuplicatePages));
        menu.Items.Add(Menus.Item($"Delete {pages}", DeletePages, "Del"));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Menus.Item("Insert Blank Page After", InsertBlankPage));
        if (SelectedPages.Count > 0 && SelectedPages.All(index => _document!.Pages[index].IsBlank))
        {
            var paper = new ToolStripMenuItem("Paper Colour") { Padding = new Padding(4, 6, 4, 6) };
            foreach (var (name, argb) in PaperColours)
                paper.DropDownItems.Add(Menus.Item(name, () => PaintPages(Color.FromArgb(argb)), isChecked: SelectedPages.All(index => _document!.Pages[index].Background == argb)));
            paper.DropDownItems.Add(new ToolStripSeparator());
            paper.DropDownItems.Add(Menus.Item("Custom Colour…", () =>
            {
                if (ColorPickerDialog.Pick(FindForm(), Color.FromArgb(_document!.Pages[SelectedPages[0]].Background)) is { } picked) PaintPages(picked);
            }));
            menu.Items.Add(paper);
        }
        menu.Items.Add(Menus.Item("Insert Pages from a File…", () => _ = InsertFromFileAsync()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Menus.Item($"Extract {pages}…", () => _ = ExtractPagesAsync()));
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        Menus.Show(menu, strip, point);
    }

    static readonly (string Name, int Argb)[] PaperColours =
    [
        ("White", unchecked((int)0xFFFFFFFF)), ("Cream", unchecked((int)0xFFFBF6E9)), ("Light Grey", unchecked((int)0xFFF1F1F3)),
        ("Pale Blue", unchecked((int)0xFFEAF2FC)), ("Pale Green", unchecked((int)0xFFEAF6EC)), ("Black", unchecked((int)0xFF111113)),
    ];

    /// <summary>Sets the paper colour of one blank page; no colour leaves it see-through.</summary>
    void PaintPage(int page, Color color)
    {
        if (_document is null || page < 0 || page >= _document.Pages.Count || !_document.Pages[page].IsBlank) return;
        int argb = color.A == 0 ? Transparent : Color.FromArgb(255, color).ToArgb();
        if (_document.Pages[page].Background == argb) return;
        ChangePages(list => list[page].Background = argb, () => [page]);
        _canvas.Focus();
    }

    /// <summary>Sets the paper colour of the selected blank pages (pages from a file keep their own).</summary>
    void PaintPages(Color color)
    {
        if (_document is null) return;
        var pages = SelectedPages.Where(index => _document.Pages[index].IsBlank).ToList();
        if (pages.Count == 0) return;
        int argb = color.A == 0 ? Transparent : Color.FromArgb(255, color).ToArgb();
        ChangePages(list => { foreach (int index in pages) list[index].Background = argb; }, () => pages);
    }

    void ShowAddPagesMenu()
    {
        var menu = Menus.Create();
        menu.ShowImageMargin = false;
        menu.Items.Add(Menus.Item("Blank Page", InsertBlankPage));
        menu.Items.Add(Menus.Item("Pages from a File…", () => _ = InsertFromFileAsync()));
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        Menus.Show(menu, _addPages, Menus.Below(_addPages));
    }

    // Menus -------------------------------------------------------------------------------------

    void ShowFileMenu()
    {
        var menu = Menus.Create();
        menu.Items.Add(Menus.Item("New Blank PDF…", () =>
        {
            if (NewBlankDialog.Ask(FindForm(), image: false) is { } choice) Shell.OpenBlankDocument(choice);
        }));
        menu.Items.Add(Menus.Item("New Blank Image…", () =>
        {
            if (NewBlankDialog.Ask(FindForm(), image: true) is { } choice) Shell.OpenBlankDocument(choice);
        }));
        menu.Items.Add(Menus.Item("Open…", () => { foreach (string file in Shell.PickFiles(FileKind.Pdf, multiple: true)) Shell.OpenDocument(file); }, "Ctrl+O"));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Menus.Item("Save", () => _ = SaveAsync(), "Ctrl+S"));
        menu.Items.Add(Menus.Item("Save As…", () => _ = SaveAsAsync(), "Ctrl+Shift+S"));
        menu.Items.Add(Menus.Item("Rename…", Rename));
        menu.Items.Add(Menus.Item("History…", ShowHistory));
        var show = Menus.Item("Show in Folder", () => Native.ShowInExplorer(_path!));
        show.Enabled = _path is not null && File.Exists(_path);
        menu.Items.Add(show);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Menus.Item("Insert Blank Page", InsertBlankPage));
        menu.Items.Add(Menus.Item("Insert Pages from a File…", () => _ = InsertFromFileAsync()));
        foreach (ToolStripItem item in menu.Items) item.Enabled &= _document is not null || item.Text is "New Blank PDF…" or "New Blank Image…" or "Open…";
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        Menus.Show(menu, _file, Menus.Below(_file));
    }

    void ShowExportMenu()
    {
        var menu = Menus.Create();
        menu.ShowImageMargin = false;
        if (IsImage)
        {
            menu.Items.Add(Menus.Item("Export as Image…", () => _ = ExportImageAsAsync(), "Ctrl+Shift+S"));
            menu.Items.Add(Menus.Item("Export as PDF…", () => _ = ExportPdfAsync()));
        }
        else
        {
            menu.Items.Add(Menus.Item("Save As…", () => _ = SaveAsAsync(), "Ctrl+Shift+S"));
            menu.Items.Add(Menus.Item("Save a Flattened Copy…", () => _ = SaveFlattenedCopyAsync()));
            menu.Items.Add(Menus.Item("Export as Images…", () => _ = ExportImageAsAsync()));
            menu.Items.Add(Menus.Item("Extract Pages…", () => _ = ExtractPagesAsync()));
        }
        foreach (ToolStripItem item in menu.Items) item.Enabled = _document is not null && !_saving;
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        Menus.Show(menu, _export, Menus.Below(_export));
    }

    // Saving ------------------------------------------------------------------------------------

    string BaseName => _path is not null ? Path.GetFileNameWithoutExtension(_path)
        : _name is not null ? _name
        : _image?.Path is { } picture ? Path.GetFileNameWithoutExtension(picture) + " (edited)"
        : _image is not null ? "Untitled image" : "Untitled";

    /// <summary>
    /// A document for editing a picture: one page the picture's size (a pixel is ¾ of a point, so it exports back at
    /// its own size), with the picture on it as an object that can be moved, cropped or recoloured like any other.
    /// A blank image is a white page of the size chosen.
    /// </summary>
    static EditDocument ImageDocument(ImageStart start, string folder)
    {
        byte[]? data = start.Path is null ? null : ImageLook.Fit(File.ReadAllBytes(start.Path));
        Size pixels = start.Pixels;
        bool seeThrough = start.Background >> 24 == 0;
        if (data is not null)
            using (var picture = Image.FromStream(new MemoryStream(data)))
            {
                pixels = picture.Size;
                // A picture with see-through parts keeps them: nothing behind it, so it exports as it came in.
                seeThrough = picture is Bitmap bitmap && HasSeeThroughPixels(bitmap);
            }
        float width = Math.Max(1, pixels.Width) * 0.75f, height = Math.Max(1, pixels.Height) * 0.75f;
        EditDocument document = DocumentStore.CreateBlank(folder, width, height);
        document.Pages[0].Background = seeThrough ? Transparent : data is null ? start.Background : White;
        if (data is not null)
            document.Pages[0].Objects.Add(new PageObject { Kind = MarkKind.Image, ImageData = data, Bounds = new RectangleF(0, 0, width, height) });
        return document;
    }

    const int White = unchecked((int)0xFFFFFFFF), Transparent = 0x00FFFFFF;

    static EditDocument NewBlank(BlankChoice? blank, string folder)
    {
        if (blank is null) return DocumentStore.CreateBlank(folder);
        EditDocument document = DocumentStore.CreateBlank(folder, blank.Size.Width, blank.Size.Height);
        document.Pages[0].Background = blank.Background;
        return document;
    }

    /// <summary>Whether any pixel of a picture is less than solid (a PNG can carry alpha and use none of it).</summary>
    static bool HasSeeThroughPixels(Bitmap picture)
    {
        if (!Image.IsAlphaPixelFormat(picture.PixelFormat)) return false;
        var area = new Rectangle(0, 0, picture.Width, picture.Height);
        System.Drawing.Imaging.BitmapData data = picture.LockBits(area, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[picture.Width * 4];
            for (int y = 0; y < picture.Height; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                for (int x = 3; x < row.Length; x += 4)
                    if (row[x] < 255) return true;
            }
            return false;
        }
        finally { picture.UnlockBits(data); }
    }

    const string ImageFilter = "PNG image (*.png)|*.png|JPEG image (*.jpg)|*.jpg;*.jpeg";

    /// <summary>
    /// A page of an image document drawn at its own size in pixels, objects and all. A PNG keeps what has nothing
    /// behind it see-through; a JPEG can't, so it gets white there.
    /// </summary>
    static Bitmap RenderImagePage(PageEntry entry, bool seeThrough)
    {
        int width = Math.Max(1, (int)Math.Round(entry.Width / 0.75f)), height = Math.Max(1, (int)Math.Round(entry.Height / 0.75f));
        var bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        bitmap.SetResolution(96, 96);
        using Graphics g = Graphics.FromImage(bitmap);
        g.Clear(seeThrough ? Color.Transparent : Color.White);
        PagePainter.DrawPage(g, entry, null, new RectangleF(0, 0, width, height), export: true);
        return bitmap;
    }

    static void SaveImage(Bitmap bitmap, string path, bool jpeg)
    {
        if (!jpeg)
        {
            bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            return;
        }
        System.Drawing.Imaging.ImageCodecInfo codec = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid);
        using var quality = new System.Drawing.Imaging.EncoderParameters(1);
        quality.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 92L);
        bitmap.Save(path, codec, quality);
    }

    static bool IsImageFile(string path) => Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg";

    /// <summary>Asks where to export the pages as pictures, then does it.</summary>
    async Task<bool> ExportImageAsAsync()
    {
        if (_document is null) return false;
        string suggested = _imageTarget ?? Path.Combine(SuggestedFolder, BaseName + ".png");
        string? target = Shell.PickSavePath(suggested, ImageFilter);
        return target is not null && await ExportImageAsync(target);
    }

    /// <summary>
    /// Exports the pages as pictures, objects and all: an image document at its own pixel size, a PDF's pages at
    /// 200 dpi. One page goes to the file named; more go to numbered files beside it.
    /// </summary>
    async Task<bool> ExportImageAsync(string target)
    {
        if (_document is null || _saving) return false;
        _canvas.CommitTextEdit();
        EditDocument snapshot = _document.Clone();
        long state = _state;
        _saving = true;
        SetBusy(true, "Exporting…");
        UpdateState();
        string temp = Path.Combine(_workFolder, $"export-{Guid.NewGuid():N}.pdf");
        try
        {
            bool jpeg = Path.GetExtension(target).ToLowerInvariant() is ".jpg" or ".jpeg";
            string PathFor(int i, int count) => count == 1 ? target
                : Path.Combine(Path.GetDirectoryName(target)!, $"{Path.GetFileNameWithoutExtension(target)}-{i + 1}{Path.GetExtension(target)}");
            var written = new List<string>();
            if (IsImage)
            {
                // Drawn here at the picture's own size rather than through a PDF, so a PNG keeps see-through parts.
                for (int i = 0; i < snapshot.Pages.Count; i++)
                {
                    PageEntry entry = snapshot.Pages[i];
                    using Bitmap picture = RenderImagePage(entry, seeThrough: !jpeg);
                    string path = PathFor(i, snapshot.Pages.Count);
                    await Task.Run(() => SaveImage(picture, path, jpeg));
                    written.Add(path);
                }
            }
            else
            {
                await Task.Run(() => DocumentStore.Save(snapshot, temp, keepEditable: false));
                using PdfPreview preview = await PdfPreview.OpenAsync(temp);
                for (int i = 0; i < preview.PageCount; i++)
                {
                    string path = PathFor(i, preview.PageCount);
                    await preview.SaveImageAsync(i, path, jpeg ? PageImageFormat.Jpeg : PageImageFormat.Png, 200);
                    written.Add(path);
                }
            }
            if (IsImage)
            {
                _imageTarget = target;
                _savedState = state;
            }
            History.Add(written, "Export as image");
            ActivityLog.Write($"Exported {Words.Count(written.Count, "image", "images")} to {target}");
            Notify(written.Count == 1 ? $"Exported {Path.GetFileName(target)}." : $"Exported {written.Count} images next to {Path.GetFileName(target)}.", target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ToolException or System.Runtime.InteropServices.ExternalException)
        {
            ActivityLog.Error($"exporting {target}", ex);
            Notify($"Couldn’t export {Path.GetFileName(target)}: {ex.Message}");
            return false;
        }
        finally
        {
            TryDelete(temp);
            _saving = false;
            if (!IsDisposed) SetBusy(false, "");
            UpdateState();
        }
    }

    /// <summary>For an image document: saves a PDF copy and keeps editing the image.</summary>
    async Task ExportPdfAsync()
    {
        if (_document is null) return;
        _canvas.CommitTextEdit();
        string? target = Shell.PickSavePath(Path.Combine(SuggestedFolder, BaseName + ".pdf"), FileDialogs.Filter(FileKind.Pdf));
        if (target is null || !await WriteAsync(_document.Clone(), target, _inspector.KeepEditable, "Saving…")) return;
        History.Add([target], "Export as PDF");
        Notify($"Saved {Path.GetFileName(target)}.", target);
    }

    string SuggestedFolder => _path is not null ? Path.GetDirectoryName(_path)!
        : Directory.Exists(Shell.Settings.LastOpenFolder) ? Shell.Settings.LastOpenFolder
        : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    async Task<bool> SaveAsync()
    {
        // An edited picture saves as a picture: where it went last time, or asked the first time.
        if (IsImage) return _imageTarget is not null ? await ExportImageAsync(_imageTarget) : await ExportImageAsAsync();
        if (_path is null) return await SaveAsAsync();
        return await SaveToAsync(_path);
    }

    async Task<bool> SaveAsAsync()
    {
        if (_document is null) return false;
        if (IsImage) return await ExportImageAsAsync();
        string? target = Shell.PickSavePath(Path.Combine(SuggestedFolder, BaseName + ".pdf"), FileDialogs.Filter(FileKind.Pdf));
        return target is not null && await SaveToAsync(target);
    }

    async Task<bool> SaveToAsync(string target)
    {
        if (_document is null || _saving) return false;
        _canvas.CommitTextEdit();
        long state = _state;
        if (!await WriteAsync(_document.Clone(), target, _inspector.KeepEditable, "Saving…")) return false;
        _savedState = state;
        bool renamed = !string.Equals(_path, target, StringComparison.OrdinalIgnoreCase);
        _path = target;
        if (renamed) History.Remember(target);
        Notify($"Saved {DateTime.Now.ToString("t", CultureInfo.CurrentCulture)}.");
        UpdateState();
        return true;
    }

    async Task SaveFlattenedCopyAsync()
    {
        if (_document is null) return;
        _canvas.CommitTextEdit();
        string? target = Shell.PickSavePath(Path.Combine(SuggestedFolder, BaseName + " (flattened).pdf"), FileDialogs.Filter(FileKind.Pdf));
        if (target is null) return;
        if (await WriteAsync(_document.Clone(), target, keepEditable: false, "Saving…"))
        {
            History.Add([target], "Flattened copy");
            Notify($"Saved a flattened copy as {Path.GetFileName(target)}.", target);
        }
    }

    /// <summary>Writes a snapshot next to its work files, then copies it over the target in one step.</summary>
    async Task<bool> WriteAsync(EditDocument snapshot, string target, bool keepEditable, string busyText)
    {
        _saving = true;
        SetBusy(true, busyText);
        UpdateState();
        string temp = Path.Combine(_workFolder, $"save-{Guid.NewGuid():N}.pdf");
        try
        {
            await Task.Run(() =>
            {
                DocumentStore.Save(snapshot, temp, keepEditable);
                File.Copy(temp, target, overwrite: true);
            });
            ActivityLog.Write($"Saved {target} ({Words.Pages(snapshot.Pages.Count)}{(keepEditable ? ", kept editable" : ", flattened")})");
            return true;
        }
        catch (Exception ex)
        {
            ActivityLog.Error($"saving {target}", ex);
            string reason = ex is IOException && File.Exists(target) ? "The file is open in another app. Close it there, then save again." : ex.Message;
            Notify($"Couldn’t save {Path.GetFileName(target)}. {reason}");
            return false;
        }
        finally
        {
            TryDelete(temp);
            _saving = false;
            if (!IsDisposed)
            {
                SetBusy(false, "");
                UpdateState();
            }
        }
    }

    /// <summary>Saves synchronously while the tab closes.</summary>
    bool SaveBeforeClose()
    {
        string? target = _path ?? Shell.PickSavePath(Path.Combine(SuggestedFolder, BaseName + ".pdf"), FileDialogs.Filter(FileKind.Pdf));
        if (target is null || _document is null) return false;
        _canvas.CommitTextEdit();
        Cursor = Cursors.WaitCursor;
        string temp = Path.Combine(_workFolder, "close.pdf");
        try
        {
            DocumentStore.Save(_document.Clone(), temp, _inspector.KeepEditable);
            File.Copy(temp, target, overwrite: true);
            ActivityLog.Write($"Saved {target} on close");
            return true;
        }
        catch (Exception ex) when (ex is ToolException or IOException or UnauthorizedAccessException)
        {
            ActivityLog.Error($"saving {target} on close", ex);
            AskDialog.Tell(FindForm(), $"Couldn’t Save {Path.GetFileName(target)}", ex.Message);
            return false;
        }
        finally
        {
            TryDelete(temp);
            Cursor = Cursors.Default;
        }
    }

    public override bool ConfirmClose()
    {
        if (_saving)
        {
            AskDialog.Tell(FindForm(), "Still Saving", "Sheaf is still saving this file. Wait a moment for it to finish, then close the tab.");
            return false;
        }
        if (!IsDirty) return true;
        DialogResult answer = AskDialog.Ask(FindForm(), $"Save Changes to {Title}?", "Your changes are lost if you don’t save them.", "Save", other: "Don’t Save");
        if (answer == DialogResult.No) return true;
        return answer == DialogResult.OK && SaveBeforeClose();
    }

    // Status ------------------------------------------------------------------------------------

    void SetBusy(bool busy, string text)
    {
        _busy.Running = busy;
        if (busy && text.Length > 0) _notice = text;
        else if (!busy && _notice.EndsWith('…')) _notice = "";
        UpdateStatus();
    }

    /// <summary>A short message in the status bar, with an optional file to show in Explorer on click.</summary>
    void Notify(string text, string? file = null)
    {
        _notice = text;
        _status.Tag = file;
        _status.Cursor = file is null ? Cursors.Default : Cursors.Hand;
        UpdateStatus();
    }

    void UpdateState()
    {
        bool ready = _document is not null;
        _undo.Enabled = ready && _undoStack.Count > 0 && !_saving;
        _redo.Enabled = ready && _redoStack.Count > 0 && !_saving;
        _save.Enabled = _export.Enabled = ready && !_saving;
        _save.Text = _saving ? "Saving…" : "Save";
        _modes.Enabled = ready;
        _pagesHeading.Text = ready ? $"Pages  {_document!.Pages.Count}" : "Pages";
        UpdateStatus();
        Shell.PageChanged(this);
    }

    void UpdateStatus()
    {
        if (!_zoomBox.Box.Focused) _zoomBox.Text = $"{Math.Round(_canvas.Zoom * 100)}";
        _zoomIn.Enabled = _canvas.CanZoomIn;
        _zoomOut.Enabled = _canvas.CanZoomOut;
        if (_document is null || _document.Pages.Count == 0)
        {
            _status.Text = _notice;
            return;
        }
        int page = Math.Min(_canvas.CurrentPage, _document.Pages.Count - 1);
        SizeF size = PagePainter.DisplaySize(_document.Pages[page]);
        string paper = PaperName(size) is { } name ? $"{name} · " : "";
        var parts = new List<string> { $"Page {page + 1} of {_document.Pages.Count}", $"{paper}{size.Width:0} × {size.Height:0} pt" };
        if (IsDirty) parts.Add("Unsaved changes");
        if (_notice.Length > 0) parts.Add(_notice);
        _status.Text = string.Join("  ·  ", parts);
    }

    static string? PaperName(SizeF size)
    {
        (string Name, float Width, float Height)[] papers = [("A4", 595, 842), ("Letter", 612, 792), ("Legal", 612, 1008), ("A3", 842, 1191), ("A5", 420, 595)];
        float shorter = Math.Min(size.Width, size.Height), longer = Math.Max(size.Width, size.Height);
        return papers.FirstOrDefault(paper => Math.Abs(paper.Width - shorter) < 3 && Math.Abs(paper.Height - longer) < 3).Name;
    }

    // Keyboard ----------------------------------------------------------------------------------

    bool TypingInTextBox()
    {
        Control? focused = ActiveControl;
        while (focused is ContainerControl { ActiveControl: { } inner }) focused = inner;
        return focused is TextBoxBase || _canvas.IsEditingText;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (_document is null) return base.ProcessCmdKey(ref msg, keyData);
        bool typing = TypingInTextBox();
        switch (keyData)
        {
            case Keys.Control | Keys.S: _ = SaveAsync(); return true;
            case Keys.Control | Keys.Shift | Keys.S: _ = SaveAsAsync(); return true;
            case Keys.Control | Keys.Z when !typing: Undo(); return true;
            case Keys.Control | Keys.Y or Keys.Control | Keys.Shift | Keys.Z when !typing: Redo(); return true;
        }
        if (!typing && _mode != DocumentMode.Pages && (keyData & Keys.Modifiers) == Keys.None && _rail.FindByKey(keyData) is { } tool)
        {
            ChooseTool(tool.Tool, announce: true);
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    public override void Activated()
    {
        if (_loaded && _canvas.Visible) _canvas.Focus();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _renderer?.Dispose();
            try { if (Directory.Exists(_workFolder)) Directory.Delete(_workFolder, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ActivityLog.Error("removing a work folder", ex); }
        }
        base.Dispose(disposing);
    }

    static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
