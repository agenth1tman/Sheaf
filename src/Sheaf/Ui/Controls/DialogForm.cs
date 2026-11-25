using Sheaf.Platform;

namespace Sheaf.Ui.Controls;

/// <summary>
/// A small modal dialog in the app's style: a heading, a line of explanation, the content and a row of buttons
/// with the main one on the right. Enter presses the main button and Esc cancels.
/// </summary>
internal class DialogForm : Form
{
    readonly TableLayoutPanel _layout;
    readonly FlowLayoutPanel _buttons;

    /// <summary>Takes the keyboard when a click lands on the dialog's background, so a text box is left by clicking away from it.</summary>
    readonly Control _focusSink = new() { Size = Size.Empty, TabStop = false, AccessibleRole = AccessibleRole.Pane };

    protected DialogForm(string title, string? explanation, int width = 420)
    {
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = AppFonts.Body;
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        ShowIcon = false;
        StartPosition = FormStartPosition.CenterParent;
        Padding = new Padding(24, 20, 24, 20);

        _layout = Make.Stack(Make.Title(title));
        _layout.Anchor = AnchorStyles.Top | AnchorStyles.Left;
        _layout.Location = new Point(Padding.Left, Padding.Top);
        _layout.Width = width;
        _layout.MinimumSize = new Size(width, 0);
        _layout.MaximumSize = new Size(width, 0);
        _layout.SizeChanged += (_, _) => FitToContent();
        if (explanation is not null) _layout.Controls.Add(Make.Paragraph(explanation));
        _buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.Right,
            Margin = new Padding(0, 20, 0, 0),
            WrapContents = false,
        };
        Controls.Add(_layout);
        Controls.Add(_focusSink);
    }

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

    protected void AddContent(Control control, int topSpace = 16)
    {
        control.Margin = new Padding(0, topSpace, 0, 0);
        control.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _layout.Controls.Add(control);
    }

    /// <summary>
    /// Adds the buttons, main one first; the main one closes with OK unless <paramref name="accept"/> says otherwise.
    /// Without a <paramref name="cancel"/> button, Escape closes the dialog as the main one does.
    /// </summary>
    protected AppButton AddButtons(string primary, Func<bool>? accept = null, string? cancel = "Cancel")
    {
        var ok = new AppButton(primary, ButtonKind.Primary) { Margin = Padding.Empty };
        ok.Click += (_, _) =>
        {
            if (accept is not null && !accept()) return;
            DialogResult = DialogResult.OK;
        };
        _buttons.Controls.Add(ok);
        AcceptButton = ok;
        CancelButton = ok;
        if (cancel is not null)
        {
            var no = new AppButton(cancel, ButtonKind.Secondary) { Margin = new Padding(0, 0, 8, 0), DialogResult = DialogResult.Cancel };
            _buttons.Controls.Add(no);
            CancelButton = no;
        }
        _layout.Controls.Add(_buttons);
        return ok;
    }

    /// <summary>Adds another answer, to the left of the others, that closes the dialog with <paramref name="result"/>.</summary>
    protected AppButton AddButton(string text, DialogResult result)
    {
        var button = new AppButton(text, ButtonKind.Secondary) { Margin = new Padding(0, 0, 8, 0), DialogResult = result };
        _buttons.Controls.Add(button);
        return button;
    }

    /// <summary>The window wraps the content (which grows when an error line appears).</summary>
    void FitToContent() =>
        ClientSize = new Size(_layout.Right + Padding.Right, _layout.Bottom + Padding.Bottom);

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        _layout.Location = new Point(Padding.Left, Padding.Top);
        FitToContent();
        LeaveFieldsOnClick(this);
    }

    /// <summary>A click on anything that isn't a control of its own (the background, headings, captions) leaves a text box.</summary>
    void LeaveFieldsOnClick(Control parent)
    {
        parent.MouseDown += (_, _) => { if (Typing(this)) _focusSink.Focus(); };
        foreach (Control child in parent.Controls)
            if (child is not (InputBox or TextBoxBase or ButtonBase)) LeaveFieldsOnClick(child);
    }

    static bool Typing(Control control) => control is TextBoxBase { Focused: true } || control.Controls.Cast<Control>().Any(Typing);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.SetDarkTitleBar(Handle, Theme.IsDark);
    }
}

/// <summary>
/// A question or a notice in Sheaf's style, in place of Windows' own message box: a heading, a line of detail, and up
/// to three answers: the main one (OK), another (No) and Cancel.
/// </summary>
internal sealed class AskDialog : DialogForm
{
    AskDialog(string heading, string text, string primary, string? cancel, string? other, bool safeDefault)
        : base(heading, text, 400)
    {
        AddButtons(primary, cancel: cancel);
        if (other is not null) AddButton(other, DialogResult.No);
        // For an answer that can't be taken back, Enter means Cancel; the main button needs a click.
        if (safeDefault && CancelButton is AppButton safe)
        {
            AcceptButton = safe;
            Shown += (_, _) => safe.Focus();
        }
    }

    /// <summary>OK for the main answer, No for the other one, Cancel for Cancel, Escape or the close box.</summary>
    public static DialogResult Ask(IWin32Window? owner, string heading, string text, string primary, string? cancel = "Cancel", string? other = null, bool safeDefault = false)
    {
        using var dialog = new AskDialog(heading, text, primary, cancel, other, safeDefault);
        return dialog.ShowDialog(owner);
    }

    /// <summary>A notice with one button.</summary>
    public static void Tell(IWin32Window? owner, string heading, string text) => Ask(owner, heading, text, "OK", cancel: null);
}

/// <summary>Asks for the password of a protected PDF.</summary>
internal sealed class PasswordDialog : DialogForm
{
    readonly InputBox _password = new() { IsPassword = true, Caption = "Password" };

    PasswordDialog(string fileName, bool retry)
        : base("Enter the Password", $"{fileName} is protected. Enter its password to open it.")
    {
        var field = new FormField("Password", _password, retry ? null : "Sheaf removes the password only from its own working copy, never from the file.");
        AddContent(field);
        if (retry) field.Check("That password didn’t work. Check it and try again.");
        AddButtons("Open", () =>
        {
            if (_password.Text.Length > 0) return true;
            field.Check("Enter the password.");
            return false;
        });
        Shown += (_, _) => _password.Focus();
    }

    public static string? Ask(IWin32Window? owner, string fileName, bool retry)
    {
        using var dialog = new PasswordDialog(fileName, retry);
        return dialog.ShowDialog(owner) == DialogResult.OK ? dialog._password.Text : null;
    }
}
