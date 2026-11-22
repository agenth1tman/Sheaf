using System.Text;
using System.Xml;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using Sheaf.Core.Ocr;
using Sheaf.Core.Rendering;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using Pic = DocumentFormat.OpenXml.Drawing.Pictures;
using Pig = UglyToad.PdfPig;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Sheaf.Core.Office.BuiltIn;

/// <summary>
/// Turns a PDF into an editable Word document (.docx) without Word: the words of each page rebuilt into lines
/// and paragraphs with their size, weight and colour, and the page's pictures in reading order. A page with no
/// text (a scan) comes in as a picture of the page, followed by its text read with Windows' OCR when available.
/// Columns, tables and exact positions are not kept.
/// </summary>
public static class PdfToWord
{
    const long EmuPerPoint = 12700;
    const int ScanDpi = 150;

    public static void Convert(string input, string output, CancellationToken cancel = default)
    {
        using Pig.PdfDocument pdf = Open(input);
        using PdfPreview? preview = NeedsRenders(pdf) ? PdfPreview.OpenAsync(input).GetAwaiter().GetResult() : null;
        using WordprocessingDocument word = WordprocessingDocument.Create(output, WordprocessingDocumentType.Document);
        MainDocumentPart main = word.AddMainDocumentPart();
        var body = new W.Body();
        main.Document = new W.Document(body);
        var writer = new DocxWriter(main, body);

        for (int number = 1; number <= pdf.NumberOfPages; number++)
        {
            cancel.ThrowIfCancellationRequested();
            Pig.Content.Page page = pdf.GetPage(number);
            if (number == 1) writer.PageSize(page.Width, page.Height);
            var words = page.GetWords().Where(item => !string.IsNullOrWhiteSpace(item.Text)).ToList();
            if (words.Count == 0 && preview is not null) writer.Scan(preview, number - 1, page.Width, cancel);
            else writer.Page(page, words);
            if (number < pdf.NumberOfPages) body.Append(new W.Paragraph(new W.Run(new W.Break { Type = W.BreakValues.Page })));
        }
        body.Append(writer.SectionProperties());
        main.Document.Save();
    }

