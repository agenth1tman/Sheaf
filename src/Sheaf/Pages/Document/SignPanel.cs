using Sheaf.Core.Editing;
using Sheaf.Platform;
using Sheaf.Ui;
using Sheaf.Ui.Controls;
using FieldRow = Sheaf.Ui.Controls.FormField;
using FormField = Sheaf.Core.Editing.FormField;

namespace Sheaf.Pages.Document;

/// <summary>
/// The Fill &amp; Sign panel: the saved signature (create, change or remove it) and every field of the PDF's
/// form, each with an input. A field's new value is one undo step.
/// </summary>
internal sealed class SignPanel : Surface
{
    readonly SignaturePreview _preview = new() { Height = 76, Anchor = AnchorStyles.Left | AnchorStyles.Right, EmptyText = "No signature yet" };
    readonly AppButton _change = new("Create Signature…", ButtonKind.Secondary, "signature");
    readonly AppButton _remove = new("Remove", ButtonKind.Quiet);
    readonly Surface _fieldsHost = new() { Dock = DockStyle.Fill, AutoScroll = true };
    readonly Dictionary<string, Control> _inputs = [];
    IReadOnlyList<FormField> _fields = [];
    bool _syncing;

    public SignPanel() : base(SurfaceRole.Panel)
    {
        Borders = AnchorStyles.Left;
        AccessibleRole = AccessibleRole.Pane;
        AccessibleName = "Fill and sign";
        _preview.Margin = new Padding(0, 0, 0, 10);
        _change.Margin = new Padding(0, 0, 8, 0);
        var signature = Make.PanelSection("Your signature", _preview, Make.Row(_change, _remove),
            Make.Hint("Pick Signature on the left, then click where it goes."));
        Controls.Add(_fieldsHost);
        Controls.Add(new Divider { Dock = DockStyle.Top, Margin = Padding.Empty });
        Controls.Add(signature);

        _change.Click += (_, _) => CreateSignature();
        _remove.Click += (_, _) => { SignatureStore.Remove(); ShowSignature(); };
        ShowSignature();
    }

    /// <summary>A field got a new value: its name and the value.</summary>
    public event EventHandler<KeyValuePair<string, string>>? FieldChanged;

    /// <summary>Asks for a new signature, keeps it, and returns it; null when cancelled.</summary>
    public PageObject? CreateSignature()
    {
        PageObject? signature = SignatureDialog.Create(FindForm());
        if (signature is null) return null;
        SignatureStore.Save(signature);
        ShowSignature();
        return signature;
    }

    void ShowSignature()
    {
        PageObject? signature = SignatureStore.Load();
        _preview.Signature = signature;
        _change.Text = signature is null ? "Create Signature…" : "Change…";
        _remove.Visible = signature is not null;
    }

    /// <summary>Builds the inputs for a document's fields (once per document) and shows their current values.</summary>
    public void Sync(EditDocument document)
    {
        if (!ReferenceEquals(_fields, document.Fields)) Build(document.Fields);
        _syncing = true;
        foreach (FormField field in _fields)
        {
            string value = document.FieldValues.GetValueOrDefault(field.Name, field.Value);
            switch (_inputs[field.Name])
            {
                case InputBox box when !box.Box.Focused: box.Text = value; break;
                case Check check: check.Checked = value == "true"; break;
                case DropDown<string> choice: choice.Value = value; break;
            }
        }
        _syncing = false;
    }

    void Build(IReadOnlyList<FormField> fields)
    {
        _fields = fields;
        _inputs.Clear();
        _fieldsHost.SuspendLayout();
        foreach (Control control in _fieldsHost.Controls.Cast<Control>().ToList()) control.Dispose();
        if (fields.Count == 0)
        {
            _fieldsHost.Controls.Add(Make.PanelSection("Form fields",
                Make.Hint("This PDF has no fillable fields. Use Text, Date, Tick and Cross on the left to fill it in.")));
        }
        else
        {
            var controls = new List<Control>();
            foreach (FormField field in fields.OrderBy(field => field.SourcePage).ThenBy(field => field.Bounds.Y).ThenBy(field => field.Bounds.X))
            {
                Control input = InputFor(field);
                input.Enabled = !field.ReadOnly;
                _inputs[field.Name] = input;
                controls.Add(field.Kind == FieldKind.CheckBox ? input : new FieldRow(field.Label, input));
            }
            _fieldsHost.Controls.Add(Make.PanelSection($"Form fields · {fields.Count}", [.. controls]));
        }
        _fieldsHost.ResumeLayout(true);
    }

    Control InputFor(FormField field)
    {
        switch (field.Kind)
        {
            case FieldKind.CheckBox:
                var check = new Check(field.Label) { Margin = new Padding(0, 4, 0, 10) };
                check.CheckedChanged += (_, _) => Changed(field, check.Checked ? "true" : "false");
                return check;
            case FieldKind.Choice or FieldKind.Radio:
                var options = new List<(string, string)> { ("None", "") };
                options.AddRange(field.Options.Where(option => option.Length > 0).Select(option => (option, option)));
                var choice = new DropDown<string>(field.Label, [.. options]) { Stretch = true };
                choice.Changed += (_, _) => Changed(field, choice.Value);
                return choice;
            default:
                var box = new InputBox { Caption = field.Label };
                box.EnterPressed += (_, _) => Changed(field, box.Text);
                box.Box.Leave += (_, _) => Changed(field, box.Text);
                return box;
        }
    }

    void Changed(FormField field, string value)
    {
        if (_syncing) return;
        FieldChanged?.Invoke(this, new(field.Name, value));
    }

    /// <summary>Puts the cursor in a field's input, after a click on the field on the page.</summary>
    public void FocusField(string name)
    {
        if (!_inputs.TryGetValue(name, out Control? input)) return;
        _fieldsHost.ScrollControlIntoView(input.Parent ?? input);
        if (input is InputBox box) box.Focus();
        else input.Focus();
    }
}
