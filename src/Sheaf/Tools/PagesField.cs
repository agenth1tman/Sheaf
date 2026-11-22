using Sheaf.Core.Pdf;
using Sheaf.Ui;
using Sheaf.Ui.Controls;

namespace Sheaf.Tools;

/// <summary>A "Pages" field that takes lists like 1-3, 5, 8-. Optional fields mean all pages when left empty.</summary>
internal sealed class PagesField
{
    readonly bool _required;
    IReadOnlyList<PageSpan> _spans = [];

    public PagesField(string caption, bool required)
    {
        _required = required;
        Input = Make.Input(required ? $"e.g. {PageRanges.Example}" : "All pages", maxLength: 500);
        Field = new FormField(caption, Input, required ? null : $"Leave empty for all pages, or list some: {PageRanges.Example}");
    }

    public FormField Field { get; }

    public InputBox Input { get; }

    /// <summary>Checks what was typed and shows the problem under the field. Returns the field when it's wrong.</summary>
    public Control? Validate()
    {
        if (!_required && string.IsNullOrWhiteSpace(Input.Text))
        {
            _spans = [];
            return Field.Check(null) ? null : Field;
        }
        bool parsed = PageRanges.TryParse(Input.Text, out _spans, out string error);
        return Field.Check(parsed ? null : error) ? null : Field;
    }

    /// <summary>
    /// Captures the validated pages on the UI thread and returns a function the worker thread can call
    /// with each document's page count to get zero-based page indexes.
    /// </summary>
    public Func<int, string, IReadOnlyList<int>> Snapshot()
    {
        IReadOnlyList<PageSpan> spans = _spans;
        return (pageCount, fileName) => spans.Count == 0
            ? PageOps.AllPages(pageCount)
            : PageRanges.ToIndexes(PageRanges.Fit(spans, pageCount, fileName));
    }

    /// <summary>The validated spans, for tools that treat each range separately.</summary>
    public IReadOnlyList<PageSpan> Spans => _spans;
}
