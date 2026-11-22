using PdfSharp.Pdf;
using Sheaf.Core;
using Sheaf.Core.Fonts;
using Sheaf.Core.IO;
using Sheaf.Core.Pdf;
using Sheaf.Ui;
using Sheaf.Ui.Controls;

namespace Sheaf.Tools;

internal sealed class CompressTool() : Tool(Definition)
{
    public static readonly ToolInfo Definition = new("compress", "Compress PDF",
        "Make a PDF smaller by recompressing its images.",
        ToolCategory.ProtectAndOptimize, "compress", FileKind.Pdf, "Compress PDF", "Compressing…");

    readonly ChoiceGroup<CompressionLevel> _level = new("Compression", horizontal: false,
        ("Light: images stay as they are", CompressionLevel.Light),
        ("Recommended: smaller, good quality", CompressionLevel.Recommended),
        ("Strong: smallest, lower image quality", CompressionLevel.Strong));

    protected override Control BuildOptions()
    {
        _level.Value = CompressionLevel.Recommended;
        return Make.Stack(new FormField("Compression", _level));
    }

    public override async Task<ToolResult> RunAsync(ToolRun run)
    {
        CompressionLevel level = _level.Value;
        long before = 0, after = 0;
        var unchanged = new List<string>();
        ToolResult result = await run.EachFileAsync("Compressing", file =>
        {
            using PdfDocument document = PdfFiles.OpenForModify(file.Path);
            Compression.Compress(document, level, run.Cancel);
            string path = run.Output.FileFor(file, "compressed");
            PdfFiles.Save(document, path);

            long newSize = new FileInfo(path).Length;
            if (newSize >= file.Size)
            {
                File.Delete(path);
                unchanged.Add(file.Name);
                return [];
            }
            before += file.Size;
            after += newSize;
            return [path];
        });
        return result with { Detail = Summary(before, after, unchanged) };
    }

    static string? Summary(long before, long after, List<string> unchanged)
    {
        string? saved = before == 0 ? null
            : $"{Words.Size(before)} → {Words.Size(after)}, {Math.Round(100.0 * (before - after) / before):0} % smaller";
        string? kept = unchanged.Count switch
        {
            0 => null,
            1 => $"“{unchanged[0]}” is already as small as Sheaf can make it",
            _ => $"{Words.Files(unchanged.Count)} are already as small as Sheaf can make them",
        };
        return saved is null ? kept : kept is null ? saved : $"{saved}. {kept}";
    }
}

internal sealed class PageNumbersTool() : Tool(Definition)
{
    public static readonly ToolInfo Definition = new("numbers", "Add Page Numbers",
        "Number the pages in the corner you choose.",
        ToolCategory.EditAndReview, "numbers", FileKind.Pdf, "Add Page Numbers", "Numbering…");

    const double MarginPoints = 28;
    readonly ChoiceGroup<bool> _top = new("Position", horizontal: true, ("Bottom", false), ("Top", true));
    readonly ChoiceGroup<int> _align = new("Alignment", horizontal: true, ("Left", 0), ("Center", 1), ("Right", 2));
    readonly ChoiceGroup<string> _format = new("Format", horizontal: false,
        ("1", "{n}"), ("Page 1", "Page {n}"), ("Page 1 of 12", "Page {n} of {total}"), ("1 / 12", "{n} / {total}"));
    readonly DropDown<string> _font = StampFonts.Picker();
    readonly ColorButton _color = new("Colour", Color.Black);
    readonly InputBox _first = Make.Number(1, 0, 99999);
    readonly InputBox _size = Make.Number(11, 6, 72);
    readonly PagesField _pages = new("Pages", required: false);
    FormField? _firstField, _sizeField;

    protected override Control BuildOptions()
    {
        _align.Value = 1;
        _firstField = new FormField("First number", _first);
        _sizeField = new FormField("Text size (pt)", _size);
        _color.Anchor = AnchorStyles.Left;
        return Make.Stack(new FormField("Position", _top), new FormField("Alignment", _align),
            new FormField("Format", _format), new FormField("Font", _font), new FormField("Colour", _color),
            _firstField, _sizeField, _pages.Field);
    }

