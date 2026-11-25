using System.Drawing;
using System.Drawing.Drawing2D;

namespace Sheaf.Core.Editing;

/// <summary>A line end drawn as its own small figure: a polygon (filled or not), a dot, or a stroke.</summary>
public sealed record EndMark(PointF[] Points, bool Filled, bool Closed);

/// <summary>One pass of a pen over part of a stroke: its points, how wide and how opaque it is, and whether its ends are square.</summary>
public sealed record InkPiece(PointF[] Points, float Width, float Alpha, bool Square);

/// <summary>
/// The shapes of objects, in page points, built once here so the editor and the saved PDF draw exactly the same
/// thing: outlines of closed shapes (with rounded corners where they apply), the paths of lines and curves, what
/// their ends look like, how an object is turned or flipped, and the passes each kind of pen makes.
/// </summary>
public static class ShapeGeometry
{
    /// <summary>Shapes drawn from an outline that takes a fill: the closed shapes, and paths (open ones too).</summary>
    public static bool IsClosedShape(MarkKind kind) => kind is MarkKind.Rectangle or MarkKind.Ellipse or MarkKind.SemiCircle or MarkKind.Pie
        or MarkKind.Polygon or MarkKind.Heart or MarkKind.Star or MarkKind.Parallelogram or MarkKind.Path;

    /// <summary>Lines this class draws: straight or bent, with ends.</summary>
    public static bool IsLine(MarkKind kind) => kind is MarkKind.Line or MarkKind.Arrow or MarkKind.Curve or MarkKind.Spline;

    /// <summary>
    /// Objects that turn and flip as a box, keeping their shape and turning it about their middle. The others
    /// (lines, curves, drawings, polygons) turn by moving their points.
    /// </summary>
    public static bool TurnsAsBox(MarkKind kind) => kind is MarkKind.Rectangle or MarkKind.Ellipse or MarkKind.SemiCircle or MarkKind.Pie
        or MarkKind.Heart or MarkKind.Star or MarkKind.Parallelogram or MarkKind.Text or MarkKind.Image;

    // Turning and flipping ------------------------------------------------------------------------------

    /// <summary>How an object is drawn turned and flipped about its middle; null when it is neither.</summary>
    public static Matrix? Transform(PageObject item)
    {
        if (item.Rotation == 0 && !item.FlipX && !item.FlipY) return null;
        float cx = item.X + item.Width / 2, cy = item.Y + item.Height / 2;
        var matrix = new Matrix();
        matrix.Translate(-cx, -cy, MatrixOrder.Append);
        matrix.Scale(item.FlipX ? -1 : 1, item.FlipY ? -1 : 1, MatrixOrder.Append);
        matrix.Rotate(item.Rotation, MatrixOrder.Append);
        matrix.Translate(cx, cy, MatrixOrder.Append);
        return matrix;
    }

    /// <summary>A page point in the object's own unturned frame, for hit tests and resizing.</summary>
    public static PointF ToLocal(PageObject item, PointF point)
    {
        using Matrix? matrix = Transform(item);
        if (matrix is null) return point;
        matrix.Invert();
        PointF[] points = [point];
        matrix.TransformPoints(points);
        return points[0];
    }

    /// <summary>Points of the object's own frame where they show on the page, turned and flipped.</summary>
    public static PointF[] ToPage(PageObject item, params PointF[] local)
    {
        using Matrix? matrix = Transform(item);
        PointF[] points = [.. local];
        matrix?.TransformPoints(points);
        return points;
    }

    /// <summary>The box round an object as it shows: its corners turned with it, then the box round those.</summary>
    public static RectangleF VisibleBounds(PageObject item)
    {
        RectangleF b = item.Bounds;
        PointF[] corners = ToPage(item, new(b.Left, b.Top), new(b.Right, b.Top), new(b.Right, b.Bottom), new(b.Left, b.Bottom));
        float left = corners.Min(p => p.X), top = corners.Min(p => p.Y);
        return RectangleF.FromLTRB(left, top, corners.Max(p => p.X), corners.Max(p => p.Y));
    }

