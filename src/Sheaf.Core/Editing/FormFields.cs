using System.Drawing;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.AcroForms;
using PdfSharp.Pdf.Advanced;
using Sheaf.Core.Fonts;
using Sheaf.Core.Pdf;

namespace Sheaf.Core.Editing;

public enum FieldKind { Text, CheckBox, Choice, Radio }

/// <summary>
/// A fillable field of the opened PDF. <see cref="Bounds"/> is its first widget, in the displayed coordinates of
/// page <see cref="SourcePage"/> of the opened file. <see cref="Value"/> is what the file holds; check boxes use
/// "true" and "false", and a radio group's options are its buttons' names.
/// </summary>
public sealed record FormField(
    string Name, string Label, FieldKind Kind, string Value, IReadOnlyList<string> Options,
    int SourcePage, RectangleF Bounds, bool Multiline, bool ReadOnly);

/// <summary>Reads the fields of a PDF form and writes new values into them.</summary>
public static class FormFields
{
    const int ReadOnlyFlag = 1, MultilineFlag = 1 << 12, RadioFlag = 1 << 15, PushbuttonFlag = 1 << 16;

    sealed record Terminal(string Name, PdfDictionary Field, string Type, int Flags, List<PdfDictionary> Widgets);

    public static List<FormField> Read(PdfDocument document)
    {
        var fields = new List<FormField>();
        if (document.Internals.Catalog.Elements.GetDictionary("/AcroForm")?.Elements.GetArray("/Fields") is not { } roots) return fields;
        var terminals = new List<Terminal>();
        for (int i = 0; i < roots.Elements.Count; i++)
            if (roots.Elements.GetDictionary(i) is { } root) Walk(root, "", "", 0, terminals);

        foreach (Terminal terminal in terminals)
        {
            if (fields.Any(field => field.Name == terminal.Name)) continue;
            if (Describe(terminal) is not (FieldKind kind, string value, List<string> options)) continue;
            PdfDictionary widget = terminal.Widgets[0];
            if (PageOf(document, widget) is not int pageIndex) continue;
            PdfPage page = document.Pages[pageIndex];
            string label = terminal.Field.Elements.GetString("/TU") is { Length: > 0 } tooltip ? tooltip : terminal.Name;
            fields.Add(new FormField(terminal.Name, label, kind, value, options, pageIndex, VisualBounds(page, widget),
                (terminal.Flags & MultilineFlag) != 0, (terminal.Flags & ReadOnlyFlag) != 0));
        }
        return fields;
    }

    /// <summary>Sets each named field to its new value; returns how many fields changed.</summary>
    public static int Apply(PdfDocument document, IReadOnlyDictionary<string, string> values)
    {
        PdfDictionary? form = document.Internals.Catalog.Elements.GetDictionary("/AcroForm");
        if (form?.Elements.GetArray("/Fields") is not { } roots || values.Count == 0) return 0;
        var terminals = new List<Terminal>();
        for (int i = 0; i < roots.Elements.Count; i++)
            if (roots.Elements.GetDictionary(i) is { } root) Walk(root, "", "", 0, terminals);

        int changed = 0;
        foreach (Terminal terminal in terminals)
        {
            if (!values.TryGetValue(terminal.Name, out string? value)) continue;
            switch (terminal.Type)
            {
                case "/Tx": SetText(document, terminal, value); break;
                case "/Btn": SetButton(terminal, value); break;
                case "/Ch": terminal.Field.Elements.SetString("/V", value); break;
                default: continue;
            }
            changed++;
        }
        if (changed > 0) form.Elements.SetBoolean("/NeedAppearances", true);
        return changed;
    }

    static void Walk(PdfDictionary field, string prefix, string type, int flags, List<Terminal> found)
    {
        string partial = field.Elements.GetString("/T");
        string name = partial.Length == 0 ? prefix : prefix.Length == 0 ? partial : $"{prefix}.{partial}";
        if (field.Elements.GetName("/FT") is { Length: > 0 } ownType) type = ownType;
        if (field.Elements.ContainsKey("/Ff")) flags = field.Elements.GetInteger("/Ff");
        var kids = new List<PdfDictionary>();
        if (field.Elements.GetArray("/Kids") is { } array)
            for (int i = 0; i < array.Elements.Count; i++)
                if (array.Elements.GetDictionary(i) is { } kid) kids.Add(kid);

        var childFields = kids.Where(kid => kid.Elements.ContainsKey("/T")).ToList();
        if (childFields.Count > 0)
        {
            foreach (PdfDictionary child in childFields) Walk(child, name, type, flags, found);
            return;
        }
        if (name.Length == 0) return;
        found.Add(new Terminal(name, field, type, flags, kids.Count > 0 ? kids : [field]));
    }

