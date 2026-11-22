using Sheaf.Core;
using Sheaf.Core.IO;
using Sheaf.Platform;
using Sheaf.Shell;
using Sheaf.Tools;
using Sheaf.Ui;
using Sheaf.Ui.Controls;

namespace Sheaf.Pages;

/// <summary>
/// A batch tool in its own tab: files on the left (most of the space), the tool's options and its one primary
/// button on the right.
/// </summary>
internal sealed class ToolPage : TabContent
{
    readonly CancellationTokenSource _closing = new();
    readonly AppButton _add = new("Add Files…", ButtonKind.Secondary, "plus");
    readonly AppButton _remove = new("Remove", ButtonKind.Quiet);
    readonly AppButton _up = new("Move Up", ButtonKind.Quiet);
    readonly AppButton _down = new("Move Down", ButtonKind.Quiet);
    readonly AppButton _clear = new("Clear", ButtonKind.Quiet);
    readonly TextLabel _totals = Make.Line("", Tone.Tertiary, AppFonts.Small);
    readonly FileList _list = new() { Dock = DockStyle.Fill };
    readonly ActionPanel _action;
    CancellationTokenSource? _run;

    public ToolPage(IShell shell, Tool tool) : base(shell)
    {
        Tool = tool;
        _action = new ActionPanel(shell);
        SuspendLayout();

        var content = new Surface { Dock = DockStyle.Fill, Padding = new Padding(32, 24, 0, 0) };
        content.Controls.Add(BuildBody());
        var header = Make.Stack(Make.Title(tool.Info.Title), Make.Paragraph(tool.Info.Summary));
        header.Dock = DockStyle.Top;
        header.Padding = new Padding(0, 0, 32, 18);
        content.Controls.Add(header);
        Controls.Add(content);

        _list.EmptyText = $"Drop {KindNoun(tool.Info.Accepts)} here, or choose Add Files…";
        _action.RunButton.Text = tool.Info.ActionText;
        _action.RunButton.ReserveWidthFor("Cancel");
        _add.Click += (_, _) => ChooseFiles();
        _remove.Click += (_, _) => _list.RemoveSelected();
        _up.Click += (_, _) => _list.MoveSelected(-1);
        _down.Click += (_, _) => _list.MoveSelected(1);
        _clear.Click += (_, _) => _list.Clear();
        _list.FilesDropped += (_, paths) => AddPaths(paths);
        _list.EmptyClicked += (_, _) => ChooseFiles();
        _list.FilesChanged += (_, _) => UpdateState();
        _list.SelectedIndexChanged += (_, _) => UpdateState();
        _list.RemoveRequested += (_, _) => { if (_run is null) _list.RemoveSelected(); };
        _action.RunButton.Click += (_, _) => OnRunClicked();
        UpdateState();
        ResumeLayout(false);
    }

    public Tool Tool { get; }

    /// <summary>The files in the list, for opening the tab again after it closes.</summary>
    public IReadOnlyList<string> FilePaths => [.. _list.Files.Select(file => file.Path)];

    public override string Title => Tool.Info.Title;

    public override string Icon => Tool.Info.Icon;

    public override bool IsBusy => _run is not null;

    Control BuildBody()
    {
        var frame = new Surface { Dock = DockStyle.Fill, Padding = new Padding(1), Margin = new Padding(0, 12, 0, 0) };
        frame.Borders = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        frame.Controls.Add(_list);
        _totals.Margin = new Padding(0, 8, 0, 12);

        var files = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = new Padding(0, 0, 24, 0) };
        files.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        files.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        files.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        files.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        files.Controls.Add(Make.Row(_add, _remove, _up, _down, _clear), 0, 0);
        files.Controls.Add(frame, 0, 1);
        files.Controls.Add(_totals, 0, 2);

        var options = new Surface { Dock = DockStyle.Fill, AutoScroll = true };
        if (Tool.Options is { } toolOptions)
        {
            var optionsStack = Make.Stack(Make.PanelHeading("Options"), toolOptions);
            toolOptions.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            optionsStack.Dock = DockStyle.Top;
            optionsStack.Padding = new Padding(20, 4, 20, 8);
            options.Controls.Add(optionsStack);
        }
        if (Tool.Unavailable is { } unavailable) _action.Notice = unavailable;

