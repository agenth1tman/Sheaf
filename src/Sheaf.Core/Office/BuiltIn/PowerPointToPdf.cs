using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using PdfSharp.Drawing;
using PdfSharp.Drawing.Layout;
using PdfSharp.Pdf;
using Sheaf.Core.Pdf;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

namespace Sheaf.Core.Office.BuiltIn;

/// <summary>
/// Turns a presentation (.pptx) into a PDF without PowerPoint, one page per shown slide: backgrounds, the
/// master's and layout's artwork, shapes with fills and outlines, pictures, groups, tables and text (placed
/// and sized from the layout's placeholders and the master's text styles). Charts, SmartArt, gradients,
/// effects and animations are not drawn.
/// </summary>
public static class PowerPointToPdf
{
    const double EmuPerPoint = 12700;

    public static void Convert(string input, string output, CancellationToken cancel = default)
    {
        using PresentationDocument presentation = Open(input);
        PresentationPart part = presentation.PresentationPart ?? throw new ToolException($"“{Path.GetFileName(input)}” has no slides inside.");
        P.SlideSize? size = part.Presentation?.SlideSize;
        double width = (size?.Cx?.Value ?? 9144000) / EmuPerPoint, height = (size?.Cy?.Value ?? 6858000) / EmuPerPoint;

        using var document = new PdfDocument();
        var images = new List<XImage>();
        try
        {
            foreach (P.SlideId id in part.Presentation?.SlideIdList?.Elements<P.SlideId>() ?? [])
            {
                cancel.ThrowIfCancellationRequested();
                if (id.RelationshipId?.Value is not { } rel || part.GetPartById(rel) is not SlidePart slide) continue;
                if (slide.Slide?.Show?.Value == false) continue;
                PdfPage page = document.AddPage();
                page.Width = XUnit.FromPoint(width);
                page.Height = XUnit.FromPoint(height);
                using XGraphics gfx = XGraphics.FromPdfPage(page);
                new SlideDrawer(gfx, slide, width, height, images).Draw();
            }
            if (document.PageCount == 0) throw new ToolException($"“{Path.GetFileName(input)}” has no slides to show.");
            PdfFiles.Save(document, output);
        }
        finally
        {
            foreach (XImage image in images) image.Dispose();
        }
    }

    static PresentationDocument Open(string input)
    {
        try
        {
            return PresentationDocument.Open(input, false);
        }
        catch (Exception ex) when (ex is OpenXmlPackageException or InvalidDataException or IOException or FileFormatException)
        {
            throw new ToolException($"“{Path.GetFileName(input)}” can’t be read. The built-in converter reads presentations saved as .pptx; save .ppt files as .pptx first.", ex);
        }
    }

    /// <summary>Maps a group's child coordinates into slide coordinates (EMU).</summary>
    readonly record struct Frame(double OffsetX, double OffsetY, double ScaleX, double ScaleY)
    {
        public static readonly Frame Slide = new(0, 0, 1, 1);
        public XRect Map(double x, double y, double cx, double cy) =>
            new((OffsetX + x * ScaleX) / EmuPerPoint, (OffsetY + y * ScaleY) / EmuPerPoint, cx * ScaleX / EmuPerPoint, cy * ScaleY / EmuPerPoint);
    }

    sealed class SlideDrawer(XGraphics gfx, SlidePart slide, double width, double height, List<XImage> images)
    {
        readonly SlideLayoutPart? _layout = slide.SlideLayoutPart;
        readonly SlideMasterPart? _master = slide.SlideLayoutPart?.SlideMasterPart;
        A.ColorScheme? Scheme => _master?.ThemePart?.Theme?.ThemeElements?.ColorScheme;

        public void Draw()
        {
            DrawBackground();
            if (_master?.SlideMaster?.CommonSlideData?.ShapeTree is { } masterShapes && _layout?.SlideLayout?.ShowMasterShapes?.Value != false)
                DrawTree(masterShapes, _master, Frame.Slide, placeholders: false);
            if (_layout?.SlideLayout?.CommonSlideData?.ShapeTree is { } layoutShapes && slide.Slide?.ShowMasterShapes?.Value != false)
                DrawTree(layoutShapes, _layout, Frame.Slide, placeholders: false);
            if (slide.Slide?.CommonSlideData?.ShapeTree is { } shapes) DrawTree(shapes, slide, Frame.Slide, placeholders: true);
        }

