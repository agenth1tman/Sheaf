using System.Globalization;
using Sheaf.Core.Editing;
using Sheaf.Ui;

namespace Sheaf.Pages.Document;

internal enum DocumentMode { Edit, Comment, Sign, Redact, Pages }

/// <summary>A tool on the rail: what it does, its icon, its name and the key that picks it.</summary>
internal sealed record RailTool(EditTool Tool, string Icon, string Name, Keys Key);

internal static class EditorModes
{
    public static readonly (string Label, DocumentMode Mode)[] Choices =
    [
        ("Edit", DocumentMode.Edit), ("Comment", DocumentMode.Comment), ("Fill & Sign", DocumentMode.Sign),
        ("Redact", DocumentMode.Redact), ("Pages", DocumentMode.Pages),
    ];

    public static DocumentMode Parse(string? id) => id switch
    {
        "comment" => DocumentMode.Comment,
        "sign" => DocumentMode.Sign,
        "redact" => DocumentMode.Redact,
        "pages" => DocumentMode.Pages,
        _ => DocumentMode.Edit,
    };

    static readonly RailTool Select = new(EditTool.Select, "select", "Select", Keys.V);
    static readonly RailTool Hand = new(EditTool.Hand, "hand", "Hand", Keys.H);

    // Select and Hand pick text too when dragged across it; the tool that only picks text shares Select's button,
    // as does the one that moves the points of a shape or line.
    static readonly RailTool SelectText = new(EditTool.SelectText, "select-text", "Select Text", Keys.None);
    static readonly RailTool VertexSelect = new(EditTool.VertexSelect, "vertex-select", "Vertex Select", Keys.D);
    static readonly RailTool Pen = new(EditTool.Draw, "draw", "Pen", Keys.P);
    static readonly RailTool PathPen = new(EditTool.PenPath, "pen-path", "Path Pen", Keys.B);
    static readonly RailTool[] Erasers =
    [
        new(EditTool.EraseArea, "eraser", "Eraser", Keys.E),
        new(EditTool.EraseStroke, "eraser-stroke", "Stroke Eraser", Keys.None),
    ];

    /// <summary>
    /// The rail's buttons, top to bottom. A button with more than one tool shows the last one used; clicking it
    /// again (or right-clicking) offers the others.
    /// </summary>
    public static IReadOnlyList<RailTool[]> ToolsOf(DocumentMode mode) => mode switch
    {
        DocumentMode.Edit =>
        [
            [Select, VertexSelect, SelectText], [Hand],
            [new(EditTool.Text, "text", "Text", Keys.T)],
            [
                new(EditTool.Rectangle, "rect", "Rectangle", Keys.R),
                new(EditTool.Ellipse, "ellipse", "Ellipse", Keys.O),
                new(EditTool.Triangle, "triangle", "Triangle", Keys.Y),
                new(EditTool.SemiCircle, "semicircle", "Semicircle", Keys.None),
                new(EditTool.Pie, "pie", "Pie", Keys.None),
                new(EditTool.Star, "star", "Star", Keys.None),
                new(EditTool.Heart, "heart", "Heart", Keys.None),
                new(EditTool.Parallelogram, "parallelogram", "Parallelogram", Keys.None),
            ],
            [
                new(EditTool.Line, "line", "Line", Keys.L),
                new(EditTool.Arrow, "arrow", "Arrow", Keys.A),
                new(EditTool.Curve, "curve", "Curve", Keys.C),
            ],
            [Pen, PathPen],
            Erasers,
            [new(EditTool.Image, "image", "Image", Keys.I)],
            [
                new(EditTool.LiftRectangle, "lift", "Lift an Area", Keys.X),
                new(EditTool.LiftFreeform, "lasso", "Lift a Freeform Area", Keys.None),
                new(EditTool.LiftPen, "lift-pen", "Lift with the Pen", Keys.None),
            ],
        ],
        DocumentMode.Comment =>
        [
            [Select, SelectText], [Hand],
            [new(EditTool.Highlight, "highlight", "Highlight", Keys.M)],
            [new(EditTool.Underline, "underline", "Underline", Keys.U)],
            [new(EditTool.Strikeout, "strike", "Strike Through", Keys.K)],
            [new(EditTool.Note, "note", "Note", Keys.N)],
            [Pen],
            Erasers,
        ],
        DocumentMode.Sign =>
        [
            [Select], [Hand],
            [new(EditTool.Signature, "signature", "Signature", Keys.S)],
            [new(EditTool.Text, "text", "Text", Keys.T)],
            [new(EditTool.Date, "calendar", "Date", Keys.D)],
            [new(EditTool.Check, "check", "Tick", Keys.X)],
            [new(EditTool.Cross, "cross", "Cross", Keys.C)],
        ],
        DocumentMode.Redact =>
        [
            [Select], [Hand],
            [new(EditTool.RedactRectangle, "rect", "Mark a Rectangle", Keys.R)],
            [new(EditTool.RedactEllipse, "ellipse", "Mark an Ellipse", Keys.O)],
            [new(EditTool.RedactFreeform, "freeform", "Mark a Freeform Area", Keys.F)],
            [new(EditTool.RedactText, "mark-text", "Mark Text", Keys.T)],
        ],
        _ => [],
    };
}