    public override Control? Validate()
    {
        Control? bad = null;
        if (!_firstField!.Check(_first.Number is null ? "Enter the number for the first page." : null)) bad ??= _firstField;
        if (!_sizeField!.Check(_size.Number is >= 6 and <= 72 ? null : "Enter a size from 6 to 72.")) bad ??= _sizeField;
        return bad ?? _pages.Validate();
    }

    public override Task<ToolResult> RunAsync(ToolRun run)
    {
        var style = new PageNumberStyle(Position(), _format.Value, _first.Number ?? 1, _size.Number ?? 11, MarginPoints, _font.Value, _color.Color);
        var pages = _pages.Snapshot();
        return run.EachFileAsync("Numbering", file =>
        {
            using PdfDocument document = PdfFiles.OpenForModify(file.Path);
            Stamping.AddPageNumbers(document, pages(document.PageCount, file.Name), style);
            string path = run.Output.FileFor(file, "numbered");
            PdfFiles.Save(document, path);
            return [path];
        });
    }

    StampPosition Position() => (_top.Value, _align.Value) switch
    {
        (true, 0) => StampPosition.TopLeft,
        (true, 1) => StampPosition.TopCenter,
        (true, _) => StampPosition.TopRight,
        (false, 0) => StampPosition.BottomLeft,
        (false, 1) => StampPosition.BottomCenter,
        _ => StampPosition.BottomRight,
    };
}

/// <summary>The fonts stamps can use: Geist in three weights, then the document fonts installed in Windows.</summary>
internal static class StampFonts
{
    public static DropDown<string> Picker()
    {
        var fonts = new List<(string, string)>
        {
            ("Geist", EmbeddedFonts.PdfFamily),
            ("Geist Medium", EmbeddedFonts.PdfMediumFamily),
            ("Geist SemiBold", EmbeddedFonts.PdfSemiBoldFamily),
        };
        fonts.AddRange(Core.Fonts.SystemFonts.DocumentFamilies.Select(name => (name, name)));
        var picker = new DropDown<string>("Font", [.. fonts]) { Stretch = true };
        picker.Value = EmbeddedFonts.PdfMediumFamily;
        return picker;
    }
}

internal sealed class WatermarkTool() : Tool(Definition)
{
    public static readonly ToolInfo Definition = new("watermark", "Add Watermark",
        "Stamp text such as “Draft”, or an image such as a logo, across your pages.",
        ToolCategory.EditAndReview, "watermark", FileKind.Pdf, "Add Watermark", "Stamping…");

    readonly ChoiceGroup<bool> _useImage = new("Watermark", horizontal: true, ("Text", false), ("Image", true));
    readonly InputBox _text = Make.Input("e.g. Draft", maxLength: 100);
    readonly InputBox _size = Make.Number(60, 8, 300);
    readonly ColorButton _color = new("Colour", Color.FromArgb(110, 110, 110));
    readonly AppButton _chooseImage = new("Choose Image…", ButtonKind.Secondary, "image");
    readonly TextLabel _imageName = new("No image chosen", Tone.Tertiary) { Font = AppFonts.Small, Margin = new Padding(0, 6, 0, 0) };
    readonly Slider _imageScale = new() { Minimum = 5, Maximum = 100, Width = 200, AccessibleName = "Image size" };
    readonly Slider _opacity = new() { Minimum = 5, Maximum = 100, Width = 200, AccessibleName = "Opacity" };
    readonly ChoiceGroup<bool> _diagonal = new("Angle", horizontal: true, ("Diagonal", true), ("Straight", false));
    readonly CheckBox _tiled = Make.Check("Repeat across the page");
    readonly PagesField _pages = new("Pages", required: false);
    FormField? _textField, _sizeField, _imageField;
    Control? _textOptions, _imageOptions;
    string? _imagePath;