    /// <summary>
    /// The clip a crop draws an object through, in its own frame: the part kept on each side that was cropped, and
    /// room to spare on a side that wasn't, so an outline, a line end or a thick pen there isn't cut off with it.
    /// </summary>
    public static RectangleF CropClip(PageObject item)
    {
        float room = Math.Max(10, PenWidth(item) * 4 + 12);
        RectangleF kept = item.CropBounds;
        return RectangleF.FromLTRB(item.CropLeft > 0 ? kept.Left : kept.Left - room, item.CropTop > 0 ? kept.Top : kept.Top - room,
            item.CropRight > 0 ? kept.Right : kept.Right + room, item.CropBottom > 0 ? kept.Bottom : kept.Bottom + room);
    }

    // Erasing ---------------------------------------------------------------------------------------------

    /// <summary>The area the eraser rubbed out of an object, in its own frame; null when it was never erased.</summary>
    public static GraphicsPath? ErasedPath(PageObject item)
    {
        if (item.Erased.Count == 0) return null;
        var area = new GraphicsPath(FillMode.Winding);
        for (int i = 0; i < item.Erased.Count; i++)
        {
            List<PointF> path = item.Erased[i];
            float width = i < item.ErasedWidths.Count ? item.ErasedWidths[i] : 8;
            if (path.Count == 0) continue;
            // An area taken whole: its outline, filled.
            if (width < 0 && path.Count >= 3)
            {
                area.AddPolygon([.. path]);
                continue;
            }
            width = Math.Max(0.5f, width);
            if (path.Count == 1 || path.All(p => Distance(p, path[0]) < 0.01f))
            {
                area.AddEllipse(path[0].X - width / 2, path[0].Y - width / 2, width, width);
                continue;
            }
            using var stroke = new GraphicsPath();
            stroke.AddLines([.. path]);
            using var pen = new Pen(Color.Black, width) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            stroke.Widen(pen, null, 0.1f);
            area.AddPath(stroke, false);
        }
        area.FillMode = FillMode.Winding;
        return area;
    }

    /// <summary>
    /// What is left of an object's area once its erased parts are taken out, as rectangles in its own frame: the
    /// clip a PDF draws it through (PDF clips can't take an area away, only keep one).
    /// </summary>
    public static List<RectangleF> KeptAfterErasing(PageObject item)
    {
        using GraphicsPath? erased = ErasedPath(item);
        if (erased is null) return [];
        // Generous room round the box: line ends, arrow heads and thick pens reach past it.
        float room = Math.Max(10, PenWidth(item) * 4 + 12);
        using var kept = new Region(RectangleF.Inflate(item.Bounds, room, room));
        using (var rubbed = new Region(erased)) kept.Exclude(rubbed);
        const float Fine = 4;
        using var fine = new Matrix(Fine, 0, 0, Fine, 0, 0);
        return [.. kept.GetRegionScans(fine).Select(r => new RectangleF(r.X / Fine, r.Y / Fine, r.Width / Fine, r.Height / Fine))];
    }

    // Outlines --------------------------------------------------------------------------------------------

    /// <summary>The outline of a closed shape, in its own frame (before it is turned).</summary>
    public static GraphicsPath Outline(PageObject item)
    {
        var path = new GraphicsPath();
        RectangleF b = item.Bounds;
        switch (item.Kind)
        {
            case MarkKind.Ellipse:
                path.AddEllipse(b);
                break;
            case MarkKind.SemiCircle when b.Width < 0.01f || b.Height < 0.01f:
                // GDI+ refuses an arc with no size, as a semicircle has when a drag starts.
                path.AddLine(b.Left, b.Bottom, b.Right, b.Bottom);
                break;
            case MarkKind.SemiCircle:
                // The flat side at the bottom, the arc filling the box above it.
                path.AddArc(b.X, b.Y, b.Width, 2 * b.Height, 180, 180);
                path.CloseFigure();
                break;
            case MarkKind.Pie:
                float sweep = Math.Clamp(item.Sweep, 5, 355);
                path.AddPie(b.X, b.Y, Math.Max(1, b.Width), Math.Max(1, b.Height), (360 - sweep) / 2, sweep);
                break;
            case MarkKind.Heart:
                AddHeart(path, b);
                break;
            case MarkKind.Star:
                AddRounded(path, StarCorners(b), item.CornerRadius);
                break;
            case MarkKind.Parallelogram:
                // Leaning right by a quarter of its width, or less in a short box, so its sides stay parallel.
                float lean = Math.Min(b.Width * 0.25f, b.Height);
                AddRounded(path, [new(b.Left + lean, b.Top), new(b.Right, b.Top), new(b.Right - lean, b.Bottom), new(b.Left, b.Bottom)], item.CornerRadius);
                break;
            case MarkKind.Polygon when item.Strokes.FirstOrDefault() is { Count: >= 3 } corners:
                AddRounded(path, corners, item.CornerRadius);
                break;
            case MarkKind.Path:
                PathEditing.AddTo(path, item);
                break;
            default:
                AddRounded(path, [new(b.Left, b.Top), new(b.Right, b.Top), new(b.Right, b.Bottom), new(b.Left, b.Bottom)], item.CornerRadius);
                break;
        }
        return path;
    }

