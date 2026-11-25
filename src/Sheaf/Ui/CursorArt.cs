using System.Drawing.Drawing2D;

namespace Sheaf.Ui;

/// <summary>Mouse pointers Windows doesn't have, drawn once at the screen's size.</summary>
internal static class CursorArt
{
    static readonly Dictionary<int, Cursor> Turns = [];

    /// <summary>
    /// The pointer for turning something: a round arrow, black with a white edge like the system pointers, its hot
    /// spot in the middle.
    /// </summary>
    public static Cursor Turn(int dpi)
    {
        if (Turns.TryGetValue(dpi, out Cursor? cursor)) return cursor;
        int size = Math.Max(24, (int)Math.Round(32 * dpi / 96f));
        using var picture = new Bitmap(size, size);
        using (Graphics g = Graphics.FromImage(picture))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float scale = size / 32f;
            g.ScaleTransform(scale, scale);
            using var arc = new GraphicsPath();
            arc.AddArc(8, 8, 16, 16, 200, 250);
            PointF end = arc.GetLastPoint();
            // An arrow head at the end of the arc (its bottom, where it runs right to left), pointing on round it.
            using var head = new GraphicsPath();
            head.AddPolygon([new PointF(end.X - 4f, end.Y), new PointF(end.X + 2f, end.Y - 4.5f), new PointF(end.X + 2f, end.Y + 4.5f)]);
            using (var edge = new Pen(Color.White, 5) { StartCap = LineCap.Round }) g.DrawPath(edge, arc);
            using (var edge = new Pen(Color.White, 2.5f) { LineJoin = LineJoin.Round }) g.DrawPath(edge, head);
            using (var line = new Pen(Color.Black, 2) { StartCap = LineCap.Round }) g.DrawPath(line, arc);
            g.FillPath(Brushes.Black, head);
            g.FillEllipse(Brushes.White, 14.5f, 14.5f, 3, 3);
            g.FillEllipse(Brushes.Black, 15.25f, 15.25f, 1.5f, 1.5f);
        }
        cursor = Platform.Native.CursorFrom(picture, new Point(size / 2, size / 2));
        Turns[dpi] = cursor;
        return cursor;
    }
}
