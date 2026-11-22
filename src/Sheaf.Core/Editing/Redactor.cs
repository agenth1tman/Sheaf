using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using Sheaf.Core.Ocr;
using Sheaf.Core.Pdf;
using Sheaf.Core.Rendering;

namespace Sheaf.Core.Editing;

/// <summary>
/// Applies redaction marks for good. On a page marked only over words, the letters under the marks are taken
/// out of the page's text and the covers drawn on top; everything else on the page stays as it was, text and
/// all. Any other page with marks is redrawn as an image with the covers painted in, so the text, images and
/// fields under a mark are gone from the file, not just hidden; with <c>searchable</c> those pages get their
/// remaining text back through OCR. Pages without marks are untouched. The result is always a new file.
/// </summary>
public static class Redactor
{
    const float Dpi = 200;
    const long JpegQuality = 88;

    /// <summary>Writes the redacted copy to <paramref name="path"/>; returns how many marks were applied.</summary>
    public static async Task<int> ApplyAsync(EditDocument document, string path, string workFolder, bool clean, bool searchable = false, CancellationToken cancel = default)
    {
        var marks = document.Pages.Select(page => page.Objects.Where(item => item.Kind == MarkKind.Redaction && !item.Hidden).ToList()).ToList();
        EditDocument flat = document.Clone();
        foreach (PageEntry page in flat.Pages) page.Objects.RemoveAll(item => item.Kind == MarkKind.Redaction);
        string? password = flat.Password;
        flat.Password = null;

        Directory.CreateDirectory(workFolder);
        string temp = Path.Combine(workFolder, $"redact-{Guid.NewGuid():N}.pdf");
        var pictures = new Dictionary<int, byte[]>();
        var texts = new Dictionary<int, List<PageWord>>();
        var patches = new Dictionary<int, List<(RectangleF Area, byte[] Jpeg)>>();
        var images = new List<XImage>();
        PdfDocument? output = null;
        try
        {
            await Task.Run(() => DocumentStore.Save(flat, temp, keepEditable: false, cancel), cancel).ConfigureAwait(false);
            // Pages marked only over words lose just those letters, when the page's text can be followed well enough.
            var lettersOut = new HashSet<int>();
            output = await Task.Run(() =>
            {
                PdfDocument opened = PdfFiles.OpenForModify(temp);
                for (int i = 0; i < marks.Count && i < opened.PageCount; i++)
                {
                    if (marks[i].Count == 0 || !marks[i].All(mark => mark.RedactShape == RedactShape.Text)) continue;
                    cancel.ThrowIfCancellationRequested();
                    PdfPage page = opened.Pages[i];
                    if (TextRemover.TryRemove(page, [.. marks[i].Select(mark => ToPdf(page, document.Pages[i], mark.Bounds))], out _)) lettersOut.Add(i);
                }
                return opened;
            }, cancel).ConfigureAwait(false);
            using (PdfPreview preview = await PdfPreview.OpenAsync(temp).ConfigureAwait(false))
            {
                for (int i = 0; i < marks.Count; i++)
                {
                    if (marks[i].Count == 0) continue;
                    bool scrambled = marks[i].Any(mark => mark.Cover is RedactCover.Pixelate or RedactCover.Blur);
                    if (lettersOut.Contains(i) && !scrambled) continue;
                    cancel.ThrowIfCancellationRequested();
                    SizeF size = preview.GetPageSize(i);
                    using Bitmap bitmap = await preview.RenderAsync(i, (int)Math.Round(size.Width / 72 * Dpi), cancel).ConfigureAwait(false);
                    if (lettersOut.Contains(i))
                    {
                        // The pixelated or blurred look still comes from a picture of the words, laid over the gap they left.
                        patches[i] = Patches(bitmap, document.Pages[i], marks[i], bitmap.Width / size.Width);
                        continue;
                    }
                    List<RectangleF> covered = Cover(bitmap, document.Pages[i], marks[i], bitmap.Width / size.Width);
                    pictures[i] = Jpeg(bitmap);
                    if (searchable && await ReadAroundAsync(bitmap, covered, cancel).ConfigureAwait(false) is { } words)
                        texts[i] = [.. words.Select(word => word with { Bounds = Scale(word.Bounds, size.Width / bitmap.Width) })];
                }
            }

            await Task.Run(() =>
            {
                foreach (int index in lettersOut)
                    DrawCovers(output.Pages[index], document.Pages[index], marks[index], patches.GetValueOrDefault(index) ?? [], images);
                foreach (var (index, jpeg) in pictures)
                {
                    PdfPage old = output.Pages[index];
                    XSize size = PageCanvas.VisualSize(old);
                    RemoveFieldsOn(output, old);
                    PdfPage page = output.InsertPage(index);
                    page.Width = XUnit.FromPoint(size.Width);
                    page.Height = XUnit.FromPoint(size.Height);
                    XImage image = XImage.FromStream(new MemoryStream(jpeg));
                    images.Add(image);
                    using (XGraphics gfx = XGraphics.FromPdfPage(page)) gfx.DrawImage(image, 0, 0, size.Width, size.Height);
                    if (texts.TryGetValue(index, out List<PageWord>? words)) TextLayer.Add(page, words);
                    output.Pages.Remove(old);
                }
                if (clean) Sanitizer.Clean(output, new CleanOptions(Metadata: true, Comments: true, Scripts: true, Attachments: true));
                if (password is not null) PdfSecurity.Protect(output, password);
                PdfFiles.Save(output, path);
            }, cancel).ConfigureAwait(false);
            return marks.Sum(list => list.Count);
        }
        finally
        {
            output?.Dispose();
            foreach (XImage image in images) image.Dispose();
            try { File.Delete(temp); } catch (IOException) { }
        }
    }

