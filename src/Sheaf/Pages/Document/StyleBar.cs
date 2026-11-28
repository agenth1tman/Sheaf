using System.Globalization;
using Sheaf.Core.Editing;
using Sheaf.Core.Fonts;
using SystemFonts = Sheaf.Core.Fonts.SystemFonts;
using Sheaf.Ui;
using Sheaf.Ui.Controls;

namespace Sheaf.Pages.Document;

/// <summary>
/// The bar above the canvas: the style of the selected objects, or of the tool about to draw. Changing a value
/// restyles the selection (one undo step, even for a slider dragged across many values) and becomes the tool's
/// style for the next object.
/// </summary>
internal sealed class StyleBar : Surface
{
    readonly DocumentCanvas _canvas;
    readonly ToolStyles _styles;
    readonly FlowLayoutPanel _row = new() { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Padding = new Padding(12, 4, 12, 0) };
    // When the window is too narrow for every control, the row slides under these arrows.
    readonly Panel _viewport = new() { Dock = DockStyle.Fill };
    readonly AppButton _back = new("", ButtonKind.Quiet, "chevron-left") { LogicalHeight = 28, AccessibleName = "Show earlier options", TabStop = false, Visible = false };
    readonly AppButton _more = new("", ButtonKind.Quiet, "chevron-right") { LogicalHeight = 28, AccessibleName = "Show more options", TabStop = false, Visible = false };
    int _scroll;
    Tween? _scrolling;
    readonly TextLabel _kind = new("", Tone.Secondary) { Font = AppFonts.SmallStrong, Margin = new Padding(0, 9, 12, 0) };
    readonly TextLabel _hint = new("", Tone.Tertiary) { Font = AppFonts.Small, Margin = new Padding(0, 9, 12, 0) };
    readonly DropDown<string> _font;
    readonly SliderDrop _size = new("Font size", 4, 144, 1, Points, 8, 10, 12, 14, 18, 24, 36, 48, 72);
    readonly AppButton _bold = Toggle("bold", "Bold");
    readonly AppButton _italic = Toggle("italic", "Italic");
    readonly AppButton _underline = Toggle("underline", "Underline");
    readonly AppButton _superscript = Toggle("superscript", "Superscript");
    readonly AppButton _subscript = Toggle("subscript", "Subscript");
    readonly AppButton _symbol = Toggle("symbol", "Insert Symbol");
    readonly AppButton _equation = Toggle("equation", "Insert Equation");
    readonly AppButton _left = Toggle("align-left", "Align Left");
    readonly AppButton _center = Toggle("align-center", "Centre");
    readonly AppButton _right = Toggle("align-right", "Align Right");
    readonly AppButton _justify = Toggle("align-justify", "Justify");
    readonly AppButton _top = Toggle("valign-top", "Top");
    readonly AppButton _middle = Toggle("valign-middle", "Middle");
    readonly AppButton _bottom = Toggle("valign-bottom", "Bottom");
    readonly TextLabel _endsCaption = Caption("Ends");
    readonly DropDown<LineEnd> _startEnd = new("Start of the line", EndChoices) { LogicalHeight = 28 };
    readonly DropDown<LineEnd> _endEnd = new("End of the line", EndChoices) { LogicalHeight = 28 };
    readonly PenPicker _pens = new();
    readonly TextLabel _eraserCaption = Caption("Size");
    readonly SliderDrop _eraserSize = new("Eraser size", 2, 120, 1, Points, 4, 8, 16, 32, 64);
    readonly TextLabel _sweepCaption = Caption("Angle");
    readonly SliderDrop _sweep = new("Pie angle", 10, 350, 5, angle => $"{angle:0}°", 90, 180, 270, 300);
    readonly Divider _split5 = new(vertical: true);
    const string AddFontChoice = "\u0001add";
    string _lastFont = EmbeddedFonts.PdfFamily;
    readonly ColorButton _ink = new("Colour", Color.Black);
    readonly TextLabel _fillCaption = Caption("Fill");
    readonly ColorButton _fill = new("Fill", Color.Transparent) { AllowNone = true };
    readonly TextLabel _widthCaption = Caption("Width");
    readonly SliderDrop _width = new("Line width", 0, 20, 0.5f, width => width == 0 ? "None" : Points(width), 0, 0.5f, 1, 2, 4, 8);
    readonly TextLabel _cornersCaption = Caption("Corners");
    readonly SliderDrop _corners = new("Corner radius", 0, 60, 1, radius => radius == 0 ? "Square" : Points(radius), 0, 4, 8, 16, 32);
    readonly TextLabel _opacityCaption = Caption("Opacity");
    readonly SliderDrop _opacity = new("Opacity", 5, 100, 1, value => $"{value:0} %", 100, 75, 50, 25);
    readonly AppButton _crop = new("Crop…", ButtonKind.Quiet, "crop") { LogicalHeight = 28 };
    readonly TextLabel _tintCaption = Caption("Recolour");
    readonly ColorButton _tint = new("Recolour", Color.Transparent) { AllowNone = true };
    readonly AppButton _turnLeft = Icon("rotate-left", "Turn Left");
    readonly AppButton _turnRight = Icon("rotate", "Turn Right");
    readonly AppButton _flipAcross = Icon("mirror", "Flip Horizontally");
    readonly AppButton _flipDown = Icon("flip-vertical", "Flip Vertically");
    readonly TextLabel _angleCaption = Caption("Angle");
    readonly SliderDrop _angle = new("Angle", -180, 180, 1, angle => $"{angle:0}°", -90, -45, 0, 45, 90, 180);
    readonly (AppButton Button, AlignEdge Edge, string Name)[] _align =
    [
        (Icon("align-left-edge"), AlignEdge.Left, "Align left edges"), (Icon("align-h-centre"), AlignEdge.Centre, "Align horizontal centres"),
        (Icon("align-right-edge"), AlignEdge.Right, "Align right edges"), (Icon("align-top-edge"), AlignEdge.Top, "Align top edges"),
        (Icon("align-v-middle"), AlignEdge.Middle, "Align vertical centres"), (Icon("align-bottom-edge"), AlignEdge.Bottom, "Align bottom edges"),
    ];
    readonly AppButton _distributeAcross = Icon("distribute-h", "Distribute Horizontally");
    readonly AppButton _distributeDown = Icon("distribute-v", "Distribute Vertically");
    readonly AppButton _toFront = Icon("bring-front", "Bring to Front");
    readonly AppButton _forward = Icon("bring-forward", "Bring Forward");
    readonly AppButton _backward = Icon("send-backward", "Send Backward");
    readonly AppButton _toBack = Icon("send-back", "Send to Back");
    readonly Divider _split6 = new(vertical: true), _split7 = new(vertical: true), _split8 = new(vertical: true);
    readonly AppButton _cut = Icon("cut", "Cut");
    readonly AppButton _copy = Icon("copy", "Copy");
    readonly AppButton _duplicate = new("Duplicate", ButtonKind.Quiet, "duplicate") { LogicalHeight = 28 };
    readonly AppButton _delete = new("", ButtonKind.Quiet, "trash") { LogicalHeight = 28, AccessibleName = "Delete" };
    readonly Divider _split1 = new(vertical: true), _split2 = new(vertical: true), _split3 = new(vertical: true), _split4 = new(vertical: true);
    StyleFamily _family;
    bool _syncing;

