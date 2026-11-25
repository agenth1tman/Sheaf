using System.Drawing;
using System.Drawing.Drawing2D;

namespace Sheaf.Core.Editing;

/// <summary>
/// Paths (<see cref="MarkKind.Path"/>): points joined by straight or curved pieces, each point with a handle in and a
/// handle out; a handle sitting on its point leaves that side straight. Builds their outlines, turns other shapes and
/// lines into paths so their points can be moved one by one, and adds and takes out points.
/// </summary>
public static class PathEditing
{
    /// <summary>A path with its points and both lists of handles in step.</summary>
    public static bool IsWellFormed(PageObject item) =>
        item.Kind == MarkKind.Path && item.Strokes.Count >= 3 && item.Strokes[0].Count > 0
        && item.Strokes[1].Count == item.Strokes[0].Count && item.Strokes[2].Count == item.Strokes[0].Count;

    public static List<PointF> Anchors(PageObject item) => item.Strokes[0];

    public static List<PointF> Ins(PageObject item) => item.Strokes[1];

    public static List<PointF> Outs(PageObject item) => item.Strokes[2];

    /// <summary>A path through some points, each with its handles (or none: straight sides).</summary>
    public static void SetPoints(PageObject item, IEnumerable<(PointF Anchor, PointF In, PointF Out)> points, bool closed)
    {
        var list = points.ToList();
        item.Kind = MarkKind.Path;
        item.Strokes = [[.. list.Select(p => p.Anchor)], [.. list.Select(p => p.In)], [.. list.Select(p => p.Out)]];
        item.Pressures = [];
        item.Closed = closed;
    }

    /// <summary>Adds the path's outline to <paramref name="path"/>: one figure, closed when the path is.</summary>
    public static void AddTo(GraphicsPath path, PageObject item)
    {
        if (!IsWellFormed(item) || Anchors(item).Count < 2) return;
        path.StartFigure();
        // A piece with no handles out of either end is straight, and goes in as a line.
        for (int s = 0; s < SegmentCount(item); s++)
        {
            var (a, b, c, d) = Segment(item, s);
            if (ShapeGeometry.Distance(a, b) < 0.01f && ShapeGeometry.Distance(c, d) < 0.01f) path.AddLine(a, d);
            else path.AddBezier(a, b, c, d);
        }
        if (item.Closed) path.CloseFigure();
    }

    public static int SegmentCount(PageObject item) => !IsWellFormed(item) ? 0 : item.Closed ? Anchors(item).Count : Anchors(item).Count - 1;

    /// <summary>The curve from point <paramref name="segment"/> to the next: its two ends and the handles between.</summary>
    public static (PointF A, PointF B, PointF C, PointF D) Segment(PageObject item, int segment)
    {
        int next = (segment + 1) % Anchors(item).Count;
        return (Anchors(item)[segment], Outs(item)[segment], Ins(item)[next], Anchors(item)[next]);
    }

    public static PointF At((PointF A, PointF B, PointF C, PointF D) s, float t)
    {
        float u = 1 - t;
        return new PointF(
            u * u * u * s.A.X + 3 * u * u * t * s.B.X + 3 * u * t * t * s.C.X + t * t * t * s.D.X,
            u * u * u * s.A.Y + 3 * u * u * t * s.B.Y + 3 * u * t * t * s.C.Y + t * t * t * s.D.Y);
    }

    /// <summary>The piece of the path nearest a point, how far along it that is (0 to 1), and how far away.</summary>
    public static (int Segment, float T, float Distance)? Nearest(PageObject item, PointF point)
    {
        (int, float, float)? best = null;
        const int Steps = 48;
        for (int s = 0; s < SegmentCount(item); s++)
        {
            var piece = Segment(item, s);
            float near = 0, nearest = float.MaxValue;
            for (int i = 0; i <= Steps; i++)
            {
                float t = i / (float)Steps, distance = ShapeGeometry.Distance(At(piece, t), point);
                if (distance < nearest) (near, nearest) = (t, distance);
            }
            // Then closer in, between the samples either side of the nearest.
            float low = Math.Max(0, near - 1f / Steps), high = Math.Min(1, near + 1f / Steps);
            for (int i = 0; i < 24; i++)
            {
                float a = low + (high - low) / 3, b = high - (high - low) / 3;
                if (ShapeGeometry.Distance(At(piece, a), point) < ShapeGeometry.Distance(At(piece, b), point)) high = b; else low = a;
            }
            float found = (low + high) / 2, gap = ShapeGeometry.Distance(At(piece, found), point);
            if (best is not { } b2 || gap < b2.Item3) best = (s, found, gap);
        }
        return best;
    }

