using System.Drawing;
using System.Text.Json.Serialization;

namespace Sheaf.Core.Editing;

public enum MarkKind
{
    Text, Rectangle, Ellipse, Line, Arrow, Ink, Image,
    Highlight, Underline, Strikeout, Note,
    Redaction,
    // Added later, so saved documents keep their numbers.
    SemiCircle, Pie, Curve, Polygon,
    Heart, Star, Parallelogram,
    /// <summary>A curve through any number of points (a <see cref="Curve"/> is the older kind with one bend handle).</summary>
    Spline,
    /// <summary>
    /// Points joined by straight or curved pieces, open or closed, as a vector app's pen draws them: each point has
    /// a handle on either side (<see cref="PageObject.Strokes"/> holds the points, then the handles in, then out).
    /// </summary>
    Path,
}

/// <summary>What an open line or curve ends in, at either end.</summary>
public enum LineEnd { None, Arrow, Triangle, Dot, Bar }

/// <summary>How a pen stroke is laid down.</summary>
public enum PenKind { Pen, Pencil, Marker, Brush, Highlighter, Crayon, PaintBrush, Watercolor }

/// <summary>The shape of a redaction mark; a Text mark covers words and takes only their letters out when applied.</summary>
public enum RedactShape { Rectangle, Ellipse, Freeform, Text }

public enum RedactCover { Black, White, Color, Pixelate, Blur }

public enum TextAlign { Left, Center, Right, Justify }

public enum TextAnchor { Top, Middle, Bottom }