        void DrawBackground()
        {
            P.Background? background = slide.Slide?.CommonSlideData?.Background
                ?? _layout?.SlideLayout?.CommonSlideData?.Background
                ?? _master?.SlideMaster?.CommonSlideData?.Background;
            XColor color = XColors.White;
            if (background?.BackgroundProperties is { } properties)
            {
                if (properties.GetFirstChild<A.SolidFill>() is { } fill && Color(fill) is { } solid) color = solid;
                else if (properties.GetFirstChild<A.GradientFill>()?.Descendants<A.GradientStop>().FirstOrDefault() is { } stop && Color(stop) is { } first) color = first;
            }
            else if (background?.BackgroundStyleReference is { } reference && Color(reference) is { } referenced) color = referenced;
            gfx.DrawRectangle(new XSolidBrush(color), 0, 0, width, height);
            if (background?.BackgroundProperties?.GetFirstChild<A.BlipFill>()?.Blip?.Embed?.Value is { } id && Image(OwnerOf(background), id) is { } picture)
                gfx.DrawImage(picture, 0, 0, width, height);
        }

        OpenXmlPart OwnerOf(OpenXmlElement element)
        {
            OpenXmlPartRootElement? root = element.Ancestors<OpenXmlPartRootElement>().FirstOrDefault();
            if (root == _master?.SlideMaster) return _master!;
            if (root == _layout?.SlideLayout) return _layout!;
            return slide;
        }

        void DrawTree(OpenXmlCompositeElement tree, OpenXmlPart owner, Frame frame, bool placeholders)
        {
            foreach (OpenXmlElement element in tree.ChildElements)
            {
                switch (element)
                {
                    case P.Shape shape:
                        bool isPlaceholder = Placeholder(shape) is not null;
                        if (isPlaceholder && !placeholders) continue;
                        DrawShape(shape, owner, frame);
                        break;
                    case P.Picture picture:
                        DrawPicture(picture, owner, frame);
                        break;
                    case P.GroupShape group:
                        DrawTree(group, owner, GroupFrame(group, frame), placeholders);
                        break;
                    case P.ConnectionShape connector:
                        DrawConnector(connector, frame);
                        break;
                    case P.GraphicFrame graphic when graphic.Descendants<A.Table>().FirstOrDefault() is { } table:
                        DrawTable(graphic, table, frame);
                        break;
                }
            }
        }

        static Frame GroupFrame(P.GroupShape group, Frame parent)
        {
            A.TransformGroup? xfrm = group.GroupShapeProperties?.TransformGroup;
            if (xfrm?.Offset is null || xfrm.Extents is null || xfrm.ChildOffset is null || xfrm.ChildExtents is null) return parent;
            double sx = xfrm.ChildExtents.Cx?.Value is > 0 and long ccx ? (double)xfrm.Extents.Cx!.Value / ccx : 1;
            double sy = xfrm.ChildExtents.Cy?.Value is > 0 and long ccy ? (double)xfrm.Extents.Cy!.Value / ccy : 1;
            double ox = xfrm.Offset.X!.Value - xfrm.ChildOffset.X!.Value * sx, oy = xfrm.Offset.Y!.Value - xfrm.ChildOffset.Y!.Value * sy;
            return new Frame(parent.OffsetX + ox * parent.ScaleX, parent.OffsetY + oy * parent.ScaleY, parent.ScaleX * sx, parent.ScaleY * sy);
        }

        static P.PlaceholderShape? Placeholder(P.Shape shape) =>
            shape.NonVisualShapeProperties?.ApplicationNonVisualDrawingProperties?.PlaceholderShape;

        /// <summary>The same placeholder on the layout, then the master: where a slide's placeholder gets its place and text style from.</summary>
        IEnumerable<P.Shape> Inherited(P.PlaceholderShape? placeholder)
        {
            if (placeholder is null) yield break;
            foreach (OpenXmlPartRootElement? root in new OpenXmlPartRootElement?[] { _layout?.SlideLayout, _master?.SlideMaster })
            {
                if (root is null) continue;
                var candidates = root.Descendants<P.Shape>().Where(shape => Placeholder(shape) is not null).ToList();
                P.Shape? match = candidates.FirstOrDefault(shape => placeholder.Index?.Value is uint index && Placeholder(shape)!.Index?.Value == index)
                    ?? candidates.FirstOrDefault(shape => SameType(Placeholder(shape)!, placeholder));
                if (match is not null) yield return match;
            }
        }

