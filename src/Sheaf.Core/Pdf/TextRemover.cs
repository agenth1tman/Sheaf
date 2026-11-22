using System.Globalization;
using System.Text;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace Sheaf.Core.Pdf;

/// <summary>
/// Takes the letters inside given areas out of a page's own text, leaving every other letter where it was and
/// still text: what redacting text should do. The page's content is read token by token, the text state is
/// followed to find where each letter lands, and only the operators that show a removed letter are rewritten,
/// with a gap where each letter was so the rest of the line keeps its place.
/// <para>
/// It changes nothing and returns false when it can't be sure the words are gone: a font without widths (so
/// letters can't be placed), a picture or form under an area (its pixels or text would stay), or ActualText,
/// which could repeat the words. The caller then redraws the page as an image instead.
/// </para>
/// </summary>
public static class TextRemover
{
    /// <summary>Removes the letters whose middles fall in <paramref name="areas"/> (PDF user space).</summary>
    public static bool TryRemove(PdfPage page, IReadOnlyList<XRect> areas, out int removed)
    {
        removed = 0;
        byte[] content = ReadContent(page);
        var reader = new Interpreter(content, page, areas);
        if (!reader.Run() || reader.HasActualText && reader.Removed > 0) return false;
        removed = reader.Removed;
        if (removed == 0) return true;

        var output = new MemoryStream(content.Length);
        int at = 0;
        foreach (var (start, end, replacement) in reader.Edits)
        {
            output.Write(content, at, start - at);
            output.Write(Encoding.ASCII.GetBytes(replacement));
            at = end;
        }
        output.Write(content, at, content.Length - at);
        page.Contents.Elements.Clear();
        page.Contents.AppendContent().CreateStream(output.ToArray());
        return true;
    }

    static byte[] ReadContent(PdfPage page)
    {
        var data = new MemoryStream();
        for (int i = 0; i < page.Contents.Elements.Count; i++)
        {
            if (page.Contents.Elements.GetDictionary(i)?.Stream is not { } stream) continue;
            data.Write(stream.UnfilteredValue);
            data.WriteByte((byte)'\n');
        }
        return data.ToArray();
    }

    /// <summary>An affine matrix as PDF writes them; <c>a.Then(b)</c> applies <c>a</c>, then <c>b</c>.</summary>
    readonly record struct Affine(double A, double B, double C, double D, double E, double F)
    {
        public static readonly Affine Identity = new(1, 0, 0, 1, 0, 0);

        public Affine Then(Affine m) => new(
            A * m.A + B * m.C, A * m.B + B * m.D,
            C * m.A + D * m.C, C * m.B + D * m.D,
            E * m.A + F * m.C + m.E, E * m.B + F * m.D + m.F);

        public XPoint Apply(double x, double y) => new(x * A + y * C + E, x * B + y * D + F);

        public static Affine Translate(double x, double y) => new(1, 0, 0, 1, x, y);
    }

    /// <summary>The text settings a q/Q saves along with the transform.</summary>
    record struct TextState(Affine Ctm, double CharSpacing, double WordSpacing, double Scale, double Leading, string? FontName, double FontSize, double Rise);

    sealed class FontInfo
    {
        public bool TwoByte;
        public double Unit = 0.001;
        public double Ascent = 0.8, Descent = -0.2;
        public Func<int, double> Width = _ => 0;
    }

    sealed class DictionaryValue
    {
        public bool HasActualText;
    }

    sealed class Interpreter(byte[] data, PdfPage page, IReadOnlyList<XRect> areas)
    {
        readonly Stack<TextState> _saved = new();
        readonly Dictionary<string, FontInfo?> _fonts = [];
        TextState _state = new(Affine.Identity, 0, 0, 1, 0, null, 0, 0);
        Affine _tm = Affine.Identity, _tlm = Affine.Identity;
        int _at;

        public List<(int Start, int End, string Replacement)> Edits { get; } = [];
        public int Removed { get; private set; }
        public bool HasActualText { get; private set; }

        /// <summary>False when the page can't be handled safely.</summary>
        public bool Run()
        {
            var operands = new List<object?>();
            int operandsStart = -1;
            while (NextToken() is { } token)
            {
                if (token.Kind == Kind.Operator)
                {
                    if (!Execute(token.Text!, operands, operandsStart < 0 ? token.Start : operandsStart, token.End)) return false;
                    operands.Clear();
                    operandsStart = -1;
                    continue;
                }
                if (operandsStart < 0) operandsStart = token.Start;
                operands.Add(Value(token));
            }
            return true;
        }