/// <summary>
/// Something added to a page: text, a shape, ink, an image, markup, a note or a redaction mark.
/// Coordinates are points in the page's own view (top-left origin, after the page's /Rotate in the source file,
/// before any turn added in Sheaf), so turning a page later carries its objects with it.
/// </summary>
public sealed class PageObject
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public MarkKind Kind { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Width { get; set; }
    public float Height { get; set; }

    /// <summary>
    /// Line and arrow end points, a curve's start, bend and end, ink strokes, polygon corners and freeform redaction
    /// outlines. Absolute page points.
    /// </summary>
    public List<List<PointF>> Strokes { get; set; } = [];

    /// <summary>Pen pressure (0 to 1) for each point of each ink stroke, from a stylus; empty for a mouse.</summary>
    public List<List<float>> Pressures { get; set; } = [];

    public PenKind Pen { get; set; }

    /// <summary>The ends of lines and curves. An <see cref="MarkKind.Arrow"/> without one set ends in an arrow.</summary>
    public LineEnd StartEnd { get; set; }
    public LineEnd EndEnd { get; set; }

    /// <summary>How much of the circle a pie keeps, in degrees.</summary>
    public float Sweep { get; set; } = 270;

    /// <summary>Whether a <see cref="MarkKind.Path"/> runs on from its last point back to its first.</summary>
    public bool Closed { get; set; }

    /// <summary>
    /// How far the object is turned clockwise about its middle, in degrees, and whether it is flipped across or
    /// down first. Shapes, text and images turn this way; lines and drawings turn by moving their points.
    /// </summary>
    public float Rotation { get; set; }
    public bool FlipX { get; set; }
    public bool FlipY { get; set; }

    /// <summary>
    /// A colour every visible pixel of a picture takes, its transparency kept (ARGB; 0 for the picture's own colours):
    /// a white icon on a transparent PNG becomes a blue one.
    /// </summary>
    public int Tint { get; set; }

    public string Text { get; set; } = "";
    public string FontFamily { get; set; } = "Geist";
    public float FontSize { get; set; } = 14;
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    public TextAlign Align { get; set; }

    /// <summary>Where the text sits in a box taller than it: top, middle or bottom.</summary>
    public TextAnchor Anchor { get; set; }

    /// <summary>ARGB.</summary>
    public int StrokeColor { get; set; } = unchecked((int)0xFF18181B);
    /// <summary>ARGB; alpha 0 means no fill.</summary>
    public int FillColor { get; set; }
    public float StrokeWidth { get; set; } = 2;

    /// <summary>Corner radius of rectangles and text box backgrounds, in points.</summary>
    public float CornerRadius { get; set; }
    public float Opacity { get; set; } = 1;
    public byte[]? ImageData { get; set; }
    public string Comment { get; set; } = "";
    public bool Hidden { get; set; }

    /// <summary>A locked object can't be chosen on the page, moved or erased; the layer list unlocks it.</summary>
    public bool Locked { get; set; }

    /// <summary>
    /// How much of the object is trimmed off each side, as fractions of its box in its own frame (0 keeps it all):
    /// cropping a shape, a drawing or a line hides what lies outside the part kept.
    /// </summary>
    public float CropLeft { get; set; }
    public float CropTop { get; set; }
    public float CropRight { get; set; }
    public float CropBottom { get; set; }

    /// <summary>
    /// Styles for stretches of a text box's text (a bold word, a red phrase), covering the text end to end in order;
    /// empty when the whole box has one style.
    /// </summary>
    public List<TextSpan> Spans { get; set; } = [];

    /// <summary>
    /// Where the eraser went over the object, in its own frame: each a path rubbed out at the width in
    /// <see cref="ErasedWidths"/>; a path with a width below zero is an outline whose inside was taken whole, as lifting
    /// part of the page takes it. What lies under them doesn't show. (The eraser takes points out of drawings.)
    /// </summary>
    public List<List<PointF>> Erased { get; set; } = [];
    public List<float> ErasedWidths { get; set; } = [];

    /// <summary>Objects that share a group move and select together; null when the object is on its own.</summary>
    public Guid? Group { get; set; }

    public RedactShape RedactShape { get; set; }
    public RedactCover Cover { get; set; }

    [JsonIgnore]
    public RectangleF Bounds
    {
        get => new(X, Y, Width, Height);
        set { X = value.X; Y = value.Y; Width = value.Width; Height = value.Height; }
    }

    [JsonIgnore]
    public bool IsCropped => CropLeft > 0 || CropTop > 0 || CropRight > 0 || CropBottom > 0;

    /// <summary>The part of the box a crop keeps, in the object's own frame.</summary>
    [JsonIgnore]
    public RectangleF CropBounds => new(X + CropLeft * Width, Y + CropTop * Height,
        Math.Max(0, (1 - CropLeft - CropRight) * Width), Math.Max(0, (1 - CropTop - CropBottom) * Height));

    [JsonIgnore]
    public bool IsMarkup => Kind is MarkKind.Highlight or MarkKind.Underline or MarkKind.Strikeout or MarkKind.Note;

    public PageObject Clone()
    {
        var copy = (PageObject)MemberwiseClone();
        copy.Strokes = [.. Strokes.Select(stroke => stroke.ToList())];
        copy.Pressures = [.. Pressures.Select(stroke => stroke.ToList())];
        copy.Spans = [.. Spans.Select(span => span with { })];
        copy.Erased = [.. Erased.Select(path => path.ToList())];
        copy.ErasedWidths = [.. ErasedWidths];
        return copy;
    }

    /// <summary>Moves the object and its strokes by the given offset.</summary>
    public void Offset(float dx, float dy)
    {
        X += dx;
        Y += dy;
        foreach (List<PointF> stroke in Strokes.Concat(Erased))
            for (int i = 0; i < stroke.Count; i++) stroke[i] = new PointF(stroke[i].X + dx, stroke[i].Y + dy);
    }

    /// <summary>Scales the object into new bounds, strokes included.</summary>
    public void Resize(RectangleF target)
    {
        float sx = Width == 0 ? 1 : target.Width / Width, sy = Height == 0 ? 1 : target.Height / Height;
        foreach (List<PointF> stroke in Strokes.Concat(Erased))
            for (int i = 0; i < stroke.Count; i++)
                stroke[i] = new PointF(target.X + (stroke[i].X - X) * sx, target.Y + (stroke[i].Y - Y) * sy);
        // Rubbed-out paths keep their width in proportion.
        float scale = MathF.Sqrt(Math.Abs(sx * sy));
        for (int i = 0; i < ErasedWidths.Count; i++) ErasedWidths[i] *= scale;
        Bounds = target;
    }

    /// <summary>Recomputes the bounds from the strokes (lines, arrows, ink, freeform marks).</summary>
    public void FitToStrokes(float padding = 0)
    {
        var points = Strokes.SelectMany(stroke => stroke).ToList();
        if (points.Count == 0) return;
        float left = points.Min(p => p.X), top = points.Min(p => p.Y), right = points.Max(p => p.X), bottom = points.Max(p => p.Y);
        Bounds = RectangleF.FromLTRB(left - padding, top - padding, Math.Max(right + padding, left - padding + 1), Math.Max(bottom + padding, top - padding + 1));
    }
}

