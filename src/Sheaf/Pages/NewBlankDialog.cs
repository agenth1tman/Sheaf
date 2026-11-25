using System.Globalization;
using Sheaf.Ui;
using Sheaf.Ui.Controls;

namespace Sheaf.Pages;

/// <summary>What a new blank document is: its page size in points (a PDF) or pixels (an image), and its paper.</summary>
internal sealed record BlankChoice(bool IsImage, SizeF Size, int Background);

/// <summary>
/// Asks for the size of a new blank PDF (a paper size, either way round, or any size in mm, cm, inches or points)
/// or a new blank image (a common size or any size in pixels), and its paper colour.
/// </summary>
internal sealed class NewBlankDialog : DialogForm
{
    const int White = unchecked((int)0xFFFFFFFF);
    const string Custom = "Custom";

    /// <summary>Paper sizes in points, portrait.</summary>
    public static readonly (string Name, SizeF Points)[] Papers =
    [
        ("A4", new(595.28f, 841.89f)), ("Letter", new(612, 792)), ("Legal", new(612, 1008)), ("A3", new(841.89f, 1190.55f)),
        ("A5", new(419.53f, 595.28f)), ("A6", new(297.64f, 419.53f)), ("B5", new(498.9f, 708.66f)), ("Tabloid", new(792, 1224)),
        ("Executive", new(522, 756)),
    ];

    /// <summary>Common picture sizes in pixels.</summary>
    public static readonly (string Name, Size Pixels)[] Pictures =
    [
        ("1920 × 1080 (Full HD)", new(1920, 1080)), ("3840 × 2160 (4K)", new(3840, 2160)), ("1280 × 720 (HD)", new(1280, 720)),
        ("1080 × 1080 (Square post)", new(1080, 1080)), ("1080 × 1350 (Portrait post)", new(1080, 1350)), ("1080 × 1920 (Story)", new(1080, 1920)),
        ("1200 × 630 (Link preview)", new(1200, 630)), ("1500 × 500 (Header)", new(1500, 500)), ("A4 at 150 dpi (1240 × 1754)", new(1240, 1754)),
        ("A4 at 300 dpi (2480 × 3508)", new(2480, 3508)),
    ];

    static readonly (string Name, float PointsPer)[] Units = [("mm", 72 / 25.4f), ("cm", 72 / 2.54f), ("in", 72), ("pt", 1)];

    readonly bool _image;
    readonly DropDown<string> _preset;
    readonly ChoiceGroup<bool> _landscape = new("Orientation", horizontal: true, ("Portrait", false), ("Landscape", true));
    readonly InputBox _width = new() { Width = 110 };
    readonly InputBox _height = new() { Width = 110 };
    readonly DropDown<string> _unit = new("Units", [.. Units.Select(unit => (unit.Name, unit.Name))]);
    readonly ChoiceGroup<int> _paper;
    readonly ColorButton _colour = new("Paper colour", Color.FromArgb(White));
    readonly FormField _sizeField;
    bool _syncing;