        bool Execute(string op, List<object?> args, int start, int end)
        {
            double Number(int index) => index < args.Count && args[index] is double value ? value : 0;
            switch (op)
            {
                case "q": _saved.Push(_state); break;
                case "Q": if (_saved.Count > 0) _state = _saved.Pop(); break;
                case "cm": _state.Ctm = new Affine(Number(0), Number(1), Number(2), Number(3), Number(4), Number(5)).Then(_state.Ctm); break;
                case "BT": _tm = _tlm = Affine.Identity; break;
                case "Tc": _state.CharSpacing = Number(0); break;
                case "Tw": _state.WordSpacing = Number(0); break;
                case "Tz": _state.Scale = Number(0) / 100; break;
                case "TL": _state.Leading = Number(0); break;
                case "Ts": _state.Rise = Number(0); break;
                case "Tf":
                    _state.FontName = args.Count > 0 ? args[0] as string : null;
                    _state.FontSize = Number(1);
                    break;
                case "Td": NextLine(Number(0), Number(1)); break;
                case "TD":
                    _state.Leading = -Number(1);
                    NextLine(Number(0), Number(1));
                    break;
                case "Tm": _tm = _tlm = new Affine(Number(0), Number(1), Number(2), Number(3), Number(4), Number(5)); break;
                case "T*": NextLine(0, -_state.Leading); break;
                case "Tj" when args.Count > 0 && args[0] is byte[] text:
                    return Show([text], start, end, "");
                case "'" when args.Count > 0 && args[0] is byte[] text:
                    NextLine(0, -_state.Leading);
                    return Show([text], start, end, "T* ");
                case "\"" when args.Count > 2 && args[2] is byte[] text:
                    _state.WordSpacing = Number(0);
                    _state.CharSpacing = Number(1);
                    NextLine(0, -_state.Leading);
                    return Show([text], start, end, $"{Format(Number(0))} Tw {Format(Number(1))} Tc T* ");
                case "TJ" when args.Count > 0 && args[0] is List<object?> parts:
                    return Show(parts, start, end, "");
                case "BDC" when args.Count > 1 && args[1] is DictionaryValue { HasActualText: true }:
                    HasActualText = true;
                    break;
                case "Do" when args.Count > 0 && args[0] is string name:
                    return !Covers(XObjectBounds(name));
                case "BI":
                    return SkipInlineImage();
            }
            return true;
        }

        void NextLine(double x, double y)
        {
            _tlm = Affine.Translate(x, y).Then(_tlm);
            _tm = _tlm;
        }

        /// <summary>
        /// Follows a text-showing operator letter by letter. When some letters fall in an area, the operator is
        /// rewritten as a TJ without them, a gap the width of each in its place.
        /// </summary>
        bool Show(List<object?> parts, int start, int end, string prefix)
        {
            if (_state.FontName is null || Font(_state.FontName) is not { } font) return !TouchesAnything();
            double size = _state.FontSize, scale = _state.Scale;
            var rewritten = new List<object>();
            var kept = new List<byte>();
            bool changed = false;
            void Flush()
            {
                if (kept.Count == 0) return;
                rewritten.Add(kept.ToArray());
                kept.Clear();
            }
            void Gap(double units)
            {
                Flush();
                if (rewritten.Count > 0 && rewritten[^1] is double previous) rewritten[^1] = previous + units;
                else rewritten.Add(units);
            }
            foreach (object? part in parts)
            {
                if (part is double adjustment)
                {
                    _tm = Affine.Translate(-adjustment / 1000 * size * scale, 0).Then(_tm);
                    Gap(adjustment);
                    continue;
                }
                if (part is not byte[] text) continue;
                int step = font.TwoByte ? 2 : 1;
                for (int i = 0; i + step <= text.Length; i += step)
                {
                    int code = step == 2 ? text[i] << 8 | text[i + 1] : text[i];
                    double width = font.Width(code) * font.Unit;
                    double spacing = _state.CharSpacing + (step == 1 && code == 32 ? _state.WordSpacing : 0);
                    var render = new Affine(size * scale, 0, 0, size, 0, _state.Rise).Then(_tm).Then(_state.Ctm);
                    XPoint middle = render.Apply(width / 2, (font.Ascent + font.Descent) / 2);
                    if (size != 0 && areas.Any(area => area.Contains(middle)))
                    {
                        Gap(-1000 * (width + spacing / size));
                        changed = true;
                        Removed++;
                    }
                    else kept.AddRange(text.AsSpan(i, step).ToArray());
                    _tm = Affine.Translate((width * size + spacing) * scale, 0).Then(_tm);
                }
            }
            Flush();
            if (changed) Edits.Add((start, end, prefix + Serialize(rewritten) + " TJ"));
            return true;
        }

