using System.Drawing;

namespace Sheaf.Core.Editing;

/// <summary>
/// Turns a pen stroke held still at its end into the shape it was meant to be: a line, a curve, a rectangle, a
/// triangle, a circle or ellipse, a semicircle or a pie. Null when it doesn't look enough like any of them.
/// </summary>
public static class ShapeRecognizer
{
    public static PageObject? Recognize(PageObject ink)
    {
        if (ink.Strokes.FirstOrDefault() is not { Count: >= 4 } points) return null;
        float length = Length(points);
        RectangleF box = Bounds(points);
        float diagonal = MathF.Sqrt(box.Width * box.Width + box.Height * box.Height);
        if (length < 10 || diagonal < 6) return null;
        PointF first = points[0], last = points[^1];
        bool closed = Distance(first, last) < Math.Max(diagonal * 0.22f, 6) && length > diagonal * 1.6f;
        return closed ? Closed(ink, points, box, diagonal) : Open(ink, points, first, last);
    }

    static PageObject Shape(PageObject ink, MarkKind kind)
    {
        PageObject shape = ink.Clone();
        shape.Id = Guid.NewGuid();
        shape.Kind = kind;
        // Drawn in pencil, it stays pencil: only the shape changes.
        shape.Pressures = [];
        shape.Strokes = [];
        shape.FillColor = 0;
        shape.CornerRadius = 0;
        return shape;
    }

    static PageObject WithCorners(PageObject ink, MarkKind kind, List<PointF> corners)
    {
        PageObject shape = Shape(ink, kind);
        shape.Strokes = [corners];
        ShapeGeometry.Fit(shape);
        return shape;
    }

    static PageObject? Open(PageObject ink, List<PointF> points, PointF first, PointF last)
    {
        float chord = Distance(first, last);
        if (chord < 6) return null;
        float worst = points.Max(point => DistanceToSegment(point, first, last));
        if (worst < Math.Max(2.5f, chord * 0.07f)) return WithCorners(ink, MarkKind.Line, [first, last]);
        if (Arc(ink, points, first, last) is { } semicircle) return semicircle;

        // One bend: a curve through the stroke's ends and its middle, with points that can be moved, added or taken out.
        PointF middle = PointAlong(points, Length(points) / 2);
        var bend = new PointF(2 * middle.X - (first.X + last.X) / 2, 2 * middle.Y - (first.Y + last.Y) / 2);
        float error = points.Average(point => DistanceToCurve(point, first, bend, last));
        return error < Math.Max(2.5f, chord * 0.06f) ? WithCorners(ink, MarkKind.Spline, [first, middle, last]) : null;
    }

    /// <summary>
    /// An open stroke that follows a circle about halfway round: the arc of a semicircle, which gets its flat side.
    /// Shallower arcs are left to the curve.
    /// </summary>
    static PageObject? Arc(PageObject ink, List<PointF> points, PointF first, PointF last)
    {
        PointF middle = PointAlong(points, Length(points) / 2);
        // The circle through the two ends and the middle of the stroke.
        double ax = first.X, ay = first.Y, bx = middle.X, by = middle.Y, cx = last.X, cy = last.Y;
        double d = 2 * (ax * (by - cy) + bx * (cy - ay) + cx * (ay - by));
        if (Math.Abs(d) < 1e-6) return null;
        double ux = ((ax * ax + ay * ay) * (by - cy) + (bx * bx + by * by) * (cy - ay) + (cx * cx + cy * cy) * (ay - by)) / d;
        double uy = ((ax * ax + ay * ay) * (cx - bx) + (bx * bx + by * by) * (ax - cx) + (cx * cx + cy * cy) * (bx - ax)) / d;
        var centre = new PointF((float)ux, (float)uy);
        float radius = Distance(centre, first);
        if (radius < 6 || points.Average(p => Math.Abs(Distance(p, centre) / radius - 1)) > 0.08f) return null;
        // How far round the stroke goes, adding up the turn from point to point.
        double swept = 0;
        for (int i = 1; i < points.Count; i++)
        {
            double step = Math.Atan2(points[i].Y - centre.Y, points[i].X - centre.X) - Math.Atan2(points[i - 1].Y - centre.Y, points[i - 1].X - centre.X);
            swept += Math.IEEERemainder(step, 2 * Math.PI);
        }
        double degrees = Math.Abs(swept) * 180 / Math.PI;
        if (degrees is < 140 or > 230) return null;
        // Half a circle round where the stroke's middle points, whichever way it was drawn.
        double toMiddle = Math.Atan2(middle.Y - centre.Y, middle.X - centre.X);
        return SemiCircleAt(ink, centre, new PointF((float)Math.Cos(toMiddle), (float)Math.Sin(toMiddle)), radius);
    }

