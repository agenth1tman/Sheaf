using System.Drawing;
using Sheaf.Core.Editing;

namespace Sheaf.Core.Tests;

public class ShapeTests
{
    static PageObject Ink(IEnumerable<PointF> points) => new() { Kind = MarkKind.Ink, StrokeWidth = 2, Strokes = [[.. points]] };

    /// <summary>Points along straight sides through the corners, closed back to the first, a little wobbly like a hand.</summary>
    static IEnumerable<PointF> Around(params PointF[] corners)
    {
        var random = new Random(7);
        for (int i = 0; i < corners.Length; i++)
        {
            PointF a = corners[i], b = corners[(i + 1) % corners.Length];
            for (int step = 0; step < 20; step++)
            {
                float t = step / 20f;
                yield return new PointF(a.X + (b.X - a.X) * t + (float)(random.NextDouble() - 0.5), a.Y + (b.Y - a.Y) * t + (float)(random.NextDouble() - 0.5));
            }
        }
        yield return corners[0];
    }

    [Fact]
    public void RoundStrokeBecomesACircle()
    {
        var points = Enumerable.Range(0, 61).Select(i => new PointF(100 + 50 * MathF.Cos(i * MathF.Tau / 60), 100 + 48 * MathF.Sin(i * MathF.Tau / 60)));
        PageObject shape = Assert.IsType<PageObject>(ShapeRecognizer.Recognize(Ink(points)));
        Assert.Equal(MarkKind.Ellipse, shape.Kind);
        Assert.Equal(shape.Width, shape.Height, 0.01f);
    }

    [Fact]
    public void BoxStrokeBecomesARectangle()
    {
        PageObject shape = Assert.IsType<PageObject>(ShapeRecognizer.Recognize(Ink(Around(new(10, 10), new(150, 10), new(150, 90), new(10, 90)))));
        Assert.Equal(MarkKind.Rectangle, shape.Kind);
        Assert.InRange(shape.Width, 135, 145);
        Assert.InRange(shape.Height, 75, 85);
    }

    [Fact]
    public void ThreeCornersBecomeATriangle()
    {
        PageObject shape = Assert.IsType<PageObject>(ShapeRecognizer.Recognize(Ink(Around(new(80, 10), new(150, 130), new(10, 130)))));
        Assert.Equal(MarkKind.Polygon, shape.Kind);
        Assert.Equal(3, shape.Strokes[0].Count);
    }

    [Fact]
    public void StraightAndBentStrokesBecomeLinesAndCurves()
    {
        var straight = Enumerable.Range(0, 30).Select(i => new PointF(10 + i * 5, 20 + i * 2 + (i % 2) * 0.6f));
        Assert.Equal(MarkKind.Line, ShapeRecognizer.Recognize(Ink(straight))?.Kind);

        var bent = Enumerable.Range(0, 41).Select(i => { float t = i / 40f; return new PointF(10 + 200 * t, 150 - 240 * t * (1 - t)); });
        PageObject curve = Assert.IsType<PageObject>(ShapeRecognizer.Recognize(Ink(bent)));
        // A curve through its ends and its middle, whose points can be moved, added to or taken out.
        Assert.Equal(MarkKind.Spline, curve.Kind);
        Assert.Equal(3, curve.Strokes[0].Count);
    }

    [Fact]
    public void ArcBecomesASemicircle()
    {
        var arc = Enumerable.Range(0, 41).Select(i => { double a = Math.PI + i * Math.PI / 40; return new PointF(100 + 60 * (float)Math.Cos(a), 100 + 60 * (float)Math.Sin(a)); });
        PageObject shape = Assert.IsType<PageObject>(ShapeRecognizer.Recognize(Ink(arc)));
        Assert.Equal(MarkKind.SemiCircle, shape.Kind);
        Assert.Equal(120, shape.Width, 2f);
        Assert.Equal(60, shape.Height, 2f);
        Assert.Equal(0, shape.Rotation);
    }

    [Fact]
    public void SemicircleDrawnSidewaysIsATurnedSemicircle()
    {
        // The flat side down the left, the arc bulging right, drawn by hand and closed.
        var random = new Random(3);
        var stroke = Enumerable.Range(0, 41).Select(i => { double a = -Math.PI / 2 + i * Math.PI / 40; return new PointF(100 + 70 * (float)Math.Cos(a) + random.Next(-2, 3), 100 + 70 * (float)Math.Sin(a)); })
            .Concat(Enumerable.Range(1, 20).Select(i => new PointF(100 + random.Next(-1, 2), 170 - i * 7))).ToList();
        PageObject shape = Assert.IsType<PageObject>(ShapeRecognizer.Recognize(Ink(stroke)));
        Assert.Equal(MarkKind.SemiCircle, shape.Kind);
        Assert.Equal(90, shape.Rotation, 1f);
        RectangleF seen = ShapeGeometry.VisibleBounds(shape);
        Assert.InRange(seen.Left, 95, 105);
        Assert.InRange(seen.Width, 64, 76);
    }

