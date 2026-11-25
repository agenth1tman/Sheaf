using Sheaf.Ui;
using Sheaf.Ui.Controls;

namespace Sheaf.Pages.Document;

/// <summary>
/// Renames a document: its file name (the file on disk too, once it is saved) and, for a PDF, the title stored in
/// it, which browsers and PDF readers show in their tab or title bar instead of the file name.
/// </summary>
internal sealed class RenameDialog : DialogForm
{
    static readonly char[] Invalid = Path.GetInvalidFileNameChars();
    readonly InputBox _name = Make.Input(maxLength: 200);
    readonly InputBox? _title;
    readonly FormField _nameField;

    RenameDialog(string name, string extension, string? title, bool saved, bool pdf)
        : base("Rename", saved ? $"Renames the file in its folder. Keep {extension} at the end, or leave it off." : "The name the file gets when you save it.", 420)
    {
        _name.Text = name;
        _nameField = new FormField("File name", _name);
        AddContent(_nameField, 18);
        if (pdf)
        {
            _title = Make.Input(maxLength: 300);
            _title.Text = title ?? "";
            _title.Placeholder = "Same as the file name";
            AddContent(new FormField("Title", _title, "Shown in browser tabs and PDF readers instead of the file name. Saved with the file."), 4);
        }
        AddButtons("Rename", () =>
        {
            string typed = _name.Text.Trim();
            if (typed.Length == 0) return _nameField.Check("Type a name.");
            if (typed.IndexOfAny(Invalid) >= 0) return _nameField.Check("A file name can't have \\ / : * ? \" < > or |.");
            if (typed.Trim('.').Length == 0) return _nameField.Check("Type a name.");
            return true;
        });
        Shown += (_, _) =>
        {
            _name.Focus();
            _name.Box.SelectAll();
        };
    }

    /// <summary>The new name (without the extension) and title (empty for none), or null when cancelled.</summary>
    public static (string Name, string? Title)? Ask(IWin32Window? owner, string name, string extension, string? title, bool saved, bool pdf)
    {
        using var dialog = new RenameDialog(name, extension, title, saved, pdf);
        if (dialog.ShowDialog(owner) != DialogResult.OK) return null;
        string typed = dialog._name.Text.Trim();
        if (typed.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) typed = typed[..^extension.Length].TrimEnd();
        return (typed, dialog._title?.Text.Trim());
    }
}