    /// <summary>
    /// Puts a new point on a piece of the path where it is <paramref name="t"/> of the way along, splitting the curve
    /// there so the path keeps its shape. Returns the new point's index.
    /// </summary>
    public static int Insert(PageObject item, int segment, float t)
    {
        List<PointF> anchors = Anchors(item), ins = Ins(item), outs = Outs(item);
        int next = (segment + 1) % anchors.Count;
        var (a, b, c, d) = Segment(item, segment);
        PointF q0 = ShapeGeometry.Lerp(a, b, t), q1 = ShapeGeometry.Lerp(b, c, t), q2 = ShapeGeometry.Lerp(c, d, t);
        PointF r0 = ShapeGeometry.Lerp(q0, q1, t), r1 = ShapeGeometry.Lerp(q1, q2, t), split = ShapeGeometry.Lerp(r0, r1, t);
        outs[segment] = q0;
        ins[next] = q2;
        int at = segment + 1;
        anchors.Insert(at, split);
        ins.Insert(at, r0);
        outs.Insert(at, r1);
        return at;
    }

    /// <summary>Takes a point out, joining its neighbours; false when that would leave fewer than two.</summary>
    public static bool Remove(PageObject item, int index)
    {
        if (Anchors(item).Count <= 2) return false;
        foreach (List<PointF> list in item.Strokes.Take(3)) list.RemoveAt(index);
        if (item.Closed && Anchors(item).Count < 3) item.Closed = false;
        return true;
    }

    /// <summary>Moves a point with its handles.</summary>
    public static void Move(PageObject item, int index, float dx, float dy)
    {
        foreach (List<PointF> list in item.Strokes.Take(3)) list[index] = new PointF(list[index].X + dx, list[index].Y + dy);
    }

    /// <summary>A point with no handles out of it: both its sides are straight.</summary>
    public static bool IsCorner(PageObject item, int index) =>
        ShapeGeometry.Distance(Ins(item)[index], Anchors(item)[index]) < 0.01f && ShapeGeometry.Distance(Outs(item)[index], Anchors(item)[index]) < 0.01f;

    /// <summary>A point whose two handles lie on one line through it, so the path runs smoothly through it.</summary>
    public static bool IsSmooth(PageObject item, int index)
    {
        PointF a = Anchors(item)[index], i = Ins(item)[index], o = Outs(item)[index];
        float inX = a.X - i.X, inY = a.Y - i.Y, outX = o.X - a.X, outY = o.Y - a.Y;
        float inLength = MathF.Sqrt(inX * inX + inY * inY), outLength = MathF.Sqrt(outX * outX + outY * outY);
        if (inLength < 0.01f || outLength < 0.01f) return false;
        return Math.Abs(inX * outY - inY * outX) / (inLength * outLength) < 0.03f && inX * outX + inY * outY > 0;
    }

    /// <summary>Gives a point handles along the way the path runs past it, a third of the way to each neighbour.</summary>
    public static void MakeSmooth(PageObject item, int index)
    {
        List<PointF> anchors = Anchors(item);
        int count = anchors.Count;
        bool hasBefore = item.Closed || index > 0, hasAfter = item.Closed || index < count - 1;
        PointF here = anchors[index];
        PointF before = hasBefore ? anchors[(index - 1 + count) % count] : here, after = hasAfter ? anchors[(index + 1) % count] : here;
        float dx = after.X - before.X, dy = after.Y - before.Y, length = MathF.Sqrt(dx * dx + dy * dy);
        if (length < 0.01f) return;
        dx /= length;
        dy /= length;
        float back = ShapeGeometry.Distance(before, here) / 3, ahead = ShapeGeometry.Distance(here, after) / 3;
        Ins(item)[index] = new PointF(here.X - dx * back, here.Y - dy * back);
        Outs(item)[index] = new PointF(here.X + dx * ahead, here.Y + dy * ahead);
    }

    public static void MakeCorner(PageObject item, int index) => Ins(item)[index] = Outs(item)[index] = Anchors(item)[index];

    /// <summary>Shapes and lines that can be turned into a path with the same look.</summary>
    public static bool CanBecomePath(PageObject item) => item.Kind switch
    {
        MarkKind.Path => false,
        MarkKind.Line or MarkKind.Arrow or MarkKind.Spline => item.Strokes.FirstOrDefault() is { Count: >= 2 },
        MarkKind.Curve => item.Strokes.FirstOrDefault() is { Count: >= 3 },
        _ => ShapeGeometry.IsClosedShape(item.Kind),
    };

