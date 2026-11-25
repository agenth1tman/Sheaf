using System.Drawing.Drawing2D;
using System.Globalization;

namespace Sheaf.Ui;

/// <summary>
/// Stroke icons on a 24-unit grid, written as SVG path data (the same strings as the design mockup) and
/// parsed once into GraphicsPaths, so they stay sharp at any size and DPI.
/// </summary>
internal static class Icons
{
    static readonly Dictionary<string, string> Data = new()
    {
        ["home"] = "M4 11l8-7 8 7v9h-5v-6H9v6H4z",
        ["plus"] = "M12 5v14M5 12h14",
        ["minus"] = "M5 12h14",
        ["close"] = "M6 6l12 12M18 6L6 18",
        ["chevron-down"] = "M6 9l6 6 6-6",
        ["chevron-right"] = "M9 6l6 6-6 6",
        ["chevron-left"] = "M15 6l-6 6 6 6",
        ["more"] = "M5 12h.01M12 12h.01M19 12h.01",
        ["search"] = "M11 4a7 7 0 1 0 0 14a7 7 0 1 0 0-14M20 20l-4-4",
        ["folder"] = "M3 7h6l2 2h10v10H3z",
        ["file"] = "M6 3h8l4 4v14H6zM14 3v4h4",
        ["file-plus"] = "M6 3h8l4 4v14H6zM14 3v4h4M12 11v6M9 14h6",
        ["scan"] = "M4 8V5h3M17 5h3v3M20 16v3h-3M7 19H4v-3M8 12h8",
        ["settings"] = "M10.21 4.82 L10.42 2.53 L13.58 2.53 L13.79 4.82 L15.81 5.66 L17.57 4.18 L19.82 6.43 L18.34 8.19 L19.18 10.21 L21.47 10.42 L21.47 13.58 L19.18 13.79 L18.34 15.81 L19.82 17.57 L17.57 19.82 L15.81 18.34 L13.79 19.18 L13.58 21.47 L10.42 21.47 L10.21 19.18 L8.19 18.34 L6.43 19.82 L4.18 17.57 L5.66 15.81 L4.82 13.79 L2.53 13.58 L2.53 10.42 L4.82 10.21 L5.66 8.19 L4.18 6.43 L6.43 4.18 L8.19 5.66ZM12 9a3 3 0 1 0 0 6a3 3 0 1 0 0-6",
        ["undo"] = "M9 14L4 9l5-5M4 9h11a5 5 0 0 1 0 10h-3",
        ["redo"] = "M15 14l5-5-5-5M20 9H9a5 5 0 0 0 0 10h3",
        ["select"] = "M5 3l14 8-6.2 1.8L11 19z",
        ["vertex-select"] = "M4 3l12 7-5.3 1.6L9 17zM15 15h5v5h-5z",
        ["pen-path"] = "M4 20l3-8 8-8 5 5-8 8zM4 20l5.4-5.4M11 11.5a1.5 1.5 0 1 0 0 3a1.5 1.5 0 1 0 0-3",
        ["hand"] = "M8 12V6a1.5 1.5 0 0 1 3 0v5M11 11V4.5a1.5 1.5 0 0 1 3 0V11M14 11V6a1.5 1.5 0 0 1 3 0v7c0 4-2.5 7-6 7-2.5 0-4-1-5.5-3L4 13.5a1.5 1.5 0 0 1 2.3-1.9L8 13",
        ["text"] = "M5 6V4h14v2M12 4v16M9 20h6",
        ["rect"] = "M6 4h12a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2z",
        ["ellipse"] = "M12 6c4.4 0 8 2.7 8 6s-3.6 6-8 6-8-2.7-8-6 3.6-6 8-6z",
        ["line"] = "M5 19L19 5",
        ["arrow"] = "M5 19L19 5M10 5h9v9",
        ["draw"] = "M4 20l4-1 11-11-3-3L5 16zM14 6l3 3",
        ["image"] = "M5 5h14a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V7a2 2 0 0 1 2-2zM9 8.4a1.6 1.6 0 1 0 0 3.2a1.6 1.6 0 1 0 0-3.2M21 16l-5-5-8 8",
        ["edit-text"] = "M4 7V5h10v2M9 5v14M7 19h4M14 13l3-3 3 3-5 5h-3v-3z",
        ["highlight"] = "M9 15l-4 4h5l2-2M9 15l8-8 3 3-8 8z",
        ["underline"] = "M7 4v7a5 5 0 0 0 10 0V4M5 20h14",
        ["strike"] = "M5 12h14M16 6.5C15.2 5 13.8 4 12 4c-2.5 0-4.5 1.5-4.5 3.5 0 4 9 2.5 9 7 0 2-2 3.5-4.5 3.5-2 0-3.6-1-4.5-2.5",
        ["note"] = "M5 4h14v11l-5 5H5zM14 20v-5h5",
        ["signature"] = "M3 17c3 0 4-9 6-9s0 9 3 9 2-5 4-5 1 3 3 3M3 21h18",
        ["calendar"] = "M5 6h14v14H5zM5 10h14M9 4v4M15 4v4",
        ["check"] = "M5 12l5 5 9-10",
        ["cross"] = "M7 7l10 10M17 7L7 17",
        ["redact"] = "M4 6h16v12H4zM8 18l8-12M4 13l5-7M12 18l8-12",
        ["freeform"] = "M6 8c3-4 10-4 12 0s-1 9-6 9-9-1-8-5 2-3 2-4z",
        ["mark-text"] = "M4 7V5h10v2M9 5v14M7 19h4M16 9v10M14 9h4M14 19h4",
        ["qr"] = "M4 4h6v6H4zM14 4h6v6h-6zM4 14h6v6H4zM14 14h2v2h-2zM18 18h2v2h-2zM14 18v2M18 14h2",
        ["eye"] = "M2 12s4-7 10-7 10 7 10 7-4 7-10 7S2 12 2 12zM12 9a3 3 0 1 0 0 6a3 3 0 1 0 0-6",
        ["eye-off"] = "M3 12s3.5-6 9-6 9 6 9 6-3.5 6-9 6-9-6-9-6zM4 4l16 16",
        ["lock"] = "M5 11h14v9H5zM8 11V8a4 4 0 0 1 8 0v3",
        ["unlock"] = "M5 11h14v9H5zM8 11V8a4 4 0 0 1 7.5-2",
        ["rotate"] = "M20 12a8 8 0 1 1-3-6.2M20 4v5h-5",
        ["rotate-left"] = "M4 12a8 8 0 1 0 3-6.2M4 4v5h5",
        ["mirror"] = "M12 3v18M9 7L3 17h6zM15 7l6 10h-6z",
        ["flip-vertical"] = "M3 12h18M7 9L17 3v6zM7 15l10 6v-6z",
        ["crop"] = "M7 3v14h14M3 7h14v14",
        ["eyedropper"] = "M14.5 4.5a2.1 2.1 0 0 1 3 0l2 2a2.1 2.1 0 0 1 0 3L17 12l-5-5zM13.5 8.5L5 17v2h2l8.5-8.5",
        ["group"] = "M4 4h9v9H4zM11 11h9v9h-9zM3 3h2M3 3v2M21 21h-2M21 21v-2",
        ["trash"] = "M4 7h16M9 7V4h6v3M6 7l1 13h10l1-13",
        ["copy"] = "M8 8h12v12H8zM4 16V4h12",
        ["download"] = "M12 4v11M7 10l5 5 5-5M5 20h14",
        ["pages"] = "M4 4h7v7H4zM13 4h7v7h-7zM4 13h7v7H4zM13 13h7v7h-7z",
        ["merge"] = "M8 4H5v16h3M16 4h3v16h-3M12 8v8M8 12h8",
        ["split"] = "M5 4h6v16H5zM13 4h6v16h-6z",
        ["extract"] = "M6 3h8l4 4v6M6 3v18h6M14 3v4h4M14 17h7M18 14l3 3-3 3",
        ["remove-page"] = "M6 3h8l4 4v14H6zM14 3v4h4M9 14h6",
        ["comment"] = "M4 5h16v11H9l-5 4z",
        ["numbers"] = "M9 4L7 20M17 4l-2 16M4 9h16M3 15h16",
        ["watermark"] = "M12 3s6 6.5 6 11a6 6 0 0 1-12 0c0-4.5 6-11 6-11z",
        ["compare"] = "M8 4v16M16 4v16M4 8l4-4 4 4M12 16l4 4 4-4",
        ["compress"] = "M12 3v6M9 6l3 3 3-3M12 21v-6M9 18l3-3 3 3M4 12h16",
        ["ocr"] = "M4 8V5h3M17 5h3v3M20 16v3h-3M7 19H4v-3M9 15l3-7 3 7M10 13h4",
        ["camera"] = "M4 8h3l2-3h6l2 3h3v11H4zM12 10a3.5 3.5 0 1 0 0 7a3.5 3.5 0 1 0 0-7",
        ["printer"] = "M7 9V4h10v5M7 17H4v-8h16v8h-3M7 14h10v6H7z",
        ["zoom-in"] = "M11 4a7 7 0 1 0 0 14a7 7 0 1 0 0-14M20 20l-4-4M8 11h6M11 8v6",
        ["fit"] = "M4 9V4h5M15 4h5v5M20 15v5h-5M9 20H4v-5",
        ["layers"] = "M12 4l9 5-9 5-9-5zM3 14l9 5 9-5",
        ["up"] = "M12 19V5M6 11l6-6 6 6",
        ["down"] = "M12 5v14M6 13l6 6 6-6",
        ["bold"] = "M7 4h6a4 4 0 0 1 0 8H7zM7 12h7a4 4 0 0 1 0 8H7z",
        ["italic"] = "M10 4h8M6 20h8M14 4l-4 16",
        ["align-left"] = "M4 6h16M4 10h10M4 14h16M4 18h10",
        ["align-center"] = "M4 6h16M7 10h10M4 14h16M7 18h10",
        ["align-right"] = "M4 6h16M10 10h10M4 14h16M10 18h10",
        ["align-left-edge"] = "M4 3v18M8 7h12v3H8zM8 14h7v3H8z",
        ["align-h-centre"] = "M12 3v18M6 7h12v3H6zM8.5 14h7v3h-7z",
        ["align-right-edge"] = "M20 3v18M4 7h12v3H4zM9 14h7v3H9z",
        ["align-top-edge"] = "M3 4h18M7 8v12h3V8zM14 8v7h3V8z",
        ["align-v-middle"] = "M3 12h18M7 6v12h3V6zM14 8.5v7h3v-7z",
        ["align-bottom-edge"] = "M3 20h18M7 4v12h3V4zM14 9v7h3V9z",
        ["distribute-h"] = "M4 3v18M20 3v18M10 7h4v10h-4z",
        ["distribute-v"] = "M3 4h18M3 20h18M7 10h10v4H7z",
        ["bring-front"] = "M12 21V9M7 14l5-5 5 5M5 3h14M5 6h14",
        ["bring-forward"] = "M12 20V8M7 13l5-5 5 5M5 4h14",
        ["send-backward"] = "M12 4v12M7 11l5 5 5-5M5 20h14",
        ["send-back"] = "M12 3v12M7 10l5 5 5-5M5 18h14M5 21h14",
        ["heart"] = "M12 20s-7.5-4.6-7.5-10.2A4.2 4.2 0 0 1 12 7.2a4.2 4.2 0 0 1 7.5 2.6C19.5 15.4 12 20 12 20z",
        ["star"] = "M12 3l2.7 5.6 6.1.8-4.5 4.2 1.2 6.1L12 16.8l-5.5 2.9 1.2-6.1-4.5-4.2 6.1-.8z",
        ["parallelogram"] = "M8 6h13l-5 12H3z",
        ["ungroup"] = "M3 3h8v8H3zM13 13h8v8h-8z",
        ["align-justify"] = "M4 6h16M4 10h16M4 14h16M4 18h16",
        ["valign-top"] = "M4 4h16M12 20V9M8 13l4-4 4 4",
        ["valign-middle"] = "M4 12h16M12 3v5M12 21v-5M9 5l3 3 3-3M9 19l3-3 3 3",
        ["valign-bottom"] = "M4 20h16M12 4v11M8 11l4 4 4-4",
        ["triangle"] = "M12 4l9 16H3z",
        ["semicircle"] = "M3 17a9 9 0 0 1 18 0z",
        ["pie"] = "M12 12L19.8 7.5a9 9 0 1 0 0 9z",
        ["curve"] = "M4 18c4-14 12-14 16 0",
        ["select-text"] = "M7 4h4M7 20h4M9 4v16M14 8h6M14 12h6M14 16h4",
        ["lift"] = "M4 4h3M10 4h3M4 4v3M4 10v3M10 10h10v10H10z",
        ["panel-left"] = "M5 5h14a1 1 0 0 1 1 1v12a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V6a1 1 0 0 1 1-1zM9.5 5v14",
        ["panel-right"] = "M5 5h14a1 1 0 0 1 1 1v12a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V6a1 1 0 0 1 1-1zM14.5 5v14",
        ["cut"] = "M6 4.5a2.5 2.5 0 1 0 0 5a2.5 2.5 0 1 0 0-5M6 14.5a2.5 2.5 0 1 0 0 5a2.5 2.5 0 1 0 0-5M8.2 8.6L20 17M8.2 15.4L20 7",
        ["duplicate"] = "M9 9h11v11H9zM5 15H4V4h11v1M14.5 12v5M12 14.5h5",
        ["eraser"] = "M7 20h13M4.6 14.6l9-9a2 2 0 0 1 2.8 0l2 2a2 2 0 0 1 0 2.8l-9 9H8.4zM9 10l5 5",
        ["eraser-stroke"] = "M4.6 12.6l7-7a2 2 0 0 1 2.8 0l1.6 1.6a2 2 0 0 1 0 2.8l-7 7H7.4zM8 9l4 4M13 20c1.5-2 3-2 4 0s2.5 2 4 0",
        ["lift-pen"] = "M4 4h3M10 4h3M4 4v3M4 10v3M11 21l1-4 6-6 3 3-6 6zM16 13l3 3",
        ["lasso"] = "M12 4c5 0 8 2.5 8 5.5s-3 5.5-8 5.5-8-2.5-8-5.5 3-5.5 8-5.5zM7 14c-1 2 0 4 2 4s1 2-1 3",
        ["history"] = "M4 12a8 8 0 1 0 2.3-5.7M4 4v4h4M12 8v4l3 2",
        ["paint"] = "M5 11l7-7 7 7-7 7zM19 15c0 1.5 1 2.5 1 3.5a1 1 0 0 1-2 0c0-1 1-2 1-3.5",
        ["sun"] = "M12 8a4 4 0 1 0 0 8a4 4 0 1 0 0-8M12 2v2M12 20v2M2 12h2M20 12h2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4",
        ["shield"] = "M12 3l8 3v6c0 5-3.5 8-8 9-4.5-1-8-4-8-9V6z",
    };

