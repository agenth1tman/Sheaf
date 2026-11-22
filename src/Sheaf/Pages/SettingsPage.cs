using Sheaf.Core.Office;
using Sheaf.Platform;
using Sheaf.Shell;
using Sheaf.Ui;
using Sheaf.Ui.Controls;

namespace Sheaf.Pages;

internal sealed class SettingsPage : TabContent
{
    const int ContentWidth = 640;
    readonly ChoiceGroup<ThemeMode> _theme = new("Theme", horizontal: true, ("Dark", ThemeMode.Dark), ("Light", ThemeMode.Light));
    readonly ChoiceGroup<bool> _saveNext = new("Save new files", horizontal: false, ("Next to the original file", true), ("In one folder", false));
    readonly TextLabel _folder = Make.Line("", Tone.Secondary);
    readonly AppButton _chooseFolder = new("Choose Folder…");
    readonly Toggle _openFolder = new("Open the folder when a task finishes") { SwitchOnRight = true, Anchor = AnchorStyles.Left | AnchorStyles.Right };
    bool _loading;

    public SettingsPage(IShell shell) : base(shell)
    {
        SuspendLayout();
        var scroller = new Surface { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(64, 36, 64, 40) };
        var content = Make.Stack(
            Make.Title("Settings"),
            Make.Section("Appearance"),
            new FormField("Theme", _theme),
            Make.Section("Saving"),
            new FormField("Save new files", _saveNext),
            _folder,
            Make.Row(_chooseFolder),
            _openFolder,
            Make.Section("Office Conversions"),
            ConverterStatus(),
            Make.Hint("Sheaf converts Office files with its own built-in converter, so nothing else is needed. When Microsoft Office or LibreOffice is installed, each conversion tool can use it instead, for an exact copy of the layout and for older .doc, .xls and .ppt files."),
            Make.Section("Diagnostics"),
            Make.Row(OpenLogButton()),
            Make.Hint("A plain text record of what Sheaf did, useful when something fails."),
            Make.Section("About"),
            About());
        content.Dock = DockStyle.Top;
        content.MaximumSize = new Size(ContentWidth, 0);
        _theme.Anchor = AnchorStyles.Left;
        _chooseFolder.Margin = new Padding(0, 8, 0, 8);
        scroller.Controls.Add(content);
        Controls.Add(scroller);

        _theme.Changed += (_, _) => Apply();
        _saveNext.Changed += (_, _) => Apply();
        _openFolder.CheckedChanged += (_, _) => Apply();
        _chooseFolder.Click += (_, _) => ChooseFolder();
        SettingsStore.Saved += OnSettingsSaved;
        LoadValues();
        ResumeLayout(false);
    }

    public override string Title => "Settings";

    public override string Icon => "settings";

    static Control ConverterStatus()
    {
        var table = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Margin = new Padding(0, 0, 0, 4) };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        foreach (var (name, found) in new[]
        {
            ("Microsoft Word", OfficeConverter.HasOfficeApp("Word.Application")),
            ("Microsoft Excel", OfficeConverter.HasOfficeApp("Excel.Application")),
            ("Microsoft PowerPoint", OfficeConverter.HasOfficeApp("PowerPoint.Application")),
            ("LibreOffice", OfficeConverter.LibreOfficePath is not null),
            ("Built-in converter", true),
        })
        {
            table.Controls.Add(new TextLabel(name) { Margin = new Padding(0, 4, 40, 4) });
            table.Controls.Add(new TextLabel(found ? "Installed" : "Not installed", found ? Tone.Success : Tone.Tertiary)
            {
                Font = found ? AppFonts.BodyMedium : AppFonts.Body,
                Margin = new Padding(0, 4, 0, 4),
            });
        }
        return table;
    }

    static AppButton OpenLogButton()
    {
        var button = new AppButton("Open Activity Log");
        button.Click += (_, _) =>
        {
            if (!File.Exists(AppPaths.LogFile)) ActivityLog.Write("Log opened from Settings");
            Native.OpenWithShell(AppPaths.LogFile);
        };
        return button;
    }

    const string RepositoryUrl = "https://github.com/agenth1tman/Sheaf/";

    static Control About()
    {
        var credit = new LinkLabel
        {
            Text = "Made by agenth1tman",
            AutoSize = true,
            Font = AppFonts.Body,
            LinkArea = new LinkArea(8, "agenth1tman".Length),
            LinkBehavior = LinkBehavior.HoverUnderline,
            UseMnemonic = false,
            Margin = new Padding(0, 0, 0, 10),
            ForeColor = Theme.Current.Text2,
            LinkColor = Theme.Current.AccentText,
            ActiveLinkColor = Theme.Current.Accent,
        };
        credit.LinkClicked += (_, _) => Native.OpenWithShell("https://github.com/agenth1tman");
        var name = new NameLink(RepositoryUrl) { Margin = new Padding(0, 0, 0, 2) };
        Tips.Shared.SetToolTip(name, RepositoryUrl);
        return Make.Stack(
            name,
            Make.Hint("PDF tools that stay on this PC."),
            credit,
            Make.Hint("Text set in Geist and Geist Mono by the Geist Project Authors, and Sacramento by Astigmatic, under the SIL Open Font License 1.1."),
            Make.Hint("PDF editing by PDFsharp and MigraDoc (empira Software GmbH), and Office files read and written by the Open XML SDK (.NET Foundation), MIT License. Finding text by PdfPig and QR codes by ZXing.Net, Apache License 2.0. Pages are drawn, text is recognised and cameras and scanners are reached through Windows itself."),
            Make.Hint("The licences are in the Licenses folder next to Sheaf.exe."),
            Make.Hint("Sheaf itself is released under the MIT License. Everything runs on this PC; nothing is uploaded and no account is needed."));
    }

    void LoadValues()
    {
        _loading = true;
        AppSettings settings = Shell.Settings;
        _theme.Value = settings.Theme;
        bool custom = !settings.SaveNextToOriginal && Directory.Exists(settings.OutputFolder);
        _saveNext.Value = !custom;
        _folder.Text = Directory.Exists(settings.OutputFolder) ? settings.OutputFolder : "No folder chosen yet";
        _openFolder.Checked = settings.OpenFolderWhenDone;
        _loading = false;
    }

    void Apply()
    {
        if (_loading) return;
        AppSettings settings = Shell.Settings;
        if (!_saveNext.Value && !Directory.Exists(settings.OutputFolder))
        {
            ChooseFolder();
            if (!Directory.Exists(settings.OutputFolder)) { LoadValues(); return; }
        }
        settings.SaveNextToOriginal = _saveNext.Value;
        settings.OpenFolderWhenDone = _openFolder.Checked;
        settings.Theme = _theme.Value;
        Theme.Use(settings.Theme);
        Shell.SaveSettings();
    }

    void ChooseFolder()
    {
        string? folder = Shell.PickFolder("Choose where Sheaf saves new files");
        if (folder is null) return;
        Shell.Settings.OutputFolder = folder;
        Shell.Settings.SaveNextToOriginal = false;
        Shell.SaveSettings();
    }

    void OnSettingsSaved(object? sender, EventArgs e)
    {
        if (!IsDisposed) LoadValues();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) SettingsStore.Saved -= OnSettingsSaved;
        base.Dispose(disposing);
    }
}

