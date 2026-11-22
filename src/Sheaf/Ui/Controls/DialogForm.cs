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

    /// <summary>Adds the buttons, main one first; the main one closes with OK unless <paramref name="accept"/> says otherwise.</summary>
    protected AppButton AddButtons(string primary, Func<bool>? accept = null, string cancel = "Cancel")
    {
        var ok = new AppButton(primary, ButtonKind.Primary) { Margin = Padding.Empty };
        var no = new AppButton(cancel, ButtonKind.Secondary) { Margin = new Padding(0, 0, 8, 0), DialogResult = DialogResult.Cancel };
        ok.Click += (_, _) =>
        {
            if (accept is not null && !accept()) return;
            DialogResult = DialogResult.OK;
        };
        _buttons.Controls.Add(ok);
        _buttons.Controls.Add(no);
        _layout.Controls.Add(_buttons);
        AcceptButton = ok;
        CancelButton = no;
        return ok;
    }

    /// <summary>The window wraps the content (which grows when an error line appears).</summary>
    void FitToContent() =>
        ClientSize = new Size(_layout.Right + Padding.Right, _layout.Bottom + Padding.Bottom);

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        _layout.Location = new Point(Padding.Left, Padding.Top);
        FitToContent();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.SetDarkTitleBar(Handle, Theme.IsDark);
    }
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