    static (FieldKind, string, List<string>)? Describe(Terminal terminal)
    {
        PdfItem? value = terminal.Field.Elements["/V"];
        switch (terminal.Type)
        {
            case "/Tx":
                return (FieldKind.Text, TextOf(value), []);
            case "/Ch":
                var options = new List<string>();
                if (terminal.Field.Elements.GetArray("/Opt") is { } opt)
                    for (int i = 0; i < opt.Elements.Count; i++)
                        options.Add(opt.Elements[i] is PdfArray pair ? TextOf(pair) : TextOf(opt.Elements[i]));
                return (FieldKind.Choice, TextOf(value), options);
            case "/Btn" when (terminal.Flags & PushbuttonFlag) != 0:
                return null;
            case "/Btn" when (terminal.Flags & RadioFlag) != 0:
                var states = terminal.Widgets.Select(OnState).OfType<string>().Distinct().ToList();
                string chosen = value is PdfName name && name.Value != "/Off" ? name.Value.TrimStart('/') : "";
                return (FieldKind.Radio, chosen, states);
            case "/Btn":
                bool on = value is PdfName checkedName && checkedName.Value != "/Off";
                return (FieldKind.CheckBox, on ? "true" : "false", []);
            default:
                return null;
        }
    }

    static string TextOf(PdfItem? item) => item switch
    {
        PdfString text => text.Value,
        PdfName name => name.Value.TrimStart('/'),
        PdfReference { Value: PdfStringObject text } => text.Value,
        PdfArray { Elements.Count: > 0 } array => TextOf(array.Elements[0]),
        _ => "",
    };

    /// <summary>The name of a check box or radio button's "on" appearance, such as "Yes" or "Choice2".</summary>
    static string? OnState(PdfDictionary widget) =>
        widget.Elements.GetDictionary("/AP")?.Elements.GetDictionary("/N")?.Elements.Keys.FirstOrDefault(key => key != "/Off")?.TrimStart('/');

    static void SetText(PdfDocument document, Terminal terminal, string value)
    {
        // PDFsharp draws the new appearance for simple text fields; others get the value and the reader draws it.
        if (terminal.Widgets.Count == 1 && document.AcroForm?.Fields[terminal.Name] is PdfTextField field)
        {
            try
            {
                field.Font = new XFont(EmbeddedFonts.PdfFamily, FontSize(terminal.Field));
                field.Text = value;
                return;
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NullReferenceException) { }
        }
        terminal.Field.Elements.SetString("/V", value);
    }

    /// <summary>The size in the field's default appearance ("/Helv 11 Tf"), or 10 when it is automatic (0).</summary>
    static double FontSize(PdfDictionary field)
    {
        var match = System.Text.RegularExpressions.Regex.Match(field.Elements.GetString("/DA"), @"(\d+(?:\.\d+)?)\s+Tf");
        return match.Success && double.TryParse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out double size) && size > 0 ? size : 10;
    }

    static void SetButton(Terminal terminal, string value)
    {
        bool radio = (terminal.Flags & RadioFlag) != 0;
        string? chosen = radio ? (value.Length > 0 ? value : null) : value == "true" ? terminal.Widgets.Select(OnState).FirstOrDefault(state => state is not null) ?? "Yes" : null;
        terminal.Field.Elements.SetName("/V", chosen is null ? "/Off" : "/" + chosen);
        foreach (PdfDictionary widget in terminal.Widgets)
        {
            string? state = OnState(widget);
            widget.Elements.SetName("/AS", chosen is not null && state == chosen ? "/" + chosen : "/Off");
        }
    }

    static int? PageOf(PdfDocument document, PdfDictionary widget)
    {
        if (widget.Elements["/P"] is PdfReference pageReference)
            for (int i = 0; i < document.PageCount; i++)
                if (document.Pages[i].Reference?.ObjectID == pageReference.ObjectID) return i;
        for (int i = 0; i < document.PageCount; i++)
        {
            PdfArray? annotations = document.Pages[i].Elements.GetArray("/Annots");
            if (annotations is null) continue;
            for (int j = 0; j < annotations.Elements.Count; j++)
                if (ReferenceEquals(annotations.Elements.GetDictionary(j), widget)) return i;
        }
        return null;
    }

    static RectangleF VisualBounds(PdfPage page, PdfDictionary widget)
    {
        PdfRectangle rect = widget.Elements.GetRectangle("/Rect");
        XPoint a = PageCanvas.FromPdf(page, new XPoint(rect.X1, rect.Y1)), b = PageCanvas.FromPdf(page, new XPoint(rect.X2, rect.Y2));
        return RectangleF.FromLTRB((float)Math.Min(a.X, b.X), (float)Math.Min(a.Y, b.Y), (float)Math.Max(a.X, b.X), (float)Math.Max(a.Y, b.Y));
    }
}