/// <summary>Which group of style settings applies to a tool or an object.</summary>
internal enum StyleFamily { None, Text, Shape, Line, Ink, Highlight, Markup, Note, Image, Redaction }

/// <summary>
/// The style each tool draws with. Changing a style in the bar with nothing selected changes these; with a
/// selection it changes the objects and these too, so the next object matches the last one.
/// </summary>
internal sealed class ToolStyles
{
    const int Black = unchecked((int)0xFF18181B), Red = unchecked((int)0xFFE5484D), Blue = unchecked((int)0xFF2563EB);

    public PageObject Text { get; } = new() { Kind = MarkKind.Text, StrokeColor = Black };
    public PageObject Shape { get; } = new() { Kind = MarkKind.Rectangle, StrokeColor = Red, StrokeWidth = 2 };
    public PageObject Line { get; } = new() { Kind = MarkKind.Line, StrokeColor = Red, StrokeWidth = 2 };
    public PageObject Ink { get; } = new() { Kind = MarkKind.Ink, StrokeColor = Blue, StrokeWidth = 2 };
    public PageObject Highlight { get; } = new() { Kind = MarkKind.Highlight, StrokeColor = unchecked((int)0xFFFACC15) };
    public PageObject Markup { get; } = new() { Kind = MarkKind.Underline, StrokeColor = Red, StrokeWidth = 1.5f };
    public PageObject Note { get; } = new() { Kind = MarkKind.Note, StrokeColor = unchecked((int)0xFFFDE047) };
    public PageObject Image { get; } = new() { Kind = MarkKind.Image };

    public RedactCover Cover { get; set; } = RedactCover.Black;
    public int CoverColor { get; set; } = Red;

    public static StyleFamily FamilyOf(EditTool tool) => tool switch
    {
        EditTool.Text or EditTool.Date => StyleFamily.Text,
        EditTool.Rectangle or EditTool.Ellipse or EditTool.Triangle or EditTool.SemiCircle or EditTool.Pie
            or EditTool.Heart or EditTool.Star or EditTool.Parallelogram or EditTool.PenPath => StyleFamily.Shape,
        EditTool.Line or EditTool.Arrow or EditTool.Curve => StyleFamily.Line,
        EditTool.Draw or EditTool.Check or EditTool.Cross => StyleFamily.Ink,
        EditTool.Highlight => StyleFamily.Highlight,
        EditTool.Underline or EditTool.Strikeout => StyleFamily.Markup,
        EditTool.Note => StyleFamily.Note,
        EditTool.Image => StyleFamily.Image,
        EditTool.RedactRectangle or EditTool.RedactEllipse or EditTool.RedactFreeform or EditTool.RedactText => StyleFamily.Redaction,
        _ => StyleFamily.None,
    };

    public static StyleFamily FamilyOf(MarkKind kind) => kind switch
    {
        MarkKind.Text => StyleFamily.Text,
        MarkKind.Rectangle or MarkKind.Ellipse or MarkKind.SemiCircle or MarkKind.Pie or MarkKind.Polygon
            or MarkKind.Heart or MarkKind.Star or MarkKind.Parallelogram or MarkKind.Path => StyleFamily.Shape,
        MarkKind.Line or MarkKind.Arrow or MarkKind.Curve or MarkKind.Spline => StyleFamily.Line,
        MarkKind.Ink => StyleFamily.Ink,
        MarkKind.Highlight => StyleFamily.Highlight,
        MarkKind.Underline or MarkKind.Strikeout => StyleFamily.Markup,
        MarkKind.Note => StyleFamily.Note,
        MarkKind.Image => StyleFamily.Image,
        MarkKind.Redaction => StyleFamily.Redaction,
        _ => StyleFamily.None,
    };

