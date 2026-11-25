using Sheaf.Core;
using Sheaf.Core.Pdf;
using Sheaf.Ui;
using Sheaf.Ui.Controls;

namespace Sheaf.Pages.Document;

/// <summary>
/// Chooses which pages of a file to add, as a print dialog chooses which to print: all of them, the odd or the even
/// ones, or a list such as 1-3, 5, 8-.
/// </summary>
internal sealed class PagePickDialog : DialogForm
{
    const int All = 0, Odd = 1, Even = 2, Some = 3;
    readonly int _count;
    readonly ChoiceGroup<int> _which = new("Pages", horizontal: true, ("All", All), ("Odd", Odd), ("Even", Even), ("Some", Some));
    readonly InputBox _list = Make.Input($"e.g. {PageRanges.Example}", maxLength: 500);
    readonly FormField _listField;

    PagePickDialog(string fileName, int count)
        : base("Insert Pages", count == 1 ? $"{fileName} has 1 page." : $"{fileName} has {Words.Pages(count)}. Choose which to add.", 420)
    {
        _count = count;
        AddContent(new FormField("Pages", _which), 18);
        _listField = new FormField("Page numbers", _list, "A range without an end, such as 8-, runs to the last page.");
        AddContent(_listField, 12);
        // Typing numbers chooses Some; choosing Some puts the cursor in the box, once the click is over.
        _which.MouseUp += (_, _) => { if (_which.Value == Some) _list.Focus(); };
        _list.Box.TextChanged += (_, _) => { if (_list.Text.Trim().Length > 0) _which.Value = Some; };
        AddButtons("Insert", Check);
    }

    bool Check()
    {
        if (_which.Value == Even && _count < 2) return _listField.Check("This file has only one page, so it has no even pages.");
        if (_which.Value != Some) return _listField.Check(null);
        if (!PageRanges.TryParse(_list.Text, out IReadOnlyList<PageSpan> spans, out string error)) return _listField.Check(error);
        try { PageRanges.Fit(spans, _count, "This file"); }
        catch (ToolException ex) { return _listField.Check(ex.Message); }
        return _listField.Check(null);
    }

    /// <summary>The pages to add, counted from zero, in the order given; null when cancelled.</summary>
    public static IReadOnlyList<int>? Ask(IWin32Window? owner, string fileName, int count)
    {
        using var dialog = new PagePickDialog(fileName, count);
        if (dialog.ShowDialog(owner) != DialogResult.OK) return null;
        return dialog._which.Value switch
        {
            Odd => [.. Enumerable.Range(0, count).Where(page => page % 2 == 0)],
            Even => [.. Enumerable.Range(0, count).Where(page => page % 2 == 1)],
            Some when PageRanges.TryParse(dialog._list.Text, out IReadOnlyList<PageSpan> spans, out _) => PageRanges.ToIndexes(PageRanges.Fit(spans, count, fileName)),
            _ => [.. Enumerable.Range(0, count)],
        };
    }
}
