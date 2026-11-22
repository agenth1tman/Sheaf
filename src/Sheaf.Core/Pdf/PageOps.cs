using System.Globalization;
using System.Text;
using PdfSharp.Pdf;

namespace Sheaf.Core.Pdf;

/// <summary>One page of a source document, turned clockwise by <see cref="Rotation"/> degrees.</summary>
public readonly record struct PagePick(int Index, int Rotation = 0);

/// <summary>Page-level operations: combining, picking, reordering and turning pages.</summary>
public static class PageOps
{
    public static PdfDocument Merge(IEnumerable<PdfSource> sources, CancellationToken cancel = default)
    {
        var output = NewDocument();
        foreach (PdfSource source in sources)
        {
            cancel.ThrowIfCancellationRequested();
            using PdfDocument input = PdfFiles.OpenForImport(source.Path, source.Password);
            foreach (PdfPage page in input.Pages)
                output.AddPage(page);
        }
        return output;
    }

    /// <summary>Builds a new document from pages of <paramref name="source"/> (opened for import).</summary>
    public static PdfDocument Assemble(PdfDocument source, IEnumerable<PagePick> picks)
    {
        var output = NewDocument();
        output.Info.Title = source.Info.Title;
        output.Info.Author = source.Info.Author;
        foreach (PagePick pick in picks)
        {
            PdfPage page = output.AddPage(source.Pages[pick.Index]);
            if (pick.Rotation != 0)
                page.Rotate = NormalizeRotation(page.Rotate + pick.Rotation);
        }
        return output;
    }

    public static PdfDocument Assemble(PdfDocument source, IEnumerable<int> pageIndexes) =>
        Assemble(source, pageIndexes.Select(index => new PagePick(index)));

    /// <summary>Turns pages of a document opened for modifying.</summary>
    public static void Rotate(PdfDocument document, IEnumerable<int> pageIndexes, int degrees)
    {
        foreach (int index in pageIndexes)
        {
            PdfPage page = document.Pages[index];
            page.Rotate = NormalizeRotation(page.Rotate + degrees);
        }
    }

    public static IReadOnlyList<int> AllPages(int pageCount) => [.. Enumerable.Range(0, pageCount)];

    public static IReadOnlyList<int> Without(int pageCount, IEnumerable<int> removed)
    {
        var skip = removed.ToHashSet();
        return [.. Enumerable.Range(0, pageCount).Where(index => !skip.Contains(index))];
    }

    /// <summary>Splits page indexes into consecutive groups of <paramref name="size"/> pages.</summary>
    public static IReadOnlyList<PageSpan> Chunks(int pageCount, int size)
    {
        var spans = new List<PageSpan>();
        for (int first = 1; first <= pageCount; first += size)
            spans.Add(new PageSpan(first, Math.Min(first + size - 1, pageCount)));
        return spans;
    }

    public static int NormalizeRotation(int degrees) => ((degrees % 360) + 360) % 360;

    static PdfDocument NewDocument()
    {
        var document = new PdfDocument();
        document.Info.Creator = "Sheaf";
        return document;
    }

    /// <summary>
    /// Mirrors what is on the page: left to right, upside down or both, as the page shows (after its own /Rotate).
    /// The content is wrapped in a flip around the middle of the visible area; links and form fields stay put.
    /// </summary>
    public static void Mirror(PdfPage page, bool leftToRight, bool upsideDown)
    {
        if (!leftToRight && !upsideDown) return;
        // On a page shown sideways, the page's own x axis runs up the screen.
        bool sideways = NormalizeRotation(page.Rotate) is 90 or 270;
        (bool flipX, bool flipY) = sideways ? (upsideDown, leftToRight) : (leftToRight, upsideDown);
        PdfRectangle box = page.Elements.ContainsKey("/CropBox") ? page.CropBox : page.MediaBox;
        string flip = string.Create(CultureInfo.InvariantCulture,
            $"q {(flipX ? -1 : 1)} 0 0 {(flipY ? -1 : 1)} {(flipX ? box.X1 + box.X2 : 0):0.###} {(flipY ? box.Y1 + box.Y2 : 0):0.###} cm\n");
        page.Contents.PrependContent().CreateStream(Encoding.ASCII.GetBytes(flip));
        page.Contents.AppendContent().CreateStream("\nQ\n"u8.ToArray());
    }

    /// <summary>Fills the page with a colour underneath everything on it: a blank page's paper colour.</summary>
    public static void Paint(PdfPage page, System.Drawing.Color color)
    {
        using var gfx = PdfSharp.Drawing.XGraphics.FromPdfPage(page, PdfSharp.Drawing.XGraphicsPdfPageOptions.Prepend);
        gfx.DrawRectangle(new PdfSharp.Drawing.XSolidBrush(PdfSharp.Drawing.XColor.FromArgb(255, color.R, color.G, color.B)), 0, 0, page.Width.Point, page.Height.Point);
    }
}