    /// <summary>
    /// A true semicircle: its flat side's middle at <paramref name="centre"/>, its arc reaching out along
    /// <paramref name="toArc"/>. The shape is drawn arc up, so it is turned to face the way it was drawn.
    /// </summary>
    static PageObject SemiCircleAt(PageObject ink, PointF centre, PointF toArc, float radius)
    {
        PageObject shape = Shape(ink, MarkKind.SemiCircle);
        var middle = new PointF(centre.X + toArc.X * radius / 2, centre.Y + toArc.Y * radius / 2);
        shape.Bounds = new RectangleF(middle.X - radius, middle.Y - radius / 2, 2 * radius, radius);
        shape.Rotation = Degrees(Math.Atan2(toArc.Y, toArc.X)) + 90;
        return shape;
    }

    static float Degrees(double radians)
    {
        double degrees = radians * 180 / Math.PI % 360;
        if (degrees > 180) degrees -= 360;
        if (degrees <= -180) degrees += 360;
        // Close enough to straight is straight.
        double nearest = Math.Round(degrees / 90) * 90;
        return (float)(Math.Abs(degrees - nearest) < 6 ? nearest : degrees);
    }

    static PageObject? Closed(PageObject ink, List<PointF> points, RectangleF box, float diagonal)
    {
        if (Outlined(ink, points, box, diagonal) is { } outlined) return outlined;
        // A circle or ellipse: every point about as far from the middle as the box allows.
        float cx = box.X + box.Width / 2, cy = box.Y + box.Height / 2, rx = Math.Max(1, box.Width / 2), ry = Math.Max(1, box.Height / 2);
        float roundness = points.Average(p => Math.Abs(MathF.Sqrt(Square((p.X - cx) / rx) + Square((p.Y - cy) / ry)) - 1));
        List<PointF> corners = Corners(points, diagonal * 0.08f);
        if (roundness < 0.085f && corners.Count >= 4)
        {
            PageObject ellipse = Shape(ink, MarkKind.Ellipse);
            // Nearly round is meant to be round.
            if (Math.Abs(rx - ry) / Math.Max(rx, ry) < 0.14f) rx = ry = (rx + ry) / 2;
            ellipse.Bounds = new RectangleF(cx - rx, cy - ry, 2 * rx, 2 * ry);
            return ellipse;
        }
        if (SemiCircle(ink, points, corners, diagonal) is { } semicircle) return semicircle;
        if (Pie(ink, points, corners, diagonal) is { } pie) return pie;
        if (corners.Count == 3) return WithCorners(ink, MarkKind.Polygon, corners);
        if (corners.Count == 4)
        {
            bool upright = Enumerable.Range(0, 4).All(i => IsUpright(corners[i], corners[(i + 1) % 4]));
            if (upright)
            {
                PageObject rectangle = Shape(ink, MarkKind.Rectangle);
                rectangle.Bounds = Bounds(corners);
                return rectangle;
            }
            return Turned(ink, corners) ?? WithCorners(ink, MarkKind.Polygon, corners);
        }
        return corners.Count is 5 or 6 ? WithCorners(ink, MarkKind.Polygon, corners) : null;
    }

