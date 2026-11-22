using System.Drawing.Drawing2D;
using Sheaf.Core;
using Sheaf.Core.Editing;
using Sheaf.Ui;
using Sheaf.Ui.Controls;

namespace Sheaf.Pages.Document;

/// <summary>What the Redact panel asks the document to look for.</summary>
internal abstract record FindRequest
{
    public sealed record Phrase(string Text) : FindRequest;
    public sealed record Pattern(FindKind Kind) : FindRequest;
    public sealed record Codes : FindRequest;
}

/// <summary>Five tiles to choose how marks are covered: black, white, a colour, pixelated or blurred.</summary>
internal sealed class CoverPicker : Panel, IItemHost
{
    static readonly (RedactCover Cover, string Name)[] Covers =
        [(RedactCover.Black, "Black"), (RedactCover.White, "White"), (RedactCover.Color, "Colour"), (RedactCover.Pixelate, "Pixelate"), (RedactCover.Blur, "Blur")];
    int _hover = -1;

    public CoverPicker()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = true;
        AccessibleRole = AccessibleRole.List;
        AccessibleName = "Cover with";
        Height = 66;
    }

    public event EventHandler? Changed;

    public RedactCover Cover { get; set { field = value; Invalidate(); } }

    public Color CoverColor { get; set { field = value; Invalidate(); } } = Theme.Hex(0xE5484D);

    Rectangle Tile(int index)
    {
        int gap = LogicalToDeviceUnits(8), width = (Width - gap * (Covers.Length - 1)) / Covers.Length;
        return new Rectangle(index * (width + gap), 0, width, Math.Min(width, Height - LogicalToDeviceUnits(20)));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(Draw.BackgroundOf(this));
        for (int i = 0; i < Covers.Length; i++)
        {
            Rectangle tile = Tile(i);
            bool selected = Covers[i].Cover == Cover;
            int radius = LogicalToDeviceUnits(Theme.Radius);
            using (GraphicsPath shape = Draw.RoundRect(Rectangle.Inflate(tile, -LogicalToDeviceUnits(3), -LogicalToDeviceUnits(3)), radius - 2))
            using (Brush fill = Covers[i].Cover switch
            {
                RedactCover.White => new SolidBrush(Color.White),
                RedactCover.Color => new SolidBrush(CoverColor),
                RedactCover.Pixelate => new HatchBrush(HatchStyle.LargeCheckerBoard, Color.FromArgb(0x9C, 0xA3, 0xAF), Color.FromArgb(0x4B, 0x55, 0x63)),
                RedactCover.Blur => new LinearGradientBrush(tile, Color.FromArgb(0xD4, 0xD4, 0xD8), Color.FromArgb(0x71, 0x71, 0x7A), 45f),
                _ => new SolidBrush(Color.FromArgb(0x0A, 0x0A, 0x0A)),
            })
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.FillPath(fill, shape);
            }
            Draw.Box(g, tile, radius, Color.Transparent, selected ? Theme.Current.Accent : i == _hover ? Theme.Current.Text3 : Theme.Current.Line2, selected ? LogicalToDeviceUnits(2) : 1);
            var label = new Rectangle(tile.X - LogicalToDeviceUnits(4), tile.Bottom + LogicalToDeviceUnits(2), tile.Width + LogicalToDeviceUnits(8), LogicalToDeviceUnits(18));
            Draw.Text(g, Covers[i].Name, AppFonts.Caption, label, selected ? Theme.Current.Text : Theme.Current.Text3, Draw.Centered);
        }
        if (Focused && ShowFocusCues)
            Draw.FocusRing(g, Rectangle.Inflate(Tile(Array.FindIndex(Covers, c => c.Cover == Cover)), 2, 2), LogicalToDeviceUnits(Theme.Radius) + 2, LogicalToDeviceUnits(2));
    }

    void Choose(int index)
    {
        if (index < 0 || Covers[index].Cover == Cover) return;
        Cover = Covers[index].Cover;
        ItemHostAccessible.ItemFocused(this, index);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int hover = Enumerable.Range(0, Covers.Length).FirstOrDefault(i => Tile(i).Contains(e.Location), -1);
        if (hover != _hover) { _hover = hover; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = -1; Invalidate(); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        Choose(Enumerable.Range(0, Covers.Length).FirstOrDefault(i => Tile(i).Contains(e.Location), -1));
    }

    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Left or Keys.Right || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        int current = Array.FindIndex(Covers, c => c.Cover == Cover);
        if (e.KeyCode == Keys.Left) Choose(Math.Max(0, current - 1));
        else if (e.KeyCode == Keys.Right) Choose(Math.Min(Covers.Length - 1, current + 1));
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new ItemHostAccessible(this, this, AccessibleRole.RadioButton);

    int IItemHost.ItemCount => Covers.Length;
    string IItemHost.ItemName(int index) => Covers[index].Name;
    Rectangle IItemHost.ItemBounds(int index) => Tile(index);
    bool IItemHost.IsItemSelected(int index) => Covers[index].Cover == Cover;
    int IItemHost.FocusedItem => Array.FindIndex(Covers, c => c.Cover == Cover);
    void IItemHost.ActivateItem(int index) => Choose(index);

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); ItemHostAccessible.ItemFocused(this, Array.FindIndex(Covers, c => c.Cover == Cover)); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
}