        /// <summary>Without a usable font, the letters can't be placed: fine only if no area is near this text at all.</summary>
        bool TouchesAnything()
        {
            XPoint origin = _tm.Then(_state.Ctm).Apply(0, 0);
            return areas.Any(area => { XRect wide = area; wide.Inflate(400, 40); return wide.Contains(origin); });
        }

        static string Serialize(List<object> parts)
        {
            var text = new StringBuilder("[");
            foreach (object part in parts)
            {
                if (part is double number) text.Append(' ').Append(Format(number)).Append(' ');
                else text.Append('<').Append(Convert.ToHexString((byte[])part)).Append('>');
            }
            return text.Append(']').ToString();
        }

        static string Format(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);

        bool Covers(XRect? box) => box is { } bounds && areas.Any(area => area.IntersectsWith(bounds));

        /// <summary>Where a picture or form lands on the page; a form with no box counts as the whole page.</summary>
        XRect? XObjectBounds(string name)
        {
            PdfDictionary? xobject = page.Resources.Elements.GetDictionary("/XObject")?.Elements.GetDictionary(name);
            if (xobject is null) return null;
            string subtype = xobject.Elements.GetName("/Subtype");
            if (subtype == "/Image") return Bounds(_state.Ctm, new XRect(0, 0, 1, 1));
            if (subtype != "/Form") return null;
            Affine matrix = xobject.Elements.GetArray("/Matrix") is { Elements.Count: 6 } m
                ? new Affine(m.Elements.GetReal(0), m.Elements.GetReal(1), m.Elements.GetReal(2), m.Elements.GetReal(3), m.Elements.GetReal(4), m.Elements.GetReal(5))
                : Affine.Identity;
            if (xobject.Elements.GetArray("/BBox") is not { Elements.Count: 4 } box) return new XRect(-1e6, -1e6, 2e6, 2e6);
            var rect = new XRect(new XPoint(box.Elements.GetReal(0), box.Elements.GetReal(1)), new XPoint(box.Elements.GetReal(2), box.Elements.GetReal(3)));
            return Bounds(matrix.Then(_state.Ctm), rect);
        }

        static XRect Bounds(Affine transform, XRect rect)
        {
            XPoint[] corners = [transform.Apply(rect.Left, rect.Top), transform.Apply(rect.Right, rect.Top), transform.Apply(rect.Left, rect.Bottom), transform.Apply(rect.Right, rect.Bottom)];
            double left = corners.Min(p => p.X), bottom = corners.Min(p => p.Y);
            return new XRect(left, bottom, corners.Max(p => p.X) - left, corners.Max(p => p.Y) - bottom);
        }

        /// <summary>Skips an inline picture's data; a picture under an area means the page can't keep its content.</summary>
        bool SkipInlineImage()
        {
            while (NextToken() is { } token)
                if (token.Kind == Kind.Operator && token.Text == "ID") break;
            _at++;
            for (; _at + 1 < data.Length; _at++)
            {
                bool before = IsWhite(data[_at - 1]);
                bool after = _at + 2 >= data.Length || IsWhite(data[_at + 2]) || IsDelimiter(data[_at + 2]);
                if (before && after && data[_at] == 'E' && data[_at + 1] == 'I')
                {
                    _at += 2;
                    break;
                }
            }
            return !Covers(Bounds(_state.Ctm, new XRect(0, 0, 1, 1)));
        }

        FontInfo? Font(string name)
        {
            if (_fonts.TryGetValue(name, out FontInfo? known)) return known;
            return _fonts[name] = ReadFont(page.Resources.Elements.GetDictionary("/Font")?.Elements.GetDictionary(name));
        }