    [Fact]
    public void WobblyHandDrawnSemicircleIsStillASemicircle()
    {
        // Squashed a little, shaky, and not quite closed, the way a hand draws one.
        var random = new Random(11);
        var stroke = Enumerable.Range(0, 50).Select(i =>
        {
            double a = Math.PI + i * Math.PI / 49;
            float r = 80 * (1 + (float)(random.NextDouble() - 0.5) * 0.1f);
            return new PointF(200 + r * (float)Math.Cos(a), 200 + 0.85f * r * (float)Math.Sin(a));
        }).Concat(Enumerable.Range(1, 24).Select(i => new PointF(280 - i * 6.5f, 200 + random.Next(-3, 4)))).ToList();
        PageObject shape = Assert.IsType<PageObject>(ShapeRecognizer.Recognize(Ink(stroke)));
        Assert.Equal(MarkKind.SemiCircle, shape.Kind);
        Assert.Equal(0, shape.Rotation);
    }

    /// <summary>A shape's outline as a hand would draw it: points along it, each nudged a little.</summary>
    static List<PointF> Traced(MarkKind kind, RectangleF box, int seed)
    {
        using var path = ShapeGeometry.Outline(new PageObject { Kind = kind, Bounds = box });
        PointF[] outline = ShapeGeometry.Flatten(path);
        var random = new Random(seed);
        var points = outline.Select(p => new PointF(p.X + (float)(random.NextDouble() - 0.5) * 4, p.Y + (float)(random.NextDouble() - 0.5) * 4)).ToList();
        points.Add(points[0]);
        return points;
    }

    [Fact]
    public void HandDrawnHeartBecomesAHeart()
    {
        PageObject shape = Assert.IsType<PageObject>(ShapeRecognizer.Recognize(Ink(Traced(MarkKind.Heart, new RectangleF(40, 40, 140, 120), 5))));
        Assert.Equal(MarkKind.Heart, shape.Kind);
    }

    [Fact]
    public void HandDrawnStarBecomesAStar()
    {
        PageObject shape = Assert.IsType<PageObject>(ShapeRecognizer.Recognize(Ink(Traced(MarkKind.Star, new RectangleF(40, 40, 150, 140), 6))));
        Assert.Equal(MarkKind.Star, shape.Kind);
    }

    [Fact]
    public void PentagramDrawnInOneGoBecomesAStar()
    {
        var random = new Random(8);
        PointF Tip(int i) => new(100 + 70 * MathF.Cos(-MathF.PI / 2 + i * 2 * MathF.PI / 5), 100 + 70 * MathF.Sin(-MathF.PI / 2 + i * 2 * MathF.PI / 5));
        int[] order = [0, 2, 4, 1, 3, 0];
        var points = new List<PointF>();
        for (int i = 0; i < order.Length - 1; i++)
            for (int step = 0; step < 20; step++)
            {
                PointF a = Tip(order[i]), b = Tip(order[i + 1]);
                float f = step / 20f;
                points.Add(new PointF(a.X + (b.X - a.X) * f + (float)(random.NextDouble() - 0.5) * 3, a.Y + (b.Y - a.Y) * f + (float)(random.NextDouble() - 0.5) * 3));
            }
        points.Add(Tip(0));
        Assert.Equal(MarkKind.Star, ShapeRecognizer.Recognize(Ink(points))?.Kind);
    }

    [Fact]
    public void ShapeDrawnInPencilStaysPencil()
    {
        var circle = Enumerable.Range(0, 61).Select(i => new PointF(100 + 50 * MathF.Cos(i * MathF.Tau / 60), 100 + 50 * MathF.Sin(i * MathF.Tau / 60)));
        PageObject ink = Ink(circle);
        ink.Pen = PenKind.Pencil;
        PageObject shape = Assert.IsType<PageObject>(ShapeRecognizer.Recognize(ink));
        Assert.Equal(MarkKind.Ellipse, shape.Kind);
        Assert.Equal(PenKind.Pencil, shape.Pen);
    }

