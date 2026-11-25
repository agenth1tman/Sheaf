namespace Sheaf.Core.Editing;

/// <summary>How one stretch of a text box's text looks.</summary>
public readonly record struct TextStyle(bool Bold, bool Italic, bool Underline, int Color);

/// <summary>A stretch of one style on a laid-out line, at its distance from the left of the text area.</summary>
public sealed record TextPiece(string Text, TextStyle Style, float X, float Width);

/// <summary>A laid-out line: where its top is below the top of the text area, its height, and its pieces.</summary>
public sealed record TextLine(float Top, float Height, IReadOnlyList<TextPiece> Pieces);

/// <summary>
/// Lays out a text box whose words have styles of their own: wraps it at word boundaries, aligns or justifies each
/// line, and places each stretch of one style. The editor and the PDF writer measure with their own fonts and
/// share this, so a bold word wraps at the same place in both.
/// </summary>
public static class TextLayout
{
    /// <summary>The text in stretches of one style; a box without spans is one stretch in the box's own style.</summary>
    public static List<(string Text, TextStyle Style)> Runs(PageObject item)
    {
        var plain = new TextStyle(item.Bold, item.Italic, false, item.StrokeColor);
        if (item.Spans.Count == 0) return [(item.Text, plain)];
        var runs = new List<(string, TextStyle)>();
        int at = 0;
        foreach (TextSpan span in item.Spans)
        {
            if (at >= item.Text.Length) break;
            int length = Math.Min(span.Length, item.Text.Length - at);
            if (length > 0) runs.Add((item.Text.Substring(at, length), new TextStyle(span.Bold, span.Italic, span.Underline, span.Color)));
            at += length;
        }
        // Text typed past the last span (an older layer) takes the box's style.
        if (at < item.Text.Length) runs.Add((item.Text[at..], plain));
        return runs;
    }

    /// <summary>Whether a box has stretches of more than one style.</summary>
    public static bool IsMixed(PageObject item) => item.Spans.Select(span => (span.Bold, span.Italic, span.Underline, span.Color)).Distinct().Count() > 1
        || item.Spans.Any(span => span.Underline);

    /// <summary>
    /// Wraps and places the text in <paramref name="width"/> points. <paramref name="measure"/> gives the width of
    /// some text in a style (spaces included); <paramref name="lineHeight"/> the height of a line in a style.
    /// </summary>
    public static List<TextLine> Lay(PageObject item, float width, Func<string, TextStyle, float> measure, Func<TextStyle, float> lineHeight)
    {
        var lines = new List<TextLine>();
        float top = 0;
        foreach (List<Token> paragraph in Paragraphs(Runs(item)))
        {
            TextStyle first = paragraph.FirstOrDefault()?.Parts[0].Style ?? new TextStyle(item.Bold, item.Italic, false, item.StrokeColor);
            if (paragraph.Count == 0)
            {
                float empty = lineHeight(first);
                lines.Add(new TextLine(top, empty, []));
                top += empty;
                continue;
            }
            foreach (Token token in paragraph) token.Width = token.Parts.Sum(part => measure(part.Text, part.Style));
            int start = 0;
            while (start < paragraph.Count)
            {
                // Spaces at the start of a wrapped line are dropped, as they are when text wraps anywhere.
                if (lines.Count > 0 && start > 0) while (start < paragraph.Count && paragraph[start].IsSpace) start++;
                if (start >= paragraph.Count) break;
                int end = start;
                float used = paragraph[start].Width;
                while (end + 1 < paragraph.Count)
                {
                    Token next = paragraph[end + 1];
                    if (!next.IsSpace && used + next.Width > width && used > 0) break;
                    used += next.Width;
                    end++;
                }
                // Trailing spaces don't count towards the line's width.
                int last = end;
                while (last > start && paragraph[last].IsSpace) last--;
                bool final = end + 1 >= paragraph.Count;
                lines.Add(Place(paragraph, start, last, final, width, item.Align, measure, lineHeight, top));
                top += lines[^1].Height;
                start = end + 1;
            }
        }
        return lines;
    }

    static TextLine Place(List<Token> tokens, int start, int last, bool final, float width, TextAlign align,
        Func<string, TextStyle, float> measure, Func<TextStyle, float> lineHeight, float top)
    {
        float content = 0;
        int spaces = 0;
        for (int i = start; i <= last; i++)
        {
            content += tokens[i].Width;
            if (tokens[i].IsSpace) spaces++;
        }
        float spare = Math.Max(0, width - content);
        bool justify = align == TextAlign.Justify && !final && spaces > 0;
        float x = align switch
        {
            TextAlign.Center => spare / 2,
            TextAlign.Right => spare,
            _ => 0,
        };
        float stretch = justify ? spare / spaces : 0;
        var pieces = new List<TextPiece>();
        float height = 0;
        for (int i = start; i <= last; i++)
        {
            Token token = tokens[i];
            foreach (var (text, style) in token.Parts)
            {
                float w = measure(text, style);
                height = Math.Max(height, lineHeight(style));
                if (!token.IsSpace) pieces.Add(new TextPiece(text, style, x, w));
                else if (style.Underline) pieces.Add(new TextPiece(text, style, x, w + (justify ? stretch : 0)));
                x += w;
            }
            if (token.IsSpace && justify) x += stretch;
        }
        return new TextLine(top, height, pieces);
    }

    /// <summary>A word or a run of spaces, made of stretches of one style each.</summary>
    sealed class Token(bool isSpace)
    {
        public bool IsSpace { get; } = isSpace;
        public List<(string Text, TextStyle Style)> Parts { get; } = [];
        public float Width { get; set; }
    }

    /// <summary>The text split into paragraphs at line breaks, each a list of words and spaces.</summary>
    static List<List<Token>> Paragraphs(List<(string Text, TextStyle Style)> runs)
    {
        var paragraphs = new List<List<Token>> { new() };
        foreach (var (text, style) in runs)
        {
            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];
                if (c == '\n')
                {
                    paragraphs.Add([]);
                    i++;
                    continue;
                }
                bool space = c == ' ';
                int j = i;
                while (j < text.Length && text[j] != '\n' && (text[j] == ' ') == space) j++;
                List<Token> current = paragraphs[^1];
                // A word whose style changes part way goes on as one token, so it never breaks at the change.
                if (current.Count == 0 || current[^1].IsSpace != space) current.Add(new Token(space));
                current[^1].Parts.Add((text[i..j], style));
                i = j;
            }
        }
        return paragraphs;
    }
}