    /// <summary>A five-pointed star filling the box, its top point up; the inner points at the golden ratio.</summary>
    static List<PointF> StarCorners(RectangleF b)
    {
        float cx = b.X + b.Width / 2, cy = b.Y + b.Height / 2, rx = b.Width / 2, ry = b.Height / 2;
        var corners = new List<PointF>();
        for (int i = 0; i < 10; i++)
        {
            double angle = -Math.PI / 2 + i * Math.PI / 5;
            float reach = i % 2 == 0 ? 1 : 0.382f;
            corners.Add(new PointF(cx + (float)Math.Cos(angle) * rx * reach, cy + (float)Math.Sin(angle) * ry * reach));
        }
        return corners;
    }

    /// <summary>A heart filling the box: two round lobes at the top meeting in a point at the bottom.</summary>
    static void AddHeart(GraphicsPath path, RectangleF b)
    {
        PointF P(float x, float y) => new(b.X + x * b.Width, b.Y + y * b.Height);
        path.AddBezier(P(0.5f, 0.28f), P(0.5f, 0.12f), P(0.36f, 0), P(0.22f, 0));
        path.AddBezier(P(0.22f, 0), P(0.07f, 0), P(0, 0.14f), P(0, 0.3f));
        path.AddBezier(P(0, 0.3f), P(0, 0.56f), P(0.3f, 0.76f), P(0.5f, 1));
        path.AddBezier(P(0.5f, 1), P(0.7f, 0.76f), P(1, 0.56f), P(1, 0.3f));
        path.AddBezier(P(1, 0.3f), P(1, 0.14f), P(0.93f, 0), P(0.78f, 0));
        path.AddBezier(P(0.78f, 0), P(0.64f, 0), P(0.5f, 0.12f), P(0.5f, 0.28f));
        path.CloseFigure();
    }

    /// <summary>
    /// A polygon whose corners are rounded by <paramref name="radius"/> (never more than half of either side), each
    /// corner a curve that meets both sides smoothly.
    /// </summary>
    static void AddRounded(GraphicsPath path, IReadOnlyList<PointF> corners, float radius)
    {
        if (radius <= 0.01f)
        {
            path.AddPolygon([.. corners]);
            return;
        }
        int n = corners.Count;
        var cuts = new (PointF In, PointF Out)[n];
        for (int i = 0; i < n; i++)
        {
            PointF corner = corners[i], before = corners[(i + n - 1) % n], after = corners[(i + 1) % n];
            float cut = Math.Min(radius, Math.Min(Distance(corner, before), Distance(corner, after)) / 2);
            cuts[i] = (Toward(corner, before, cut), Toward(corner, after, cut));
        }
        for (int i = 0; i < n; i++)
        {
            PointF corner = corners[i];
            var (into, outOf) = cuts[i];
            // A cubic that hugs the corner like a quarter circle would (0.55 of the way to it).
            path.AddBezier(into, Lerp(into, corner, 0.55f), Lerp(outOf, corner, 0.55f), outOf);
            path.AddLine(outOf, cuts[(i + 1) % n].In);
        }
        path.CloseFigure();
    }

    // Lines and curves ------------------------------------------------------------------------------------

