using System.Globalization;
using System.Text.RegularExpressions;

namespace Sheaf.Core.Pdf;

/// <summary>Pages <see cref="First"/> to <see cref="Last"/>, 1-based and inclusive.</summary>
public readonly record struct PageSpan(int First, int Last)
{
    /// <summary>Marks a range written without an end ("5-"), which runs to the last page.</summary>
    public const int ToEnd = int.MaxValue;

    public override string ToString() =>
        First == Last ? $"{First}" : Last == ToEnd ? $"{First}-end" : $"{First}-{Last}";
}

/// <summary>Reads page lists such as "1-3, 5, 9-" the way people type them.</summary>
public static partial class PageRanges
{
    public const string Example = "1-3, 5, 8-";

    public static bool TryParse(string? text, out IReadOnlyList<PageSpan> spans, out string error)
    {
        spans = [];
        error = "";
        if (string.IsNullOrWhiteSpace(text))
        {
            error = $"Enter the pages, for example {Example}.";
            return false;
        }

        var parsed = new List<PageSpan>();
        foreach (string token in Tokens(text))
        {
            if (!TryParseToken(token, out PageSpan span, out error)) return false;
            parsed.Add(span);
        }
        spans = parsed;
        return true;
    }

    /// <summary>Checks the spans against a document and closes open-ended ranges at its last page.</summary>
    public static IReadOnlyList<PageSpan> Fit(IReadOnlyList<PageSpan> spans, int pageCount, string fileName)
    {
        var fitted = new List<PageSpan>(spans.Count);
        foreach (PageSpan span in spans)
        {
            int lastAsked = span.Last == PageSpan.ToEnd ? span.First : span.Last;
            if (lastAsked > pageCount)
                throw new ToolException($"“{fileName}” has {Words.Pages(pageCount)}, so there’s no page {lastAsked}.");
            fitted.Add(span with { Last = Math.Min(span.Last, pageCount) });
        }
        return fitted;
    }

    /// <summary>Zero-based page indexes in the order given, each page once.</summary>
    public static IReadOnlyList<int> ToIndexes(IEnumerable<PageSpan> fittedSpans)
    {
        var seen = new HashSet<int>();
        var indexes = new List<int>();
        foreach (PageSpan span in fittedSpans)
            for (int page = span.First; page <= span.Last; page++)
                if (seen.Add(page - 1)) indexes.Add(page - 1);
        return indexes;
    }

    static IEnumerable<string> Tokens(string text)
    {
        string normalized = DashWithSpaces().Replace(text.Replace('–', '-').Replace('—', '-'), "-");
        return normalized.Split([',', ';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
    }

    static bool TryParseToken(string token, out PageSpan span, out string error)
    {
        span = default;
        error = $"“{token}” isn’t a page or a range. Use numbers like {Example}.";
        string[] parts = token.Split('-');
        if (parts.Length > 2) return false;

        bool isRange = parts.Length == 2;
        if (!TryPage(parts[0], isRange ? 1 : -1, out int first)) return false;
        if (!TryPage(isRange ? parts[1] : parts[0], isRange ? PageSpan.ToEnd : -1, out int last)) return false;

        if (first < 1 || last < 1)
        {
            error = "Page numbers start at 1.";
            return false;
        }
        if (first > last)
        {
            error = $"“{token}” runs backwards. Write it as {last}-{first}.";
            return false;
        }
        span = new PageSpan(first, last);
        error = "";
        return true;
    }

    static bool TryPage(string text, int valueWhenEmpty, out int page)
    {
        page = valueWhenEmpty;
        if (text.Length == 0) return valueWhenEmpty > 0;
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out page);
    }

    [GeneratedRegex(@"\s*-\s*")]
    private static partial Regex DashWithSpaces();
}