    NewBlankDialog(bool image) : base(image ? "New Blank Image" : "New Blank PDF",
        image ? "Choose a size in pixels, or type your own." : "Choose a paper size, or type your own.", 440)
    {
        _image = image;
        _preset = image
            ? new DropDown<string>("Size", [.. Pictures.Select(p => (p.Name, p.Name)), (Custom, Custom)])
            : new DropDown<string>("Paper size", [.. Papers.Select(p => (p.Name, p.Name)), (Custom, Custom)]);
        _preset.ReserveWidthFor("A4 at 300 dpi (2480 × 3508)");
        _paper = image
            ? new ChoiceGroup<int>("Background", horizontal: true, ("White", 1), ("See-through", 0), ("Colour", 2))
            : new ChoiceGroup<int>("Paper", horizontal: true, ("White", 1), ("Colour", 2));
        foreach (InputBox box in new[] { _width, _height })
        {
            box.Box.MaxLength = 8;
            box.Box.TextChanged += (_, _) => { if (!_syncing) _preset.Value = Custom; };
        }
        _width.Caption = image ? "Width in pixels" : "Width";
        _height.Caption = image ? "Height in pixels" : "Height";
        var times = new TextLabel("×", Tone.Secondary) { Margin = new Padding(8, 8, 8, 0) };
        var size = image ? Make.Row(_width, times, _height, new TextLabel("px", Tone.Secondary) { Margin = new Padding(8, 8, 0, 0) })
            : Make.Row(_width, times, _height, _unit);
        _unit.Margin = new Padding(8, 0, 0, 0);
        _unit.ReserveWidthFor("mm");

        AddContent(new FormField(image ? "Size" : "Paper size", _preset), 18);
        if (!image) AddContent(new FormField("Orientation", _landscape), 4);
        _sizeField = new FormField(image ? "Width and height" : "Width and height", size, image ? "Up to 10000 pixels each way." : null);
        AddContent(_sizeField, 4);
        _colour.Margin = new Padding(12, 0, 0, 0);
        _colour.Enabled = false;
        AddContent(new FormField(image ? "Background" : "Paper", Make.Row(_paper, _colour)), 4);
        AddButtons("Create", Accept);

        _preset.Changed += (_, _) => ShowPreset();
        _landscape.Changed += (_, _) => ShowPreset();
        _unit.Changed += (_, _) => ShowPreset(keepTyped: true);
        _paper.Changed += (_, _) => _colour.Enabled = _paper.Value == 2;
        _colour.Changed += (_, _) => _paper.Value = 2;
        _lastUnit = Units[0].Name;
        ShowPreset();
        Shown += (_, _) => _preset.Focus();
    }

    string _lastUnit;

    float PointsPer(string unit) => Units.First(u => u.Name == unit).PointsPer;

    /// <summary>Fills the width and height boxes from the chosen size (or, when only the units change, converts them).</summary>
    void ShowPreset(bool keepTyped = false)
    {
        _syncing = true;
        if (_image)
        {
            if (Pictures.FirstOrDefault(p => p.Name == _preset.Value) is { Name: not null } picture)
            {
                _width.Text = picture.Pixels.Width.ToString(CultureInfo.CurrentCulture);
                _height.Text = picture.Pixels.Height.ToString(CultureInfo.CurrentCulture);
            }
        }
        else if (keepTyped)
        {
            float from = PointsPer(_lastUnit), to = PointsPer(_unit.Value);
            if (Parse(_width.Text) is float w) _width.Text = Show(w * from / to);
            if (Parse(_height.Text) is float h) _height.Text = Show(h * from / to);
        }
        else if (Papers.FirstOrDefault(p => p.Name == _preset.Value) is { Name: not null } paper)
        {
            SizeF points = _landscape.Value ? new SizeF(paper.Points.Height, paper.Points.Width) : paper.Points;
            float per = PointsPer(_unit.Value);
            _width.Text = Show(points.Width / per);
            _height.Text = Show(points.Height / per);
        }
        _lastUnit = _unit.Value;
        _syncing = false;
    }

    static string Show(float value) => value.ToString("0.##", CultureInfo.CurrentCulture);

    static float? Parse(string text) =>
        float.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out float value) && value > 0 ? value : null;

    BlankChoice? _choice;

    bool Accept()
    {
        float? width = Parse(_width.Text), height = Parse(_height.Text);
        if (width is null || height is null) return _sizeField.Check("Type a width and a height above zero.");
        int background = _paper.Value switch { 0 => 0x00FFFFFF, 2 => Color.FromArgb(255, _colour.Color).ToArgb(), _ => White };
        if (_image)
        {
            if (width > 10000 || height > 10000) return _sizeField.Check("Pictures can be up to 10000 pixels each way.");
            _choice = new BlankChoice(true, new SizeF(MathF.Round(width.Value), MathF.Round(height.Value)), background);
            return true;
        }
        float per = PointsPer(_unit.Value);
        // PDF pages can be from 3 to 14400 points (200 inches) each way.
        float w = width.Value * per, h = height.Value * per;
        if (w < 3 || h < 3 || w > 14400 || h > 14400) return _sizeField.Check("Pages can be from about 1 mm to 5 metres (200 inches) each way.");
        _choice = new BlankChoice(false, new SizeF(w, h), background);
        return true;
    }

    public static BlankChoice? Ask(IWin32Window? owner, bool image)
    {
        using var dialog = new NewBlankDialog(image);
        return dialog.ShowDialog(owner) == DialogResult.OK ? dialog._choice : null;
    }
}