        static FontInfo? ReadFont(PdfDictionary? font)
        {
            if (font is null) return null;
            var info = new FontInfo();
            PdfDictionary? descriptor;
            if (font.Elements.GetName("/Subtype") == "/Type0")
            {
                // Two-byte codes laid out left to right; other encodings (or vertical text) aren't followed.
                if (font.Elements["/Encoding"] is not PdfName encoding || !encoding.Value.EndsWith("-H", StringComparison.Ordinal)) return null;
                if (font.Elements.GetArray("/DescendantFonts")?.Elements.GetDictionary(0) is not { } descendant) return null;
                info.TwoByte = true;
                double fallback = descendant.Elements.ContainsKey("/DW") ? descendant.Elements.GetReal("/DW") : 1000;
                Dictionary<int, double> widths = CidWidths(descendant.Elements.GetArray("/W"));
                info.Width = code => widths.TryGetValue(code, out double width) ? width : fallback;
                descriptor = descendant.Elements.GetDictionary("/FontDescriptor");
            }
            else
            {
                if (font.Elements.GetArray("/Widths") is not { } list) return null;
                int first = font.Elements.GetInteger("/FirstChar");
                descriptor = font.Elements.GetDictionary("/FontDescriptor");
                double missing = descriptor?.Elements.GetReal("/MissingWidth") ?? 0;
                double[] widths = [.. Enumerable.Range(0, list.Elements.Count).Select(i => Number(list.Elements[i]) ?? missing)];
                info.Width = code => code >= first && code - first < widths.Length ? widths[code - first] : missing;
                if (font.Elements.GetName("/Subtype") == "/Type3")
                {
                    if (font.Elements.GetArray("/FontMatrix") is not { Elements.Count: 6 } matrix) return null;
                    info.Unit = matrix.Elements.GetReal(0);
                    return info;
                }
            }
            if (descriptor is not null && descriptor.Elements.GetReal("/Ascent") is var ascent and > 0)
            {
                info.Ascent = ascent / 1000;
                info.Descent = Math.Min(0, descriptor.Elements.GetReal("/Descent") / 1000);
            }
            return info;
        }

        /// <summary>A CID font's /W: "c [w1 w2 …]" gives widths from c on; "c1 c2 w" gives one width to a range.</summary>
        static Dictionary<int, double> CidWidths(PdfArray? list)
        {
            var widths = new Dictionary<int, double>();
            if (list is null) return widths;
            for (int i = 0; i + 1 < list.Elements.Count;)
            {
                if (Number(list.Elements[i]) is not { } first) break;
                if (Resolve(list.Elements[i + 1]) is PdfArray run)
                {
                    for (int j = 0; j < run.Elements.Count; j++) widths[(int)first + j] = Number(run.Elements[j]) ?? 0;
                    i += 2;
                }
                else if (i + 2 < list.Elements.Count && Number(list.Elements[i + 1]) is { } last && Number(list.Elements[i + 2]) is { } width)
                {
                    for (int code = (int)first; code <= (int)last && code - (int)first < 65536; code++) widths[code] = width;
                    i += 3;
                }
                else break;
            }
            return widths;
        }

        static PdfItem? Resolve(PdfItem? item) => item is PdfReference reference ? reference.Value : item;

        static double? Number(PdfItem? item) => Resolve(item) switch
        {
            PdfInteger integer => integer.Value,
            PdfLongInteger integer => integer.Value,
            PdfReal real => real.Value,
            _ => null,
        };

        // Tokens ---------------------------------------------------------------------------------

        enum Kind { Number, Name, String, ArrayStart, ArrayEnd, DictionaryStart, DictionaryEnd, Operator }

        sealed record Token(Kind Kind, int Start, int End, string? Text = null, double Number = 0, byte[]? Bytes = null);

        object? Value(Token token) => token.Kind switch
        {
            Kind.Number => token.Number,
            Kind.Name => token.Text,
            Kind.String => token.Bytes,
            Kind.ArrayStart => ReadArray(),
            Kind.DictionaryStart => ReadDictionary(),
            _ => null,
        };

        List<object?> ReadArray()
        {
            var items = new List<object?>();
            while (NextToken() is { } token && token.Kind != Kind.ArrayEnd) items.Add(Value(token));
            return items;
        }