    static readonly (string, LineEnd)[] EndChoices = [("None", LineEnd.None), ("Arrow", LineEnd.Arrow), ("Triangle", LineEnd.Triangle), ("Dot", LineEnd.Dot), ("Bar", LineEnd.Bar)];

    /// <summary>Something to tell people that doesn't fit in the bar (a font that wouldn't load).</summary>
    public event EventHandler<string>? Notice;

    public StyleBar(DocumentCanvas canvas, ToolStyles styles) : base(SurfaceRole.Panel)
    {
        _canvas = canvas;
        _styles = styles;
        Borders = AnchorStyles.Bottom;
        AccessibleRole = AccessibleRole.ToolBar;
        AccessibleName = "Style";

        _font = new DropDown<string>("Font", FontChoices()) { LogicalHeight = 28 };
        _font.ReserveWidthFor("Times New Roman");
        _size.LogicalHeight = _width.LogicalHeight = _opacity.LogicalHeight = _corners.LogicalHeight = _sweep.LogicalHeight = 28;
        _startEnd.ReserveWidthFor("Triangle");
        _endEnd.ReserveWidthFor("Triangle");
        _angle.LogicalHeight = 28;
        _eraserSize.LogicalHeight = 28;
        _eraserSize.ReserveWidthFor("120 pt");
        _eraserSize.Value = _canvas.EraserSize;
        _eraserSize.Changed += (_, _) => _canvas.EraserSize = _eraserSize.Value;
        _eraserCaption.Margin = new Padding(0, 9, 6, 0);
        _eraserSize.Margin = new Padding(0, 2, 12, 0);
        _angle.ReserveWidthFor("-180°");
        _sweep.ReserveWidthFor("350°");
        _corners.ReserveWidthFor("Square");
        _ink.LogicalHeight = _fill.LogicalHeight = 28;
        _size.ReserveWidthFor("144 pt");
        _width.ReserveWidthFor("0.5 pt");
        _opacity.ReserveWidthFor("100 %");
        _ink.PickFromPage = _fill.PickFromPage = picked => _canvas.PickColor(picked);
        foreach (Divider divider in new[] { _split1, _split2, _split3, _split4, _split5, _split6, _split7, _split8 }) divider.Margin = new Padding(8, 8, 8, 0);
        Tips.Shared.SetToolTip(_delete, "Delete (Del)");
        Tips.Shared.SetToolTip(_duplicate, "Duplicate (Ctrl+D)");
        Tips.Shared.SetToolTip(_cut, "Cut (Ctrl+X)");
        Tips.Shared.SetToolTip(_copy, "Copy (Ctrl+C)\nPaste with Ctrl+V on any page, or right-click the page");
        Tips.Shared.SetToolTip(_turnLeft, "Turn left 90°");
        Tips.Shared.SetToolTip(_turnRight, "Turn right 90°\nOr drag the round handle above it; Shift keeps to 15° steps");
        Tips.Shared.SetToolTip(_flipAcross, "Flip horizontally");
        Tips.Shared.SetToolTip(_flipDown, "Flip vertically");
        Tips.Shared.SetToolTip(_justify, "Justify");
        Tips.Shared.SetToolTip(_top, "Text at the top of the box");
        Tips.Shared.SetToolTip(_middle, "Text in the middle of the box");
        Tips.Shared.SetToolTip(_bottom, "Text at the bottom of the box");

        _row.Controls.AddRange([_kind, _eraserCaption, _eraserSize, _hint, _font, _size, _bold, _italic, _underline, _superscript, _subscript, _symbol, _equation, _split1, _left, _center, _right, _justify, _split5, _top, _middle, _bottom, _split2,
            _crop, _tintCaption, _tint, _split4, _pens,
            _ink, _fillCaption, _fill, _widthCaption, _width, _endsCaption, _startEnd, _endEnd, _cornersCaption, _corners, _sweepCaption, _sweep,
            _opacityCaption, _opacity, _split3, .. _align.Select(entry => entry.Button), _distributeAcross, _distributeDown, _split6,
            _toFront, _forward, _backward, _toBack, _split7, _turnLeft, _turnRight, _flipAcross, _flipDown, _angleCaption, _angle, _split8,
            _cut, _copy, _duplicate, _delete]);
        foreach (Control control in _row.Controls)
            if (control is AppButton) control.Margin = new Padding(0, 2, 4, 0);
        _fillCaption.Margin = _widthCaption.Margin = _opacityCaption.Margin = _cornersCaption.Margin = _endsCaption.Margin = _sweepCaption.Margin = _tintCaption.Margin = new Padding(8, 9, 6, 0);
        _startEnd.Margin = _endEnd.Margin = new Padding(0, 2, 4, 0);
        _angleCaption.Margin = new Padding(4, 9, 6, 0);
        _viewport.Controls.Add(_row);
        _viewport.Controls.Add(_back);
        _viewport.Controls.Add(_more);
        _back.BringToFront();
        _more.BringToFront();
        Controls.Add(_viewport);
        _back.Click += (_, _) => ScrollRow(-_viewport.ClientSize.Width * 2 / 3);
        _more.Click += (_, _) => ScrollRow(_viewport.ClientSize.Width * 2 / 3);
        _row.SizeChanged += (_, _) => UpdateOverflow();
        _viewport.Resize += (_, _) => UpdateOverflow();
        VisibleChanged += (_, _) => { if (Visible) UpdateOverflow(); };
        _viewport.MouseWheel += (_, e) => ScrollRow(-Math.Sign(e.Delta) * LogicalToDeviceUnits(120));
        // Tabbing to a control that is out of sight brings it into view.
        foreach (Control control in _row.Controls) control.Enter += (_, _) => ScrollIntoView(control);

        _font.Changed += (_, _) =>
        {
            if (_font.Value == AddFontChoice) { AddFont(); return; }
            _lastFont = _font.Value;
            Apply(item => item.FontFamily = _font.Value, StyleFamily.Text);
        };
        _justify.Click += (_, _) => SetAlign(TextAlign.Justify);
        _top.Click += (_, _) => SetAnchor(TextAnchor.Top);
        _middle.Click += (_, _) => SetAnchor(TextAnchor.Middle);
        _bottom.Click += (_, _) => SetAnchor(TextAnchor.Bottom);
        _startEnd.Changed += (_, _) => SetEnd(start: true, _startEnd.Value);
        _endEnd.Changed += (_, _) => SetEnd(start: false, _endEnd.Value);
        _pens.Changed += (_, _) => Apply(item => item.Pen = _pens.Value);
        _turnLeft.Click += (_, _) => { _canvas.RotateSelection(-90); _canvas.Focus(); };
        _turnRight.Click += (_, _) => { _canvas.RotateSelection(90); _canvas.Focus(); };
        _flipAcross.Click += (_, _) => { _canvas.FlipSelection(across: true); _canvas.Focus(); };
        _flipDown.Click += (_, _) => { _canvas.FlipSelection(across: false); _canvas.Focus(); };
        _angle.Changed += (_, _) => { if (!_syncing) _canvas.SetRotation(_angle.Value, _angle.Session); };
        _sweep.Changed += (_, _) => Apply(item => item.Sweep = _sweep.Value, StyleFamily.Shape, _sweep.Session);
        _size.Changed += (_, _) => Apply(item => item.FontSize = _size.Value, StyleFamily.Text, _size.Session);
        // While text is being typed these style the words picked in it; otherwise the whole box.
        Tips.Shared.SetToolTip(_bold, "Bold (Ctrl+B)\nWhile typing, makes the words you pick bold");
        Tips.Shared.SetToolTip(_italic, "Italic (Ctrl+I)\nWhile typing, makes the words you pick italic");
        Tips.Shared.SetToolTip(_underline, "Underline (Ctrl+U)\nWhile typing, underlines the words you pick");
        Tips.Shared.SetToolTip(_superscript, "Superscript (Ctrl+Shift+=)\nRaises the words you pick, small: x²");
        Tips.Shared.SetToolTip(_subscript, "Subscript (Ctrl+=)\nLowers the words you pick, small: H₂O");
        Tips.Shared.SetToolTip(_symbol, "Insert a symbol\nGreek letters, maths signs, arrows and units");
        Tips.Shared.SetToolTip(_equation, "Insert an equation\nType x^2, v_0, \\alpha, <= and the like; Sheaf sets it out");
        _superscript.Click += (_, _) => { if (_canvas.StartTyping()) _canvas.ToggleEditorScript(TextScript.Super); };
        _subscript.Click += (_, _) => { if (_canvas.StartTyping()) _canvas.ToggleEditorScript(TextScript.Sub); };
        _symbol.Click += (_, _) =>
        {
            if (!_canvas.StartTyping()) return;
            _canvas.KeepTyping = true;
            SymbolPicker.Show(_symbol, symbol => _canvas.InsertIntoEditor([(symbol, TextScript.Normal)], MathText.SymbolFont), () =>
            {
                _canvas.KeepTyping = false;
                _canvas.ResumeTyping();
            });
        };
        _equation.Click += (_, _) =>
        {
            if (!_canvas.StartTyping()) return;
            if (EquationDialog.Ask(FindForm()) is { Count: > 0 } runs) _canvas.InsertIntoEditor(runs, MathText.SymbolFont);
            else _canvas.ResumeTyping();
        };
        _bold.Click += (_, _) =>
        {
            if (_canvas.IsEditingText) { _canvas.ToggleEditorStyle(FontStyle.Bold); return; }
            _bold.Selected = !_bold.Selected;
            Apply(item => { item.Bold = _bold.Selected; foreach (TextSpan span in item.Spans) span.Bold = _bold.Selected; }, StyleFamily.Text);
        };
        _italic.Click += (_, _) =>
        {
            if (_canvas.IsEditingText) { _canvas.ToggleEditorStyle(FontStyle.Italic); return; }
            _italic.Selected = !_italic.Selected;
            Apply(item => { item.Italic = _italic.Selected; foreach (TextSpan span in item.Spans) span.Italic = _italic.Selected; }, StyleFamily.Text);
        };
        _underline.Click += (_, _) =>
        {
            if (_canvas.IsEditingText) { _canvas.ToggleEditorStyle(FontStyle.Underline); return; }
            _underline.Selected = !_underline.Selected;
            Apply(item => SetUnderline(item, _underline.Selected), StyleFamily.Text);
        };
        _left.Click += (_, _) => SetAlign(TextAlign.Left);
        _center.Click += (_, _) => SetAlign(TextAlign.Center);
        _right.Click += (_, _) => SetAlign(TextAlign.Right);
        _ink.Changed += (_, _) =>
        {
            _pens.InkColor = _ink.Color;
            if (_canvas.IsEditingText && !_syncing) { _canvas.ColorEditorSelection(_ink.Color); return; }
            Apply(item => { item.StrokeColor = _ink.Color.ToArgb(); foreach (TextSpan span in item.Spans) span.Color = item.StrokeColor; });
        };
        _fill.Changed += (_, _) => Apply(item => item.FillColor = _fill.Color.ToArgb());
        _width.Changed += (_, _) => Apply(item => item.StrokeWidth = _width.Value, session: _width.Session);
        _opacity.Changed += (_, _) => Apply(item => item.Opacity = _opacity.Value / 100f, session: _opacity.Session);
        _corners.Changed += (_, _) => Apply(item => item.CornerRadius = _corners.Value, session: _corners.Session);
        _crop.Click += (_, _) => CropImage();
        _tint.LogicalHeight = 28;
        _tint.PickFromPage = picked => _canvas.PickColor(picked);
        Tips.Shared.SetToolTip(_tint, "Recolour\nEvery visible part of the picture takes this colour, its transparency kept; None brings back its own colours");
        _tint.Changed += (_, _) =>
        {
            if (_syncing) return;
            int tint = _tint.Color.A == 0 ? 0 : Color.FromArgb(255, _tint.Color).ToArgb();
            _canvas.ChangeSelection(item => { if (item.Kind == MarkKind.Image) item.Tint = tint; });
            _canvas.Focus();
        };

        foreach (var (button, edge, _) in _align) button.Click += (_, _) => { _canvas.Align(edge); _canvas.Focus(); };
        _distributeAcross.Click += (_, _) => { _canvas.Distribute(across: true); _canvas.Focus(); };
        _distributeDown.Click += (_, _) => { _canvas.Distribute(across: false); _canvas.Focus(); };
        _toFront.Click += (_, _) => { _canvas.Arrange(toFront: true); _canvas.Focus(); };
        _forward.Click += (_, _) => { _canvas.ArrangeStep(forward: true); _canvas.Focus(); };
        _backward.Click += (_, _) => { _canvas.ArrangeStep(forward: false); _canvas.Focus(); };
        _toBack.Click += (_, _) => { _canvas.Arrange(toFront: false); _canvas.Focus(); };
        Tips.Shared.SetToolTip(_distributeAcross, "Distribute horizontally\nSpaces three or more objects evenly");
        Tips.Shared.SetToolTip(_distributeDown, "Distribute vertically\nSpaces three or more objects evenly");
        Tips.Shared.SetToolTip(_toFront, "Bring to front");
        Tips.Shared.SetToolTip(_forward, "Bring forward");
        Tips.Shared.SetToolTip(_backward, "Send backward");
        Tips.Shared.SetToolTip(_toBack, "Send to back");
        _duplicate.Click += (_, _) => _canvas.DuplicateSelection();
        _cut.Click += (_, _) => { _canvas.Cut(); _canvas.Focus(); };
        _copy.Click += (_, _) => { _canvas.Copy(); _canvas.Focus(); };
        _delete.Click += (_, _) => _canvas.DeleteSelection();
    }