    /// <summary>
    /// The text without characters a Word file can't hold. Some PDFs carry control codes in their text (a font's
    /// private markers, say) that no one sees but that would stop the document being written at all.
    /// </summary>
    internal static string XmlSafe(string text)
    {
        if (text.All(XmlConvert.IsXmlChar)) return text;
        var kept = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            if (XmlConvert.IsXmlChar(text[i])) kept.Append(text[i]);
            else if (i + 1 < text.Length && XmlConvert.IsXmlSurrogatePair(text[i + 1], text[i])) kept.Append(text, i++, 2);
        }
        return kept.ToString();
    }

    static Pig.PdfDocument Open(string input)
    {
        try
        {
            return Pig.PdfDocument.Open(input);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new ToolException(ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase)
                ? $"“{Path.GetFileName(input)}” has a password. Unlock it with Unlock PDF first."
                : $"“{Path.GetFileName(input)}” can’t be read as a PDF.", ex);
        }
    }

    static bool NeedsRenders(Pig.PdfDocument pdf) =>
        Enumerable.Range(1, pdf.NumberOfPages).Any(number => !pdf.GetPage(number).GetWords().Any(word => !string.IsNullOrWhiteSpace(word.Text)));

    /// <summary>A line of a page: its words left to right, and where it sits.</summary>
    sealed record Line(List<Pig.Content.Word> Words, double Top, double Bottom, double Left, double Right, double Size);

    sealed class DocxWriter(MainDocumentPart main, W.Body body)
    {
        uint _pictureId;
        double _pageWidth = 595.28, _pageHeight = 841.89, _margin = 56;

        public void PageSize(double width, double height)
        {
            _pageWidth = width;
            _pageHeight = height;
        }

        public W.SectionProperties SectionProperties() => new(
            new W.PageSize { Width = (uint)(_pageWidth * 20), Height = (uint)(_pageHeight * 20) },
            new W.PageMargin { Top = (int)(_margin * 20), Bottom = (int)(_margin * 20), Left = (uint)(_margin * 20), Right = (uint)(_margin * 20), Header = 708, Footer = 708, Gutter = 0 });

        double ContentWidth => _pageWidth - 2 * _margin;

        public void Page(Pig.Content.Page page, List<Pig.Content.Word> words)
        {
            var blocks = new List<(double Top, OpenXmlElement Element)>();
            foreach (var paragraph in Paragraphs(Lines(words)))
                blocks.Add((paragraph[0].Top, Paragraph(paragraph, page.Width)));
            foreach (Pig.Content.IPdfImage image in page.GetImages())
            {
                if (Picture(image) is not { } picture) continue;
                double top = page.Height - image.BoundingBox.Top;
                blocks.Add((top, picture));
            }
            foreach (var (_, element) in blocks.OrderBy(block => block.Top)) body.Append(element);
        }

        /// <summary>A page with no text: its picture, then its text read with OCR.</summary>
        public void Scan(PdfPreview preview, int index, double width, CancellationToken cancel)
        {
            using Bitmap image = preview.RenderAsync(index, (int)(width / 72 * ScanDpi), cancel).GetAwaiter().GetResult();
            using var png = new MemoryStream();
            image.Save(png, System.Drawing.Imaging.ImageFormat.Png);
            double shown = Math.Min(ContentWidth, width);
            body.Append(new W.Paragraph(PictureRun(png.ToArray(), ImagePartType.Png, shown, shown * image.Height / image.Width)));
            try
            {
                List<Editing.PageWord> words = TextRecognizer.ReadAsync(image, null, cancel).GetAwaiter().GetResult();
                foreach (string line in OcrLines(words)) body.Append(new W.Paragraph(new W.Run(new W.Text(XmlSafe(line)) { Space = SpaceProcessingModeValues.Preserve })));
            }
            catch (ToolException)
            {
                // No OCR language in Windows: the page stays a picture.
            }
        }

        static IEnumerable<string> OcrLines(List<Editing.PageWord> words)
        {
            var lines = new List<List<Editing.PageWord>>();
            foreach (Editing.PageWord word in words.OrderBy(item => item.Bounds.Top))
            {
                var line = lines.LastOrDefault();
                if (line is not null && Math.Abs(line[0].Bounds.Top + line[0].Bounds.Height / 2 - (word.Bounds.Top + word.Bounds.Height / 2)) < word.Bounds.Height * 0.6) line.Add(word);
                else lines.Add([word]);
            }
            return lines.Select(line => string.Join(" ", line.OrderBy(item => item.Bounds.Left).Select(item => item.Text)));
        }

        /// <summary>Groups words into lines by where they sit, top to bottom.</summary>
        static List<Line> Lines(List<Pig.Content.Word> words)
        {
            var lines = new List<Line>();
            foreach (Pig.Content.Word word in words.OrderByDescending(item => item.BoundingBox.Top).ThenBy(item => item.BoundingBox.Left))
            {
                double size = SizeOf(word), middle = (word.BoundingBox.Top + word.BoundingBox.Bottom) / 2;
                int match = lines.FindIndex(line => Math.Abs((line.Top + line.Bottom) / 2 - middle) < Math.Max(line.Size, size) * 0.45);
                if (match >= 0)
                {
                    Line line = lines[match];
                    line.Words.Add(word);
                    lines[match] = line with
                    {
                        Top = Math.Max(line.Top, word.BoundingBox.Top), Bottom = Math.Min(line.Bottom, word.BoundingBox.Bottom),
                        Left = Math.Min(line.Left, word.BoundingBox.Left), Right = Math.Max(line.Right, word.BoundingBox.Right), Size = Math.Max(line.Size, size),
                    };
                }
                else lines.Add(new Line([word], word.BoundingBox.Top, word.BoundingBox.Bottom, word.BoundingBox.Left, word.BoundingBox.Right, size));
            }
            foreach (Line line in lines) line.Words.Sort((a, b) => a.BoundingBox.Left.CompareTo(b.BoundingBox.Left));
            return [.. lines.OrderByDescending(line => line.Top)];
        }

        /// <summary>Joins lines into paragraphs while they keep the same size and margin with normal spacing.</summary>
        static IEnumerable<List<(double Top, Line Line)>> Paragraphs(List<Line> lines)
        {
            var current = new List<(double Top, Line Line)>();
            Line? previous = null;
            foreach (Line line in lines)
            {
                bool continues = previous is not null
                    && Math.Abs(previous.Size - line.Size) < 1
                    && previous.Bottom - line.Top < line.Size * 0.9
                    && Math.Abs(previous.Left - line.Left) < line.Size * 2;
                if (!continues && current.Count > 0)
                {
                    yield return current;
                    current = [];
                }
                current.Add((-line.Top, line));
                previous = line;
            }
            if (current.Count > 0) yield return current;
        }

        W.Paragraph Paragraph(List<(double Top, Line Line)> lines, double pageWidth)
        {
            var paragraph = new W.Paragraph();
            Line first = lines[0].Line;
            double leftGap = first.Left, rightGap = pageWidth - first.Right;
            var properties = new W.ParagraphProperties(new W.SpacingBetweenLines { After = ((int)(first.Size * 6)).ToString(), Line = "240", LineRule = W.LineSpacingRuleValues.Auto });
            if (lines.Count == 1 && leftGap > 72 && Math.Abs(leftGap - rightGap) < 24)
                properties.Append(new W.Justification { Val = W.JustificationValues.Center });
            paragraph.Append(properties);

            Pig.Content.Word? last = null;
            for (int i = 0; i < lines.Count; i++)
            {
                foreach (Pig.Content.Word word in lines[i].Line.Words)
                {
                    bool space = last is not null;
                    paragraph.Append(Run((space ? " " : "") + word.Text, word));
                    last = word;
                }
            }
            return paragraph;
        }

        static double SizeOf(Pig.Content.Word word) => word.Letters.Count == 0 ? 11 : Math.Round(word.Letters.Average(letter => letter.PointSize) * 2) / 2;

        static W.Run Run(string text, Pig.Content.Word word)
        {
            string font = XmlSafe(word.Letters.FirstOrDefault()?.FontName ?? "");
            int plus = font.IndexOf('+');
            if (plus >= 0) font = font[(plus + 1)..];
            var properties = new W.RunProperties();
            string family = font.Split('-', ',')[0];
            if (family.Length > 0) properties.Append(new W.RunFonts { Ascii = family, HighAnsi = family });
            if (font.Contains("Bold", StringComparison.OrdinalIgnoreCase) || font.Contains("Black", StringComparison.OrdinalIgnoreCase) || font.Contains("Heavy", StringComparison.OrdinalIgnoreCase)) properties.Append(new W.Bold());
            if (font.Contains("Italic", StringComparison.OrdinalIgnoreCase) || font.Contains("Oblique", StringComparison.OrdinalIgnoreCase)) properties.Append(new W.Italic());
            if (word.Letters.FirstOrDefault()?.Color?.ToRGBValues() is var (r, g, b) && (r + g + b) > 0.05)
                properties.Append(new W.Color { Val = $"{(int)(r * 255):X2}{(int)(g * 255):X2}{(int)(b * 255):X2}" });
            properties.Append(new W.FontSize { Val = ((int)Math.Round(SizeOf(word) * 2)).ToString() });
            return new W.Run(properties, new W.Text(XmlSafe(text)) { Space = SpaceProcessingModeValues.Preserve });
        }

        W.Paragraph? Picture(Pig.Content.IPdfImage image)
        {
            byte[]? bytes = null;
            PartTypeInfo type = ImagePartType.Png;
            if (image.TryGetPng(out byte[]? png)) bytes = png;
            else if (image.ImageDictionary.ContainsKey(UglyToad.PdfPig.Tokens.NameToken.Filter) && image.ImageDictionary.Data[UglyToad.PdfPig.Tokens.NameToken.Filter.Data]?.ToString()?.Contains("DCT") == true)
            {
                bytes = [.. image.RawBytes];
                type = ImagePartType.Jpeg;
            }
            if (bytes is null || image.BoundingBox.Width < 4 || image.BoundingBox.Height < 4) return null;
            double width = Math.Min(ContentWidth, image.BoundingBox.Width), height = width * image.BoundingBox.Height / image.BoundingBox.Width;
            return new W.Paragraph(PictureRun(bytes, type, width, height));
        }

        W.Run PictureRun(byte[] bytes, PartTypeInfo type, double width, double height)
        {
            ImagePart part = main.AddImagePart(type);
            using (var stream = new MemoryStream(bytes)) part.FeedData(stream);
            string relationship = main.GetIdOfPart(part);
            uint id = ++_pictureId;
            long cx = (long)(width * EmuPerPoint), cy = (long)(height * EmuPerPoint);
            var graphic = new A.Graphic(new A.GraphicData(
                new Pic.Picture(
                    new Pic.NonVisualPictureProperties(new Pic.NonVisualDrawingProperties { Id = id, Name = $"Picture {id}" }, new Pic.NonVisualPictureDrawingProperties()),
                    new Pic.BlipFill(new A.Blip { Embed = relationship }, new A.Stretch(new A.FillRectangle())),
                    new Pic.ShapeProperties(new A.Transform2D(new A.Offset { X = 0, Y = 0 }, new A.Extents { Cx = cx, Cy = cy }), new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle })))
            { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" });
            var inline = new DW.Inline(
                new DW.Extent { Cx = cx, Cy = cy },
                new DW.EffectExtent { LeftEdge = 0, TopEdge = 0, RightEdge = 0, BottomEdge = 0 },
                new DW.DocProperties { Id = id, Name = $"Picture {id}" },
                new DW.NonVisualGraphicFrameDrawingProperties(new A.GraphicFrameLocks { NoChangeAspect = true }),
                graphic)
            { DistanceFromTop = 0, DistanceFromBottom = 0, DistanceFromLeft = 0, DistanceFromRight = 0 };
            return new W.Run(new W.Drawing(inline));
        }
    }
}