/// <summary>
/// The app's name in About: its mark beside "Sheaf" set large, linking to the project page. On hover the back
/// sheet leans further, the name takes the accent colour and an arrow shows it goes out to the web.
/// </summary>
internal sealed class NameLink : Control
{
    const int MarkSize = 30, Gap = 10;
    readonly string _url;
    readonly Fade _hover;

    public NameLink(string url)
    {
        _url = url;
        _hover = new Fade(this, 180);
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true);
        TabStop = true;
        Cursor = Cursors.Hand;
        Font = AppFonts.Display;
        AccessibleRole = AccessibleRole.Link;
        AccessibleName = "Sheaf";
        AccessibleDescription = $"Opens the project page, {url}";
        AccessibleDefaultActionDescription = "Open";
        Size = GetPreferredSize(Size.Empty);
    }

    Size Word => TextRenderer.MeasureText("Sheaf", Font, Size.Empty, TextFormatFlags.NoPadding);

    public override Size GetPreferredSize(Size proposedSize) =>
        new(LogicalToDeviceUnits(MarkSize + Gap + 22) + Word.Width, Math.Max(LogicalToDeviceUnits(MarkSize + 8), Word.Height + LogicalToDeviceUnits(6)));

    void Open() => Native.OpenWithShell(_url);

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        Open();
    }

    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Enter or Keys.Space || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Enter or Keys.Space) Open();
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover.To(1); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover.To(0); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); _hover.To(1); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); _hover.To(0); }

    protected override void OnPaint(PaintEventArgs e)
    {
        Palette p = Theme.Current;
        Graphics g = e.Graphics;
        g.Clear(Draw.BackgroundOf(this));
        double hover = _hover.Value;
        int mark = LogicalToDeviceUnits(MarkSize);
        Draw.AppMark(g, new Rectangle(0, (Height - mark) / 2, mark, mark), (float)(-12 - 10 * hover));
        Size word = Word;
        var text = new Rectangle(mark + LogicalToDeviceUnits(Gap), (Height - word.Height) / 2, word.Width, word.Height);
        Draw.Text(g, "Sheaf", Font, text, Theme.Blend(p.Text, p.AccentText, hover), TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter);
        if (hover > 0.01)
        {
            int arrow = LogicalToDeviceUnits(14);
            var box = new Rectangle(text.Right + LogicalToDeviceUnits(6), text.Y + LogicalToDeviceUnits(4) - (int)(LogicalToDeviceUnits(3) * hover), arrow, arrow);
            Icons.Draw(g, "arrow", box, Color.FromArgb((int)(255 * hover), p.AccentText), 2f);
        }
        if (Focused && ShowFocusCues) Draw.FocusRing(g, ClientRectangle, LogicalToDeviceUnits(Theme.Radius), LogicalToDeviceUnits(2));
    }
}
