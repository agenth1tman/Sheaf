namespace Sheaf.Ui.Controls;

internal enum Tone { Primary, Secondary, Tertiary, Accent, Success, Error, Warning }

internal static class Tones
{
    public static Color Of(Tone tone) => tone switch
    {
        Tone.Secondary => Theme.Current.Text2,
        Tone.Tertiary => Theme.Current.Text3,
        Tone.Accent => Theme.Current.AccentText,
        Tone.Success => Theme.Current.Success,
        Tone.Error => Theme.Current.DangerText,
        Tone.Warning => Theme.Current.Warning,
        _ => Theme.Current.Text,
    };
}

/// <summary>A one-line label whose colour follows the theme. Ends with "…" when it doesn't fit.</summary>
internal class TextLabel : Label
{
    public TextLabel(string text = "", Tone tone = Tone.Primary)
    {
        Text = text;
        Tone = tone;
        UseMnemonic = false;
        AutoSize = true;
        AutoEllipsis = true;
        Font = AppFonts.Body;
        Margin = Padding.Empty;
    }

    public Tone Tone { get; set; }

    public override Color ForeColor
    {
        get => Tones.Of(Tone);
        set { }
    }
}

/// <summary>A label that wraps to the width its container offers and grows downwards.</summary>
internal class WrapLabel : TextLabel
{
    readonly WrapMeasure _measure = new();

    public WrapLabel()
    {
        AutoEllipsis = false;
    }

    public override Size GetPreferredSize(Size proposedSize) => _measure.Size(this, proposedSize);

    protected override void OnTextChanged(EventArgs e) { _measure.Reset(); base.OnTextChanged(e); }
    protected override void OnFontChanged(EventArgs e) { _measure.Reset(); base.OnFontChanged(e); }
}

/// <summary>
/// Measures wrapped text for a width and remembers the answer: layout asks for the same width many times,
/// and measuring on every call made building a page take seconds.
/// </summary>
internal sealed class WrapMeasure
{
    readonly Dictionary<int, int> _heights = [];

    public void Reset() => _heights.Clear();

    public Size Size(Control control, Size proposed)
    {
        int width = proposed.Width is > 1 and < int.MaxValue / 2 ? proposed.Width
            : control.MaximumSize.Width > 0 ? control.MaximumSize.Width
            : control.Parent?.ClientSize.Width ?? 300;
        if (!_heights.TryGetValue(width, out int height))
        {
            int inner = Math.Max(1, width - control.Padding.Horizontal);
            string text = control.Text.Length == 0 ? " " : control.Text;
            height = TextRenderer.MeasureText(text, control.Font, new Size(inner, int.MaxValue), Draw.Wrapped).Height + control.Padding.Vertical;
            _heights[width] = height;
        }
        return new Size(width, height);
    }
}

/// <summary>
/// The line under a primary button that says what just happened, with links such as "Open File".
/// Feedback stays next to the action that caused it.
/// </summary>
internal sealed class ResultLabel : LinkLabel
{
    readonly WrapMeasure _measure = new();
    Tone _tone = Tone.Secondary;

    public ResultLabel()
    {
        AutoSize = true;
        UseMnemonic = false;
        Font = AppFonts.Body;
        LinkBehavior = LinkBehavior.HoverUnderline;
        AccessibleRole = AccessibleRole.StaticText;
        Text = "";
        ApplyColors();
        Theme.Changed += OnThemeChanged;
    }

    public override Color ForeColor
    {
        get => Tones.Of(_tone);
        set { }
    }

    public void Show(string message, Tone tone, params (string Text, Action Run)[] actions)
    {
        const string Gap = "    ";
        _tone = tone;
        Text = message + string.Concat(actions.Select(action => Gap + action.Text));
        Links.Clear();
        int start = message.Length;
        foreach (var action in actions)
        {
            start += Gap.Length;
            Links.Add(start, action.Text.Length, action.Run);
            start += action.Text.Length;
        }
        AccessibleName = message;
        AccessibilityNotifyClients(AccessibleEvents.NameChange, -1);
        Invalidate();
    }

    public void Clear() => Show("", Tone.Secondary);

    void ApplyColors()
    {
        LinkColor = VisitedLinkColor = Theme.Current.AccentText;
        ActiveLinkColor = Theme.Current.Accent;
    }

    void OnThemeChanged(object? sender, EventArgs e) => ApplyColors();

    public override Size GetPreferredSize(Size proposedSize) =>
        Text.Length == 0 ? new Size(Math.Max(1, proposedSize.Width), Font.Height) : _measure.Size(this, proposedSize);

    protected override void OnTextChanged(EventArgs e) { _measure.Reset(); base.OnTextChanged(e); }

    protected override void OnLinkClicked(LinkLabelLinkClickedEventArgs e)
    {
        base.OnLinkClicked(e);
        (e.Link?.LinkData as Action)?.Invoke();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Theme.Changed -= OnThemeChanged;
        base.Dispose(disposing);
    }
}

/// <summary>A link-styled label that runs an action; for "Choose Folder…" and similar.</summary>
internal sealed class ActionLink : LinkLabel
{
    public ActionLink(string text, Action action)
    {
        Text = text;
        AutoSize = true;
        UseMnemonic = false;
        Font = AppFonts.Small;
        LinkBehavior = LinkBehavior.HoverUnderline;
        Margin = new Padding(0, 4, 12, 0);
        LinkClicked += (_, _) => action();
        ApplyColors();
        Theme.Changed += OnThemeChanged;
    }

    void ApplyColors() { LinkColor = VisitedLinkColor = Theme.Current.AccentText; ActiveLinkColor = Theme.Current.Accent; }

    void OnThemeChanged(object? sender, EventArgs e) => ApplyColors();

    protected override void Dispose(bool disposing)
    {
        if (disposing) Theme.Changed -= OnThemeChanged;
        base.Dispose(disposing);
    }
}
