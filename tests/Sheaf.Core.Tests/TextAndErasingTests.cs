using System.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Sheaf.Core.Editing;
using Sheaf.Core.Rendering;

namespace Sheaf.Core.Tests;

public class TextAndErasingTests
{
    // Every character one point wide, every line ten high: easy to check where things land.
    static List<TextLine> Lay(PageObject item, float width) => TextLayout.Lay(item, width, (text, _) => text.Length, _ => 10);

    [Fact]
    public void AWordCanHaveAStyleOfItsOwn()
    {
        var item = new PageObject
        {
            Kind = MarkKind.Text, Text = "a bold word", StrokeColor = Color.Black.ToArgb(),
            Spans = [new TextSpan { Length = 2, Color = Color.Black.ToArgb() }, new TextSpan { Length = 4, Bold = true, Color = Color.Red.ToArgb() }, new TextSpan { Length = 5, Color = Color.Black.ToArgb() }],
        };
        var runs = TextLayout.Runs(item);
        Assert.Equal(["a ", "bold", " word"], runs.Select(run => run.Text));
        Assert.True(runs[1].Style.Bold);
        Assert.Equal(Color.Red.ToArgb(), runs[1].Style.Color);

        TextLine line = Assert.Single(Lay(item, 100));
        TextPiece bold = line.Pieces.Single(piece => piece.Style.Bold);
        Assert.Equal("bold", bold.Text);
        Assert.Equal(2, bold.X);
    }

    [Fact]
    public void StyledTextWrapsAtWordsAndNeverInsideAWordWhoseStyleChanges()
    {
        var item = new PageObject
        {
            Kind = MarkKind.Text, Text = "one twothree four",
            Spans = [new TextSpan { Length = 7 }, new TextSpan { Length = 5, Italic = true }, new TextSpan { Length = 5 }],
        };
        List<TextLine> lines = Lay(item, 10);
        Assert.Equal(["one", "twothree", "four"], lines.Select(line => string.Concat(line.Pieces.Select(piece => piece.Text))));
        Assert.Equal([0f, 10f, 20f], lines.Select(line => line.Top));
    }

    [Fact]
    public void StyledTextIsCentredAndJustifiedLikePlainText()
    {
        var spans = new List<TextSpan> { new() { Length = 7, Underline = true } };
        var centred = new PageObject { Kind = MarkKind.Text, Text = "abc def", Align = TextAlign.Center, Spans = spans };
        Assert.Equal(1.5f, Lay(centred, 10)[0].Pieces[0].X);

        var justified = new PageObject { Kind = MarkKind.Text, Text = "aa bb cc dd", Align = TextAlign.Justify, Spans = [new TextSpan { Length = 11 }] };
        List<TextLine> lines = Lay(justified, 9);
        // The first line spreads to the width; the last line of the paragraph stays as it is.
        Assert.Equal(7, lines[0].Pieces.Single(piece => piece.Text == "cc").X);
        Assert.Equal(0, lines[1].Pieces[0].X);
    }

    [Fact]
    public void ErasedPartsAreLeftOutOfWhatIsKept()
    {
        var item = new PageObject { Kind = MarkKind.Rectangle, X = 0, Y = 0, Width = 100, Height = 100, StrokeWidth = 2 };
        item.Erased.Add([new PointF(0, 50), new PointF(100, 50)]);
        item.ErasedWidths.Add(10);
        List<RectangleF> kept = ShapeGeometry.KeptAfterErasing(item);
        bool Kept(float x, float y) => kept.Any(rect => rect.Contains(x, y));
        Assert.True(Kept(50, 20));
        Assert.True(Kept(50, 80));
        Assert.False(Kept(50, 50));

        // Moving or resizing the object carries what was rubbed out with it.
        item.Offset(10, 0);
        Assert.Equal(10, item.Erased[0][0].X);
        item.Resize(new RectangleF(10, 0, 200, 200));
        Assert.Equal(100, item.Erased[0][0].Y);
        Assert.Equal(20, item.ErasedWidths[0], 0.01);
    }

    [Fact]
    public void CopiesKeepTheirOwnSpansErasingAndCrop()
    {
        var item = new PageObject { Kind = MarkKind.Text, Text = "hi", Spans = [new TextSpan { Length = 2, Bold = true }], CropLeft = 0.25f };
        item.Erased.Add([new PointF(1, 1)]);
        item.ErasedWidths.Add(4);
        PageObject copy = item.Clone();
        copy.Spans[0].Bold = false;
        copy.Erased[0].Add(new PointF(2, 2));
        Assert.True(item.Spans[0].Bold);
        Assert.Single(item.Erased[0]);
        Assert.True(copy.IsCropped);
        Assert.Equal(new RectangleF(item.X + 0.25f * item.Width, item.Y, 0.75f * item.Width, item.Height), copy.CropBounds);
    }

    [Fact]
    public async Task ACroppedAndErasedShapeIsSavedThatWay()
    {
        using var temp = new TempFolder();
        EditDocument edit = DocumentStore.CreateBlank(temp.File("work"), 200, 200);
        var square = new PageObject { Kind = MarkKind.Rectangle, X = 0, Y = 0, Width = 200, Height = 200, StrokeWidth = 0, FillColor = Color.Black.ToArgb(), CropRight = 0.5f };
        square.Erased.Add([new PointF(0, 150), new PointF(200, 150)]);
        square.ErasedWidths.Add(20);
        edit.Pages[0].Objects.Add(square);
        string path = temp.File("out.pdf");
        DocumentStore.Save(edit, path, keepEditable: true);

        using var preview = await PdfPreview.OpenAsync(path);
        using Bitmap page = await preview.RenderAsync(0, 200);
        Assert.True(page.GetPixel(50, 50).GetBrightness() < 0.2f, "The part kept is drawn.");
        Assert.True(page.GetPixel(150, 50).GetBrightness() > 0.8f, "The cropped half is left out.");
        Assert.True(page.GetPixel(50, 150).GetBrightness() > 0.8f, "The erased stripe is left out.");

        // Kept editable, the crop and the rubbing come back on opening.
        PageObject back = Assert.Single(DocumentStore.Open(path, temp.File("again"), null).Pages[0].Objects);
        Assert.Equal(0.5f, back.CropRight);
        Assert.Single(back.Erased);
    }

    [Fact]
    public void TheTitleIsSavedForBrowsersToShow()
    {
        using var temp = new TempFolder();
        EditDocument edit = DocumentStore.CreateBlank(temp.File("work"), 300, 300);
        edit.Title = "Quarterly Report";
        string path = temp.File("titled.pdf");
        DocumentStore.Save(edit, path, keepEditable: false);
        using (PdfDocument saved = PdfReader.Open(path, PdfDocumentOpenMode.Import))
        {
            Assert.Equal("Quarterly Report", saved.Info.Title);
        }
        Assert.Equal("Quarterly Report", DocumentStore.Open(path, temp.File("again"), null).Title);
    }

    [Fact]
    public void ABlankDocumentIsABlankPageWithItsOwnPaper()
    {
        using var temp = new TempFolder();
        EditDocument edit = DocumentStore.CreateBlank(temp.File("work"), 612, 1008);
        Assert.True(edit.Pages[0].IsBlank);
        edit.Pages[0].Background = unchecked((int)0xFFEAF2FC);
        string path = temp.File("legal.pdf");
        DocumentStore.Save(edit, path, keepEditable: false);
        using PdfDocument saved = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        Assert.Equal(1, saved.PageCount);
        Assert.Equal(1008, saved.Pages[0].Height.Point, 0.5);
    }
}