        static bool SameType(P.PlaceholderShape a, P.PlaceholderShape b)
        {
            P.PlaceholderValues Kind(P.PlaceholderShape shape) =>
                shape.Type?.Value is { } type ? (type == P.PlaceholderValues.CenteredTitle ? P.PlaceholderValues.Title : type) : P.PlaceholderValues.Body;
            return Kind(a) == Kind(b);
        }

        bool IsTitle(P.PlaceholderShape? placeholder) =>
            placeholder?.Type?.Value is { } type && (type == P.PlaceholderValues.Title || type == P.PlaceholderValues.CenteredTitle);

        void DrawShape(P.Shape shape, OpenXmlPart owner, Frame frame)
        {
            P.PlaceholderShape? placeholder = Placeholder(shape);
            A.Transform2D? xfrm = shape.ShapeProperties?.Transform2D ?? Inherited(placeholder).Select(item => item.ShapeProperties?.Transform2D).FirstOrDefault(item => item is not null);
            if (xfrm?.Offset is null || xfrm.Extents is null) return;
            XRect box = frame.Map(xfrm.Offset.X!.Value, xfrm.Offset.Y!.Value, xfrm.Extents.Cx!.Value, xfrm.Extents.Cy!.Value);

            string geometry = shape.ShapeProperties?.GetFirstChild<A.PresetGeometry>()?.Preset?.Value.ToString() ?? "rect";
            XBrush? fill = Fill(shape.ShapeProperties) ?? StyleFill(shape);
            XPen? pen = Outline(shape.ShapeProperties?.GetFirstChild<A.Outline>()) ?? StyleOutline(shape);
            if (fill is not null || pen is not null)
            {
                if (geometry.Contains("ellipse", StringComparison.OrdinalIgnoreCase)) gfx.DrawEllipse(pen, fill, box);
                else if (geometry.Contains("roundRect", StringComparison.OrdinalIgnoreCase))
                    gfx.DrawRoundedRectangle(pen, fill, box, new XSize(Math.Min(box.Width, box.Height) * 0.33, Math.Min(box.Width, box.Height) * 0.33));
                else if (geometry.Contains("line", StringComparison.OrdinalIgnoreCase) && pen is not null) gfx.DrawLine(pen, box.TopLeft, box.BottomRight);
                else gfx.DrawRectangle(pen, fill, box);
            }
            if (shape.TextBody is { } body) DrawText(body, box, placeholder);
        }

        XBrush? StyleFill(P.Shape shape) =>
            shape.ShapeStyle?.FillReference is { } reference && reference.Index?.Value > 0 && Color(reference) is { } color ? new XSolidBrush(color) : null;

        XPen? StyleOutline(P.Shape shape) =>
            shape.ShapeStyle?.LineReference is { } reference && reference.Index?.Value > 0 && Color(reference) is { } color ? new XPen(color, 0.75) : null;

        XBrush? Fill(OpenXmlCompositeElement? properties)
        {
            if (properties is null || properties.GetFirstChild<A.NoFill>() is not null) return null;
            if (properties.GetFirstChild<A.SolidFill>() is { } solid && Color(solid) is { } color) return new XSolidBrush(color);
            if (properties.GetFirstChild<A.GradientFill>()?.Descendants<A.GradientStop>().FirstOrDefault() is { } stop && Color(stop) is { } first) return new XSolidBrush(first);
            return null;
        }

        XPen? Outline(A.Outline? outline)
        {
            if (outline is null || outline.GetFirstChild<A.NoFill>() is not null) return null;
            if (outline.GetFirstChild<A.SolidFill>() is not { } fill || Color(fill) is not { } color) return null;
            return new XPen(color, Math.Max(0.25, (outline.Width?.Value ?? 9525) / EmuPerPoint));
        }

        void DrawPicture(P.Picture picture, OpenXmlPart owner, Frame frame)
        {
            A.Transform2D? xfrm = picture.ShapeProperties?.Transform2D;
            if (xfrm?.Offset is null || xfrm.Extents is null) return;
            if (picture.BlipFill?.Blip?.Embed?.Value is not { } id || Image(owner, id) is not { } image) return;
            gfx.DrawImage(image, frame.Map(xfrm.Offset.X!.Value, xfrm.Offset.Y!.Value, xfrm.Extents.Cx!.Value, xfrm.Extents.Cy!.Value));
        }