    /// <summary>The path a line or curve follows, trimmed where a filled triangle end covers it.</summary>
    public static GraphicsPath Stroke(PageObject item)
    {
        var path = new GraphicsPath();
        if (item.Strokes.FirstOrDefault() is not { Count: >= 2 } points) return path;
        var (startEnd, endEnd) = Ends(item);
        PointF start = points[0], end = points[^1];
        if (item.Kind == MarkKind.Spline)
        {
            var through = points.ToList();
            if (startEnd == LineEnd.Triangle) through[0] = Toward(through[0], through[1], HeadLength(item) * 0.8f);
            if (endEnd == LineEnd.Triangle) through[^1] = Toward(through[^1], through[^2], HeadLength(item) * 0.8f);
            path.AddBeziers(SplineBeziers(through));
            return path;
        }
        if (item.Kind == MarkKind.Curve && points.Count >= 3)
        {
            PointF bend = points[1];
            if (startEnd == LineEnd.Triangle) start = Toward(start, bend, HeadLength(item) * 0.8f);
            if (endEnd == LineEnd.Triangle) end = Toward(end, bend, HeadLength(item) * 0.8f);
            // A quadratic through the bend handle, written as the cubic PDF and GDI+ both draw.
            path.AddBezier(start, Lerp(start, points[1], 2 / 3f), Lerp(end, points[1], 2 / 3f), end);
            return path;
        }
        if (startEnd == LineEnd.Triangle) start = Toward(start, end, HeadLength(item) * 0.8f);
        if (endEnd == LineEnd.Triangle) end = Toward(end, start, HeadLength(item) * 0.8f);
        path.AddLine(start, end);
        return path;
    }

    /// <summary>
    /// A smooth curve through every point (Catmull-Rom), as the cubic Béziers GDI+ and PDF draw: the first point,
    /// then two control points and the next point for each piece.
    /// </summary>
    public static PointF[] SplineBeziers(IReadOnlyList<PointF> points)
    {
        var result = new List<PointF> { points[0] };
        for (int i = 0; i < points.Count - 1; i++)
        {
            PointF before = points[Math.Max(0, i - 1)], a = points[i], b = points[i + 1], after = points[Math.Min(points.Count - 1, i + 2)];
            result.Add(new PointF(a.X + (b.X - before.X) / 6, a.Y + (b.Y - before.Y) / 6));
            result.Add(new PointF(b.X - (after.X - a.X) / 6, b.Y - (after.Y - a.Y) / 6));
            result.Add(b);
        }
        return [.. result];
    }

    /// <summary>The ends of a line; an arrow saved before ends could be chosen ends in an arrow.</summary>
    public static (LineEnd Start, LineEnd End) Ends(PageObject item) =>
        item.Kind == MarkKind.Arrow && item.StartEnd == LineEnd.None && item.EndEnd == LineEnd.None ? (LineEnd.None, LineEnd.Arrow) : (item.StartEnd, item.EndEnd);

    static float HeadLength(PageObject item) => 3.2f * Math.Max(2, item.StrokeWidth) + 4;

    /// <summary>The figures at the two ends of a line or curve.</summary>
    public static IEnumerable<EndMark> EndMarks(PageObject item)
    {
        if (item.Strokes.FirstOrDefault() is not { Count: >= 2 } points) yield break;
        var (startEnd, endEnd) = Ends(item);
        bool bent = item.Kind is MarkKind.Curve or MarkKind.Spline && points.Count >= 3;
        PointF start = points[0], end = points[^1];
        // Each end points away from the line: from the next point along it, or from the other end of a straight one.
        PointF fromStart = bent ? points[1] : end, fromEnd = bent ? points[^2] : start;
        if (EndMark(startEnd, start, fromStart, item) is { } first) yield return first;
        if (EndMark(endEnd, end, fromEnd, item) is { } last) yield return last;
    }

    static EndMark? EndMark(LineEnd kind, PointF tip, PointF from, PageObject item)
    {
        if (kind == LineEnd.None) return null;
        double angle = Math.Atan2(tip.Y - from.Y, tip.X - from.X);
        float length = HeadLength(item), width = Math.Max(1, item.StrokeWidth);
        PointF Back(double spread, float distance) =>
            new(tip.X + (float)(Math.Cos(angle + Math.PI + spread) * distance), tip.Y + (float)(Math.Sin(angle + Math.PI + spread) * distance));
        switch (kind)
        {
            case LineEnd.Arrow:
                return new EndMark([Back(-0.5, length), tip, Back(0.5, length)], Filled: false, Closed: false);
            case LineEnd.Triangle:
                return new EndMark([tip, Back(-0.42, length * 1.05f), Back(0.42, length * 1.05f)], Filled: true, Closed: true);
            case LineEnd.Dot:
                float r = Math.Max(2.5f, width * 1.8f);
                return new EndMark([new(tip.X - r, tip.Y - r), new(tip.X + r, tip.Y + r)], Filled: true, Closed: true);
            default:
                float half = width * 1.5f + 3;
                double across = angle + Math.PI / 2;
                return new EndMark([new(tip.X + (float)(Math.Cos(across) * half), tip.Y + (float)(Math.Sin(across) * half)),
                    new(tip.X - (float)(Math.Cos(across) * half), tip.Y - (float)(Math.Sin(across) * half))], Filled: false, Closed: false);
        }
    }