    /// <summary>
    /// Shapes known by their whole outline rather than their corners: a heart, and a star drawn round its points
    /// or as a pentagram in one go. The stroke has to follow the shape drawn in the same box closely, and the
    /// shape has to be covered by the stroke all the way round.
    /// </summary>
    static PageObject? Outlined(PageObject ink, List<PointF> points, RectangleF box, float diagonal)
    {
        if (box.Width < 12 || box.Height < 12 || box.Width / box.Height is < 0.55f or > 1.8f) return null;
        (MarkKind Kind, List<PointF> Outline)[] candidates =
        [
            (MarkKind.Heart, OutlineOf(MarkKind.Heart, box)),
            (MarkKind.Star, OutlineOf(MarkKind.Star, box)),
            (MarkKind.Star, Pentagram(box)),
        ];
        MarkKind? best = null;
        float bestScore = 0.06f;
        foreach (var (kind, outline) in candidates)
        {
            // Both ways: the stroke near the outline, and the outline near the stroke (so half a heart isn't one).
            float along = points.Average(p => DistanceToPolyline(p, outline)) / diagonal;
            float covered = Resample(outline, 60).Average(p => DistanceToPolyline(p, points)) / diagonal;
            float score = Math.Max(along, covered);
            if (score < bestScore) { bestScore = score; best = kind; }
        }
        if (best is not { } found) return null;
        PageObject shape = Shape(ink, found);
        shape.Bounds = box;
        return shape;
    }

    static List<PointF> OutlineOf(MarkKind kind, RectangleF box)
    {
        using var path = ShapeGeometry.Outline(new PageObject { Kind = kind, Bounds = box });
        var outline = ShapeGeometry.Flatten(path).ToList();
        outline.Add(outline[0]);
        return outline;
    }

    /// <summary>A five-pointed star drawn in one line, point to every other point.</summary>
    static List<PointF> Pentagram(RectangleF box)
    {
        float cx = box.X + box.Width / 2, cy = box.Y + box.Height / 2;
        PointF Point(int i)
        {
            double angle = -Math.PI / 2 + i * 2 * Math.PI / 5;
            return new PointF(cx + (float)Math.Cos(angle) * box.Width / 2, cy + (float)Math.Sin(angle) * box.Height / 2);
        }
        return [Point(0), Point(2), Point(4), Point(1), Point(3), Point(0)];
    }

    static float DistanceToPolyline(PointF p, List<PointF> line)
    {
        float best = float.MaxValue;
        for (int i = 1; i < line.Count; i++) best = Math.Min(best, DistanceToSegment(p, line[i - 1], line[i]));
        return best;
    }

    static List<PointF> Resample(List<PointF> line, int count)
    {
        float total = Length(line);
        return [.. Enumerable.Range(0, count).Select(i => PointAlong(line, total * i / count))];
    }

    /// <summary>A rectangle drawn at a slant: four square corners. It becomes a rectangle turned to match.</summary>
    static PageObject? Turned(PageObject ink, List<PointF> corners)
    {
        for (int i = 0; i < 4; i++)
        {
            PointF before = corners[(i + 3) % 4], corner = corners[i], after = corners[(i + 1) % 4];
            float ax = before.X - corner.X, ay = before.Y - corner.Y, bx = after.X - corner.X, by = after.Y - corner.Y;
            float cos = (ax * bx + ay * by) / Math.Max(0.001f, MathF.Sqrt(ax * ax + ay * ay) * MathF.Sqrt(bx * bx + by * by));
            if (Math.Abs(cos) > 0.2f) return null;
        }
        float width = (Distance(corners[0], corners[1]) + Distance(corners[2], corners[3])) / 2;
        float height = (Distance(corners[1], corners[2]) + Distance(corners[3], corners[0])) / 2;
        var middle = new PointF(corners.Average(p => p.X), corners.Average(p => p.Y));
        PageObject rectangle = Shape(ink, MarkKind.Rectangle);
        rectangle.Bounds = new RectangleF(middle.X - width / 2, middle.Y - height / 2, width, height);
        rectangle.Rotation = Degrees(Math.Atan2(corners[1].Y - corners[0].Y, corners[1].X - corners[0].X));
        return rectangle;
    }