        DictionaryValue ReadDictionary()
        {
            var value = new DictionaryValue();
            while (NextToken() is { } token && token.Kind != Kind.DictionaryEnd)
            {
                if (token is { Kind: Kind.Name, Text: "/ActualText" }) value.HasActualText = true;
                if (Value(token) is DictionaryValue { HasActualText: true }) value.HasActualText = true;
            }
            return value;
        }

        static bool IsWhite(byte b) => b is 0 or 9 or 10 or 12 or 13 or 32;

        static bool IsDelimiter(byte b) => b is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';

        Token? NextToken()
        {
            while (_at < data.Length)
            {
                if (IsWhite(data[_at])) { _at++; continue; }
                if (data[_at] == '%')
                {
                    while (_at < data.Length && data[_at] is not ((byte)'\n' or (byte)'\r')) _at++;
                    continue;
                }
                break;
            }
            if (_at >= data.Length) return null;
            int start = _at;
            byte c = data[_at];
            switch (c)
            {
                case (byte)'[': _at++; return new Token(Kind.ArrayStart, start, _at);
                case (byte)']': _at++; return new Token(Kind.ArrayEnd, start, _at);
                case (byte)'{' or (byte)'}': _at++; return NextToken();
                case (byte)'(': return new Token(Kind.String, start, ReadLiteral(out byte[] literal), Bytes: literal);
                case (byte)'<' when _at + 1 < data.Length && data[_at + 1] == '<': _at += 2; return new Token(Kind.DictionaryStart, start, _at);
                case (byte)'>' when _at + 1 < data.Length && data[_at + 1] == '>': _at += 2; return new Token(Kind.DictionaryEnd, start, _at);
                case (byte)'<': return new Token(Kind.String, start, ReadHex(out byte[] hex), Bytes: hex);
                case (byte)'/':
                    _at++;
                    while (_at < data.Length && !IsWhite(data[_at]) && !IsDelimiter(data[_at])) _at++;
                    return new Token(Kind.Name, start, _at, Encoding.Latin1.GetString(data, start, _at - start));
            }
            _at++;
            while (_at < data.Length && !IsWhite(data[_at]) && !IsDelimiter(data[_at])) _at++;
            string word = Encoding.Latin1.GetString(data, start, _at - start);
            bool numeric = c is (byte)'+' or (byte)'-' or (byte)'.' || (c >= '0' && c <= '9');
            if (numeric && double.TryParse(word, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
                return new Token(Kind.Number, start, _at, Number: number);
            return new Token(Kind.Operator, start, _at, word);
        }

        int ReadLiteral(out byte[] bytes)
        {
            var result = new List<byte>();
            int depth = 0;
            _at++;
            while (_at < data.Length)
            {
                byte b = data[_at++];
                if (b == '\\' && _at < data.Length)
                {
                    byte e = data[_at++];
                    switch (e)
                    {
                        case (byte)'n': result.Add(10); break;
                        case (byte)'r': result.Add(13); break;
                        case (byte)'t': result.Add(9); break;
                        case (byte)'b': result.Add(8); break;
                        case (byte)'f': result.Add(12); break;
                        case (byte)'\r': if (_at < data.Length && data[_at] == '\n') _at++; break;
                        case (byte)'\n': break;
                        case >= (byte)'0' and <= (byte)'7':
                            int value = e - '0';
                            for (int k = 0; k < 2 && _at < data.Length && data[_at] is >= (byte)'0' and <= (byte)'7'; k++) value = value * 8 + data[_at++] - '0';
                            result.Add((byte)value);
                            break;
                        default: result.Add(e); break;
                    }
                    continue;
                }
                if (b == '(') depth++;
                else if (b == ')' && depth-- == 0) break;
                result.Add(b);
            }
            bytes = [.. result];
            return _at;
        }

        int ReadHex(out byte[] bytes)
        {
            var digits = new StringBuilder();
            _at++;
            while (_at < data.Length && data[_at] != '>')
            {
                if (Uri.IsHexDigit((char)data[_at])) digits.Append((char)data[_at]);
                _at++;
            }
            _at++;
            if (digits.Length % 2 == 1) digits.Append('0');
            bytes = Convert.FromHexString(digits.ToString());
            return _at;
        }
    }
}