    static readonly Dictionary<string, GraphicsPath> Parsed = [];

    public static bool Has(string name) => Data.ContainsKey(name);

    /// <summary>Draws the icon in <paramref name="bounds"/> with a stroke that stays about 1.75 px at 24 px.</summary>
    public static void Draw(Graphics g, string name, Rectangle bounds, Color color, float strokeUnits = 1.75f)
    {
        if (!Data.ContainsKey(name)) return;
        if (!Parsed.TryGetValue(name, out GraphicsPath? source))
            Parsed[name] = source = SvgPath.Parse(Data[name]);

        float scale = Math.Min(bounds.Width, bounds.Height) / 24f;
        using var path = (GraphicsPath)source.Clone();
        using var transform = new Matrix();
        transform.Translate(bounds.X + (bounds.Width - 24 * scale) / 2, bounds.Y + (bounds.Height - 24 * scale) / 2);
        transform.Scale(scale, scale);
        path.Transform(transform);

        SmoothingMode old = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(color, Math.Max(1f, strokeUnits * scale)) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawPath(pen, path);
        g.SmoothingMode = old;
    }
}

/// <summary>Parses SVG path data (M L H V C S Q T A Z, absolute and relative) into a GraphicsPath.</summary>
internal static class SvgPath
{
    public static GraphicsPath Parse(string data)
    {
        var path = new GraphicsPath();
        var tokens = new Tokens(data);
        PointF current = default, start = default, lastControl = default;
        char command = 'M', previous = ' ';
        while (tokens.More)
        {
            if (tokens.PeekCommand() is char next) { command = next; tokens.Skip(); }
            bool relative = char.IsLower(command);
            PointF Rel(float x, float y) => relative ? new PointF(current.X + x, current.Y + y) : new PointF(x, y);
            switch (char.ToUpperInvariant(command))
            {
                case 'M':
                    current = start = Rel(tokens.Number(), tokens.Number());
                    path.StartFigure();
                    command = relative ? 'l' : 'L';
                    break;
                case 'L':
                {
                    PointF to = Rel(tokens.Number(), tokens.Number());
                    path.AddLine(current, to);
                    current = to;
                    break;
                }
                case 'H':
                {
                    float x = tokens.Number();
                    PointF to = new(relative ? current.X + x : x, current.Y);
                    path.AddLine(current, to);
                    current = to;
                    break;
                }
                case 'V':
                {
                    float y = tokens.Number();
                    PointF to = new(current.X, relative ? current.Y + y : y);
                    path.AddLine(current, to);
                    current = to;
                    break;
                }
                case 'C':
                {
                    PointF c1 = Rel(tokens.Number(), tokens.Number()), c2 = Rel(tokens.Number(), tokens.Number()), to = Rel(tokens.Number(), tokens.Number());
                    path.AddBezier(current, c1, c2, to);
                    lastControl = c2;
                    current = to;
                    break;
                }
                case 'S':
                {
                    PointF c1 = "CcSs".Contains(previous) ? Reflect(lastControl, current) : current;
                    PointF c2 = Rel(tokens.Number(), tokens.Number()), to = Rel(tokens.Number(), tokens.Number());
                    path.AddBezier(current, c1, c2, to);
                    lastControl = c2;
                    current = to;
                    break;
                }
                case 'Q':
                {
                    PointF q = Rel(tokens.Number(), tokens.Number()), to = Rel(tokens.Number(), tokens.Number());
                    AddQuadratic(path, current, q, to);
                    lastControl = q;
                    current = to;
                    break;
                }
                case 'T':
                {
                    PointF q = "QqTt".Contains(previous) ? Reflect(lastControl, current) : current;
                    PointF to = Rel(tokens.Number(), tokens.Number());
                    AddQuadratic(path, current, q, to);
                    lastControl = q;
                    current = to;
                    break;
                }
                case 'A':
                {
                    float rx = tokens.Number(), ry = tokens.Number(), angle = tokens.Number();
                    bool large = tokens.Number() != 0, sweep = tokens.Number() != 0;
                    PointF to = Rel(tokens.Number(), tokens.Number());
                    AddArc(path, current, to, rx, ry, angle, large, sweep);
                    current = to;
                    break;
                }
                case 'Z':
                    path.CloseFigure();
                    current = start;
                    break;
                default:
                    tokens.Skip();
                    break;
            }
            previous = command;
        }
        return path;
    }