    public PageObject? TemplateOf(StyleFamily family) => family switch
    {
        StyleFamily.Text => Text,
        StyleFamily.Shape => Shape,
        StyleFamily.Line => Line,
        StyleFamily.Ink => Ink,
        StyleFamily.Highlight => Highlight,
        StyleFamily.Markup => Markup,
        StyleFamily.Note => Note,
        StyleFamily.Image => Image,
        _ => null,
    };

    /// <summary>A new object for a tool, in the tool's style, at the page origin (the canvas moves it into place).</summary>
    public PageObject Create(EditTool tool)
    {
        PageObject item = (TemplateOf(FamilyOf(tool)) ?? Text).Clone();
        item.Id = Guid.NewGuid();
        item.Text = "";
        item.Comment = "";
        item.Strokes = [];
        item.Bounds = RectangleF.Empty;
        switch (tool)
        {
            case EditTool.Text:
                item.Width = 220;
                item.Height = item.FontSize * 1.6f;
                break;
            case EditTool.Date:
                item.Text = DateTime.Today.ToString("d", CultureInfo.CurrentCulture);
                item.Width = PagePainter.TextWidth(item) + 8;
                item.Height = item.FontSize * 1.6f;
                break;
            case EditTool.Rectangle: item.Kind = MarkKind.Rectangle; break;
            case EditTool.Ellipse: item.Kind = MarkKind.Ellipse; break;
            case EditTool.Triangle: item.Kind = MarkKind.Polygon; break;
            case EditTool.SemiCircle: item.Kind = MarkKind.SemiCircle; break;
            case EditTool.Pie: item.Kind = MarkKind.Pie; break;
            case EditTool.Heart: item.Kind = MarkKind.Heart; break;
            case EditTool.Star: item.Kind = MarkKind.Star; break;
            case EditTool.Parallelogram: item.Kind = MarkKind.Parallelogram; break;
            case EditTool.PenPath: item.Kind = MarkKind.Path; break;
            // Ends are set on a line once drawn; each line tool starts from its own.
            case EditTool.Line or EditTool.Arrow or EditTool.Curve:
                item.Kind = tool switch { EditTool.Arrow => MarkKind.Arrow, EditTool.Curve => MarkKind.Spline, _ => MarkKind.Line };
                item.StartEnd = item.EndEnd = LineEnd.None;
                break;
            case EditTool.LiftRectangle or EditTool.LiftFreeform:
                // Only the outline of the area being lifted; it never joins the page.
                item = new PageObject
                {
                    Kind = tool == EditTool.LiftFreeform ? MarkKind.Polygon : MarkKind.Rectangle,
                    StrokeColor = Theme.Current.Accent.ToArgb(),
                    FillColor = Color.FromArgb(30, Theme.Current.Accent).ToArgb(),
                    StrokeWidth = 1,
                };
                break;
            case EditTool.Underline: item.Kind = MarkKind.Underline; break;
            case EditTool.Strikeout: item.Kind = MarkKind.Strikeout; break;
            case EditTool.Note:
                item.Width = item.Height = 22;
                break;
            case EditTool.Check:
                item.StrokeColor = Text.StrokeColor;
                item.Strokes = [[new(1, 8), new(6, 13), new(15, 2)]];
                item.FitToStrokes(1);
                break;
            case EditTool.Cross:
                item.StrokeColor = Text.StrokeColor;
                item.Strokes = [[new(2, 2), new(14, 14)], [new(14, 2), new(2, 14)]];
                item.FitToStrokes(1);
                break;
            case EditTool.RedactRectangle or EditTool.RedactEllipse or EditTool.RedactFreeform or EditTool.RedactText:
                item = new PageObject
                {
                    Kind = MarkKind.Redaction,
                    RedactShape = tool switch
                    {
                        EditTool.RedactEllipse => RedactShape.Ellipse,
                        EditTool.RedactFreeform => RedactShape.Freeform,
                        EditTool.RedactText => RedactShape.Text,
                        _ => RedactShape.Rectangle,
                    },
                    Cover = Cover,
                    FillColor = CoverColor,
                    StrokeWidth = 0,
                };
                break;
        }
        return item;
    }

