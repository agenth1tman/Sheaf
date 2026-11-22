using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Sheaf.Core.Office.BuiltIn;

/// <summary>
/// Turns a Word document (.docx) into a PDF without Word: paragraphs with their styles, lists, tables and
/// pictures, laid out by MigraDoc. Headers, footers, text boxes and floating layouts are not drawn.
/// </summary>
public static class WordToPdf
{
    const double TwipsPerPoint = 20, EmuPerPoint = 12700;

    public static void Convert(string input, string output, CancellationToken cancel = default)
    {
        using WordprocessingDocument word = Open(input);
        MainDocumentPart main = word.MainDocumentPart ?? throw new ToolException($"“{Path.GetFileName(input)}” has no document inside.");
        var reader = new DocxReader(main);
        Document document = reader.Build(cancel);
        var renderer = new PdfDocumentRenderer { Document = document };
        renderer.RenderDocument();
        renderer.PdfDocument.Save(output);
    }

    static WordprocessingDocument Open(string input)
    {
        try
        {
            return WordprocessingDocument.Open(input, false);
        }
        catch (Exception ex) when (ex is OpenXmlPackageException or InvalidDataException or IOException or FileFormatException)
        {
            throw new ToolException($"“{Path.GetFileName(input)}” can’t be read. The built-in converter reads Word documents saved as .docx; save .doc files as .docx first.", ex);
        }
    }

    /// <summary>Text formatting gathered from document defaults, styles and the run itself.</summary>
    sealed record TextStyle(string Font, double Size, bool Bold, bool Italic, bool Underline, string? Color)
    {
        public static readonly TextStyle Default = new("Calibri", 11, false, false, false, null);

        public TextStyle With(OpenXmlElement? properties, Func<W.RunFonts, string?> font)
        {
            if (properties is null) return this;
            TextStyle style = this;
            foreach (OpenXmlElement child in properties.ChildElements)
                style = child switch
                {
                    W.Bold bold => style with { Bold = bold.Val?.Value ?? true },
                    W.Italic italic => style with { Italic = italic.Val?.Value ?? true },
                    W.Underline underline => style with { Underline = underline.Val?.Value != W.UnderlineValues.None },
                    W.FontSize size when double.TryParse(size.Val?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double half) => style with { Size = half / 2 },
                    W.Color color => style with { Color = color.Val?.Value is { } hex && hex != "auto" ? hex : null },
                    W.RunFonts fonts when font(fonts) is { } name => style with { Font = name },
                    _ => style,
                };
            return style;
        }
    }

    sealed record ParagraphLook(ParagraphAlignment Alignment, double Before, double After, double Left, double FirstLine, double LineSpacing)
    {
        public static readonly ParagraphLook Default = new(ParagraphAlignment.Left, 0, 8, 0, 0, 1.08);

        public ParagraphLook With(OpenXmlElement? properties)
        {
            if (properties is null) return this;
            ParagraphLook look = this;
            foreach (OpenXmlElement child in properties.ChildElements)
                look = child switch
                {
                    W.Justification justification => look with { Alignment = Align(justification.Val?.Value) },
                    W.SpacingBetweenLines spacing => look with
                    {
                        Before = Twips(spacing.Before?.Value) ?? look.Before,
                        After = Twips(spacing.After?.Value) ?? look.After,
                        LineSpacing = spacing.LineRule?.Value == W.LineSpacingRuleValues.Auto && double.TryParse(spacing.Line?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double line)
                            ? line / 240 : look.LineSpacing,
                    },
                    W.Indentation indent => look with
                    {
                        Left = Twips(indent.Left?.Value ?? indent.Start?.Value) ?? look.Left,
                        FirstLine = Twips(indent.FirstLine?.Value) ?? (Twips(indent.Hanging?.Value) is double hanging ? -hanging : look.FirstLine),
                    },
                    _ => look,
                };
            return look;
        }

        static ParagraphAlignment Align(W.JustificationValues? value) =>
            value == W.JustificationValues.Center ? ParagraphAlignment.Center
            : value == W.JustificationValues.Right || value == W.JustificationValues.End ? ParagraphAlignment.Right
            : value == W.JustificationValues.Both || value == W.JustificationValues.Distribute ? ParagraphAlignment.Justify
            : ParagraphAlignment.Left;
    }