    protected override Control BuildOptions()
    {
        _imageScale.Value = 40;
        _opacity.Value = 30;
        _textField = new FormField("Text", _text);
        _sizeField = new FormField("Text size (pt)", _size, "Long text shrinks to fit the page.");
        _color.Anchor = AnchorStyles.Left;
        _textOptions = Make.Stack(_textField, _sizeField, new FormField("Colour", _color));
        _imageField = new FormField("Image", Make.Stack(_chooseImage, _imageName), "A PNG with a clear background works best for logos.");
        _imageOptions = Make.Stack(_imageField, new FormField("Width (of the page)", WithValue(_imageScale, value => $"{value} %")));
        _imageOptions.Visible = false;
        _chooseImage.Anchor = AnchorStyles.Left;
        _chooseImage.Click += (_, _) => ChooseImage();
        _useImage.Changed += (_, _) =>
        {
            _textOptions.Visible = !_useImage.Value;
            _imageOptions.Visible = _useImage.Value;
        };
        return Make.Stack(new FormField("Watermark", _useImage), _textOptions, _imageOptions,
            new FormField("Opacity", WithValue(_opacity, value => $"{value} %")),
            new FormField("Angle", _diagonal), _tiled, _pages.Field);
    }

    /// <summary>A slider with its value written beside it.</summary>
    static Control WithValue(Slider slider, Func<int, string> describe)
    {
        var value = new TextLabel(describe(slider.Value), Tone.Secondary) { Font = AppFonts.Small, Margin = new Padding(12, 4, 0, 0) };
        slider.ValueChanged += (_, _) => value.Text = describe(slider.Value);
        return Make.Row(slider, value);
    }

    void ChooseImage()
    {
        using var dialog = new OpenFileDialog { Filter = Shell.FileDialogs.Filter(FileKind.Image), Title = "Choose the watermark image" };
        if (dialog.ShowDialog(_chooseImage.FindForm()) != DialogResult.OK) return;
        _imagePath = dialog.FileName;
        _imageName.Text = Path.GetFileName(_imagePath);
        _imageName.Tone = Tone.Secondary;
        _imageField?.Check(null);
    }

    public override Control? Validate()
    {
        Control? bad = null;
        if (_useImage.Value)
        {
            if (!_imageField!.Check(_imagePath is not null && File.Exists(_imagePath) ? null : "Choose the image to stamp.")) bad ??= _imageField;
        }
        else
        {
            if (!_textField!.Check(string.IsNullOrWhiteSpace(_text.Text) ? "Enter the text to stamp." : null)) bad ??= _textField;
            if (!_sizeField!.Check(_size.Number is >= 8 and <= 300 ? null : "Enter a size from 8 to 300.")) bad ??= _sizeField;
        }
        return bad ?? _pages.Validate();
    }

    public override Task<ToolResult> RunAsync(ToolRun run)
    {
        var style = new WatermarkStyle(_text.Text.Trim(), _size.Number ?? 60, _color.Color, _opacity.Value / 100.0, _diagonal.Value, _tiled.Checked,
            _useImage.Value ? _imagePath : null, _imageScale.Value / 100.0);
        var pages = _pages.Snapshot();
        return run.EachFileAsync("Stamping", file =>
        {
            using PdfDocument document = PdfFiles.OpenForModify(file.Path);
            string path = run.Output.FileFor(file, "watermarked");
            using (Stamping.AddWatermark(document, pages(document.PageCount, file.Name), style))
                PdfFiles.Save(document, path);
            return [path];
        });
    }
}

internal sealed class ProtectTool() : Tool(Definition)
{
    public static readonly ToolInfo Definition = new("protect", "Protect PDF",
        "Lock a PDF with a password.",
        ToolCategory.ProtectAndOptimize, "lock", FileKind.Pdf, "Protect PDF", "Protecting…");