/// <summary>
/// The Redact panel: how marks are covered, finding text, codes and personal details to mark, the list of
/// marks, and applying them to a new copy of the file.
/// </summary>
internal sealed class RedactPanel : Surface
{
    readonly CoverPicker _cover = new() { Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0, 0, 0, 4) };
    readonly ColorButton _coverColor = new("Cover colour", Theme.Hex(0xE5484D)) { Visible = false, Margin = new Padding(0, 6, 0, 0) };
    readonly InputBox _search = new() { Placeholder = "Words or numbers to hide", Caption = "Find and mark" };
    readonly AppButton _markFound = new("Mark", ButtonKind.Secondary) { Margin = new Padding(8, 0, 0, 0) };
    readonly TextLabel _searchResult = new("", Tone.Tertiary) { Font = AppFonts.Small, Margin = new Padding(0, 6, 0, 0) };
    readonly ObjectList _marks = new() { Dock = DockStyle.Fill, AccessibleName = "Marked", Action = "close", EmptyText = "Nothing marked yet. Drag over the page with a tool on the left, or find what to hide above." };
    readonly TextLabel _marksHeading = Make.PanelHeading("Marked");
    readonly Check _clean = Make.Check("Also remove comments and metadata", true);
    readonly Check _searchable = Make.Check("Keep the other text searchable", Core.Ocr.TextRecognizer.DefaultLanguage is not null);
    readonly AppButton _apply = new("Apply Redactions", ButtonKind.Primary) { Stretch = true, Anchor = AnchorStyles.Left | AnchorStyles.Right };
    bool _syncing;

    public RedactPanel() : base(SurfaceRole.Panel)
    {
        Borders = AnchorStyles.Left;
        AccessibleRole = AccessibleRole.Pane;
        AccessibleName = "Redactions";

        var searchRow = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = Padding.Empty };
        searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _search.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        searchRow.Controls.Add(_search, 0, 0);
        searchRow.Controls.Add(_markFound, 1, 0);

        var finds = new List<Control>();
        foreach (var (label, icon, request) in new (string, string, FindRequest)[]
        {
            ("QR Codes and Barcodes", "qr", new FindRequest.Codes()),
            ("Email Addresses", "mark-text", new FindRequest.Pattern(FindKind.Emails)),
            ("Phone Numbers", "mark-text", new FindRequest.Pattern(FindKind.PhoneNumbers)),
            ("Bank and Card Numbers", "mark-text", new FindRequest.Pattern(FindKind.BankAndCardNumbers)),
        })
        {
            var button = new AppButton(label, ButtonKind.Quiet, icon) { LogicalHeight = 30, Margin = new Padding(0, 2, 0, 0) };
            button.Click += (_, _) => FindRequested?.Invoke(this, request);
            finds.Add(button);
        }

        var cover = Make.PanelSection("Cover with", _cover, _coverColor);
        var find = Make.PanelSection("Find and mark", [searchRow, _searchResult, .. finds]);
        var marksHeader = Make.Stack(_marksHeading);
        marksHeader.Dock = DockStyle.Top;
        marksHeader.Padding = new Padding(16, 14, 16, 0);
        _apply.Margin = new Padding(0, 10, 0, 0);
        var apply = Make.PanelSection(null, _clean, _searchable, _apply,
            Make.Hint("Marked words are taken out of the text and the rest of the page stays as it is. A page with other marks is saved as an image, so nothing under them survives. Sheaf saves a new copy and leaves this file as it is."));
        _searchable.Enabled = Core.Ocr.TextRecognizer.DefaultLanguage is not null;
        Tips.Shared.SetToolTip(_searchable, "Reads the rest of each redacted page with the text recognition built into Windows, so it can still be searched and copied.");
        apply.Dock = DockStyle.Bottom;
        Tips.Shared.SetToolTip(_clean, "Removes notes and other comments, author and title details, scripts and attached files from the whole copy.");

        Controls.Add(_marks);
        Controls.Add(marksHeader);
        Controls.Add(new Divider { Dock = DockStyle.Top, Margin = Padding.Empty });
        Controls.Add(find);
        Controls.Add(new Divider { Dock = DockStyle.Top, Margin = Padding.Empty });
        Controls.Add(cover);
        Controls.Add(new Divider { Dock = DockStyle.Bottom, Margin = Padding.Empty });
        Controls.Add(apply);

        _cover.Changed += (_, _) => CoverChosen();
        _coverColor.Changed += (_, _) => { _cover.CoverColor = _coverColor.Color; CoverChosen(); };
        _search.EnterPressed += (_, _) => FindPhrase();
        _markFound.Click += (_, _) => FindPhrase();
        _marks.Picked += (_, row) => MarkPicked?.Invoke(this, row);
        _marks.ActionClicked += (_, row) => MarkRemoved?.Invoke(this, row);
        _apply.Click += (_, _) => ApplyRequested?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? CoverChanged;
    public event EventHandler<FindRequest>? FindRequested;
    public event EventHandler<ObjectRow>? MarkPicked;
    public event EventHandler<ObjectRow>? MarkRemoved;
    public event EventHandler? ApplyRequested;

    public RedactCover Cover => _cover.Cover;

    public int CoverColor => _coverColor.Color.ToArgb();

    public bool Clean => _clean.Checked;

    public bool Searchable => _searchable.Checked && _searchable.Enabled;

    void CoverChosen()
    {
        _coverColor.Visible = _cover.Cover == RedactCover.Color;
        if (!_syncing) CoverChanged?.Invoke(this, EventArgs.Empty);
    }

    void FindPhrase()
    {
        if (_search.Text.Trim().Length == 0) { _search.Focus(); return; }
        FindRequested?.Invoke(this, new FindRequest.Phrase(_search.Text));
    }

    public void ShowResult(string text) => _searchResult.Text = text;

    /// <summary>Shows that the copy is being written; <see cref="Sync"/> restores the button afterwards.</summary>
    public void SetBusy(bool busy)
    {
        _apply.Enabled = !busy;
        if (busy) _apply.Text = "Redacting…";
    }

    /// <summary>Lists every mark in the document and updates the Apply button's count.</summary>
    public void Sync(EditDocument document, Guid? selected)
    {
        _syncing = true;
        var rows = document.Pages.SelectMany((entry, page) => entry.Objects
            .Where(item => item.Kind == MarkKind.Redaction)
            .Select(item => new ObjectRow(page, item, ToolStyles.Describe(item), $"Page {page + 1} · {Where(item)}"))).ToList();
        _marks.SetRows(rows, selected is { } id ? [id] : []);
        _marksHeading.Text = rows.Count == 0 ? "Marked" : $"Marked · {rows.Count}";
        _apply.Text = rows.Count switch { 0 => "Apply Redactions", 1 => "Apply 1 Redaction", _ => $"Apply {rows.Count} Redactions" };
        _apply.Enabled = rows.Count > 0;
        if (rows.FirstOrDefault()?.Item is { } first)
        {
            _cover.Cover = first.Cover;
            _cover.CoverColor = _coverColor.Color = Color.FromArgb(255, Color.FromArgb(first.FillColor == 0 ? _coverColor.Color.ToArgb() : first.FillColor));
            _coverColor.Visible = first.Cover == RedactCover.Color;
        }
        _syncing = false;
    }

    static string Where(PageObject item) => item.Comment.Length > 0 && item.RedactShape is RedactShape.Rectangle or RedactShape.Text
        ? "found"
        : item.RedactShape switch { RedactShape.Ellipse => "ellipse", RedactShape.Freeform => "freeform", RedactShape.Text => "text", _ => "rectangle" };
}