        void DrawConnector(P.ConnectionShape connector, Frame frame)
        {
            A.Transform2D? xfrm = connector.ShapeProperties?.Transform2D;
            if (xfrm?.Offset is null || xfrm.Extents is null) return;
            XPen pen = Outline(connector.ShapeProperties?.GetFirstChild<A.Outline>()) ?? new XPen(XColors.Black, 0.75);
            XRect box = frame.Map(xfrm.Offset.X!.Value, xfrm.Offset.Y!.Value, xfrm.Extents.Cx!.Value, xfrm.Extents.Cy!.Value);
            bool flipH = xfrm.HorizontalFlip?.Value == true, flipV = xfrm.VerticalFlip?.Value == true;
            gfx.DrawLine(pen, flipH ? box.Right : box.Left, flipV ? box.Bottom : box.Top, flipH ? box.Left : box.Right, flipV ? box.Top : box.Bottom);
        }

        void DrawTable(P.GraphicFrame graphic, A.Table table, Frame frame)
        {
            P.Transform? xfrm = graphic.Transform;
            if (xfrm?.Offset is null) return;
            double x = xfrm.Offset.X!.Value, y = xfrm.Offset.Y!.Value;
            var widths = table.TableGrid?.Elements<A.GridColumn>().Select(column => (double)(column.Width?.Value ?? 914400)).ToList() ?? [];
            var line = new XPen(XColor.FromArgb(160, 160, 160), 0.5);
            foreach (A.TableRow row in table.Elements<A.TableRow>())
            {
                double rowHeight = row.Height?.Value ?? 370840, cellX = x;
                int column = 0;
                foreach (A.TableCell cell in row.Elements<A.TableCell>())
                {
                    double cellWidth = column < widths.Count ? widths[column] : 914400;
                    XRect box = frame.Map(cellX, y, cellWidth, rowHeight);
                    if (Fill(cell.TableCellProperties) is { } fill) gfx.DrawRectangle(fill, box);
                    gfx.DrawRectangle(line, box);
                    if (cell.TextBody is { } body) DrawText(body, box, null, defaultSize: 14);
                    cellX += cellWidth;
                    column++;
                }
                y += rowHeight;
            }
        }

        // Text -------------------------------------------------------------------------------

        sealed record Word(string Text, XFont Font, XBrush Brush, bool Space);

        void DrawText(OpenXmlCompositeElement body, XRect box, P.PlaceholderShape? placeholder, double defaultSize = 18)
        {
            A.BodyProperties? properties = body.GetFirstChild<A.BodyProperties>();
            double left = (properties?.LeftInset?.Value ?? 91440) / EmuPerPoint, right = (properties?.RightInset?.Value ?? 91440) / EmuPerPoint;
            double top = (properties?.TopInset?.Value ?? 45720) / EmuPerPoint, bottom = (properties?.BottomInset?.Value ?? 45720) / EmuPerPoint;
            double scale = (properties?.GetFirstChild<A.NormalAutoFit>()?.FontScale?.Value ?? 100000) / 100000.0;
            var area = new XRect(box.X + left, box.Y + top, Math.Max(1, box.Width - left - right), Math.Max(1, box.Height - top - bottom));

            var lines = new List<(List<Word> Words, double Height, XParagraphAlignment Align, double Indent)>();
            foreach (A.Paragraph paragraph in body.Elements<A.Paragraph>())
                lines.AddRange(LayOut(paragraph, area.Width, placeholder, defaultSize, scale));
            double total = lines.Sum(line => line.Height);
            double y = (properties?.Anchor?.Value is { } anchor ? anchor.ToString() : IsTitle(placeholder) ? "ctr" : "t") switch
            {
                "ctr" => area.Y + (area.Height - total) / 2,
                "b" => area.Bottom - total,
                _ => area.Y,
            };
            foreach (var (words, lineHeight, align, indent) in lines)
            {
                double lineWidth = words.Sum(word => Measure(word));
                double x = align switch
                {
                    XParagraphAlignment.Center => area.X + (area.Width - lineWidth) / 2,
                    XParagraphAlignment.Right => area.Right - lineWidth,
                    _ => area.X + indent,
                };
                foreach (Word word in words)
                {
                    gfx.DrawString(word.Text, word.Font, word.Brush, x, y + lineHeight * 0.8, XStringFormats.BaseLineLeft);
                    x += Measure(word);
                }
                y += lineHeight;
            }
        }