    static double? Twips(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double twips) ? twips / TwipsPerPoint : null;

    sealed class DocxReader(MainDocumentPart main)
    {
        readonly Dictionary<string, W.Style> _styles = main.StyleDefinitionsPart?.Styles?.Elements<W.Style>()
            .Where(style => style.StyleId?.Value is not null).GroupBy(style => style.StyleId!.Value!).ToDictionary(group => group.Key, group => group.First()) ?? [];
        readonly Dictionary<(int Num, int Level), int> _counters = [];
        readonly (string Minor, string Major) _themeFonts = ThemeFonts(main);
        TextStyle _defaultText = TextStyle.Default;
        ParagraphLook _defaultLook = ParagraphLook.Default;
        Section _section = null!;
        double _contentWidth;

        public Document Build(CancellationToken cancel)
        {
            var document = new Document();
            ReadDefaults();
            _section = document.AddSection();
            SetUpPage(main.Document?.Body?.Elements<W.SectionProperties>().LastOrDefault());
            Style normal = document.Styles[StyleNames.Normal]!;
            normal.Font.Name = _defaultText.Font;
            normal.Font.Size = _defaultText.Size;
            foreach (OpenXmlElement element in Blocks(main.Document?.Body))
            {
                cancel.ThrowIfCancellationRequested();
                switch (element)
                {
                    case W.Paragraph paragraph: AddParagraph(paragraph, () => _section.AddParagraph(), pageBreaks: true); break;
                    case W.Table table: AddTable(table); break;
                }
            }
            if (_section.Elements.Count == 0) _section.AddParagraph();
            return document;
        }

        /// <summary>Paragraphs and tables, including those inside content controls.</summary>
        static IEnumerable<OpenXmlElement> Blocks(OpenXmlElement? parent)
        {
            if (parent is null) yield break;
            foreach (OpenXmlElement child in parent.ChildElements)
            {
                if (child is W.SdtBlock sdt) { foreach (OpenXmlElement inner in Blocks(sdt.SdtContentBlock)) yield return inner; }
                else if (child is W.Paragraph or W.Table) yield return child;
            }
        }

        void ReadDefaults()
        {
            W.DocDefaults? defaults = main.StyleDefinitionsPart?.Styles?.DocDefaults;
            _defaultText = TextStyle.Default.With(defaults?.RunPropertiesDefault?.RunPropertiesBaseStyle, FontName);
            _defaultLook = ParagraphLook.Default.With(defaults?.ParagraphPropertiesDefault?.ParagraphPropertiesBaseStyle);
            if (_styles.Values.FirstOrDefault(style => style.Default?.Value == true && style.Type?.Value == W.StyleValues.Paragraph) is { } normal)
            {
                _defaultText = _defaultText.With(normal.StyleRunProperties, FontName);
                _defaultLook = _defaultLook.With(normal.StyleParagraphProperties);
            }
        }

        void SetUpPage(W.SectionProperties? properties)
        {
            PageSetup setup = _section.PageSetup;
            W.PageSize? size = properties?.GetFirstChild<W.PageSize>();
            W.PageMargin? margin = properties?.GetFirstChild<W.PageMargin>();
            double width = size?.Width?.Value is uint w ? w / TwipsPerPoint : 595.28, height = size?.Height?.Value is uint h ? h / TwipsPerPoint : 841.89;
            setup.PageWidth = Unit.FromPoint(width);
            setup.PageHeight = Unit.FromPoint(height);
            double left = margin?.Left?.Value is uint l ? l / TwipsPerPoint : 72, right = margin?.Right?.Value is uint r ? r / TwipsPerPoint : 72;
            setup.LeftMargin = Unit.FromPoint(left);
            setup.RightMargin = Unit.FromPoint(right);
            setup.TopMargin = Unit.FromPoint(margin?.Top?.Value is int t ? Math.Abs(t) / TwipsPerPoint : 72);
            setup.BottomMargin = Unit.FromPoint(margin?.Bottom?.Value is int b ? Math.Abs(b) / TwipsPerPoint : 72);
            _contentWidth = Math.Max(72, width - left - right);
        }