    readonly InputBox _password = new() { IsPassword = true, Box = { MaxLength = 127 } };
    readonly InputBox _confirm = new() { IsPassword = true, Box = { MaxLength = 127 } };
    FormField? _passwordField, _confirmField;

    protected override Control BuildOptions()
    {
        _passwordField = new FormField("Password", _password,
            "Anyone opening the PDF will need it. Sheaf can’t recover a forgotten password.");
        _confirmField = new FormField("Type it again", _confirm);
        return Make.Stack(_passwordField, _confirmField);
    }

    public override Control? Validate()
    {
        if (!_passwordField!.Check(_password.Text.Length == 0 ? "Enter a password." : null)) return _passwordField;
        return _confirmField!.Check(_confirm.Text == _password.Text ? null : "The two passwords don’t match.") ? null : _confirmField;
    }

    public override Task<ToolResult> RunAsync(ToolRun run)
    {
        string password = _password.Text;
        return run.EachFileAsync("Protecting", file =>
        {
            using PdfDocument document = PdfFiles.OpenForModify(file.Path);
            PdfSecurity.Protect(document, password);
            string path = run.Output.FileFor(file, "protected");
            PdfFiles.Save(document, path);
            return [path];
        });
    }
}

internal sealed class UnlockTool() : Tool(Definition)
{
    public static readonly ToolInfo Definition = new("unlock", "Unlock PDF",
        "Remove the password from a PDF you can open.",
        ToolCategory.ProtectAndOptimize, "unlock", FileKind.Pdf, "Unlock PDF", "Unlocking…");

    readonly InputBox _password = new() { IsPassword = true, Box = { MaxLength = 127 } };
    FormField? _field;

    protected override Control BuildOptions()
    {
        _field = new FormField("Current password", _password, "Sheaf saves an unlocked copy; the original keeps its password.");
        return Make.Stack(_field);
    }

    public override Control? Validate() =>
        _field!.Check(_password.Text.Length == 0 ? "Enter the PDF’s password." : null) ? null : _field;

    public override Task<ToolResult> RunAsync(ToolRun run)
    {
        string password = _password.Text;
        return run.EachFileAsync("Unlocking", file =>
        {
            using PdfDocument document = PdfFiles.OpenForModify(file.Path, password);
            PdfSecurity.RemoveProtection(document);
            string path = run.Output.FileFor(file, "unlocked");
            PdfFiles.Save(document, path);
            return [path];
        });
    }
}

internal sealed class RemoveHiddenDataTool() : Tool(Definition)
{
    public static readonly ToolInfo Definition = new("remove-hidden", "Remove Hidden Data",
        "Strip metadata, comments, scripts and attachments before you share.",
        ToolCategory.ProtectAndOptimize, "eye-off", FileKind.Pdf, "Remove Hidden Data", "Cleaning…");

    readonly CheckBox _metadata = Make.Check("Author, title, dates and other metadata", isChecked: true);
    readonly CheckBox _comments = Make.Check("Comments, highlights and other markup", isChecked: true);
    readonly CheckBox _scripts = Make.Check("JavaScript and actions that run on opening", isChecked: true);
    readonly CheckBox _attachments = Make.Check("Attached files", isChecked: true);

    protected override Control BuildOptions() => Make.Stack(Make.FieldCaption("Remove"), _metadata, _comments, _scripts, _attachments,
        Make.Hint("Links and form fields stay. The original file is not changed."));

    public override async Task<ToolResult> RunAsync(ToolRun run)
    {
        var options = new CleanOptions(_metadata.Checked, _comments.Checked, _scripts.Checked, _attachments.Checked);
        int total = 0;
        ToolResult result = await run.EachFileAsync("Cleaning", file =>
        {
            using PdfDocument document = PdfFiles.OpenForModify(file.Path);
            total += Sanitizer.Clean(document, options);
            string path = run.Output.FileFor(file, "clean");
            PdfFiles.Save(document, path);
            return [path];
        });
        return result with { Detail = Words.Count(total, "item removed", "items removed") };
    }
}
