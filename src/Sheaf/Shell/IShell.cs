using Sheaf.Core.IO;
using Sheaf.Platform;
using Sheaf.Ui;

namespace Sheaf.Shell;

/// <summary>What a tab's page can ask of the main window.</summary>
internal interface IShell
{
    AppSettings Settings { get; }

    void SaveSettings();

    void ShowHome();

    /// <summary>Opens a PDF in its own tab, or switches to the tab that already has it.</summary>
    void OpenDocument(string path, string? mode = null);

    /// <summary>Opens a new blank PDF (A4 and white unless a size and paper are given) or a new blank image.</summary>
    void OpenBlankDocument(Pages.BlankChoice? choice = null);

    /// <summary>Opens a picture to edit (or a blank one of a size, when <paramref name="path"/> is null), to export as an image or a PDF.</summary>
    void OpenImageDocument(string? path, Size pixels);

    void OpenTool(string toolId, IReadOnlyList<string>? files = null);

    void OpenScan();

    void OpenSettings();

    /// <summary>The page's title, unsaved state or busy state changed.</summary>
    void PageChanged(TabContent page);

    IReadOnlyList<string> PickFiles(FileKind kinds, bool multiple);

    string? PickFolder(string description);

    string? PickSavePath(string suggestedPath, string filter);
}

/// <summary>
/// One tab's content. Each page scales itself for the screen's DPI (pages are created after the window is
/// shown), and its background follows the theme.
/// </summary>
internal abstract class TabContent : UserControl
{
    protected TabContent(IShell shell)
    {
        Shell = shell;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Dock = DockStyle.Fill;
        Font = AppFonts.Body;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
    }

    protected IShell Shell { get; }

    public abstract string Title { get; }

    public virtual string Icon => "file";

    /// <summary>File-type colour shown in place of the icon on document tabs.</summary>
    public virtual Color? Badge => null;

    public virtual bool IsDirty => false;

    public virtual bool IsBusy => false;

    /// <summary>Shown and in front of the other pages (Home stays visible behind them, covered).</summary>
    public bool IsInFront => Visible && (Parent is null || Parent.Controls.GetChildIndex(this) == 0);

    public override Color BackColor
    {
        get => Theme.Current.Panel;
        set { }
    }

    public override Color ForeColor
    {
        get => Theme.Current.Text;
        set { }
    }

    /// <summary>Asks to save or discard when needed. Returns false to keep the tab open.</summary>
    public virtual bool ConfirmClose() => true;

    /// <summary>Called each time the tab comes to the front.</summary>
    public virtual void Activated() { }

    /// <summary>Whether the tab can be renamed (by double-clicking it).</summary>
    public virtual bool CanRename => false;

    public virtual void Rename() { }

    /// <summary>Files dropped on the window while this tab is in front. Returns false to let the window handle them.</summary>
    public virtual bool AcceptDrop(string[] paths) => false;
}