    // The row's own measure, so it is right even before a hidden bar has been laid out.
    int Overflow => Math.Max(0, _row.PreferredSize.Width - _viewport.ClientSize.Width);

    /// <summary>Places the row and shows an arrow on each side that has controls out of sight.</summary>
    void UpdateOverflow()
    {
        if (_scrolling is null) _scroll = Math.Clamp(_scroll, 0, Overflow);
        _row.Location = new Point(-_scroll, 0);
        int width = LogicalToDeviceUnits(28), top = LogicalToDeviceUnits(6);
        _back.SetBounds(0, top, width, _back.Height);
        _more.SetBounds(_viewport.ClientSize.Width - width, top, width, _more.Height);
        _back.Visible = _scroll > 0;
        _more.Visible = _scroll < Overflow;
    }

    void ScrollRow(int by) => ScrollTo(Math.Clamp((_scrolling is null ? _scroll : _scrollTarget) + by, 0, Overflow));

    int _scrollTarget;

    void ScrollTo(int target)
    {
        if (target == _scroll && _scrolling is null) return;
        _scrolling?.Cancel();
        int from = _scroll;
        _scrollTarget = target;
        _scrolling = Motion.Run(240, t =>
        {
            _scroll = (int)Math.Round(from + (target - from) * t);
            _row.Left = -_scroll;
        }, Easing.Out, () =>
        {
            _scrolling = null;
            UpdateOverflow();
        });
    }