    static PointF Reflect(PointF control, PointF about) => new(2 * about.X - control.X, 2 * about.Y - control.Y);

    static void AddQuadratic(GraphicsPath path, PointF from, PointF q, PointF to) =>
        path.AddBezier(from,
            new PointF(from.X + 2f / 3 * (q.X - from.X), from.Y + 2f / 3 * (q.Y - from.Y)),
            new PointF(to.X + 2f / 3 * (q.X - to.X), to.Y + 2f / 3 * (q.Y - to.Y)), to);

    /// <summary>SVG's endpoint arc, converted to its centre form and drawn as cubic Béziers.</summary>
    static void AddArc(GraphicsPath path, PointF from, PointF to, double rx, double ry, double angleDegrees, bool large, bool sweep)
    {
        if (rx == 0 || ry == 0 || from == to)
        {
            path.AddLine(from, to);
            return;
        }
        rx = Math.Abs(rx); ry = Math.Abs(ry);
        double phi = angleDegrees * Math.PI / 180, cos = Math.Cos(phi), sin = Math.Sin(phi);
        double dx = (from.X - to.X) / 2.0, dy = (from.Y - to.Y) / 2.0;
        double x1 = cos * dx + sin * dy, y1 = -sin * dx + cos * dy;
        double lambda = x1 * x1 / (rx * rx) + y1 * y1 / (ry * ry);
        if (lambda > 1) { rx *= Math.Sqrt(lambda); ry *= Math.Sqrt(lambda); }
        double numerator = rx * rx * ry * ry - rx * rx * y1 * y1 - ry * ry * x1 * x1;
        double factor = Math.Sqrt(Math.Max(0, numerator / (rx * rx * y1 * y1 + ry * ry * x1 * x1))) * (large == sweep ? -1 : 1);
        double cx1 = factor * rx * y1 / ry, cy1 = -factor * ry * x1 / rx;
        double cx = cos * cx1 - sin * cy1 + (from.X + to.X) / 2.0, cy = sin * cx1 + cos * cy1 + (from.Y + to.Y) / 2.0;
        double theta = Angle(1, 0, (x1 - cx1) / rx, (y1 - cy1) / ry);
        double delta = Angle((x1 - cx1) / rx, (y1 - cy1) / ry, (-x1 - cx1) / rx, (-y1 - cy1) / ry);
        if (!sweep && delta > 0) delta -= 2 * Math.PI;
        else if (sweep && delta < 0) delta += 2 * Math.PI;

        int segments = (int)Math.Ceiling(Math.Abs(delta) / (Math.PI / 2));
        double step = delta / segments, k = 4.0 / 3 * Math.Tan(step / 4);
        PointF Point(double t) => new((float)(cx + rx * Math.Cos(t) * cos - ry * Math.Sin(t) * sin), (float)(cy + rx * Math.Cos(t) * sin + ry * Math.Sin(t) * cos));
        PointF Derivative(double t) => new((float)(-rx * Math.Sin(t) * cos - ry * Math.Cos(t) * sin), (float)(-rx * Math.Sin(t) * sin + ry * Math.Cos(t) * cos));
        for (int i = 0; i < segments; i++)
        {
            double a = theta + i * step, b = a + step;
            PointF p0 = Point(a), p3 = Point(b), d0 = Derivative(a), d3 = Derivative(b);
            path.AddBezier(p0, new PointF(p0.X + (float)k * d0.X, p0.Y + (float)k * d0.Y), new PointF(p3.X - (float)k * d3.X, p3.Y - (float)k * d3.Y), p3);
        }
    }

    static double Angle(double ux, double uy, double vx, double vy) =>
        Math.Atan2(ux * vy - uy * vx, ux * vx + uy * vy);

    sealed class Tokens(string text)
    {
        int _position;

        public bool More
        {
            get
            {
                SkipSeparators();
                return _position < text.Length;
            }
        }

        public char? PeekCommand()
        {
            SkipSeparators();
            return _position < text.Length && char.IsLetter(text[_position]) ? text[_position] : null;
        }

        public void Skip() => _position++;

        public float Number()
        {
            SkipSeparators();
            int start = _position;
            if (_position < text.Length && (text[_position] == '-' || text[_position] == '+')) _position++;
            bool dot = false;
            while (_position < text.Length)
            {
                char c = text[_position];
                if (char.IsDigit(c)) { _position++; continue; }
                if (c == '.' && !dot) { dot = true; _position++; continue; }
                if ((c == 'e' || c == 'E') && _position + 1 < text.Length) { _position += 2; continue; }
                break;
            }
            return start == _position ? 0 : float.Parse(text.AsSpan(start, _position - start), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        void SkipSeparators()
        {
            while (_position < text.Length && (char.IsWhiteSpace(text[_position]) || text[_position] == ',')) _position++;
        }
    }
}