    /// <summary>A dot end is stored as the two corners of its circle.</summary>
    public static bool IsDot(EndMark mark) => mark.Points.Length == 2 && mark.Filled;

    /// <summary>An end as lines a pen can draw: a triangle's or a dot's outline, closed, or an arrow's or bar's strokes.</summary>
    public static List<List<PointF>> EndMarkStrokes(EndMark mark)
    {
        if (IsDot(mark))
        {
            PointF a = mark.Points[0], b = mark.Points[1];
            float cx = (a.X + b.X) / 2, cy = (a.Y + b.Y) / 2, r = (b.X - a.X) / 2;
            return [[.. Enumerable.Range(0, 25).Select(i => new PointF(cx + r * MathF.Cos(i * MathF.Tau / 24), cy + r * MathF.Sin(i * MathF.Tau / 24)))]];
        }
        var points = mark.Points.ToList();
        if (mark.Closed) points.Add(points[0]);
        return [points];
    }

    /// <summary>How solidly a pen fills a filled end: see-through for a highlighter, lighter for the grainy pens.</summary>
    public static float FillAlpha(PenKind pen) => pen switch
    {
        PenKind.Highlighter => 0.35f,
        PenKind.Pencil => 0.55f,
        PenKind.Crayon => 0.6f,
        PenKind.PaintBrush => 0.85f,
        PenKind.Watercolor => 0.35f,
        _ => 1,
    };

    // Pens ----------------------------------------------------------------------------------------------

    /// <summary>The passes that draw an object's pen strokes, in its pen: what both the editor and the saved PDF draw.</summary>
    public static List<InkPiece> InkPieces(PageObject item) => InkPieces(item, item.Strokes, item.Pressures);

    /// <summary>
    /// The passes a pen makes along some strokes. A pen draws one steady line; a marker a bold one; a highlighter a
    /// wide see-through band with square ends; a brush swells and tapers; a pencil and a crayon lay several thin,
    /// slightly wandering passes that break up like graphite or wax on paper; a paint brush lays a row of bristles
    /// that run dry; watercolour lays thin washes over each other. The wander comes from the object's id,
    /// so a stroke looks the same every time it is drawn, on screen and in the PDF.
    /// </summary>
    public static List<InkPiece> InkPieces(PageObject item, IReadOnlyList<List<PointF>> strokes, IReadOnlyList<List<float>> pressures)
    {
        var pieces = new List<InkPiece>();
        float width = Math.Max(0.1f, item.StrokeWidth);
        int seed = item.Id.GetHashCode();
        for (int s = 0; s < strokes.Count; s++)
        {
            List<PointF> points = strokes[s];
            if (points.Count < 2) continue;
            List<float>? pressure = s < pressures.Count && pressures[s].Count == points.Count ? pressures[s] : null;
            switch (item.Pen)
            {
                case PenKind.Highlighter:
                    pieces.Add(new InkPiece([.. points], PenWidth(item), 0.35f, Square: true));
                    break;
                case PenKind.Marker:
                    Varying(pieces, points, pressure, PenWidth(item), 1, taper: false);
                    break;
                case PenKind.Brush:
                    Varying(pieces, points, (IReadOnlyList<float>?)pressure ?? Speeds(points, width), PenWidth(item), 1, taper: true);
                    break;
                case PenKind.Pencil:
                    Grain(pieces, points, Mix(seed, s), passes: 3, Math.Max(0.35f, width * 0.5f), alpha: 0.7f, spread: Math.Max(0.3f, width * 0.3f), gaps: 0.05f);
                    break;
                case PenKind.Crayon:
                    Grain(pieces, points, Mix(seed, s), passes: 6, Math.Max(0.8f, width * 0.95f), alpha: 0.6f, spread: Math.Max(1f, width * 0.85f), gaps: 0.18f);
                    break;
                case PenKind.PaintBrush:
                    Bristles(pieces, points, (IReadOnlyList<float>?)pressure ?? Speeds(points, width), Mix(seed, s), width);
                    break;
                case PenKind.Watercolor:
                    Wash(pieces, points, Mix(seed, s), width);
                    break;
                default:
                    Varying(pieces, points, pressure, width, 1, taper: false);
                    break;
            }
        }
        return pieces;
    }