/// <summary>The style of a stretch of a text box's text: a word made bold, a phrase in another colour.</summary>
public sealed record TextSpan
{
    public int Length { get; set; }
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    public bool Underline { get; set; }

    /// <summary>ARGB.</summary>
    public int Color { get; set; }
}

/// <summary>A page of the working document: a page of one of the source files, or a blank page.</summary>
public sealed class PageEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Index into <see cref="EditDocument.Sources"/>; ignored for blank pages.</summary>
    public int Source { get; set; }

    /// <summary>Zero-based page in the source, or -1 for a blank page.</summary>
    public int SourcePage { get; set; }

    /// <summary>The paper colour of a blank page (ARGB); white unless chosen.</summary>
    public int Background { get; set; } = unchecked((int)0xFFFFFFFF);

    /// <summary>Size of the page as shown in its source (or of the blank page), in points.</summary>
    public float Width { get; set; }
    public float Height { get; set; }

    /// <summary>Turn added in Sheaf: 0, 90, 180 or 270 degrees clockwise.</summary>
    public int Rotation { get; set; }

    /// <summary>
    /// The page's own content is mirrored left to right (<see cref="MirrorX"/>) or upside down (<see cref="MirrorY"/>),
    /// as it shows before Sheaf's turn. Objects added in Sheaf aren't mirrored, so their text stays readable.
    /// </summary>
    public bool MirrorX { get; set; }
    public bool MirrorY { get; set; }

    public bool IsMirrored => MirrorX || MirrorY;

    public List<PageObject> Objects { get; set; } = [];

    public bool IsBlank => SourcePage < 0;

    public PageEntry Clone()
    {
        var copy = (PageEntry)MemberwiseClone();
        copy.Objects = [.. Objects.Select(o => o.Clone())];
        return copy;
    }
}

/// <summary>
/// The document being edited: the files its pages come from, and the pages in order with what was added to them.
/// Source 0 is a private working copy of the opened file, so saving over the original never affects the edit.
/// </summary>
public sealed class EditDocument
{
    public List<string> Sources { get; set; } = [];
    public List<PageEntry> Pages { get; set; } = [];

    /// <summary>
    /// The title stored in the PDF, which browsers and readers show in their tab or title bar instead of the file
    /// name; null keeps the file's own.
    /// </summary>
    public string? Title { get; set; }

    /// <summary>The password the original file was opened with, if it had one.</summary>
    public string? Password { get; set; }

    /// <summary>The form fields of the opened file, as it was opened. Never changes.</summary>
    public IReadOnlyList<FormField> Fields { get; set; } = [];

    /// <summary>Values typed into form fields, by field name.</summary>
    public Dictionary<string, string> FieldValues { get; set; } = [];

    public EditDocument Clone() => new()
    {
        Sources = [.. Sources],
        Pages = [.. Pages.Select(page => page.Clone())],
        Password = Password,
        Title = Title,
        Fields = Fields,
        FieldValues = new Dictionary<string, string>(FieldValues),
    };

    /// <summary>Field values that differ from what the file holds.</summary>
    public Dictionary<string, string> ChangedFieldValues() =>
        Fields.Where(field => FieldValues.TryGetValue(field.Name, out string? value) && value != field.Value)
            .ToDictionary(field => field.Name, field => FieldValues[field.Name]);

    public int ObjectCount => Pages.Sum(page => page.Objects.Count);
}