        double Measure(Word word) => gfx.MeasureString(word.Text + (word.Space ? " " : ""), word.Font).Width;

        /// <summary>Breaks a paragraph into lines that fit the width, keeping each run's font and colour.</summary>
        IEnumerable<(List<Word>, double, XParagraphAlignment, double)> LayOut(A.Paragraph paragraph, double width, P.PlaceholderShape? placeholder, double defaultSize, double scale)
        {
            A.ParagraphProperties? properties = paragraph.ParagraphProperties;
            int level = properties?.Level?.Value ?? 0;
            OpenXmlElement? inherited = LevelStyle(placeholder, level);
            double size = (FontSize(paragraph.Elements<A.Run>().FirstOrDefault()?.RunProperties) ?? FontSize(inherited?.GetFirstChild<A.DefaultRunProperties>()) ?? (IsTitle(placeholder) ? 40 : placeholder is null ? defaultSize : 24)) * scale;
            XParagraphAlignment align = (properties?.Alignment?.Value ?? (inherited as A.TextParagraphPropertiesType)?.Alignment?.Value)?.ToString() switch
            {
                "ctr" => XParagraphAlignment.Center,
                "r" => XParagraphAlignment.Right,
                _ => XParagraphAlignment.Left,
            };
            bool bullets = properties?.GetFirstChild<A.NoBullet>() is null
                && (properties?.GetFirstChild<A.CharacterBullet>() is not null || (placeholder is not null && !IsTitle(placeholder) && placeholder.Type?.Value != P.PlaceholderValues.SubTitle
                    && inherited?.GetFirstChild<A.NoBullet>() is null && inherited?.GetFirstChild<A.CharacterBullet>() is not null));
            double indent = level * size * 1.2;

            var words = new List<Word>();
            if (bullets)
            {
                string symbol = properties?.GetFirstChild<A.CharacterBullet>()?.Char?.Value ?? "•";
                words.Add(new Word(symbol.Length == 1 && symbol[0] >= 0xF000 ? "•" : symbol, Font(null, size, false, false), XBrushes.Black, true));
            }
            XColor? inheritedColor = inherited?.GetFirstChild<A.DefaultRunProperties>()?.GetFirstChild<A.SolidFill>() is { } inheritedFill ? Color(inheritedFill) : null;
            foreach (OpenXmlElement child in paragraph.ChildElements)
            {
                if (child is A.Break) { words.Add(new Word("\n", Font(null, size, false, false), XBrushes.Black, false)); continue; }
                if (child is not (A.Run or A.Field)) continue;
                var run = child.GetFirstChild<A.RunProperties>();
                double runSize = (FontSize(run) ?? size / scale) * scale;
                bool bold = run?.Bold?.Value ?? (inherited?.GetFirstChild<A.DefaultRunProperties>()?.Bold?.Value ?? false);
                bool italic = run?.Italic?.Value ?? false;
                XFont font = Font(run?.GetFirstChild<A.LatinFont>()?.Typeface?.Value, runSize, bold, italic);
                XColor color = (run?.GetFirstChild<A.SolidFill>() is { } fill ? Color(fill) : null) ?? inheritedColor ?? SchemeColor("tx1") ?? XColors.Black;
                var brush = new XSolidBrush(color);
                string text = child.GetFirstChild<A.Text>()?.Text ?? "";
                string[] parts = text.Split(' ');
                for (int i = 0; i < parts.Length; i++)
                    if (parts[i].Length > 0 || i < parts.Length - 1) words.Add(new Word(parts[i], font, brush, i < parts.Length - 1));
            }
            if (words.Count == 0)
            {
                yield return ([], size * 1.2, align, indent);
                yield break;
            }

            var line = new List<Word>();
            double used = 0, lineHeight = size * 1.2;
            foreach (Word word in words)
            {
                double wordWidth = Measure(word);
                if (word.Text == "\n" || (line.Count > 0 && used + gfx.MeasureString(word.Text, word.Font).Width > width - indent))
                {
                    yield return (line, lineHeight, align, indent);
                    line = [];
                    used = 0;
                    lineHeight = size * 1.2;
                    if (word.Text == "\n") continue;
                }
                line.Add(word);
                used += wordWidth;
                lineHeight = Math.Max(lineHeight, word.Font.Size * 1.2);
            }
            if (line.Count > 0) yield return (line, lineHeight, align, indent);
        }