    /// <summary>
    /// A straight side with an arc over it: a semicircle, even a squashed one, at any angle. Each side long enough
    /// to be the flat one (or two sides in a near-straight line) is tried, and the arc that best fits wins.
    /// </summary>
    static PageObject? SemiCircle(PageObject ink, List<PointF> points, List<PointF> corners, float diagonal)
    {
        if (corners.Count < 2) return null;
        PageObject? best = null;
        float bestError = 0.12f;
        for (int i = 0; i < corners.Count; i++)
            for (int span = 1; span <= 2 && span < corners.Count; span++)
            {
                PointF a = corners[i], b = corners[(i + span) % corners.Count];
                // Two sides count as one flat side only if the corner between them is barely a corner.
                if (span == 2 && DistanceToSegment(corners[(i + 1) % corners.Count], a, b) > Distance(a, b) * 0.08f) continue;
                if (Fit(ink, points, a, b, diagonal) is ({ } shape, float error) && error < bestError)
                {
                    best = shape;
                    bestError = error;
                }
            }
        return best;
    }

    static (PageObject Shape, float Error)? Fit(PageObject ink, List<PointF> points, PointF a, PointF b, float diagonal)
    {
        float chord = Distance(a, b), half = chord / 2;
        if (chord < diagonal * 0.6f) return null;
        var middle = new PointF((a.X + b.X) / 2, (a.Y + b.Y) / 2);
        var along = new PointF((b.X - a.X) / chord, (b.Y - a.Y) / chord);
        var across = new PointF(-along.Y, along.X);
        float Along(PointF p) => (p.X - middle.X) * along.X + (p.Y - middle.Y) * along.Y;
        float Across(PointF p) => (p.X - middle.X) * across.X + (p.Y - middle.Y) * across.Y;
        var arc = points.Where(p => DistanceToSegment(p, a, b) > half * 0.12f).ToList();
        if (arc.Count < 6) return null;
        float side = Math.Sign(arc.Average(Across));
        if (side == 0 || arc.Count(p => Across(p) * side < 0) > arc.Count / 8) return null;
        float height = arc.Max(p => Across(p) * side);
        if (height < half * 0.5f || height > half * 1.6f) return null;
        // How far each arc point is from a half-ellipse over the flat side; a semicircle is the round case of it.
        // Close on average, and nowhere far off: a rectangle's straight sides fit well enough on average, but its
        // corners stand well away from any arc.
        var errors = arc.Select(p =>
        {
            float u = Along(p) / half, v = Across(p) * side / height;
            return Math.Abs(MathF.Sqrt(u * u + v * v) - 1);
        }).Order().ToList();
        if (errors[(int)(errors.Count * 0.9f)] > 0.25f) return null;
        return (SemiCircleAt(ink, middle, new PointF(across.X * side, across.Y * side), half), errors.Average());
    }

    /// <summary>A corner with two equal straight sides, and an arc between their far ends centred on the corner.</summary>
    static PageObject? Pie(PageObject ink, List<PointF> points, List<PointF> corners, float diagonal)
    {
        if (corners.Count < 3) return null;
        for (int i = 0; i < corners.Count; i++)
        {
            PointF centre = corners[i], p = corners[(i + corners.Count - 1) % corners.Count], q = corners[(i + 1) % corners.Count];
            float r1 = Distance(centre, p), r2 = Distance(centre, q), radius = (r1 + r2) / 2;
            if (radius < diagonal * 0.3f || Math.Abs(r1 - r2) / radius > 0.25f) continue;
            var arc = points.Where(point => DistanceToSegment(point, centre, p) > radius * 0.15f && DistanceToSegment(point, centre, q) > radius * 0.15f).ToList();
            if (arc.Count < 4 || arc.Average(point => Math.Abs(Distance(point, centre) / radius - 1)) > 0.13f) continue;
            // An arc bulges away from the straight line between its ends; a triangle's third side doesn't.
            if (arc.Max(point => DistanceToSegment(point, p, q)) < radius * 0.12f) continue;
            double from = Math.Atan2(p.Y - centre.Y, p.X - centre.X), to = Math.Atan2(q.Y - centre.Y, q.X - centre.X);
            PointF mid = arc[arc.Count / 2];
            double through = Math.Atan2(mid.Y - centre.Y, mid.X - centre.X);
            double sweep = Normalize(to - from), via = Normalize(through - from);
            if (via > sweep) sweep -= 2 * Math.PI;
            // A true pie, turned so its arc faces the way it was drawn (it is drawn with the arc's middle to the left).
            PageObject pie = Shape(ink, MarkKind.Pie);
            pie.Bounds = new RectangleF(centre.X - radius, centre.Y - radius, 2 * radius, 2 * radius);
            pie.Sweep = (float)Math.Clamp(Math.Abs(sweep) * 180 / Math.PI, 5, 355);
            pie.Rotation = Degrees(from + sweep / 2) - 180;
            return pie;
        }
        return null;
    }