    void ScrollIntoView(Control control)
    {
        int left = control.Left - _scroll, right = control.Right - _scroll, edge = LogicalToDeviceUnits(32);
        if (left < edge) ScrollTo(Math.Max(0, control.Left - edge));
        else if (right > _viewport.ClientSize.Width - edge) ScrollTo(Math.Min(Overflow, control.Right - _viewport.ClientSize.Width + edge));
    }

    /// <summary>What the bar says when no tool with a style is in use, per mode.</summary>
    public string IdleHint { get; set; } = "";

    static string Points(float value) => $"{value.ToString("0.#", CultureInfo.CurrentCulture)} pt";

    static AppButton Toggle(string icon, string name) => new("", ButtonKind.Quiet, icon)
    {
        LogicalHeight = 28,
        AccessibleName = name,
        NeutralSelection = true,
    };

    static TextLabel Caption(string text) => new(text, Tone.Tertiary) { Font = AppFonts.Small };

    static AppButton Icon(string icon, string? name = null) => new("", ButtonKind.Quiet, icon) { LogicalHeight = 28, AccessibleName = name ?? icon };

    void SetAlign(TextAlign align)
    {
        _left.Selected = align == TextAlign.Left;
        _center.Selected = align == TextAlign.Center;
        _right.Selected = align == TextAlign.Right;
        _justify.Selected = align == TextAlign.Justify;
        Apply(item => item.Align = align, StyleFamily.Text);
    }