        /// <summary>The master's text style for a placeholder kind and level (its default size, colour, bullets).</summary>
        OpenXmlElement? LevelStyle(P.PlaceholderShape? placeholder, int level)
        {
            static A.TextParagraphPropertiesType? Level(OpenXmlElement? list, int level) =>
                list?.ChildElements.OfType<A.TextParagraphPropertiesType>().Where(item => item is not A.DefaultParagraphProperties).ElementAtOrDefault(level);

            foreach (P.Shape shape in Inherited(placeholder))
                if (Level(shape.TextBody?.GetFirstChild<A.ListStyle>(), level) is { } own && own.GetFirstChild<A.DefaultRunProperties>()?.FontSize is not null)
                    return own;
            P.TextStyles? styles = _master?.SlideMaster?.TextStyles;
            return Level(IsTitle(placeholder) ? styles?.TitleStyle : placeholder is null ? styles?.OtherStyle : styles?.BodyStyle, level);
        }

        static double? FontSize(OpenXmlElement? properties) =>
            properties?.GetAttributes().FirstOrDefault(attribute => attribute.LocalName == "sz").Value is { } size && int.TryParse(size, out int hundredths) ? hundredths / 100.0 : null;

        static XFont Font(string? typeface, double size, bool bold, bool italic)
        {
            string family = typeface is null || typeface.StartsWith('+') ? "Calibri" : typeface;
            var style = (bold, italic) switch { (true, true) => XFontStyleEx.BoldItalic, (true, false) => XFontStyleEx.Bold, (false, true) => XFontStyleEx.Italic, _ => XFontStyleEx.Regular };
            return new XFont(family, Math.Clamp(size, 1, 400), style);
        }

        // Colours ----------------------------------------------------------------------------

        XColor? Color(OpenXmlElement holder)
        {
            if (holder.GetFirstChild<A.RgbColorModelHex>()?.Val?.Value is { } hex) return Hex(hex);
            if (holder.GetFirstChild<A.SchemeColor>()?.Val?.Value is { } scheme) return SchemeColor(scheme.ToString());
            if (holder.GetFirstChild<A.SystemColor>()?.LastColor?.Value is { } system) return Hex(system);
            if (holder.GetFirstChild<A.PresetColor>()?.Val?.Value is { } preset) return preset.ToString() == "white" ? XColors.White : preset.ToString() == "black" ? XColors.Black : null;
            return null;
        }

        XColor? SchemeColor(string name)
        {
            A.ColorScheme? scheme = Scheme;
            if (scheme is null) return name is "bg1" or "lt1" ? XColors.White : XColors.Black;
            OpenXmlCompositeElement? entry = name switch
            {
                "tx1" or "dk1" => scheme.Dark1Color,
                "bg1" or "lt1" => scheme.Light1Color,
                "tx2" or "dk2" => scheme.Dark2Color,
                "bg2" or "lt2" => scheme.Light2Color,
                "accent1" => scheme.Accent1Color,
                "accent2" => scheme.Accent2Color,
                "accent3" => scheme.Accent3Color,
                "accent4" => scheme.Accent4Color,
                "accent5" => scheme.Accent5Color,
                "accent6" => scheme.Accent6Color,
                "hlink" => scheme.Hyperlink,
                _ => null,
            };
            return entry is null ? null : Color(entry);
        }

        static XColor? Hex(string hex) =>
            hex.Length == 6 && int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out int rgb) ? XColor.FromArgb(255, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF) : null;

        XImage? Image(OpenXmlPart owner, string id)
        {
            try
            {
                if (owner.GetPartById(id) is not ImagePart part) return null;
                using Stream stream = part.GetStream();
                var copy = new MemoryStream();
                stream.CopyTo(copy);
                copy.Position = 0;
                XImage image = XImage.FromStream(copy);
                images.Add(image);
                return image;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException or OutOfMemoryException)
            {
                return null;
            }
        }
    }
}
