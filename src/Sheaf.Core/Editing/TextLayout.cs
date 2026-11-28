namespace Sheaf.Core.Editing;

/// <summary>How one stretch of a text box's text looks: a <see cref="Font"/> of null is the box's own.</summary>
public readonly record struct TextStyle(bool Bold, bool Italic, bool Underline, int Color, TextScript Script = TextScript.Normal, string? Font = null);

/// <summary>
/// A stretch of one style on a laid-out line, at its distance from the left of the text area. A <see cref="Bar"/>
/// piece has no text: it is a fraction's bar, drawn across its width.
/// </summary>
public sealed record TextPiece(string Text, TextStyle Style, float X, float Width, bool Bar = false);

/// <summary>
/// A laid-out line: where its top is below the top of the text area, its height, and its pieces. <see cref="Lead"/> is
/// room above the line's text for the tops of fractions on it; the text itself starts that far down.
/// </summary>
public sealed record TextLine(float Top, float Height, IReadOnlyList<TextPiece> Pieces, float Lead = 0);

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
            if (length > 0)
                runs.Add((item.Text.Substring(at, length), new TextStyle(span.Bold, span.Italic, span.Underline, span.Color, span.Script, span.Font.Length > 0 ? span.Font : null)));
            at += length;
        }
        // Text typed past the last span (an older layer) takes the box's style.
        if (at < item.Text.Length) runs.Add((item.Text[at..], plain));
        return runs;
    }

    /// <summary>Whether a box has stretches of more than one style.</summary>
    public static bool IsMixed(PageObject item) => item.Spans.Select(span => (span.Bold, span.Italic, span.Underline, span.Color)).Distinct().Count() > 1
        || item.Spans.Any(span => span.Underline || span.Script != TextScript.Normal || span.Font.Length > 0);

    /// <summary>How much smaller a raised or lowered stretch is than the text round it.</summary>
    public const float ScriptScale = 0.62f;

    /// <summary>The parts of a fraction are a little smaller than the text round them, so a fraction keeps near its line.</summary>
    public const float FractionScale = 0.72f;

    /// <summary>The font family a stretch is drawn in.</summary>
    public static string FamilyOf(PageObject item, TextStyle style) => string.IsNullOrEmpty(style.Font) ? item.FontFamily : style.Font;

    /// <summary>The size a stretch is drawn at, in points.</summary>
    public static float SizeOf(PageObject item, TextStyle style) => Math.Max(1, item.FontSize) * style.Script switch
    {
        TextScript.Normal => 1,
        TextScript.Over or TextScript.Under => FractionScale,
        _ => ScriptScale,
    };

    /// <summary>
    /// How far below the top of its line a stretch is drawn, in points. Drawn from the top, smaller text already sits
    /// high; a subscript is lowered so its letters hang below the line's.
    /// </summary>
    public static float Drop(PageObject item, TextStyle style) => Math.Max(1, item.FontSize) * style.Script switch
    {
        TextScript.Sub => 0.5f,
        TextScript.Over => -0.17f,
        TextScript.Under => 0.68f,
        _ => 0,
    };

    /// <summary>How far below the top of a line's text a fraction's bar is, in points (where a minus sign sits).</summary>
    public static float BarDrop(PageObject item) => Math.Max(1, item.FontSize) * 0.62f;

    /// <summary>How thick a fraction's bar is, in points.</summary>
    public static float BarThickness(PageObject item) => Math.Max(0.5f, item.FontSize * 0.05f);

    /// <summary>
    /// The width of stretches side by side, where a fraction (an Over stretch and the Under one after it) takes the
    /// width of its wider part and a little room either side.
    /// </summary>
    static float Width(IReadOnlyList<(string Text, TextStyle Style)> parts, Func<string, TextStyle, float> measure, float fontSize)
    {
        float total = 0;
        for (int i = 0; i < parts.Count; i++)
        {
            if (parts[i].Style.Script == TextScript.Over && i + 1 < parts.Count && parts[i + 1].Style.Script == TextScript.Under)
            {
                total += Math.Max(measure(parts[i].Text, parts[i].Style), measure(parts[i + 1].Text, parts[i + 1].Style)) + FractionPad(fontSize) * 2;
                i++;
                continue;
            }
            total += measure(parts[i].Text, parts[i].Style);
        }
        return total;
    }

    static float FractionPad(float fontSize) => Math.Max(1, fontSize) * 0.1f;

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
            foreach (Token token in paragraph) token.Width = Width(token.Parts, measure, item.FontSize);
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
                lines.Add(Place(paragraph, start, last, final, width, item.Align, item.FontSize, measure, lineHeight, top));
                top += lines[^1].Height;
                start = end + 1;
            }
        }
        return lines;
    }

    static TextLine Place(List<Token> tokens, int start, int last, bool final, float width, TextAlign align, float fontSize,
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
        bool fractions = false;
        for (int i = start; i <= last; i++)
        {
            Token token = tokens[i];
            for (int p = 0; p < token.Parts.Count; p++)
            {
                var (text, style) = token.Parts[p];
                height = Math.Max(height, lineHeight(style));
                if (style.Script == TextScript.Over && p + 1 < token.Parts.Count && token.Parts[p + 1].Style.Script == TextScript.Under)
                {
                    // A fraction: its top and bottom centred on each other, over and under a bar as wide as the wider.
                    var (below, belowStyle) = token.Parts[p + 1];
                    float over = measure(text, style), under = measure(below, belowStyle), pad = FractionPad(fontSize);
                    float across = Math.Max(over, under) + pad * 2;
                    pieces.Add(new TextPiece(text, style, x + (across - over) / 2, over));
                    pieces.Add(new TextPiece("", style, x + pad / 2, across - pad, Bar: true));
                    pieces.Add(new TextPiece(below, belowStyle, x + (across - under) / 2, under));
                    x += across;
                    fractions = true;
                    p++;
                    continue;
                }
                float w = measure(text, style);
                if (!token.IsSpace) pieces.Add(new TextPiece(text, style, x, w));
                else if (style.Underline) pieces.Add(new TextPiece(text, style, x, w + (justify ? stretch : 0)));
                x += w;
            }
            if (token.IsSpace && justify) x += stretch;
        }
        // A line with fractions makes room above for their tops and below for their bottoms.
        if (!fractions) return new TextLine(top, height, pieces);
        float lead = Math.Max(1, fontSize) * 0.2f;
        return new TextLine(top, height + lead + Math.Max(1, fontSize) * 0.45f, pieces, lead);
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