    /// <summary>How wide a pen's stroke is, overall; for hit tests too.</summary>
    public static float PenWidth(PageObject item) => item.Pen switch
    {
        PenKind.Highlighter => Math.Max(6, item.StrokeWidth * 5),
        PenKind.Marker => item.StrokeWidth * 2.4f,
        PenKind.Brush => item.StrokeWidth * 2.4f,
        PenKind.Crayon => item.StrokeWidth * 2.6f,
        PenKind.PaintBrush => item.StrokeWidth * 2.8f,
        PenKind.Watercolor => item.StrokeWidth * 3,
        _ => item.StrokeWidth,
    };

    /// <summary>One steady line, or piece by piece where pressure (or a brush) changes the width.</summary>
    static void Varying(List<InkPiece> pieces, List<PointF> points, IReadOnlyList<float>? scales, float width, float alpha, bool taper)
    {
        if (scales is null && !taper)
        {
            pieces.Add(new InkPiece([.. points], width, alpha, Square: false));
            return;
        }
        float total = 0, along = 0;
        for (int i = 1; i < points.Count; i++) total += Distance(points[i - 1], points[i]);
        float ends = Math.Min(total * 0.3f, width * 10);
        for (int i = 0; i < points.Count - 1; i++)
        {
            float scale = scales is null ? 1 : (scales[i] + scales[Math.Min(i + 1, scales.Count - 1)]) / 2;
            float middle = along + Distance(points[i], points[i + 1]) / 2;
            // A brush comes to a point at both ends.
            if (taper && ends > 0) scale *= 0.12f + 0.88f * Math.Min(1, Math.Min(middle, total - middle) / ends);
            pieces.Add(new InkPiece([points[i], points[i + 1]], Math.Max(0.2f, width * scale), alpha, Square: false));
            along += Distance(points[i], points[i + 1]);
        }
    }

    /// <summary>Without a stylus, a brush thins as the hand moves faster: a scale for each point, smoothed.</summary>
    static float[] Speeds(List<PointF> points, float width)
    {
        var scales = new float[points.Count];
        for (int i = 0; i < points.Count; i++)
        {
            float step = i == 0 ? 0 : Distance(points[i - 1], points[i]);
            scales[i] = Math.Clamp(1.4f - step / (width * 2 + 3), 0.3f, 1.4f);
        }
        for (int pass = 0; pass < 3; pass++)
            for (int i = 1; i < scales.Length - 1; i++)
                scales[i] = (scales[i - 1] + 2 * scales[i] + scales[i + 1]) / 4;
        return scales;
    }

    /// <summary>
    /// Several thin passes, each drifting a little to either side along the stroke and skipping a few short
    /// stretches: the grain of a pencil or crayon.
    /// </summary>
    static void Grain(List<InkPiece> pieces, List<PointF> points, int seed, int passes, float width, float alpha, float spread, float gaps)
    {
        for (int pass = 0; pass < passes; pass++)
        {
            var run = new List<PointF>();
            for (int i = 0; i < points.Count; i++)
            {
                PointF shifted = Wandered(points, i, seed, pass, spread);
                bool skip = i > 0 && Noise(seed, pass + 101, i) < gaps;
                if (skip)
                {
                    if (run.Count >= 2) pieces.Add(new InkPiece([.. run], width, alpha, Square: false));
                    run.Clear();
                }
                run.Add(shifted);
            }
            if (run.Count >= 2) pieces.Add(new InkPiece([.. run], width, alpha, Square: false));
        }
    }

    /// <summary>A point of a stroke moved to one side or the other by a smooth wander: a random offset every few points, eased in between.</summary>
    static PointF Wandered(List<PointF> points, int i, int seed, int pass, float spread)
    {
        float position = i / 4f;
        int knot = (int)position;
        float blend = position - knot;
        float offset = spread * (Lerp(Noise(seed, pass, knot), Noise(seed, pass, knot + 1), blend * blend * (3 - 2 * blend)) * 2 - 1);
        return Across(points, i, offset);
    }

    /// <summary>A point of a stroke moved square to the stroke by some distance (to its left for a positive one).</summary>
    static PointF Across(List<PointF> points, int i, float offset)
    {
        PointF before = points[Math.Max(0, i - 1)], after = points[Math.Min(points.Count - 1, i + 1)];
        float dx = after.X - before.X, dy = after.Y - before.Y, length = MathF.Sqrt(dx * dx + dy * dy);
        return length < 0.001f ? points[i] : new PointF(points[i].X - dy / length * offset, points[i].Y + dx / length * offset);
    }