    /// <summary>
    /// Turns a shape or a line into a path that looks the same, so its points can be moved one by one: the turn and
    /// flip of a shape go into its points, and what was cropped or rubbed out stays so. Line ends are not kept.
    /// </summary>
    public static void MakePath(PageObject item)
    {
        if (item.Kind == MarkKind.Path) return;
        var points = new List<(PointF Anchor, PointF In, PointF Out)>();
        bool closed = ShapeGeometry.IsClosedShape(item.Kind);
        List<PointF> through = item.Strokes.FirstOrDefault() ?? [];
        switch (item.Kind)
        {
            case MarkKind.Line or MarkKind.Arrow when through.Count >= 2:
                points = [(through[0], through[0], through[0]), (through[^1], through[^1], through[^1])];
                break;
            case MarkKind.Curve when through.Count >= 3:
                // The bend handle of a quadratic, written as the two handles of the same curve.
                points = [(through[0], through[0], ShapeGeometry.Lerp(through[0], through[1], 2 / 3f)),
                          (through[^1], ShapeGeometry.Lerp(through[^1], through[1], 2 / 3f), through[^1])];
                break;
            case MarkKind.Spline when through.Count >= 2:
                PointF[] beziers = ShapeGeometry.SplineBeziers(through);
                for (int i = 0; i < through.Count; i++)
                    points.Add((through[i], i == 0 ? through[i] : beziers[3 * i - 1], i == through.Count - 1 ? through[i] : beziers[3 * i + 1]));
                break;
            case MarkKind.Polygon when item.CornerRadius <= 0.01f && through.Count >= 3:
                points = [.. through.Select(p => (p, p, p))];
                break;
            default:
                using (GraphicsPath outline = ShapeGeometry.Outline(item)) points = FromOutline(outline, out closed);
                break;
        }
        if (points.Count < 2) return;

        using Matrix? turn = ShapeGeometry.Transform(item);
        RectangleF? cropped = item.IsCropped && turn is null ? item.CropBounds : null;
        (bool Left, bool Top, bool Right, bool Bottom) sides = (item.CropLeft > 0, item.CropTop > 0, item.CropRight > 0, item.CropBottom > 0);
        SetPoints(item, points, closed);
        if (turn is not null)
            foreach (List<PointF> list in item.Strokes.Concat(item.Erased))
            {
                PointF[] moved = [.. list];
                turn.TransformPoints(moved);
                for (int i = 0; i < moved.Length; i++) list[i] = moved[i];
            }
        item.Rotation = 0;
        item.FlipX = item.FlipY = false;
        item.CornerRadius = 0;
        item.StartEnd = item.EndEnd = LineEnd.None;
        item.CropLeft = item.CropTop = item.CropRight = item.CropBottom = 0;
        ShapeGeometry.Fit(item);
        // What was cropped stays cropped: the same part of the page, as fractions of the new box.
        if (cropped is { } kept && item.Width > 0 && item.Height > 0)
        {
            if (sides.Left) item.CropLeft = Math.Clamp((kept.Left - item.X) / item.Width, 0, 1);
            if (sides.Top) item.CropTop = Math.Clamp((kept.Top - item.Y) / item.Height, 0, 1);
            if (sides.Right) item.CropRight = Math.Clamp((item.X + item.Width - kept.Right) / item.Width, 0, 1);
            if (sides.Bottom) item.CropBottom = Math.Clamp((item.Y + item.Height - kept.Bottom) / item.Height, 0, 1);
        }
    }

    /// <summary>The points and handles of the first figure of an outline, and whether it closes.</summary>
    static List<(PointF Anchor, PointF In, PointF Out)> FromOutline(GraphicsPath outline, out bool closed)
    {
        closed = false;
        var anchors = new List<PointF>();
        var ins = new List<PointF>();
        var outs = new List<PointF>();
        if (outline.PointCount == 0) return [];
        PointF[] points = outline.PathPoints;
        byte[] types = outline.PathTypes;
        int i = 0;
        while (i < points.Length)
        {
            int type = types[i] & 0x7, last = i;
            if (type == 0)
            {
                if (anchors.Count > 0) break;
                anchors.Add(points[i]); ins.Add(points[i]); outs.Add(points[i]);
                i++;
            }
            else if (type == 3 && i + 2 < points.Length)
            {
                outs[^1] = points[i];
                anchors.Add(points[i + 2]); ins.Add(points[i + 1]); outs.Add(points[i + 2]);
                last = i + 2;
                i += 3;
            }
            else
            {
                anchors.Add(points[i]); ins.Add(points[i]); outs.Add(points[i]);
                i++;
            }
            if ((types[last] & 0x80) != 0)
            {
                closed = true;
                break;
            }
        }
        // A closed figure that comes back to its first point ends on a copy of it; the copy's handle in is the first's.
        if (closed && anchors.Count > 2 && ShapeGeometry.Distance(anchors[0], anchors[^1]) < 0.01f)
        {
            ins[0] = ins[^1];
            anchors.RemoveAt(anchors.Count - 1);
            ins.RemoveAt(ins.Count - 1);
            outs.RemoveAt(outs.Count - 1);
        }
        return [.. anchors.Select((anchor, k) => (anchor, ins[k], outs[k]))];
    }
}