    /// <summary>The corners of a closed stroke: its outline simplified, with the end joined to the start.</summary>
    static List<PointF> Corners(List<PointF> points, float tolerance)
    {
        List<PointF> simple = Simplify(points, tolerance);
        if (simple.Count > 2 && Distance(simple[0], simple[^1]) < tolerance * 2.5f) simple.RemoveAt(simple.Count - 1);
        // A corner that barely turns is part of a side.
        for (int i = 0; i < simple.Count && simple.Count > 3; i++)
        {
            PointF before = simple[(i + simple.Count - 1) % simple.Count], after = simple[(i + 1) % simple.Count];
            if (DistanceToSegment(simple[i], before, after) < tolerance * 0.9f) simple.RemoveAt(i--);
        }
        return simple;
    }

    /// <summary>Douglas–Peucker: keeps the points that stand out more than <paramref name="tolerance"/> from a straight run.</summary>
    static List<PointF> Simplify(List<PointF> points, float tolerance)
    {
        if (points.Count < 3) return [.. points];
        int far = -1;
        float worst = 0;
        for (int i = 1; i < points.Count - 1; i++)
        {
            float d = DistanceToSegment(points[i], points[0], points[^1]);
            if (d > worst) { worst = d; far = i; }
        }
        if (worst <= tolerance) return [points[0], points[^1]];
        List<PointF> left = Simplify(points[..(far + 1)], tolerance), right = Simplify(points[far..], tolerance);
        return [.. left.Take(left.Count - 1), .. right];
    }

    static bool IsUpright(PointF a, PointF b)
    {
        double angle = Math.Abs(Math.Atan2(b.Y - a.Y, b.X - a.X) * 180 / Math.PI) % 90;
        return angle < 16 || angle > 74;
    }

    static double Normalize(double angle) => (angle % (2 * Math.PI) + 2 * Math.PI) % (2 * Math.PI);

    static double Cross(PointF a, PointF b, PointF p) => (b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X);

    static float Square(float value) => value * value;

    static float Distance(PointF a, PointF b) => ShapeGeometry.Distance(a, b);

    static float Length(List<PointF> points)
    {
        float total = 0;
        for (int i = 1; i < points.Count; i++) total += Distance(points[i - 1], points[i]);
        return total;
    }

    static PointF PointAlong(List<PointF> points, float distance)
    {
        for (int i = 1; i < points.Count; i++)
        {
            float step = Distance(points[i - 1], points[i]);
            if (distance <= step) return ShapeGeometry.Lerp(points[i - 1], points[i], step == 0 ? 0 : distance / step);
            distance -= step;
        }
        return points[^1];
    }

    static RectangleF Bounds(IReadOnlyList<PointF> points)
    {
        float left = points.Min(p => p.X), top = points.Min(p => p.Y);
        return RectangleF.FromLTRB(left, top, points.Max(p => p.X), points.Max(p => p.Y));
    }

    static float DistanceToSegment(PointF p, PointF a, PointF b)
    {
        float dx = b.X - a.X, dy = b.Y - a.Y, lengthSquared = dx * dx + dy * dy;
        float t = lengthSquared == 0 ? 0 : Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lengthSquared, 0, 1);
        return Distance(p, new PointF(a.X + t * dx, a.Y + t * dy));
    }

    static float DistanceToCurve(PointF p, PointF start, PointF bend, PointF end)
    {
        float best = float.MaxValue;
        for (int i = 0; i <= 24; i++)
        {
            float t = i / 24f, u = 1 - t;
            var on = new PointF(u * u * start.X + 2 * u * t * bend.X + t * t * end.X, u * u * start.Y + 2 * u * t * bend.Y + t * t * end.Y);
            best = Math.Min(best, Distance(p, on));
        }
        return best;
    }
}