        var side = new Surface { Dock = DockStyle.Fill, Borders = AnchorStyles.Left };
        var sideLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(1, 0, 0, 0) };
        sideLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        sideLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        sideLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        sideLayout.Controls.Add(options, 0, 0);
        sideLayout.Controls.Add(_action, 0, 1);
        side.Controls.Add(sideLayout);

        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.SidePanelWidth));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        body.Controls.Add(files, 0, 0);
        body.Controls.Add(side, 1, 0);
        return body;
    }

    public override bool AcceptDrop(string[] paths)
    {
        AddPaths(paths);
        return true;
    }

    void ChooseFiles()
    {
        if (_run is not null) return;
        AddPaths(Shell.PickFiles(Tool.Info.Accepts, multiple: true));
    }

    public void AddPaths(IEnumerable<string> paths)
    {
        if (_run is not null) return;
        var candidates = paths.SelectMany(Expand).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var known = _list.Files.Select(file => file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var accepted = candidates.Where(path => FileKinds.Matches(path, Tool.Info.Accepts) && !known.Contains(path)).ToList();
        int skipped = candidates.Count(path => !FileKinds.Matches(path, Tool.Info.Accepts));

        var files = accepted.Select(path => new SourceFile(path)).ToList();
        _list.Add(files);
        foreach (SourceFile file in files) _ = InspectAsync(file);
        if (skipped > 0)
            _action.Result.Show($"{Words.Files(skipped)} skipped: {Tool.Info.Title} works with {KindNoun(Tool.Info.Accepts)}.", Tone.Secondary);
    }

    static IEnumerable<string> Expand(string path) =>
        Directory.Exists(path) ? Directory.EnumerateFiles(path) : File.Exists(path) ? [path] : [];

    async Task InspectAsync(SourceFile file)
    {
        try { await file.Inspect(LogicalToDeviceUnits(FileList.ThumbnailWidth), _closing.Token); }
        catch (OperationCanceledException) { return; }
        if (IsDisposed) return;
        _list.RefreshFile(file);
        UpdateTotals();
    }

    void UpdateState()
    {
        bool running = _run is not null;
        bool hasFiles = _list.Files.Count > 0, hasSelection = _list.SelectedIndices.Count > 0;
        _add.Enabled = !running;
        _remove.Enabled = _up.Enabled = _down.Enabled = !running && hasSelection;
        _clear.Enabled = !running && hasFiles;
        string? notice = Tool.Unavailable ?? MissingFilesNotice();
        _action.Notice = running ? null : notice;
        _action.RunButton.Enabled = running || notice is null;
        UpdateTotals();
    }

    string? MissingFilesNotice()
    {
        int count = _list.Files.Count, minimum = Tool.Info.MinimumFiles;
        return count == 0 || count >= minimum ? null : $"{Tool.Info.Title} needs at least {Words.Files(minimum)}.";
    }

    void UpdateTotals()
    {
        var files = _list.Files;
        if (files.Count == 0) { _totals.Text = ""; return; }
        var parts = new List<string> { Words.Files(files.Count) };
        if (files.All(file => file.PageCount is not null) && files.Any(file => file.PageCount > 0))
            parts.Add(Words.Pages(files.Sum(file => file.PageCount ?? 0)));
        parts.Add(Words.Size(files.Sum(file => file.Size)));
        _totals.Text = string.Join(" · ", parts);
    }

    async void OnRunClicked()
    {
        if (_run is not null)
        {
            _run.Cancel();
            _action.Result.Show("Stopping after the current file…", Tone.Secondary);
            return;
        }
        if (_list.Files.Count == 0)
        {
            _action.Result.Show($"Add {KindNoun(Tool.Info.Accepts)} first.", Tone.Secondary);
            _add.Focus();
            return;
        }
        if (Tool.Validate() is { } invalid)
        {
            (invalid as FormField)?.FocusInput();
            return;
        }
        await RunAsync([.. _list.Files]);
    }

    async Task RunAsync(IReadOnlyList<SourceFile> files)
    {
        _run = new CancellationTokenSource();
        SetRunning(true);
        var progress = new Progress<string>(text => { if (_run is not null) _action.Result.Show(text, Tone.Secondary); });
        var share = new Progress<RunShare>(share => _action.Progress.Report(share.Done, share.Next));
        var run = new ToolRun(files, new OutputPlanner(Shell.Settings), progress, share, _run.Token);
        bool completed = false;
        try
        {
            ActivityLog.Write($"{Tool.Info.Title}: started with {Words.Files(files.Count)}");
            ToolResult result = await Tool.RunAsync(run);
            ActivityLog.Write($"{Tool.Info.Title}: saved {result.Outputs.Count}, failed {result.Failures.Count}");
            foreach (string failure in result.Failures) ActivityLog.Write($"{Tool.Info.Title}: {failure}");
            completed = result.Outputs.Count > 0 || result.Failures.Count == 0;
            History.Add(result.Outputs, Tool.Info.Title);
            _action.ShowOutcome(result);
            if (Shell.Settings.OpenFolderWhenDone && result.Outputs.Count > 0) Native.ShowInExplorer(result.Outputs[0]);
        }
        catch (OperationCanceledException)
        {
            _action.Result.Show("Stopped. Files finished before that were kept.", Tone.Secondary);
        }
        catch (ToolException ex)
        {
            _action.Result.Show(ex.Message, Tone.Error);
        }
        catch (Exception ex)
        {
            ActivityLog.Error($"{Tool.Info.Title} failed", ex);
            _action.Result.Show($"{Tool.Info.Title} failed: {ex.Message} Details are in the activity log.", Tone.Error);
        }
        finally
        {
            _run.Dispose();
            _run = null;
            _action.Progress.Finish(completed);
            SetRunning(false);
        }
    }

    void SetRunning(bool running)
    {
        if (running) _action.Progress.Start();
        _action.RunButton.Text = running ? "Cancel" : Tool.Info.ActionText;
        if (running) _action.Result.Show(Tool.Info.BusyText, Tone.Secondary);
        _list.AllowDrop = !running;
        if (Tool.Options is { } options) options.Enabled = !running;
        Shell.PageChanged(this);
        UpdateState();
    }

    public override bool ConfirmClose()
    {
        if (_run is null) return true;
        var stop = new TaskDialogButton("Stop and Close");
        var page = new TaskDialogPage
        {
            Caption = "Sheaf",
            Heading = $"{Tool.Info.Title} is still running",
            Text = "Closing stops it. Files it already finished are kept.",
            Buttons = { new TaskDialogButton("Keep Running"), stop },
            Icon = TaskDialogIcon.Warning,
        };
        page.DefaultButton = page.Buttons[0];
        if (TaskDialog.ShowDialog(this, page) != stop) return false;
        _run.Cancel();
        return true;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Control | Keys.Enter: OnRunClicked(); return true;
            case Keys.Control | Keys.O: ChooseFiles(); return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    public static string KindNoun(FileKind kind) => kind switch
    {
        FileKind.Pdf => "PDF files",
        FileKind.Image => "images",
        FileKind.Word => "Word documents",
        FileKind.Excel => "spreadsheets",
        FileKind.PowerPoint => "presentations",
        _ => "files",
    };

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _closing.Cancel();
            _closing.Dispose();
            _list.Clear();
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// The bottom of a tool's side panel: where files are saved, the primary button and the result line.
/// The result line is the only feedback for a run: no toasts, no message boxes.
/// </summary>
internal sealed class ActionPanel : TableLayoutPanel
{
    readonly IShell _shell;
    readonly TextLabel _saveTo = Make.Line("", Tone.Primary);
    readonly FlowLayoutPanel _links = Make.Row();
    readonly WrapLabel _notice = new() { Font = AppFonts.Small, Tone = Tone.Warning, Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0, 12, 0, 0), Visible = false };
    readonly ToolTip _tips = new();

    public ActionPanel(IShell shell)
    {
        _shell = shell;
        ColumnCount = 1;
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Dock = DockStyle.Fill;
        Padding = new Padding(20, 16, 20, 20);
        Margin = Padding.Empty;

        RunButton = new AppButton("", ButtonKind.Primary) { Stretch = true, Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0, 14, 0, 0), LogicalHeight = 36 };
        Progress.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        Progress.Margin = new Padding(0, 10, 0, 0);
        Result.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        Result.Margin = new Padding(0, 9, 0, 0);

        Controls.Add(new Divider { Margin = new Padding(0, 0, 0, 14) });
        Controls.Add(Make.FieldCaption("Save to"));
        Controls.Add(_saveTo);
        Controls.Add(_links);
        Controls.Add(_notice);
        Controls.Add(RunButton);
        Controls.Add(Progress);
        Controls.Add(Result);

        SettingsStore.Saved += OnSettingsSaved;
        UpdateSaveTo();
    }

    public AppButton RunButton { get; }

    /// <summary>The line under the button that shows how far a run has got.</summary>
    public ProgressLine Progress { get; } = new();

    public ResultLabel Result { get; } = new();

    public string? Notice
    {
        set
        {
            _notice.Text = value ?? "";
            _notice.Visible = value is not null;
        }
    }

    public void ShowOutcome(ToolResult result)
    {
        if (result.Outputs.Count == 0)
        {
            Tone tone = result.Failures.Count > 0 ? Tone.Error : Tone.Secondary;
            Result.Show(FailureText(result.Failures) ?? result.Detail ?? "Nothing was saved.", tone);
            return;
        }

        string saved = SavedText(result);
        string? failed = FailureText(result.Failures);
        string message = failed is null ? saved : $"{saved} {failed}";
        string first = result.Outputs[0];
        var actions = result.Outputs.Count == 1 && File.Exists(first)
            ? new (string, Action)[] { ("Open", () => Open(first)), ("Show in Folder", () => Native.ShowInExplorer(first)) }
            : [(Directory.Exists(first) ? "Open Folder" : "Show in Folder", () => Native.ShowInExplorer(first))];
        Result.Show(message, failed is null ? Tone.Success : Tone.Error, actions);
    }

    static string SavedText(ToolResult result)
    {
        string first = result.Outputs[0];
        string detail = result.Detail is null ? "" : $" ({result.Detail})";
        if (result.Outputs.Count > 1) return $"Saved {Words.Files(result.Outputs.Count)}{detail}.";
        if (Directory.Exists(first)) return $"Saved {Words.Files(Directory.EnumerateFiles(first).Count())} in “{Path.GetFileName(first)}”{detail}.";
        string size = result.Detail ?? Words.Size(File.Exists(first) ? new FileInfo(first).Length : 0);
        return $"Saved “{Path.GetFileName(first)}” ({size}).";
    }

    static string? FailureText(IReadOnlyList<string> failures) => failures.Count switch
    {
        0 => null,
        1 => failures[0],
        _ => $"{failures[0]} ({failures.Count - 1} more {(failures.Count == 2 ? "file" : "files")} also failed.)",
    };

    void Open(string path)
    {
        if (Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase)) _shell.OpenDocument(path);
        else Native.OpenWithShell(path);
    }

    void UpdateSaveTo()
    {
        AppSettings settings = _shell.Settings;
        bool custom = !settings.SaveNextToOriginal && Directory.Exists(settings.OutputFolder);
        _saveTo.Text = custom ? settings.OutputFolder : "Next to each original file";
        _tips.SetToolTip(_saveTo, custom ? settings.OutputFolder : null);
        _links.SuspendLayout();
        foreach (Control link in _links.Controls.Cast<Control>().ToList()) link.Dispose();
        _links.Controls.Clear();
        _links.Controls.Add(new ActionLink("Choose Folder…", ChooseFolder));
        if (custom) _links.Controls.Add(new ActionLink("Use Original Folders", UseOriginalFolders));
        _links.ResumeLayout(true);
    }

    void ChooseFolder()
    {
        string? folder = _shell.PickFolder("Choose where Sheaf saves new files");
        if (folder is null) return;
        _shell.Settings.SaveNextToOriginal = false;
        _shell.Settings.OutputFolder = folder;
        _shell.SaveSettings();
    }

    void UseOriginalFolders()
    {
        _shell.Settings.SaveNextToOriginal = true;
        _shell.SaveSettings();
    }

    void OnSettingsSaved(object? sender, EventArgs e)
    {
        if (!IsDisposed) UpdateSaveTo();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SettingsStore.Saved -= OnSettingsSaved;
            _tips.Dispose();
        }
        base.Dispose(disposing);
    }
}
