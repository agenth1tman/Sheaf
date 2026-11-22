using System.Runtime.InteropServices;
using Sheaf.Core.IO;
using Sheaf.Pages;
using Sheaf.Pages.Document;
using Sheaf.Pages.Scan;
using Sheaf.Platform;
using Sheaf.Tools;
using Sheaf.Ui;
using Sheaf.Ui.Controls;

namespace Sheaf.Shell;

/// <summary>
/// The window: a custom title bar that holds the tabs, and the selected tab's page below it.
/// The standard frame is kept on the sides and bottom (so Windows still resizes, snaps and shadows the window)
/// and removed at the top, where the title bar draws its own caption.
/// </summary>
internal sealed class MainWindow : Form, IShell
{
    readonly AppSettings _settings;
    readonly LaunchOptions _launch;
    readonly TitleBar _titleBar = new();
    readonly Surface _host = new(SurfaceRole.Panel) { Dock = DockStyle.Fill };
    readonly HomePage _home;
    TabItem? _current;
    bool _shown;

    public MainWindow(AppSettings settings, LaunchOptions launch)
    {
        _settings = settings;
        _launch = launch;
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = AppFonts.Body;
        Text = AppPaths.IsSandbox ? "Sheaf (sandbox)" : "Sheaf";
        Icon = new Icon(typeof(MainWindow).Assembly.GetManifestResourceStream("App.ico")!);
        MinimumSize = new Size(Theme.MinimumWindowWidth, Theme.MinimumWindowHeight);
        Size = new Size(1360, 860);
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;
        AllowDrop = true;

        Controls.Add(_host);
        Controls.Add(_titleBar);
        _titleBar.TabSelected += (_, tab) => ShowTab(tab);
        _home = new HomePage(this);
        AddTab(new TabItem("Home", "home", _home) { IsPinned = true }, select: true);

        _titleBar.CloseRequested += (_, tab) => CloseTab(tab);
        _titleBar.NewTabRequested += (_, _) => NewTab();
        _titleBar.SettingsRequested += (_, _) => OpenSettings();
        Theme.Changed += (_, _) => Invalidate(true);
        DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
        DragDrop += (_, e) => { if (e.Data?.GetData(DataFormats.FileDrop) is string[] paths) OpenFiles(paths, preferCurrent: true); };
        ResumeLayout(false);
    }

    public AppSettings Settings => _settings;

    public override Color BackColor
    {
        get => Theme.Current.Chrome;
        set { }
    }

    public void SaveSettings() => SettingsStore.Save(_settings);