    /// <summary>A mark's box in the saved page's PDF user space, a little larger so letters on its edge go too.</summary>
    static XRect ToPdf(PdfPage page, PageEntry entry, RectangleF bounds)
    {
        using Matrix turn = Turn(entry);
        PointF[] corners = [new(bounds.Left, bounds.Top), new(bounds.Right, bounds.Bottom)];
        turn.TransformPoints(corners);
        var area = new XRect(PageCanvas.ToPdf(page, new XPoint(corners[0].X, corners[0].Y)), PageCanvas.ToPdf(page, new XPoint(corners[1].X, corners[1].Y)));
        area.Inflate(0.5, 0.5);
        return area;
    }

    /// <summary>Draws the covers of text marks over the gaps their letters left, as shapes (or pictures, for pixelate and blur).</summary>
    static void DrawCovers(PdfPage page, PageEntry entry, List<PageObject> marks, List<(RectangleF Area, byte[] Jpeg)> patches, List<XImage> images)
    {
        using PageCanvas canvas = PageCanvas.Open(page);
        using Matrix turn = Turn(entry);
        foreach (PageObject mark in marks.Where(mark => mark.Cover is not (RedactCover.Pixelate or RedactCover.Blur)))
        {
            using GraphicsPath path = Shape(mark);
            path.Transform(turn);
            RectangleF box = path.GetBounds();
            Color fill = mark.Cover switch { RedactCover.White => Color.White, RedactCover.Color => Color.FromArgb(255, Color.FromArgb(mark.FillColor)), _ => Color.Black };
            canvas.Graphics.DrawRectangle(new XSolidBrush(XColor.FromArgb(255, fill.R, fill.G, fill.B)), box.X, box.Y, box.Width, box.Height);
        }
        foreach (var (area, jpeg) in patches)
        {
            XImage image = XImage.FromStream(new MemoryStream(jpeg));
            images.Add(image);
            canvas.Graphics.DrawImage(image, area.X, area.Y, area.Width, area.Height);
        }
    }

    /// <summary>Pictures of the pixelated or blurred marks, with where each goes on the page as shown (in points).</summary>
    static List<(RectangleF Area, byte[] Jpeg)> Patches(Bitmap bitmap, PageEntry entry, List<PageObject> marks, float scale)
    {
        var patches = new List<(RectangleF, byte[])>();
        using Matrix toBitmap = Turn(entry);
        toBitmap.Scale(scale, scale, MatrixOrder.Append);
        var canvas = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        foreach (PageObject mark in marks.Where(mark => mark.Cover is RedactCover.Pixelate or RedactCover.Blur))
        {
            using GraphicsPath path = Shape(mark);
            path.Transform(toBitmap);
            Rectangle area = Rectangle.Intersect(Rectangle.Ceiling(path.GetBounds()), canvas);
            if (area.Width <= 0 || area.Height <= 0) continue;
            using Bitmap small = Shrink(bitmap, area, mark.Cover, out InterpolationMode stretch);
            using var patch = new Bitmap(area.Width, area.Height);
            using (Graphics g = Graphics.FromImage(patch))
            {
                g.InterpolationMode = stretch;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(small, new Rectangle(0, 0, area.Width, area.Height));
            }
            patches.Add((new RectangleF(area.X / scale, area.Y / scale, area.Width / scale, area.Height / scale), Jpeg(patch)));
        }
        return patches;
    }

    /// <summary>Maps page points (before a turn added in Sheaf) to the page as it is saved, turned.</summary>
    public static Matrix Turn(PageEntry entry) => entry.Rotation switch
    {
        90 => new Matrix(0, 1, -1, 0, entry.Height, 0),
        180 => new Matrix(-1, 0, 0, -1, entry.Width, entry.Height),
        270 => new Matrix(0, -1, 1, 0, 0, entry.Width),
        _ => new Matrix(),
    };

    public static GraphicsPath Shape(PageObject mark)
    {
        var path = new GraphicsPath();
        switch (mark.RedactShape)
        {
            case RedactShape.Ellipse: path.AddEllipse(mark.Bounds); break;
            case RedactShape.Freeform when mark.Strokes.FirstOrDefault() is { Count: > 2 } points: path.AddPolygon([.. points]); break;
            default: path.AddRectangle(mark.Bounds); break;
        }
        return path;
    }

