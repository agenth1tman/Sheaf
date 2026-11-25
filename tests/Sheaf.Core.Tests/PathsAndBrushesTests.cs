using System.Drawing;
using Sheaf.Core.Editing;
using Sheaf.Core.Rendering;

namespace Sheaf.Core.Tests;

public class PathsAndBrushesTests
{
    static bool Near(PointF a, PointF b, float tolerance = 0.01f) => ShapeGeometry.Distance(a, b) <= tolerance;

    [Fact]
    public void ARectangleBecomesAClosedPathThroughItsCorners()
    {
        var item = new PageObject { Kind = MarkKind.Rectangle, X = 10, Y = 20, Width = 100, Height = 50 };
        PathEditing.MakePath(item);
        Assert.Equal(MarkKind.Path, item.Kind);
        Assert.True(item.Closed);
        Assert.Equal(4, PathEditing.Anchors(item).Count);
        Assert.All(Enumerable.Range(0, 4), i => Assert.True(PathEditing.IsCorner(item, i)));
        Assert.Equal(new RectangleF(10, 20, 100, 50), item.Bounds);
    }

    [Fact]
    public void AnEllipseBecomesFourSmoothPoints()
    {
        var item = new PageObject { Kind = MarkKind.Ellipse, X = 0, Y = 0, Width = 80, Height = 40 };
        PathEditing.MakePath(item);
        Assert.Equal(4, PathEditing.Anchors(item).Count);
        Assert.All(Enumerable.Range(0, 4), i => Assert.True(PathEditing.IsSmooth(item, i)));
        Assert.Equal(80, item.Width, 0.5);
        Assert.Equal(40, item.Height, 0.5);
    }

    [Fact]
    public void ATurnedShapeKeepsWhereItShowsAsAPath()
    {
        var item = new PageObject { Kind = MarkKind.Rectangle, X = 0, Y = 0, Width = 100, Height = 20, Rotation = 90 };
        PathEditing.MakePath(item);
        Assert.Equal(0, item.Rotation);
        // Turned a quarter about its middle (50, 10): 20 wide and 100 tall.
        Assert.Equal(new RectangleF(40, -40, 20, 100), Rectangle.Round(item.Bounds));
    }

    [Fact]
    public void AddingAPointKeepsTheCurve()
    {
        var item = new PageObject();
        PathEditing.SetPoints(item, [(new PointF(0, 0), new PointF(0, 0), new PointF(0, 60)), (new PointF(100, 0), new PointF(100, 60), new PointF(100, 0))], closed: false);
        var before = PathEditing.Segment(item, 0);
        PointF[] samples = [.. Enumerable.Range(0, 11).Select(i => PathEditing.At(before, i / 10f))];
        int added = PathEditing.Insert(item, 0, 0.3f);
        Assert.Equal(1, added);
        Assert.True(Near(PathEditing.Anchors(item)[1], PathEditing.At(before, 0.3f)));
        // Every point of the old curve is still on one of the two new pieces.
        foreach (PointF sample in samples)
            Assert.True(PathEditing.Nearest(item, sample) is { Distance: < 0.5f });
        Assert.True(PathEditing.Remove(item, 1));
        Assert.False(PathEditing.Remove(item, 1));
    }

    [Fact]
    public void ASplineBecomesAPathThroughTheSamePoints()
    {
        var item = new PageObject { Kind = MarkKind.Spline, Strokes = [[new(0, 0), new(50, 40), new(100, 0)]] };
        PointF[] curve = ShapeGeometry.SplinePoints(item.Strokes[0]);
        PathEditing.MakePath(item);
        Assert.Equal(3, PathEditing.Anchors(item).Count);
        Assert.False(item.Closed);
        foreach (PointF point in curve) Assert.True(PathEditing.Nearest(item, point) is { Distance: < 0.5f });
    }

    [Fact]
    public async Task APathIsSavedAndComesBack()
    {
        using var temp = new TempFolder();
        EditDocument edit = DocumentStore.CreateBlank(temp.File("work"), 200, 200);
        var path = new PageObject { FillColor = Color.Black.ToArgb(), StrokeWidth = 0 };
        PathEditing.SetPoints(path, [(new PointF(20, 20), new PointF(20, 20), new PointF(20, 20)), (new PointF(180, 20), new PointF(180, 20), new PointF(180, 20)),
            (new PointF(180, 180), new PointF(180, 180), new PointF(180, 180))], closed: true);
        ShapeGeometry.Fit(path);
        edit.Pages[0].Objects.Add(path);
        string file = temp.File("path.pdf");
        DocumentStore.Save(edit, file, keepEditable: true);

        using var preview = await PdfPreview.OpenAsync(file);
        using Bitmap page = await preview.RenderAsync(0, 200);
        Assert.True(page.GetPixel(150, 60).GetBrightness() < 0.2f, "Inside the triangle is filled.");
        Assert.True(page.GetPixel(40, 150).GetBrightness() > 0.8f, "Outside it is not.");
        PageObject back = Assert.Single(DocumentStore.Open(file, temp.File("again"), null).Pages[0].Objects);
        Assert.Equal(MarkKind.Path, back.Kind);
        Assert.True(back.Closed);
    }

    [Fact]
    public void ACropOnOneSideLeavesTheOutlineOnTheOthers()
    {
        var item = new PageObject { Kind = MarkKind.Rectangle, X = 0, Y = 0, Width = 100, Height = 100, StrokeWidth = 4, CropRight = 0.25f };
        RectangleF clip = ShapeGeometry.CropClip(item);
        Assert.Equal(75, clip.Right, 0.01);
        Assert.True(clip.Left < -2 && clip.Top < -2 && clip.Bottom > 102, "The stroke reaching past the other sides isn't cut.");
    }

    [Fact]
    public void AnAreaTakenWholeLeavesNothingThere()
    {
        var item = new PageObject { Kind = MarkKind.Rectangle, X = 0, Y = 0, Width = 100, Height = 100 };
        item.Erased.Add([new(40, 40), new(60, 40), new(60, 60), new(40, 60)]);
        item.ErasedWidths.Add(-1);
        List<RectangleF> kept = ShapeGeometry.KeptAfterErasing(item);
        Assert.DoesNotContain(kept, rect => rect.Contains(50, 50));
        Assert.Contains(kept, rect => rect.Contains(20, 20));
        Assert.Equal(-1, item.Clone().ErasedWidths[0]);
    }

    [Theory]
    [InlineData(PenKind.PaintBrush)]
    [InlineData(PenKind.Watercolor)]
    public void BrushesDrawTheSameStrokeTheSameWay(PenKind pen)
    {
        var item = new PageObject { Kind = MarkKind.Ink, Pen = pen, StrokeWidth = 4, Strokes = [[.. Enumerable.Range(0, 40).Select(i => new PointF(i * 3, MathF.Sin(i / 5f) * 10))]] };
        List<InkPiece> first = ShapeGeometry.InkPieces(item), again = ShapeGeometry.InkPieces(item);
        Assert.True(first.Count > 1, "Several bristles or washes.");
        Assert.Equal(first.Select(piece => piece.Points[^1]), again.Select(piece => piece.Points[^1]));
        Assert.All(first, piece => Assert.InRange(piece.Alpha, 0.01f, 1));
    }
}