    [Fact]
    public void EachPenDrawsDifferently()
    {
        var wave = Enumerable.Range(0, 60).Select(i => new PointF(10 + i * 4, 50 + 20 * MathF.Sin(i * 0.2f))).ToList();
        var looks = new Dictionary<PenKind, List<InkPiece>>();
        foreach (PenKind pen in Enum.GetValues<PenKind>())
        {
            var item = new PageObject { Kind = MarkKind.Ink, Pen = pen, StrokeWidth = 3, Strokes = [wave], Id = new Guid("11111111-2222-3333-4444-555555555555") };
            looks[pen] = ShapeGeometry.InkPieces(item);
            Assert.NotEmpty(looks[pen]);
            // The same stroke always comes out the same, on screen and in the PDF.
            Assert.Equal(looks[pen].Count, ShapeGeometry.InkPieces(item).Count);
        }
        Assert.Single(looks[PenKind.Pen]);
        Assert.True(looks[PenKind.Highlighter][0].Alpha < 0.5f && looks[PenKind.Highlighter][0].Square, "A highlighter is see-through with square ends.");
        Assert.True(looks[PenKind.Marker][0].Width > 2 * 3, "A marker is bolder than the pen.");
        Assert.True(looks[PenKind.Pencil].Count >= 3 && looks[PenKind.Pencil].All(piece => piece.Width < 3), "A pencil lays several thin passes.");
        Assert.True(looks[PenKind.Crayon].Count > looks[PenKind.Pencil].Count, "A crayon breaks up more than a pencil.");
        float[] brush = [.. looks[PenKind.Brush].Select(piece => piece.Width)];
        Assert.True(brush[0] < brush[brush.Length / 2] && brush[^1] < brush[brush.Length / 2], "A brush tapers at both ends.");
    }

    [Fact]
    public void TurnedAndFlippedObjectsMapBothWays()
    {
        var item = new PageObject { Kind = MarkKind.Star, Bounds = new RectangleF(50, 40, 100, 60), Rotation = 30, FlipX = true };
        var point = new PointF(120, 85);
        PointF back = ShapeGeometry.ToPage(item, ShapeGeometry.ToLocal(item, point))[0];
        Assert.Equal(point.X, back.X, 0.01f);
        Assert.Equal(point.Y, back.Y, 0.01f);
        // The middle never moves.
        PointF middle = ShapeGeometry.ToPage(item, new PointF(100, 70))[0];
        Assert.Equal(100, middle.X, 0.01f);
        Assert.Equal(70, middle.Y, 0.01f);
    }

    [Theory]
    [InlineData(MarkKind.SemiCircle)]
    [InlineData(MarkKind.Pie)]
    [InlineData(MarkKind.Ellipse)]
    [InlineData(MarkKind.Rectangle)]
    [InlineData(MarkKind.Heart)]
    [InlineData(MarkKind.Star)]
    [InlineData(MarkKind.Parallelogram)]
    public void ShapesWithNoSizeYetStillHaveOutlines(MarkKind kind)
    {
        // A drag starts with an empty box; drawing it must not fail.
        foreach (SizeF size in new[] { SizeF.Empty, new SizeF(40, 0), new SizeF(0, 40) })
            using (ShapeGeometry.Outline(new PageObject { Kind = kind, Bounds = new RectangleF(new PointF(10, 10), size), CornerRadius = 4 })) { }
    }

    [Fact]
    public void ScribbleStaysADrawing() =>
        Assert.Null(ShapeRecognizer.Recognize(Ink(Enumerable.Range(0, 80).Select(i => new PointF(10 + i * 3, 50 + 30 * MathF.Sin(i * 0.9f) + i % 7 * 4)))));

    [Fact]
    public void CurveBoundsFollowTheCurveNotItsHandle()
    {
        var curve = new PageObject { Kind = MarkKind.Curve, Strokes = [[new(0, 0), new(50, 100), new(100, 0)]] };
        ShapeGeometry.Fit(curve);
        // A quadratic reaches halfway to its handle.
        Assert.Equal(50, curve.Height, 0.5f);
        Assert.Equal(100, curve.Width, 0.5f);
    }

    [Fact]
    public void NewShapesHaveOutlines()
    {
        foreach (MarkKind kind in new[] { MarkKind.SemiCircle, MarkKind.Pie, MarkKind.Rectangle, MarkKind.Ellipse, MarkKind.Heart, MarkKind.Star, MarkKind.Parallelogram })
        {
            using var outline = ShapeGeometry.Outline(new PageObject { Kind = kind, Bounds = new RectangleF(10, 10, 100, 60), CornerRadius = 8 });
            RectangleF bounds = outline.GetBounds();
            Assert.True(bounds.Width > 50 && bounds.Height > 20, $"{kind} outline is {bounds}.");
        }
    }
}