        string? FontName(W.RunFonts fonts)
        {
            if (fonts.Ascii?.Value is { } ascii) return ascii;
            if (fonts.HighAnsi?.Value is { } high) return high;
            if (fonts.AsciiTheme?.Value is { } theme) return theme == W.ThemeFontValues.MajorHighAnsi || theme == W.ThemeFontValues.MajorAscii ? _themeFonts.Major : _themeFonts.Minor;
            return null;
        }

        static (string, string) ThemeFonts(MainDocumentPart main)
        {
            A.FontScheme? scheme = main.ThemePart?.Theme?.ThemeElements?.FontScheme;
            return (scheme?.MinorFont?.LatinFont?.Typeface?.Value ?? "Calibri", scheme?.MajorFont?.LatinFont?.Typeface?.Value ?? "Calibri Light");
        }

        /// <summary>A style and the styles it is based on, the base first.</summary>
        IEnumerable<W.Style> Chain(string? styleId)
        {
            var chain = new List<W.Style>();
            for (int guard = 0; styleId is not null && _styles.TryGetValue(styleId, out W.Style? style) && guard < 12; guard++)
            {
                chain.Insert(0, style);
                styleId = style.BasedOn?.Val?.Value;
            }
            return chain;
        }

        void AddParagraph(W.Paragraph source, Func<Paragraph> create, bool pageBreaks)
        {
            W.ParagraphProperties? properties = source.ParagraphProperties;
            string? styleId = properties?.ParagraphStyleId?.Val?.Value;
            TextStyle text = _defaultText;
            ParagraphLook look = _defaultLook;
            foreach (W.Style style in Chain(styleId))
            {
                text = text.With(style.StyleRunProperties, FontName);
                look = look.With(style.StyleParagraphProperties);
            }
            look = look.With(properties);

            Paragraph paragraph = create();
            Format(paragraph, look);
            string? number = ListLabel(properties, styleId);
            if (number is not null)
            {
                paragraph.Format.LeftIndent = Unit.FromPoint(Math.Max(look.Left, 18));
                paragraph.Format.FirstLineIndent = Unit.FromPoint(-14);
                Write(paragraph, number + "\t", text);
                paragraph.Format.TabStops.AddTabStop(Unit.FromPoint(Math.Max(look.Left, 18)));
            }

            foreach (OpenXmlElement child in source.ChildElements)
            {
                switch (child)
                {
                    case W.Run run:
                        paragraph = AddRun(run, paragraph, text, look, create, pageBreaks);
                        break;
                    case W.Hyperlink or W.SimpleField or W.InsertedRun or W.SdtRun:
                        foreach (W.Run run in child.Descendants<W.Run>())
                            paragraph = AddRun(run, paragraph, text, look, create, pageBreaks);
                        break;
                }
            }
        }

        static void Format(Paragraph paragraph, ParagraphLook look)
        {
            paragraph.Format.Alignment = look.Alignment;
            paragraph.Format.SpaceBefore = Unit.FromPoint(look.Before);
            paragraph.Format.SpaceAfter = Unit.FromPoint(look.After);
            paragraph.Format.LeftIndent = Unit.FromPoint(look.Left);
            paragraph.Format.FirstLineIndent = Unit.FromPoint(look.FirstLine);
            paragraph.Format.LineSpacingRule = LineSpacingRule.Multiple;
            paragraph.Format.LineSpacing = Math.Clamp(look.LineSpacing, 0.8, 3);
        }