    // Window frame ------------------------------------------------------------------------------

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_NCCALCSIZE && m.WParam != IntPtr.Zero)
        {
            var before = Marshal.PtrToStructure<Native.NcCalcSizeParams>(m.LParam);
            int top = before.Proposed.Top;
            base.WndProc(ref m);
            var after = Marshal.PtrToStructure<Native.NcCalcSizeParams>(m.LParam);
            after.Proposed.Top = top + (WindowState == FormWindowState.Maximized ? Native.ResizeBorder(DeviceDpi) : 0);
            Marshal.StructureToPtr(after, m.LParam, false);
            m.Result = IntPtr.Zero;
            return;
        }
        if (m.Msg == Native.WM_NCHITTEST)
        {
            base.WndProc(ref m);
            if ((int)m.Result != Native.HTCLIENT) return;
            Point point = PointToClient(new Point(unchecked((short)(long)m.LParam), unchecked((short)((long)m.LParam >> 16))));
            int edge = LogicalToDeviceUnits(6), corner = LogicalToDeviceUnits(12);
            if (WindowState != FormWindowState.Maximized && point.Y < edge)
                m.Result = point.X < corner ? Native.HTTOPLEFT : point.X > Width - corner ? Native.HTTOPRIGHT : Native.HTTOP;
            else if (point.Y < _titleBar.Height)
                m.Result = Native.HTCAPTION;
            return;
        }
        base.WndProc(ref m);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        _titleBar.Invalidate();
    }

    // Startup -----------------------------------------------------------------------------------

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        RestoreWindowBounds();
        OpenFromLaunch(_launch.Files, _launch.ToolId);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _shown = true;
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        ActivityLog.Write($"Window shown {(DateTime.Now - process.StartTime).TotalMilliseconds:0} ms after launch");
    }

    void RestoreWindowBounds()
    {
        if (!_settings.HasWindowBounds) return;
        var bounds = new Rectangle(_settings.WindowX, _settings.WindowY, _settings.WindowWidth, _settings.WindowHeight);
        if (!Screen.AllScreens.Any(screen => screen.WorkingArea.IntersectsWith(bounds))) return;
        StartPosition = FormStartPosition.Manual;
        Bounds = bounds;
        if (_settings.WindowMaximized) WindowState = FormWindowState.Maximized;
    }

    public void OpenFromLaunch(IReadOnlyList<string> files, string? toolId)
    {
        if (toolId is not null && ToolCatalog.FindInfo(toolId) is not null) OpenTool(toolId, files);
        else if (files.Count > 0) OpenFiles([.. files], preferCurrent: false);
    }

    public void BringToFront(IReadOnlyList<string> args)
    {
        var options = LaunchOptions.Parse(args);
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        OpenFromLaunch(options.Files, options.ToolId);
    }

    // Tabs --------------------------------------------------------------------------------------

    void AddTab(TabItem tab, bool select)
    {
        tab.Page.Visible = false;
        _host.Controls.Add(tab.Page);
        _titleBar.Add(tab, select);
    }

    void ShowTab(TabItem tab)
    {
        if (_current == tab) return;
        if (_shown) Native.PauseDrawing(_host);
        _host.SuspendLayout();
        // Home is never hidden, only covered by the page in front: hiding and showing its hundred-odd controls took a
        // fifth of a second, which held up the tab closing on the way back to it. Other pages hide as before.
        if (_current is not null && _current.Page != _home) _current.Page.Visible = false;
        tab.Page.Visible = true;
        tab.Page.BringToFront();
        _host.ResumeLayout(true);
        if (_shown) Native.ResumeDrawing(_host);
        _current = tab;
        UpdateWindowTitle();
        if (_shown) ScreenReader.Say(_titleBar, _titleBar.Describe(_titleBar.Tabs.ToList().IndexOf(tab)));
        (tab.Page as TabContent)?.Activated();
        if (!tab.Page.ContainsFocus) tab.Page.SelectNextControl(tab.Page, true, true, true, true);
    }

    void UpdateWindowTitle()
    {
        string app = AppPaths.IsSandbox ? "Sheaf (sandbox)" : "Sheaf";
        Text = _current is null || _current.IsPinned ? app : $"{_current.Title} – {app}";
    }

    TabItem? TabOf(Control page) => _titleBar.Tabs.FirstOrDefault(tab => tab.Page == page);

    /// <summary>Tab moves through the page in front only, so it never wanders into Home behind it.</summary>
    protected override bool ProcessTabKey(bool forward)
    {
        if (_current?.Page is not { } page || page == _home || !page.ContainsFocus) return base.ProcessTabKey(forward);
        Control? focused = ActiveControl;
        while (focused is ContainerControl { ActiveControl: { } inner }) focused = inner;
        return page.SelectNextControl(focused, forward, tabStopOnly: true, nested: true, wrap: true);
    }

    /// <summary>How to bring back each closed tab, newest last, for Ctrl+Shift+T. Only these small notes are kept.</summary>
    readonly List<Action> _closed = [];

    void CloseTab(TabItem tab)
    {
        if (tab.IsPinned) return;
        if (tab.Page is TabContent content && !content.ConfirmClose()) return;
        if (ReopenerOf(tab.Page) is { } reopen)
        {
            _closed.Add(reopen);
            if (_closed.Count > 20) _closed.RemoveAt(0);
        }
        if (_current == tab) _current = null;
        // The page stays where it is, covered by the one shown next, and is let go once its tab has finished
        // closing: taking it out of the window or freeing it now would hold up the animation.
        _titleBar.Remove(tab, gone: tab.Page.Dispose);
        if (_titleBar.Selected is { } next) ShowTab(next);
    }

    Action? ReopenerOf(Control page) => page switch
    {
        DocumentPage { FilePath: { } path } document => () => { if (File.Exists(path)) OpenDocument(path, document.ModeName); },
        ToolPage tool => () => OpenTool(tool.Tool.Info.Id, [.. tool.FilePaths.Where(File.Exists)]),
        SettingsPage => OpenSettings,
        Pages.Scan.ScanPage => OpenScan,
        _ => null,
    };

    /// <summary>Ctrl+Shift+T: opens the tab closed last again.</summary>
    void ReopenClosed()
    {
        if (_closed.Count == 0) return;
        Action reopen = _closed[^1];
        _closed.RemoveAt(_closed.Count - 1);
        reopen();
    }

    public void PageChanged(TabContent page)
    {
        if (TabOf(page) is not { } tab) return;
        tab.Title = page.Title;
        tab.IsDirty = page.IsDirty;
        tab.Icon = page.Icon;
        _titleBar.RepaintTab();
        if (tab == _current) UpdateWindowTitle();
    }

    TabItem Open(TabContent page)
    {
        var tab = new TabItem(page.Title, page.Icon, page) { Badge = page.Badge, Detail = (page as DocumentPage)?.FilePath };
        AddTab(tab, select: true);
        return tab;
    }

    public void ShowHome() => _titleBar.Select(0);

    /// <summary>
    /// The + button and Ctrl+T: new things start from Home. Its tab lights up and its search box takes the cursor,
    /// so the press shows even when Home is already in front.
    /// </summary>
    void NewTab()
    {
        ShowHome();
        _titleBar.Pulse(0);
        _home.Beckon();
    }

    public void OpenDocument(string path, string? mode = null)
    {
        string full = Path.GetFullPath(path);
        if (_titleBar.Tabs.FirstOrDefault(tab => tab.Page is DocumentPage document && string.Equals(document.FilePath, full, StringComparison.OrdinalIgnoreCase)) is { } open)
        {
            _titleBar.Select(open);
            if (mode is not null) ((DocumentPage)open.Page).SetMode(mode);
            return;
        }
        Open(new DocumentPage(this, full, mode));
        History.Remember(full);
    }

    public void OpenBlankDocument() => Open(new DocumentPage(this, null, null));

    public void OpenImageDocument(string? path, Size pixels)
    {
        Open(new DocumentPage(this, null, null, new ImageStart(path, pixels)));
        if (path is not null) History.Remember(path);
    }

    public void OpenTool(string toolId, IReadOnlyList<string>? files = null)
    {
        if (toolId == ToolCatalog.Organize.Id)
        {
            if (files is { Count: > 0 }) OpenDocument(files[0], "pages");
            else if (PickFiles(FileKind.Pdf, multiple: false).FirstOrDefault() is { } picked) OpenDocument(picked, "pages");
            return;
        }
        if (ToolCatalog.FindPageTool(toolId) is { } pageTool)
        {
            string? file = files?.FirstOrDefault() ?? PickFiles(FileKind.Pdf, multiple: false).FirstOrDefault();
            if (file is not null) OpenDocument(file, pageTool);
            return;
        }
        if (_titleBar.Tabs.FirstOrDefault(tab => tab.Page is ToolPage page && page.Tool.Info.Id == toolId) is { } existing)
        {
            _titleBar.Select(existing);
            if (files is { Count: > 0 }) ((ToolPage)existing.Page).AddPaths(files);
            return;
        }
        if (ToolCatalog.Create(toolId) is not { } tool) return;
        var toolPage = new ToolPage(this, tool);
        Open(toolPage);
        if (files is { Count: > 0 }) toolPage.AddPaths(files);
        RememberTool(toolId);
    }

    public void OpenScan() => Open(new ScanPage(this));

    public void OpenSettings()
    {
        if (_titleBar.Tabs.FirstOrDefault(tab => tab.Page is SettingsPage) is { } open) _titleBar.Select(open);
        else Open(new SettingsPage(this));
    }

    void RememberTool(string toolId)
    {
        _settings.RecentTools.Remove(toolId);
        _settings.RecentTools.Insert(0, toolId);
        if (_settings.RecentTools.Count > SettingsStore.RecentToolLimit)
            _settings.RecentTools.RemoveRange(SettingsStore.RecentToolLimit, _settings.RecentTools.Count - SettingsStore.RecentToolLimit);
    }

    /// <summary>PDFs open as documents; other files go to the tool that converts them, or to the tool in front.</summary>
    void OpenFiles(string[] paths, bool preferCurrent)
    {
        if (preferCurrent && _current?.Page is TabContent content && content.AcceptDrop(paths)) return;
        foreach (string pdf in paths.Where(path => FileKinds.Of(path) == FileKind.Pdf)) OpenDocument(pdf);
        var others = paths.Where(path => FileKinds.Of(path) is not FileKind.Pdf and not FileKind.None).ToList();
        foreach (var group in others.GroupBy(ToolCatalog.ForFile))
            if (group.Key is not null) OpenTool(group.Key, [.. group]);
    }

    // Dialogs -----------------------------------------------------------------------------------

    public IReadOnlyList<string> PickFiles(FileKind kinds, bool multiple)
    {
        using var dialog = new OpenFileDialog
        {
            Multiselect = multiple,
            Filter = FileDialogs.Filter(kinds),
            InitialDirectory = Directory.Exists(_settings.LastOpenFolder) ? _settings.LastOpenFolder : "",
            Title = multiple ? "Add files" : "Open a file",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return [];
        _settings.LastOpenFolder = Path.GetDirectoryName(dialog.FileNames[0]) ?? "";
        return dialog.FileNames;
    }

    public string? PickFolder(string description)
    {
        using var dialog = new FolderBrowserDialog { Description = description, UseDescriptionForTitle = true, ShowNewFolderButton = true };
        if (Directory.Exists(_settings.OutputFolder)) dialog.InitialDirectory = _settings.OutputFolder;
        return dialog.ShowDialog(this) == DialogResult.OK ? dialog.SelectedPath : null;
    }

    public string? PickSavePath(string suggestedPath, string filter)
    {
        using var dialog = new SaveFileDialog
        {
            Filter = filter,
            FileName = Path.GetFileName(suggestedPath),
            InitialDirectory = Path.GetDirectoryName(suggestedPath) ?? "",
            OverwritePrompt = true,
            AddExtension = true,
        };
        return dialog.ShowDialog(this) == DialogResult.OK ? dialog.FileName : null;
    }

    // Keyboard and closing ----------------------------------------------------------------------

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Control | Keys.T: NewTab(); return true;
            case Keys.Control | Keys.Shift | Keys.T: ReopenClosed(); return true;
            case Keys.Control | Keys.OemCloseBrackets: _titleBar.Select((_titleBar.SelectedIndex + 1) % _titleBar.Tabs.Count); return true;
            case Keys.Control | Keys.OemOpenBrackets: _titleBar.Select((_titleBar.SelectedIndex - 1 + _titleBar.Tabs.Count) % _titleBar.Tabs.Count); return true;
            case Keys.Control | Keys.W: if (_current is not null) CloseTab(_current); return true;
            case Keys.Control | Keys.Tab: _titleBar.Select((_titleBar.SelectedIndex + 1) % _titleBar.Tabs.Count); return true;
            case Keys.Control | Keys.Shift | Keys.Tab: _titleBar.Select((_titleBar.SelectedIndex - 1 + _titleBar.Tabs.Count) % _titleBar.Tabs.Count); return true;
            case Keys.Control | Keys.O when _current?.Page is not ToolPage:
                foreach (string file in PickFiles(FileKind.Pdf, multiple: true)) OpenDocument(file);
                return true;
            case Keys.Control | Keys.Oemcomma: OpenSettings(); return true;
        }
        if (keyData >= (Keys.Control | Keys.D1) && keyData <= (Keys.Control | Keys.D9))
        {
            int index = (int)(keyData & Keys.KeyCode) - (int)Keys.D1;
            _titleBar.Select(keyData == (Keys.Control | Keys.D9) ? _titleBar.Tabs.Count - 1 : index);
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            foreach (TabItem tab in _titleBar.Tabs.ToList())
            {
                if (tab.Page is not TabContent content || (!content.IsDirty && !content.IsBusy)) continue;
                _titleBar.Select(tab);
                if (!content.ConfirmClose()) { e.Cancel = true; return; }
            }
        }
        SaveWindowState();
        base.OnFormClosing(e);
    }

    void SaveWindowState()
    {
        if (!_shown) return;
        Rectangle bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        _settings.WindowX = bounds.X;
        _settings.WindowY = bounds.Y;
        _settings.WindowWidth = bounds.Width;
        _settings.WindowHeight = bounds.Height;
        _settings.WindowMaximized = WindowState == FormWindowState.Maximized;
        SaveSettings();
    }
}

internal static class FileDialogs
{
    public static string Filter(FileKind kinds)
    {
        string patterns = string.Join(";", FileKinds.Extensions(kinds).Select(extension => "*" + extension));
        string name = kinds switch
        {
            FileKind.Pdf => "PDF files",
            FileKind.Image => "Images",
            FileKind.Word => "Word documents",
            FileKind.Excel => "Spreadsheets",
            FileKind.PowerPoint => "Presentations",
            _ => "Supported files",
        };
        return $"{name} ({patterns})|{patterns}|All files (*.*)|*.*";
    }
}
