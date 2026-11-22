using System.Globalization;
using Sheaf.Ui.Controls;

namespace Sheaf.Ui;

/// <summary>Small builders that keep every screen's text and spacing the same.</summary>
internal static class Make
{
    public static TextLabel Title(string text) => new(text) { Font = AppFonts.Title, Margin = new Padding(0, 0, 0, 4) };

    public static TextLabel Section(string text, int topSpace = 20) => new(text) { Font = AppFonts.Section, Margin = new Padding(0, topSpace, 0, 10) };

    /// <summary>The small heading of a panel section: "Position and size".</summary>
    public static TextLabel PanelHeading(string text) => new(text, Tone.Secondary) { Font = AppFonts.SmallStrong, Margin = new Padding(0, 0, 0, 8) };

    public static TextLabel FieldCaption(string text) => new(text, Tone.Secondary) { Font = AppFonts.SmallStrong, Margin = Padding.Empty };

    public static WrapLabel Hint(string text) => new()
    {
        Text = text,
        Font = AppFonts.Small,
        Tone = Tone.Tertiary,
        Anchor = AnchorStyles.Left | AnchorStyles.Right,
        Margin = new Padding(0, 4, 0, 0),
    };

    public static WrapLabel Paragraph(string text, Tone tone = Tone.Secondary) => new()
    {
        Text = text,
        Tone = tone,
        Anchor = AnchorStyles.Left | AnchorStyles.Right,
        Margin = new Padding(0, 2, 0, 0),
    };

    public static TextLabel Line(string text, Tone tone = Tone.Primary, Font? font = null) => new(text, tone)
    {
        Font = font ?? AppFonts.Body,
        AutoSize = false,
        Height = (font ?? AppFonts.Body).Height + 6,
        Anchor = AnchorStyles.Left | AnchorStyles.Right,
        TextAlign = ContentAlignment.MiddleLeft,
    };

    public static Check Check(string text, bool isChecked = false) => new(text, isChecked) { Anchor = AnchorStyles.Left | AnchorStyles.Right };

    /// <summary>Controls one under another, in reading (and tab) order.</summary>
    public static TableLayoutPanel Stack(params Control[] controls)
    {
        var stack = new TableLayoutPanel
        {
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        stack.SuspendLayout();
        foreach (Control control in controls) stack.Controls.Add(control);
        stack.ResumeLayout(false);
        return stack;
    }

    /// <summary>Controls side by side, vertically centred.</summary>
    public static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        row.SuspendLayout();
        foreach (Control control in controls)
        {
            control.Anchor = AnchorStyles.Left;
            row.Controls.Add(control);
        }
        row.ResumeLayout(false);
        return row;
    }

    public static InputBox Input(string placeholder = "", int maxLength = 200) => new()
    {
        Placeholder = placeholder,
        Box = { MaxLength = maxLength },
    };

    public static InputBox Number(int value, int minimum, int maximum)
    {
        var input = new InputBox { NumberRange = (minimum, maximum), Width = 96, Monospace = true, Box = { MaxLength = maximum.ToString(CultureInfo.InvariantCulture).Length } };
        input.Text = value.ToString(CultureInfo.CurrentCulture);
        return input;
    }

    /// <summary>A panel section: hairline on top, padding, a small heading and the controls.</summary>
    public static TableLayoutPanel PanelSection(string? heading, params Control[] controls)
    {
        var stack = Stack(heading is null ? controls : [PanelHeading(heading), .. controls]);
        stack.Dock = DockStyle.Top;
        stack.Padding = new Padding(16, 14, 16, 14);
        return stack;
    }
}