    void SetAnchor(TextAnchor anchor)
    {
        _top.Selected = anchor == TextAnchor.Top;
        _middle.Selected = anchor == TextAnchor.Middle;
        _bottom.Selected = anchor == TextAnchor.Bottom;
        Apply(item => item.Anchor = anchor, StyleFamily.Text);
    }

    /// <summary>Sets one end of the selected lines; an arrow from before ends could be chosen becomes a line with that end.</summary>
    void SetEnd(bool start, LineEnd end)
    {
        if (_syncing) return;
        _canvas.ChangeSelection(item =>
        {
            if (!ShapeGeometry.IsLine(item.Kind)) return;
            (item.StartEnd, item.EndEnd) = ShapeGeometry.Ends(item);
            if (item.Kind == MarkKind.Arrow) item.Kind = MarkKind.Line;
            if (start) item.StartEnd = end; else item.EndEnd = end;
        });
        _canvas.Focus();
    }

    /// <summary>Underlines a whole text box (or takes it off): underlining lives in the box's stretches of style.</summary>
    static void SetUnderline(PageObject item, bool underline)
    {
        if (item.Kind != MarkKind.Text) return;
        if (item.Spans.Count == 0)
        {
            if (!underline) return;
            item.Spans = [new TextSpan { Length = item.Text.Length, Bold = item.Bold, Italic = item.Italic, Color = item.StrokeColor }];
        }
        foreach (TextSpan span in item.Spans) span.Underline = underline;
        // Back to one plain style, it needs no stretches.
        if (!underline && item.Spans.Select(span => (span.Bold, span.Italic, span.Color)).Distinct().Count() == 1) item.Spans = [];
    }