    /// <summary>A short name for an object, for layer and comment lists.</summary>
    public static string Describe(PageObject item) => item.Kind switch
    {
        MarkKind.Text => FirstLine(item.Text, "Text"),
        MarkKind.Rectangle or MarkKind.Polygon when item.Comment.Length > 0 => item.Comment,
        MarkKind.Rectangle => "Rectangle",
        MarkKind.Ellipse => "Ellipse",
        MarkKind.SemiCircle => "Semicircle",
        MarkKind.Pie => "Pie",
        MarkKind.Polygon => ShapeGeometry.PolygonName(item),
        MarkKind.Curve or MarkKind.Spline => "Curve",
        MarkKind.Heart => "Heart",
        MarkKind.Star => "Star",
        MarkKind.Parallelogram => "Parallelogram",
        MarkKind.Path => item.Closed ? "Closed path" : "Path",
        MarkKind.Line or MarkKind.Arrow => ShapeGeometry.Ends(item) == (LineEnd.None, LineEnd.None) ? "Line" : "Arrow",
        MarkKind.Ink => item.Pen switch
        {
            PenKind.Pencil => "Pencil drawing",
            PenKind.Marker => "Marker drawing",
            PenKind.Brush => "Brush drawing",
            PenKind.Highlighter => "Highlighter",
            PenKind.Crayon => "Crayon drawing",
            PenKind.PaintBrush => "Painting",
            PenKind.Watercolor => "Watercolour",
            _ => "Drawing",
        },
        MarkKind.Image => "Image",
        MarkKind.Highlight => "Highlight",
        MarkKind.Underline => "Underline",
        MarkKind.Strikeout => "Strike-through",
        MarkKind.Note => "Note",
        MarkKind.Redaction => item.Comment.Length > 0 ? item.Comment : item.RedactShape switch
        {
            RedactShape.Ellipse => "Ellipse",
            RedactShape.Freeform => "Freeform area",
            RedactShape.Text => "Text",
            _ => "Rectangle",
        },
        _ => item.Kind.ToString(),
    };

    static string FirstLine(string text, string fallback)
    {
        string line = text.Split('\n').Select(part => part.Trim()).FirstOrDefault(part => part.Length > 0) ?? "";
        return line.Length == 0 ? fallback : line.Length > 40 ? line[..40] + "…" : line;
    }

    public static string IconOf(PageObject item) => item.Kind switch
    {
        MarkKind.Text => "text",
        MarkKind.Rectangle => "rect",
        MarkKind.Ellipse => "ellipse",
        MarkKind.SemiCircle => "semicircle",
        MarkKind.Pie => "pie",
        MarkKind.Polygon => ShapeGeometry.PolygonName(item) switch { "Triangle" => "triangle", "Pie" => "pie", "Semicircle" => "semicircle", _ => "freeform" },
        MarkKind.Curve or MarkKind.Spline => "curve",
        MarkKind.Heart => "heart",
        MarkKind.Star => "star",
        MarkKind.Parallelogram => "parallelogram",
        MarkKind.Path => "pen-path",
        MarkKind.Line or MarkKind.Arrow => ShapeGeometry.Ends(item) == (LineEnd.None, LineEnd.None) ? "line" : "arrow",
        MarkKind.Ink => "draw",
        MarkKind.Image => "image",
        MarkKind.Highlight => "highlight",
        MarkKind.Underline => "underline",
        MarkKind.Strikeout => "strike",
        MarkKind.Note => "note",
        MarkKind.Redaction => "redact",
        _ => "file",
    };

    /// <summary>The colour that stands for an object in lists.</summary>
    public static Color SwatchOf(PageObject item) => item.Kind switch
    {
        MarkKind.Image => Theme.Current.Text3,
        MarkKind.Redaction => Theme.Current.Danger,
        MarkKind.Text when item.FillColor >> 24 != 0 => Color.FromArgb(item.FillColor),
        _ => Color.FromArgb(255, Color.FromArgb(item.StrokeColor)),
    };
}