    /// <summary>
    /// A loaded paint brush: a row of bristles side by side across the stroke, each a thin line of its own and some
    /// carrying more paint than others. The outer ones run dry before the end, and the row narrows where the brush
    /// touches down and lifts off, and where it is pressed less (or, without a stylus, moved faster).
    /// </summary>
    static void Bristles(List<InkPiece> pieces, List<PointF> points, IReadOnlyList<float> scales, int seed, float width)
    {
        const int Count = 9;
        var along = new float[points.Count];
        for (int i = 1; i < points.Count; i++) along[i] = along[i - 1] + Distance(points[i - 1], points[i]);
        float total = along[^1], reach = width * 1.3f;
        for (int b = 0; b < Count; b++)
        {
            float place = b / (Count - 1f) * 2 - 1 + (Noise(seed, b, 0) - 0.5f) * 0.18f;
            float thickness = width * (0.45f + 0.4f * Noise(seed, b, 1)), alpha = 0.45f + 0.45f * Noise(seed, b, 2);
            float dry = total * (1 - Math.Abs(place) * (0.15f + 0.35f * Noise(seed, b, 3)));
            var run = new List<PointF>();
            for (int i = 0; i < points.Count; i++)
            {
                if (i >= 2 && along[i] > dry) break;
                float scale = Math.Clamp(scales[Math.Min(i, scales.Count - 1)], 0.3f, 1.4f);
                float ends = Math.Min(1, Math.Min(along[i], total - along[i]) / Math.Max(0.01f, width * 3));
                run.Add(Across(points, i, place * reach * scale * (0.55f + 0.45f * ends)));
            }
            if (run.Count >= 2) pieces.Add(new InkPiece([.. run], thickness, alpha, Square: false));
        }
    }

    /// <summary>
    /// Watercolour: a few wide, thin washes laid over each other, each wandering a little its own way, so the colour
    /// pools deeper along the middle and fades out at the edges.
    /// </summary>
    static void Wash(List<InkPiece> pieces, List<PointF> points, int seed, float width)
    {
        for (int pass = 0; pass < 4; pass++)
        {
            var run = new PointF[points.Count];
            for (int i = 0; i < points.Count; i++) run[i] = Wandered(points, i, seed, pass, width * 0.7f);
            pieces.Add(new InkPiece(run, width * (1.6f + 1.4f * Noise(seed, pass, 7)), 0.16f, Square: false));
        }
    }

    static int Mix(int a, int b) => (int)Hash((uint)a * 0x9E3779B1u ^ (uint)b);

    static uint Hash(uint x)
    {
        x ^= x >> 16;
        x *= 0x7FEB352Du;
        x ^= x >> 15;
        x *= 0x846CA68Bu;
        x ^= x >> 16;
        return x;
    }

    /// <summary>A number from 0 to 1 that is always the same for the same inputs.</summary>
    static float Noise(int seed, int pass, int index) => Hash((uint)Mix(Mix(seed, pass), index)) / (float)uint.MaxValue;

    static float Lerp(float a, float b, float t) => a + (b - a) * t;

    /// <summary>
    /// The outline of a shape or the path of a line as point lists, for drawing it in a pen other than the plain
    /// one: a circle snapped from a pencil stroke stays pencil.
    /// </summary>
    public static List<List<PointF>> PenStrokes(PageObject item)
    {
        using GraphicsPath path = IsClosedShape(item.Kind) ? Outline(item) : Stroke(item);
        path.Flatten(null, 0.3f);
        var strokes = new List<List<PointF>>();
        if (path.PointCount == 0) return strokes;
        PointF[] points = path.PathPoints;
        byte[] types = path.PathTypes;
        List<PointF>? current = null;
        for (int i = 0; i < points.Length; i++)
        {
            if ((types[i] & 0x7) == 0)
            {
                current = [];
                strokes.Add(current);
            }
            current?.Add(points[i]);
            if ((types[i] & 0x80) != 0 && current is { Count: > 0 }) current.Add(current[0]);
        }
        return strokes;
    }

    // Points -----------------------------------------------------------------------------------------

    public static float Distance(PointF a, PointF b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    public static PointF Lerp(PointF a, PointF b, float t) => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);