    /// <summary>While text is typed: the toggles and colour show the style of the words picked in it.</summary>
    public void SyncEditing()
    {
        if (_canvas.EditorStyle is not var (bold, italic, underline, color, script)) return;
        _syncing = true;
        _bold.Selected = bold;
        _italic.Selected = italic;
        _underline.Selected = underline;
        _superscript.Selected = script == TextScript.Super;
        _subscript.Selected = script == TextScript.Sub;
        _ink.Color = color;
        _syncing = false;
    }

    static (string, string)[] FontChoices()
    {
        var families = new List<(string, string)> { ("Geist", EmbeddedFonts.PdfFamily), ("Geist Mono", EmbeddedFonts.PdfMonoFamily) };
        families.AddRange(SystemFonts.DocumentFamilies.Select(name => (name, name)));
        families.AddRange(AppFonts.AddedFamilies.Where(name => !families.Any(known => known.Item2 == name)).Select(name => (name, name)));
        families.Add(("Add Font…", AddFontChoice));
        return [.. families];
    }

    /// <summary>Asks for a TrueType font file, keeps a copy, and uses it for the text.</summary>
    void AddFont()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "TrueType fonts (*.ttf)|*.ttf",
            Title = "Add a font",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Fonts),
        };
        bool chosen = dialog.ShowDialog(FindForm()) == DialogResult.OK;
        string? family = chosen ? AppFonts.AddFont(dialog.FileName) : null;
        _syncing = true;
        _font.SetOptions(FontChoices());
        _font.Value = family ?? _lastFont;
        _syncing = false;
        if (family is null)
        {
            if (chosen) Notice?.Invoke(this, $"Couldn't use {Path.GetFileName(dialog.FileName)} as a font. Try another TrueType (.ttf) file.");
            return;
        }
        _lastFont = family;
        Apply(item => item.FontFamily = family, StyleFamily.Text);
    }

    /// <summary>
    /// Sets a style on the selection (when it has this family) and on the tool's template. Changes carrying the
    /// same <paramref name="session"/> (one opening of a slider) undo together.
    /// </summary>
    void Apply(Action<PageObject> change, StyleFamily? only = null, object? session = null)
    {
        if (_syncing) return;
        StyleFamily family = only ?? _family;
        if (_styles.TemplateOf(family) is { } template) change(template);
        if (_canvas.Selection.Count > 0 && _canvas.Selection.All(item => ToolStyles.FamilyOf(item.Kind) == _family))
            _canvas.ChangeSelection(change, session);
        if (session is null) _canvas.Focus();
    }

    static bool Croppable(PageObject item) => item.Kind is not (MarkKind.Text or MarkKind.Note or MarkKind.Redaction or MarkKind.Highlight
        or MarkKind.Underline or MarkKind.Strikeout);

    /// <summary>
    /// Crops the selected picture (what is kept stays where it was on the page, at the same size), or the selected
    /// shape, line or drawing: what lies outside the part kept is hidden, and can be brought back by cropping again.
    /// </summary>
    void CropImage()
    {
        if (_canvas.Selection is [{ Kind: not MarkKind.Image } shape] && Croppable(shape))
        {
            CropShape(shape);
            return;
        }
        if (_canvas.Selection is not [{ Kind: MarkKind.Image, ImageData: { } data } image] || ImageCache.Get(image) is not { } picture) return;
        if (ImageCropDialog.Ask(FindForm(), picture) is not { } part) return;
        _canvas.ChangeSelection(item =>
        {
            RectangleF b = item.Bounds;
            item.ImageData = ImageLook.Crop(data, part);
            item.Bounds = new RectangleF(b.X + part.X * b.Width, b.Y + part.Y * b.Height, part.Width * b.Width, part.Height * b.Height);
        });
        _canvas.Focus();
    }

    void CropShape(PageObject shape)
    {
        // The object as it is before it turns, drawn on white for the crop box.
        PageObject flat = shape.Clone();
        flat.Rotation = 0;
        flat.FlipX = flat.FlipY = false;
        flat.CropLeft = flat.CropTop = flat.CropRight = flat.CropBottom = 0;
        float scale = 560 / Math.Max(1, Math.Max(flat.Width, flat.Height));
        using var picture = new Bitmap(Math.Max(1, (int)Math.Ceiling(flat.Width * scale)), Math.Max(1, (int)Math.Ceiling(flat.Height * scale)));
        using (Graphics g = Graphics.FromImage(picture))
        {
            g.Clear(Color.White);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.ScaleTransform(scale, scale);
            g.TranslateTransform(-flat.X, -flat.Y);
            PagePainter.DrawObject(g, flat);
        }
        var kept = new RectangleF(shape.CropLeft, shape.CropTop, 1 - shape.CropLeft - shape.CropRight, 1 - shape.CropTop - shape.CropBottom);
        if (ImageCropDialog.AskPart(FindForm(), picture, $"Crop {KindName(shape)}", kept) is not { } part) return;
        bool whole = part.Width > 0.999f && part.Height > 0.999f;
        _canvas.ChangeSelection(item =>
        {
            item.CropLeft = whole ? 0 : part.Left;
            item.CropTop = whole ? 0 : part.Top;
            item.CropRight = whole ? 0 : 1 - part.Right;
            item.CropBottom = whole ? 0 : 1 - part.Bottom;
        });
        _canvas.Focus();
    }

    /// <summary>Shows the controls for the selection, or for the current tool when nothing is selected.</summary>
    public void Sync()
    {
        IReadOnlyList<PageObject> selection = _canvas.Selection;
        bool hasSelection = selection.Count > 0;
        PageObject? typing = _canvas.EditingObject;
        StyleFamily family = typing is not null ? StyleFamily.Text
            : hasSelection
            ? selection.Select(item => ToolStyles.FamilyOf(item.Kind)).Distinct().Count() == 1 ? ToolStyles.FamilyOf(selection[0].Kind) : StyleFamily.None
            : ToolStyles.FamilyOf(_canvas.Tool);
        _family = family;
        PageObject? source = typing ?? (hasSelection && family != StyleFamily.None ? selection[0] : _styles.TemplateOf(family));

        _syncing = true;
        _row.SuspendLayout();
        bool grouped = selection.Count > 1 && selection.All(item => item.Group is not null) && selection.Select(item => item.Group).Distinct().Count() == 1;
        _kind.Text = typing is not null ? "Text box"
            : hasSelection
            ? grouped ? ToolStyles.GroupTitle(selection) : selection.Count == 1 ? KindName(selection[0]) : $"{selection.Count} objects"
            : family == StyleFamily.None ? "" : ToolName(_canvas.Tool);
        _kind.Visible = _kind.Text.Length > 0;
        _hint.Text = !hasSelection && (family is StyleFamily.None or StyleFamily.Redaction || _canvas.Tool == EditTool.PenPath)
            ? family != StyleFamily.Redaction ? _canvas.Tool switch
            {
                EditTool.SelectText => "Drag across the text to pick it, then press Ctrl+C to copy it.",
                EditTool.PenPath => "Click for corners, drag for curves. Click the first point to close; Enter ends.",
                EditTool.Zoom => "Drag right to zoom in, left to zoom out. Click to zoom in a step, Alt+click or right-click to zoom out. Ctrl+wheel zooms at the pointer.",
                EditTool.VertexSelect => "Click a shape, line or drawing, then drag its points. Alt-drag a point for curve handles; double-click the outline to add a point.",
                EditTool.LiftRectangle or EditTool.LiftFreeform => "Drag around part of the page to cut it out and move it. White is left behind.",
                EditTool.LiftPen => "Click round the part to lift, point by point (drag to curve). Click the first point (or press Enter) to lift it; Backspace takes a point back.",
                EditTool.EraseArea => "Rubs out drawings, shapes, lines and pictures under it. Text stays.",
                EditTool.EraseStroke => "Takes away each drawing, shape, line or picture it touches. Text stays.",
                _ => IdleHint,
            }
            : _canvas.Tool == EditTool.RedactText ? "Drag across the words to hide." : "Drag over what to hide. Choose the cover in the panel on the right."
            : "";
        _hint.Visible = _hint.Text.Length > 0;
        _eraserSize.Visible = _eraserCaption.Visible = !hasSelection && _canvas.Tool == EditTool.EraseArea;
        if (_eraserSize.Visible) _kind.Text = "Eraser";
        else if (!hasSelection && _canvas.Tool == EditTool.EraseStroke) _kind.Text = "Stroke Eraser";
        _kind.Visible = _kind.Text.Length > 0;

        bool text = family == StyleFamily.Text, shape = family == StyleFamily.Shape, image = family == StyleFamily.Image;
        bool stroked = family is StyleFamily.Shape or StyleFamily.Line or StyleFamily.Ink or StyleFamily.Markup;
        bool coloured = family is StyleFamily.Text or StyleFamily.Shape or StyleFamily.Line or StyleFamily.Ink or StyleFamily.Highlight or StyleFamily.Markup or StyleFamily.Note;
        bool opacity = family is StyleFamily.Text or StyleFamily.Shape or StyleFamily.Line or StyleFamily.Ink or StyleFamily.Highlight or StyleFamily.Image;
        _font.Visible = _size.Visible = _bold.Visible = _italic.Visible = _underline.Visible = _split1.Visible = text;
        // Raising, lowering and symbols work on a text box: the one being typed in, or the one chosen.
        _superscript.Visible = _subscript.Visible = _symbol.Visible = _equation.Visible = text;
        _superscript.Enabled = _subscript.Enabled = _symbol.Enabled = _equation.Enabled = typing is not null || selection is [{ Kind: MarkKind.Text }];
        if (typing is null) _superscript.Selected = _subscript.Selected = false;
        _left.Visible = _center.Visible = _right.Visible = _justify.Visible = _split2.Visible = text;
        _top.Visible = _middle.Visible = _bottom.Visible = _split5.Visible = text;
        // Ends belong to a drawn line; each line tool starts plain or with its arrow.
        _startEnd.Visible = _endEnd.Visible = _endsCaption.Visible = hasSelection && selection.All(item => ShapeGeometry.IsLine(item.Kind));
        // Shapes and lines can be drawn in any pen too: a circle drawn in pencil stays pencil.
        _pens.Visible = family is StyleFamily.Ink or StyleFamily.Shape or StyleFamily.Line
            && (hasSelection || _canvas.Tool is not (EditTool.Check or EditTool.Cross));
        _sweep.Visible = _sweepCaption.Visible = shape && (hasSelection ? selection.All(item => item.Kind == MarkKind.Pie) : _canvas.Tool == EditTool.Pie);
        bool pictures = image && hasSelection;
        _crop.Visible = hasSelection && selection.Count == 1 && Croppable(selection[0]);
        _tint.Visible = _tintCaption.Visible = pictures;
        _split4.Visible = pictures;
        bool turnable = hasSelection && _canvas.CanTurn;
        _turnLeft.Visible = _turnRight.Visible = _flipAcross.Visible = _flipDown.Visible = _split8.Visible = turnable;
        _angle.Visible = _angleCaption.Visible = turnable;
        _ink.Visible = coloured;
        _fill.Visible = _fillCaption.Visible = text || shape;
        _width.Visible = _widthCaption.Visible = stroked;
        // Only a shape can go without an outline; a line or a pen stroke would vanish.
        _width.Minimum = shape ? 0 : 0.5f;
        _opacity.Visible = _opacityCaption.Visible = opacity;
        bool rounded = text || image || (shape && (hasSelection
            ? selection.All(item => item.Kind is MarkKind.Rectangle or MarkKind.Polygon or MarkKind.Star or MarkKind.Parallelogram)
            : _canvas.Tool is EditTool.Rectangle or EditTool.Triangle or EditTool.Star or EditTool.Parallelogram));
        _corners.Visible = _cornersCaption.Visible = rounded;
        _split3.Visible = hasSelection && (coloured || opacity);
        _duplicate.Visible = _delete.Visible = _cut.Visible = _copy.Visible = _split6.Visible = _split7.Visible = hasSelection;
        foreach (var (button, _, name) in _align)
        {
            button.Visible = hasSelection;
            // One object lines up with its page; several with each other.
            Tips.Shared.SetToolTip(button, selection.Count == 1 ? name.Replace("edges", "edge").Replace("centres", "centre") + " to the page" : name);
        }
        _distributeAcross.Visible = _distributeDown.Visible = hasSelection;
        _distributeAcross.Enabled = _distributeDown.Enabled = _canvas.CanDistribute;
        _toFront.Visible = _forward.Visible = _backward.Visible = _toBack.Visible = hasSelection;

        if (source is not null)
        {
            _font.Value = _lastFont = source.FontFamily;
            _justify.Selected = source.Align == TextAlign.Justify;
            _top.Selected = source.Anchor == TextAnchor.Top;
            _middle.Selected = source.Anchor == TextAnchor.Middle;
            _bottom.Selected = source.Anchor == TextAnchor.Bottom;
            (_startEnd.Value, _endEnd.Value) = ShapeGeometry.IsLine(source.Kind) ? ShapeGeometry.Ends(source) : (LineEnd.None, LineEnd.None);
            _pens.Value = source.Pen;
            _pens.InkColor = Color.FromArgb(255, Color.FromArgb(source.StrokeColor));
            _angle.Value = _canvas.AngleShown(source);
            _tint.Color = source.Tint >> 24 == 0 ? Color.Transparent : Color.FromArgb(source.Tint);
            _sweep.Value = source.Sweep;
            _size.Value = source.FontSize;
            _bold.Selected = source.Bold;
            _italic.Selected = source.Italic;
            _underline.Selected = source.Spans.Count > 0 && source.Spans.All(span => span.Underline);
            _left.Selected = source.Align == TextAlign.Left;
            _center.Selected = source.Align == TextAlign.Center;
            _right.Selected = source.Align == TextAlign.Right;
            _ink.Color = Color.FromArgb(source.StrokeColor);
            _fill.Color = Color.FromArgb(source.FillColor);
            _width.Value = source.StrokeWidth;
            _opacity.Value = (float)Math.Round(source.Opacity * 100);
            _corners.Value = source.CornerRadius;
        }
        Tips.Shared.SetToolTip(_ink, text ? "Text colour" : family is StyleFamily.Shape or StyleFamily.Line or StyleFamily.Markup ? "Line colour" : "Colour");
        _row.ResumeLayout(true);
        UpdateOverflow();
        _syncing = false;
    }

    static string KindName(PageObject item) => item.Kind switch
    {
        MarkKind.Text => "Text box",
        MarkKind.Ink => "Drawing",
        MarkKind.Redaction => "Redaction mark",
        _ => ToolStyles.Describe(item),
    };

    static string ToolName(EditTool tool) => tool switch
    {
        EditTool.Text => "Text",
        EditTool.Date => "Date",
        EditTool.Draw => "Pen",
        EditTool.PenPath => "Path Pen",
        EditTool.SemiCircle => "Semicircle",
        EditTool.Strikeout => "Strike-through",
        EditTool.Check => "Tick",
        EditTool.RedactRectangle or EditTool.RedactEllipse or EditTool.RedactFreeform or EditTool.RedactText => "Redact",
        _ => tool.ToString(),
    };
}