        Paragraph AddRun(W.Run run, Paragraph paragraph, TextStyle paragraphText, ParagraphLook look, Func<Paragraph> create, bool pageBreaks)
        {
            TextStyle style = paragraphText;
            foreach (W.Style runStyle in Chain(run.RunProperties?.RunStyle?.Val?.Value)) style = style.With(runStyle.StyleRunProperties, FontName);
            style = style.With(run.RunProperties, FontName);
            foreach (OpenXmlElement child in run.ChildElements)
            {
                switch (child)
                {
                    case W.Text text: Write(paragraph, text.Text, style); break;
                    case W.TabChar: paragraph.AddTab(); break;
                    case W.CarriageReturn: paragraph.AddLineBreak(); break;
                    case W.Break { Type.Value: var type } when type == W.BreakValues.Page && pageBreaks:
                        _section.AddPageBreak();
                        paragraph = create();
                        Format(paragraph, look with { Before = 0 });
                        break;
                    case W.Break: paragraph.AddLineBreak(); break;
                    case W.Drawing drawing: AddPicture(paragraph, drawing); break;
                    case W.SymbolChar symbol when int.TryParse(symbol.Char?.Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int code):
                        Write(paragraph, code is >= 0xF000 and <= 0xF0FF ? "•" : char.ConvertFromUtf32(code), style);
                        break;
                }
            }
            return paragraph;
        }

        static void Write(Paragraph paragraph, string text, TextStyle style)
        {
            if (text.Length == 0) return;
            FormattedText formatted = paragraph.AddFormattedText(text);
            formatted.Font.Name = style.Font;
            formatted.Font.Size = Math.Clamp(style.Size, 1, 400);
            formatted.Bold = style.Bold;
            formatted.Italic = style.Italic;
            if (style.Underline) formatted.Underline = MigraDoc.DocumentObjectModel.Underline.Single;
            if (ParseColor(style.Color) is { } color) formatted.Color = color;
        }

        void AddPicture(Paragraph paragraph, W.Drawing drawing)
        {
            string? id = drawing.Descendants<A.Blip>().FirstOrDefault()?.Embed?.Value;
            if (id is null || main.GetPartById(id) is not ImagePart part) return;
            DW.Extent? extent = drawing.Descendants<DW.Extent>().FirstOrDefault();
            using var stream = part.GetStream();
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            var image = paragraph.AddImage("base64:" + System.Convert.ToBase64String(copy.ToArray()));
            if (extent?.Cx?.Value is long cx && extent.Cy?.Value is long cy && cx > 0 && cy > 0)
            {
                double width = cx / EmuPerPoint, height = cy / EmuPerPoint;
                double scale = Math.Min(1, _contentWidth / width);
                image.Width = Unit.FromPoint(width * scale);
                image.Height = Unit.FromPoint(height * scale);
            }
        }

        /// <summary>The bullet or number in front of a list paragraph, counting as Word does.</summary>
        string? ListLabel(W.ParagraphProperties? properties, string? styleId)
        {
            W.NumberingProperties? numbering = properties?.NumberingProperties
                ?? Chain(styleId).Select(style => style.StyleParagraphProperties?.NumberingProperties).LastOrDefault(found => found is not null);
            int? num = numbering?.NumberingId?.Val?.Value;
            int level = numbering?.NumberingLevelReference?.Val?.Value ?? 0;
            if (num is null or 0) return null;
            W.Numbering? definitions = main.NumberingDefinitionsPart?.Numbering;
            int? abstractId = definitions?.Elements<W.NumberingInstance>().FirstOrDefault(instance => instance.NumberID?.Value == num)?.AbstractNumId?.Val?.Value;
            W.Level? definition = definitions?.Elements<W.AbstractNum>().FirstOrDefault(item => item.AbstractNumberId?.Value == abstractId)
                ?.Elements<W.Level>().FirstOrDefault(item => item.LevelIndex?.Value == level);
            W.NumberFormatValues? format = definition?.NumberingFormat?.Val?.Value;
            if (format is null || format == W.NumberFormatValues.Bullet) return level % 2 == 0 ? "•" : "◦";

            // A deeper level starts again whenever a shallower one moves on.
            int key = num.Value;
            foreach (var counter in _counters.Keys.Where(item => item.Num == key && item.Level > level).ToList()) _counters.Remove(counter);
            int start = definition?.StartNumberingValue?.Val?.Value ?? 1;
            _counters[(key, level)] = _counters.TryGetValue((key, level), out int previous) ? previous + 1 : start;
            string template = definition?.LevelText?.Val?.Value ?? $"%{level + 1}.";
            for (int i = 0; i <= level; i++)
                template = template.Replace($"%{i + 1}", Numeral(_counters.GetValueOrDefault((key, i), 1), i == level ? format : W.NumberFormatValues.Decimal));
            return template;
        }