    /// <summary>
    /// The text OCR reads on a redrawn page, leaving out any word that touches a mark (a pixelated or blurred
    /// area can read as letters). Null when Windows has no OCR language.
    /// </summary>
    static async Task<List<PageWord>?> ReadAroundAsync(Bitmap page, List<RectangleF> covered, CancellationToken cancel)
    {
        try
        {
            List<PageWord> words = await TextRecognizer.ReadAsync(page, null, cancel).ConfigureAwait(false);
            return [.. words.Where(word => !covered.Any(area => RectangleF.Inflate(area, 4, 4).IntersectsWith(word.Bounds)))];
        }
        catch (ToolException)
        {
            return null;
        }
    }

    static RectangleF Scale(RectangleF box, float scale) => new(box.X * scale, box.Y * scale, box.Width * scale, box.Height * scale);

    /// <summary>Paints each mark's cover onto the page image; returns the areas covered, in pixels.</summary>
    static List<RectangleF> Cover(Bitmap bitmap, PageEntry entry, List<PageObject> marks, float scale)
    {
        var covered = new List<RectangleF>();
        using Graphics g = Graphics.FromImage(bitmap);
        using Matrix toBitmap = Turn(entry);
        toBitmap.Scale(scale, scale, MatrixOrder.Append);
        var canvas = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        foreach (PageObject mark in marks)
        {
            using GraphicsPath path = Shape(mark);
            path.Transform(toBitmap);
            covered.Add(path.GetBounds());
            Rectangle area = Rectangle.Intersect(Rectangle.Ceiling(path.GetBounds()), canvas);
            area.Inflate(1, 1);
            area.Intersect(canvas);
            if (area.Width <= 0 || area.Height <= 0) continue;
            switch (mark.Cover)
            {
                case RedactCover.Pixelate or RedactCover.Blur: Scramble(bitmap, g, path, area, mark.Cover); break;
                default:
                    Color fill = mark.Cover switch { RedactCover.White => Color.White, RedactCover.Color => Color.FromArgb(255, Color.FromArgb(mark.FillColor)), _ => Color.Black };
                    using (var brush = new SolidBrush(fill)) g.FillPath(brush, path);
                    using (var edge = new Pen(fill, 2)) g.DrawPath(edge, path);
                    break;
            }
        }
        return covered;
    }

    /// <summary>
    /// The area of a page image shrunk to a few cells, for a pixelate or blur cover: stretched back with
    /// <paramref name="stretch"/>, it becomes blocks or a smear. The editor's preview uses it too, so it looks the same.
    /// </summary>
    public static Bitmap Shrink(Bitmap page, Rectangle area, RedactCover cover, out InterpolationMode stretch)
    {
        bool pixelate = cover == RedactCover.Pixelate;
        stretch = pixelate ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBilinear;
        float cell = Math.Max(6, Math.Max(area.Width, area.Height) / (pixelate ? 14f : 24f));
        var small = new Bitmap(Math.Max(1, (int)(area.Width / cell)), Math.Max(1, (int)(area.Height / cell)));
        using Graphics shrink = Graphics.FromImage(small);
        shrink.InterpolationMode = InterpolationMode.HighQualityBilinear;
        shrink.DrawImage(page, new Rectangle(0, 0, small.Width, small.Height), area, GraphicsUnit.Pixel);
        return small;
    }

    /// <summary>Shrinks the area to a few cells and stretches it back: blocks (pixelate) or a smear (blur).</summary>
    static void Scramble(Bitmap bitmap, Graphics g, GraphicsPath path, Rectangle area, RedactCover cover)
    {
        using Bitmap small = Shrink(bitmap, area, cover, out InterpolationMode stretch);
        GraphicsState state = g.Save();
        g.SetClip(path);
        g.InterpolationMode = stretch;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(small, area);
        g.Restore(state);
    }

    static byte[] Jpeg(Bitmap bitmap)
    {
        ImageCodecInfo codec = ImageCodecInfo.GetImageEncoders().First(encoder => encoder.FormatID == ImageFormat.Jpeg.Guid);
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(Encoder.Quality, JpegQuality);
        using var stream = new MemoryStream();
        bitmap.Save(stream, codec, parameters);
        return stream.ToArray();
    }

    /// <summary>Removes form fields whose widgets sit on a page that is being replaced, values and all.</summary>
    static void RemoveFieldsOn(PdfDocument document, PdfPage page)
    {
        if (document.Internals.Catalog.Elements.GetDictionary("/AcroForm")?.Elements.GetArray("/Fields") is not { } fields || page.Reference is null) return;
        for (int i = fields.Elements.Count - 1; i >= 0; i--)
        {
            PdfDictionary? field = fields.Elements.GetDictionary(i);
            if (field is not null && OnPage(field, page.Reference.ObjectID)) fields.Elements.RemoveAt(i);
        }
    }

    static bool OnPage(PdfDictionary field, PdfObjectID page)
    {
        if (field.Elements["/P"] is PdfSharp.Pdf.Advanced.PdfReference reference && reference.ObjectID == page) return true;
        if (field.Elements.GetArray("/Kids") is not { } kids) return false;
        for (int i = 0; i < kids.Elements.Count; i++)
            if (kids.Elements.GetDictionary(i) is { } kid && OnPage(kid, page)) return true;
        return false;
    }
}