    static PointF Toward(PointF from, PointF to, float distance)
    {
        float length = Distance(from, to);
        return length < 0.001f ? from : Lerp(from, to, Math.Min(1, distance / length));
    }

    /// <summary>The points a pie, semicircle or other bent shape follows, for drawing it as a polygon.</summary>
    public static PointF[] Flatten(GraphicsPath path)
    {
        using var copy = (GraphicsPath)path.Clone();
        copy.Flatten(null, 0.25f);
        return copy.PathPoints;
    }

    /// <summary>Points along a quadratic curve (start, bend handle, end), for hit tests and bounds.</summary>
    public static PointF[] CurvePoints(IReadOnlyList<PointF> points, int steps = 32)
    {
        PointF a = points[0], b = points[1], c = points[^1];
        var result = new PointF[steps + 1];
        for (int i = 0; i <= steps; i++)
        {
            float t = i / (float)steps, u = 1 - t;
            result[i] = new PointF(u * u * a.X + 2 * u * t * b.X + t * t * c.X, u * u * a.Y + 2 * u * t * b.Y + t * t * c.Y);
        }
        return result;
    }

    /// <summary>Points along a curve through every point (a spline), for hit tests, bounds and adding points.</summary>
    public static PointF[] SplinePoints(IReadOnlyList<PointF> points, int stepsPerPiece = 16)
    {
        if (points.Count < 2) return [.. points];
        PointF[] beziers = SplineBeziers(points);
        var result = new List<PointF> { beziers[0] };
        for (int i = 0; i + 3 < beziers.Length; i += 3)
            for (int step = 1; step <= stepsPerPiece; step++)
            {
                float t = step / (float)stepsPerPiece, u = 1 - t;
                PointF a = beziers[i], b = beziers[i + 1], c = beziers[i + 2], d = beziers[i + 3];
                result.Add(new PointF(
                    u * u * u * a.X + 3 * u * u * t * b.X + 3 * u * t * t * c.X + t * t * t * d.X,
                    u * u * u * a.Y + 3 * u * u * t * b.Y + 3 * u * t * t * c.Y + t * t * t * d.Y));
            }
        return [.. result];
    }

    /// <summary>The points a line or curve passes along, for hit tests.</summary>
    public static PointF[] PathPoints(PageObject item)
    {
        if (item.Strokes.FirstOrDefault() is not { Count: >= 2 } points) return [];
        return item.Kind switch
        {
            MarkKind.Spline => SplinePoints(points),
            MarkKind.Curve when points.Count >= 3 => CurvePoints(points),
            _ => [.. points],
        };
    }

    /// <summary>Fits an object's bounds to its points; a curve's to the curve itself, not its handles.</summary>
    public static void Fit(PageObject item)
    {
        if (item.Kind == MarkKind.Path)
        {
            // Round the path itself, not its handles, which reach out past its curves.
            using GraphicsPath outline = Outline(item);
            PointF[] along = outline.PointCount == 0 ? [] : Flatten(outline);
            if (along.Length == 0) return;
            float x = along.Min(p => p.X), y = along.Min(p => p.Y);
            item.Bounds = RectangleF.FromLTRB(x, y, Math.Max(along.Max(p => p.X), x + 1), Math.Max(along.Max(p => p.Y), y + 1));
            return;
        }
        if (item.Kind is not (MarkKind.Curve or MarkKind.Spline) || item.Strokes.FirstOrDefault() is not { Count: >= 2 })
        {
            item.FitToStrokes();
            return;
        }
        PointF[] curve = PathPoints(item);
        float left = curve.Min(p => p.X), top = curve.Min(p => p.Y);
        item.Bounds = RectangleF.FromLTRB(left, top, Math.Max(curve.Max(p => p.X), left + 1), Math.Max(curve.Max(p => p.Y), top + 1));
    }

    /// <summary>What a many-cornered shape is: a recognised pie starts at its centre, a semicircle doesn't.</summary>
    public static string PolygonName(PageObject item)
    {
        if (item.Strokes.FirstOrDefault() is not { } points) return "Shape";
        if (points.Count == 3) return "Triangle";
        if (points.Count < 20) return "Shape";
        float[] fromFirst = [.. points.Skip(1).Select(p => Distance(points[0], p))];
        return fromFirst.Max() - fromFirst.Min() < fromFirst.Max() * 0.05f ? "Pie" : "Semicircle";
    }
}