        static string Numeral(int value, W.NumberFormatValues? format)
        {
            if (format == W.NumberFormatValues.LowerLetter) return ((char)('a' + (value - 1) % 26)).ToString();
            if (format == W.NumberFormatValues.UpperLetter) return ((char)('A' + (value - 1) % 26)).ToString();
            if (format == W.NumberFormatValues.LowerRoman) return Roman(value).ToLowerInvariant();
            if (format == W.NumberFormatValues.UpperRoman) return Roman(value);
            return value.ToString(CultureInfo.InvariantCulture);
        }

        static string Roman(int value)
        {
            (int Value, string Text)[] numerals = [(1000, "M"), (900, "CM"), (500, "D"), (400, "CD"), (100, "C"), (90, "XC"), (50, "L"), (40, "XL"), (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I")];
            var text = new System.Text.StringBuilder();
            foreach (var (number, letters) in numerals)
                while (value >= number) { text.Append(letters); value -= number; }
            return text.ToString();
        }

        void AddTable(W.Table source)
        {
            var widths = source.GetFirstChild<W.TableGrid>()?.Elements<W.GridColumn>()
                .Select(column => Twips(column.Width?.Value) ?? 72).ToList() ?? [];
            int columns = Math.Max(widths.Count, source.Elements<W.TableRow>().Select(row => row.Elements<W.TableCell>().Sum(Span)).DefaultIfEmpty(1).Max());
            while (widths.Count < columns) widths.Add(_contentWidth / columns);
            double total = widths.Sum(), scale = total > _contentWidth ? _contentWidth / total : 1;

            Table table = _section.AddTable();
            bool borders = HasBorders(source);
            table.Borders.Visible = borders;
            if (borders)
            {
                table.Borders.Width = 0.5;
                table.Borders.Color = Colors.Gray;
            }
            foreach (double width in widths) table.AddColumn(Unit.FromPoint(Math.Max(12, width * scale)));
            foreach (W.TableRow sourceRow in source.Elements<W.TableRow>())
            {
                Row row = table.AddRow();
                int column = 0;
                foreach (W.TableCell sourceCell in sourceRow.Elements<W.TableCell>())
                {
                    if (column >= columns) break;
                    Cell cell = row.Cells[column];
                    int span = Span(sourceCell);
                    if (span > 1) cell.MergeRight = Math.Min(span, columns - column) - 1;
                    if (ParseColor(sourceCell.TableCellProperties?.Shading?.Fill?.Value) is { } fill) cell.Shading.Color = fill;
                    foreach (W.Paragraph paragraph in sourceCell.Descendants<W.Paragraph>())
                        AddParagraph(paragraph, () => cell.AddParagraph(), pageBreaks: false);
                    column += span;
                }
            }
            _section.AddParagraph().Format.SpaceAfter = Unit.FromPoint(4);
        }

        static int Span(W.TableCell cell) => Math.Max(1, cell.TableCellProperties?.GridSpan?.Val?.Value ?? 1);

        bool HasBorders(W.Table table)
        {
            W.TableBorders? borders = table.GetFirstChild<W.TableProperties>()?.TableBorders;
            if (borders is not null) return borders.ChildElements.OfType<W.BorderType>().Any(border => border.Val?.Value != W.BorderValues.None && border.Val?.Value != W.BorderValues.Nil);
            string? style = table.GetFirstChild<W.TableProperties>()?.TableStyle?.Val?.Value;
            return style is not null && (style.Contains("Grid", StringComparison.OrdinalIgnoreCase)
                || Chain(style).Any(item => item.StyleTableProperties?.TableBorders is not null));
        }
    }

    internal static MigraDoc.DocumentObjectModel.Color? ParseColor(string? hex) =>
        hex is { Length: 6 } && int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb) && hex != "auto"
            ? new MigraDoc.DocumentObjectModel.Color((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)
            : null;
}
