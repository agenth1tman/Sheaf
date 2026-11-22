using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Sheaf.Core;
using Sheaf.Core.Editing;
using Sheaf.Ui;
using Sheaf.Ui.Controls;
using FormField = Sheaf.Core.Editing.FormField;

namespace Sheaf.Pages.Document;

internal enum AlignEdge { Left, Centre, Right, Top, Middle, Bottom }

internal enum EditTool
{
    Select, Hand,
    Text, Rectangle, Ellipse, Line, Arrow, Draw, Image,
    Highlight, Underline, Strikeout, Note,
    Signature, Date, Check, Cross,
    RedactRectangle, RedactEllipse, RedactFreeform, RedactText,
    Triangle, SemiCircle, Pie, Curve, SelectText, LiftRectangle, LiftFreeform,
    Heart, Star, Parallelogram,
}

/// <summary>
/// The document as one column of pages: renders them at the current zoom off the UI thread, draws the objects
/// on top, and lets people select, move, resize and create objects with the current tool. Every change goes
/// through <see cref="BeginChange"/> so the owner can keep an undo step.
/// </summary>
internal sealed class DocumentCanvas : Panel
{
    const int PageGap = 20, PageMargin = 28, HandleSize = 8, HitTolerance = 5, WheelStep = 72, LineStep = 48;
    static readonly float[] ZoomSteps = [0.25f, 0.33f, 0.5f, 0.67f, 0.75f, 0.9f, 1f, 1.1f, 1.25f, 1.5f, 1.75f, 2f, 2.5f, 3f, 4f];
    static List<PageObject> _clipboard = [];

    readonly List<RectangleF> _pages = [];
    readonly List<PageObject> _selection = [];
    /// <summary>Renders that failed, so the loop doesn't ask for them again and again.</summary>
    readonly HashSet<(int Source, int Page, int Width)> _failedRenders = [];
    CancellationTokenSource _renderCancel = new();
    bool _rendering, _renderAgain, _fitWidth = true, _spaceDown, _changeStarted, _relayoutQueued;
    float _zoom = 1f;
    int _currentPage, _selectionPage = -1;

    // Interaction state
    enum Drag { None, Pan, Move, Resize, Create, Marquee, Text, Rotate }
    Drag _drag;

    // Turning with the round handle: about the object's middle, from the angle the drag started at.
    PointF _turnCentre;
    double _turnFrom;
    float _turnStart;
    // The object as it was when a resize began, for resizing it in its own (turned) frame.
    PageObject? _resizeOriginal;
    // A curve's point picked by clicking its handle: Delete takes it out. A click on a curve already chosen adds one.
    int _activePoint = -1;
    bool _pressOnChosen;

    /// <summary>A stretch of a highlighter stroke: across text (from one point to another) or free (a path).</summary>
    sealed class HighlightRun(bool text, PointF from)
    {
        public bool Text { get; } = text;
        public PointF From { get; } = from;
        public PointF To { get; set; } = from;
        public List<PointF> Path { get; } = [from];
    }

    List<HighlightRun>? _marking;

    // Text picked with the Select Text tool: its page, the ends of the drag, and a box per line.
    (int Page, PointF From, PointF To, IReadOnlyList<RectangleF> Lines)? _textPick;

    // A pen stroke held still turns into the shape it looks like.
    const int HoldToSnapMs = 500;
    readonly System.Windows.Forms.Timer _holdTimer = new() { Interval = HoldToSnapMs };
    Point _holdAnchor;
    bool _snapped;

    // The pressure of a stylus, read from the pointer messages that come before the mouse ones.
    float _penPressure = 1;
    long _penAt = long.MinValue / 2;
    Rectangle? _marquee;
    bool _marqueeAdds;

    // Picking a colour from the page: what gets it, a picture of the view to read colours from, and where the pointer is.
    Action<Color>? _picking;
    Bitmap? _pickView;
    (Point Scroll, float Zoom, Size Size) _pickViewOf;
    Point? _pickAt;
    bool _hideSelection;

    // Style changes made while one slider is open share an undo step.
    object? _session;
    Point _pressScreen, _panOrigin;
    PointF _pressPage, _lastPage;
    int _dragPage = -1, _handle = -1;
    RectangleF _resizeStart;
    PageObject? _creating;
    TextBox? _editor;
    PageObject? _editing;
    bool _editingIsNew;
    Font? _editorFont;
    EditTool _tool;

    public DocumentCanvas()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        AutoScroll = true;
        TabStop = true;
        AccessibleRole = AccessibleRole.Document;
        AccessibleName = "Page area";
        Theme.Changed += (_, _) => { NativeTheme(); Invalidate(); };
        _holdTimer.Tick += (_, _) => SnapHeldStroke();
    }

    public EditDocument? Document { get; private set; }
    public PageRenderer? Renderer { get; private set; }

    /// <summary>Creates the object a tool draws, in the current style.</summary>
    public Func<EditTool, PageObject>? NewObject { get; set; }

    /// <summary>Asks for the bytes of an image to place, or null when cancelled.</summary>
    public Func<byte[]?>? PickImage { get; set; }

    /// <summary>Supplies the signature to place, or null when none is set up yet.</summary>
    public Func<PageObject?>? CurrentSignature { get; set; }

    public event EventHandler? BeforeChange;
    public event EventHandler? Changed;
    public event EventHandler? SelectionChanged;
    public event EventHandler? ViewChanged;
    public event EventHandler? ToolFinished;

    /// <summary>Something was placed with a one-click tool (a note, a signature, a tick).</summary>
    public event EventHandler<PageObject>? Placed;

    /// <summary>A form field was clicked while fields are shown.</summary>
    public event EventHandler<FormField>? FieldClicked;

    /// <summary>
    /// The boxes of the text between two points of a page, line by line, for marking text; null when the page
    /// has no text there (or its text isn't read yet).
    /// </summary>
    public Func<int, PointF, PointF, IReadOnlyList<RectangleF>?>? SelectText { get; set; }

    /// <summary>The text between two points of a page, line by line, for copying; null when there is none.</summary>
    public Func<int, PointF, PointF, string?>? TextBetween { get; set; }

    /// <summary>Whether a point of a page is on its text, for the highlighter to follow the lines.</summary>
    public Func<int, PointF, bool>? IsOverText { get; set; }

    /// <summary>A sharp render of a page for lifting part of it, or null for a blank page.</summary>
    public Func<PageEntry, int, Task<Bitmap?>>? RenderForLift { get; set; }

    /// <summary>Draws redaction marks as they will look once applied, instead of hatched.</summary>
    public bool PreviewRedactions
    {
        get => field;
        set { field = value; Invalidate(); }
    }

    /// <summary>Tints the PDF's form fields and lets clicks reach them (Fill &amp; Sign).</summary>
    public bool ShowFields
    {
        get => field;
        set { field = value; Invalidate(); }
    }

    public EditTool Tool
    {
        get => _tool;
        set
        {
            CommitTextEdit();
            _tool = value;
            if (value is not EditTool.Select) ClearSelection();
            if (_textPick is not null) { _textPick = null; Invalidate(); }
            UpdateCursor(null);
        }
    }

    public IReadOnlyList<PageObject> Selection => _selection;

    public int SelectionPage => _selectionPage;

    public int CurrentPage => _currentPage;

    public float Zoom => _zoom;

    public bool IsEditingText => _editor is not null;

    public void Load(EditDocument document, PageRenderer renderer)
    {
        Document = document;
        Renderer = renderer;
        _selection.Clear();
        Relayout();
        Invalidate();
        RequestRender();
    }

    /// <summary>The model was replaced or reordered (undo, page edits): keep the scroll position, drop the selection.</summary>
    public void Reload(EditDocument document)
    {
        CommitTextEdit();
        Document = document;
        _selection.Clear();
        _selectionPage = -1;
        float anchor = DocumentFraction();
        Relayout();
        AutoScrollPosition = new Point(-AutoScrollPosition.X, (int)(anchor * AutoScrollMinSize.Height));
        AfterScroll();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    // Zoom and navigation -----------------------------------------------------------------------

    public bool CanZoomIn => _zoom < ZoomSteps[^1] - 0.001f;
    public bool CanZoomOut => _zoom > ZoomSteps[0] + 0.001f;

    public void ZoomIn() => SetZoom(ZoomSteps.FirstOrDefault(step => step > _zoom + 0.001f, ZoomSteps[^1]), false);
    public void ZoomOut() => SetZoom(ZoomSteps.LastOrDefault(step => step < _zoom - 0.001f, ZoomSteps[0]), false);
    public void FitWidth() => SetZoom(_zoom, true);

    /// <summary>Zooms to a typed percentage, within what the view can show (25 to 400 %).</summary>
    public void ZoomTo(float percent) => SetZoom(Math.Clamp(percent / 100f, ZoomSteps[0], ZoomSteps[^1]), false);

    public static int MinimumZoom => (int)Math.Round(ZoomSteps[0] * 100);

    public static int MaximumZoom => (int)Math.Round(ZoomSteps[^1] * 100);

    void SetZoom(float zoom, bool fitWidth)
    {
        CommitTextEdit();
        float anchor = DocumentFraction();
        _zoom = zoom;
        _fitWidth = fitWidth;
        Relayout();
        AutoScrollPosition = new Point(Math.Max(0, (AutoScrollMinSize.Width - ClientSize.Width) / 2), Math.Max(0, (int)(anchor * AutoScrollMinSize.Height)));
        AfterScroll();
    }

    float DocumentFraction() => AutoScrollMinSize.Height == 0 ? 0 : (float)-AutoScrollPosition.Y / AutoScrollMinSize.Height;

    public void GoToPage(int index)
    {
        if (index < 0 || index >= _pages.Count) return;
        AutoScrollPosition = new Point(-AutoScrollPosition.X, Math.Max(0, (int)_pages[index].Top - Px(PageGap)));
        AfterScroll();
    }

    void ScrollBy(int dy)
    {
        AutoScrollPosition = new Point(-AutoScrollPosition.X, Math.Max(0, -AutoScrollPosition.Y + dy));
        AfterScroll();
    }

    void AfterScroll()
    {
        PositionEditor();
        Invalidate();
        UpdateCurrentPage();
        RequestRender();
    }

    // Layout ------------------------------------------------------------------------------------

    int Px(int logical) => LogicalToDeviceUnits(logical);

    float PixelsPerPoint => _zoom * DeviceDpi / 72f;

    void Relayout()
    {
        _pages.Clear();
        if (Document is null || Document.Pages.Count == 0)
        {
            AutoScrollMinSize = Size.Empty;
            return;
        }
        var sizes = Document.Pages.Select(PagePainter.DisplaySize).ToList();
        // The width left once the vertical scroll bar shows, which it does whenever the pages are taller than the view.
        int available = ClientSize.Width - (VerticalScroll.Visible ? 0 : SystemInformation.VerticalScrollBarWidth);
        if (_fitWidth)
        {
            float widest = sizes.Max(size => size.Width) * DeviceDpi / 72f;
            _zoom = Math.Clamp((available - 2 * Px(PageMargin)) / widest, ZoomSteps[0], ZoomSteps[^1]);
        }
        float ppp = PixelsPerPoint;
        float totalHeight = sizes.Sum(size => size.Height * ppp) + (sizes.Count - 1) * Px(PageGap) + 2 * Px(PageMargin);
        if (totalHeight <= ClientSize.Height && !VerticalScroll.Visible) available = ClientSize.Width;
        int contentWidth = (int)(sizes.Max(size => size.Width) * ppp) + 2 * Px(PageMargin);
        if (_fitWidth) contentWidth = Math.Min(contentWidth, available);
        int column = Math.Max(contentWidth, available);
        float y = Px(PageMargin);
        foreach (SizeF size in sizes)
        {
            float width = size.Width * ppp, height = size.Height * ppp;
            // Whole pixels, so a page drawn at its rendered size lands on the pixel grid and stays sharp.
            _pages.Add(new RectangleF(MathF.Round((column - width) / 2), MathF.Round(y), width, height));
            y += height + Px(PageGap);
        }
        AutoScrollMinSize = new Size(contentWidth, (int)(y - Px(PageGap) + Px(PageMargin)));
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    RectangleF ScreenRect(int page)
    {
        RectangleF rect = _pages[page];
        rect.Offset(AutoScrollPosition.X, AutoScrollPosition.Y);
        return rect;
    }

    protected override void OnClientSizeChanged(EventArgs e)
    {
        base.OnClientSizeChanged(e);
        // Scroll bars appearing change the client size while the layout sets them; lay out again afterwards.
        if (Document is null || _relayoutQueued || !IsHandleCreated) return;
        _relayoutQueued = true;
        BeginInvoke(RelayoutAfterResize);
    }

    void RelayoutAfterResize()
    {
        _relayoutQueued = false;
        if (Document is null) return;
        float anchor = DocumentFraction();
        Relayout();
        AutoScrollPosition = new Point(-AutoScrollPosition.X, (int)(anchor * AutoScrollMinSize.Height));
        AfterScroll();
    }

    protected override void OnScroll(ScrollEventArgs se)
    {
        base.OnScroll(se);
        AfterScroll();
    }

    (int First, int Last) VisibleRange(int extra = 0)
    {
        float top = -AutoScrollPosition.Y, bottom = top + ClientSize.Height;
        int first = _pages.FindIndex(page => page.Bottom >= top);
        if (first < 0) return (0, -1);
        int last = _pages.FindLastIndex(page => page.Top <= bottom);
        return (Math.Max(0, first - extra), Math.Min(_pages.Count - 1, Math.Max(first, last) + extra));
    }

    void UpdateCurrentPage()
    {
        float probe = -AutoScrollPosition.Y + ClientSize.Height / 3f;
        int page = _pages.FindIndex(rect => rect.Bottom + Px(PageGap) >= probe);
        page = page < 0 ? Math.Max(0, _pages.Count - 1) : page;
        if (page == _currentPage) return;
        _currentPage = page;
        ViewChanged?.Invoke(this, EventArgs.Empty);
        Announce($"Page {page + 1} of {_pages.Count}");
    }

    Matrix PageMatrix(int page) => PagePainter.Transform(Document!.Pages[page], ScreenRect(page));

    /// <summary>The page under a screen point and the point in that page's coordinates.</summary>
    bool ToPage(Point screen, out int page, out PointF point)
    {
        page = -1;
        point = default;
        if (Document is null) return false;
        for (int i = 0; i < _pages.Count; i++)
        {
            if (!ScreenRect(i).Contains(screen)) continue;
            page = i;
            point = ToPagePoint(i, screen);
            return true;
        }
        return false;
    }

    PointF ToPagePoint(int page, Point screen)
    {
        using Matrix matrix = PageMatrix(page);
        matrix.Invert();
        PointF[] points = [screen];
        matrix.TransformPoints(points);
        PageEntry entry = Document!.Pages[page];
        return new PointF(Math.Clamp(points[0].X, 0, entry.Width), Math.Clamp(points[0].Y, 0, entry.Height));
    }

    RectangleF ToScreen(int page, RectangleF rect)
    {
        using Matrix matrix = PageMatrix(page);
        PointF[] corners = [new(rect.Left, rect.Top), new(rect.Right, rect.Bottom)];
        matrix.TransformPoints(corners);
        return RectangleF.FromLTRB(Math.Min(corners[0].X, corners[1].X), Math.Min(corners[0].Y, corners[1].Y),
            Math.Max(corners[0].X, corners[1].X), Math.Max(corners[0].Y, corners[1].Y));
    }

    PointF ToScreen(int page, PointF point)
    {
        using Matrix matrix = PageMatrix(page);
        PointF[] points = [point];
        matrix.TransformPoints(points);
        return points[0];
    }

    // Rendering ---------------------------------------------------------------------------------

    int RenderWidth(int page) => Math.Max(1, (int)(Document!.Pages[page].Width * PixelsPerPoint));

    void RequestRender()
    {
        if (_rendering) { _renderAgain = true; return; }
        _ = RenderLoopAsync();
    }

    async Task RenderLoopAsync()
    {
        _rendering = true;
        try
        {
            do
            {
                _renderAgain = false;
                while (NextPageToRender() is int page && Renderer is { } renderer && Document is { } document)
                {
                    CancellationToken cancel = _renderCancel.Token;
                    PageEntry entry = document.Pages[page];
                    int width = RenderWidth(page);
                    try { await renderer.RenderAsync(entry, width, cancel); }
                    catch (ToolException) { _failedRenders.Add((entry.Source, entry.SourcePage, width)); }
                    if (cancel.IsCancellationRequested) break;
                    Invalidate();
                }
            } while (_renderAgain);
        }
        catch (OperationCanceledException) { }
        finally { _rendering = false; }
    }

    int? NextPageToRender()
    {
        if (Document is null || Renderer is null) return null;
        var (first, last) = VisibleRange(extra: 1);
        foreach (int page in Enumerable.Range(first, Math.Max(0, last - first + 1)).OrderBy(p => Math.Abs(p - _currentPage)))
        {
            PageEntry entry = Document.Pages[page];
            int width = RenderWidth(page);
            if (!Renderer.HasRender(entry, width) && !_failedRenders.Contains((entry.Source, entry.SourcePage, width))) return page;
        }
        return null;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(Theme.Current.Canvas);
        if (Document is null || Renderer is null) return;
        var (first, last) = VisibleRange();
        for (int i = first; i <= last; i++)
        {
            RectangleF rect = ScreenRect(i);
            PageEntry entry = Document.Pages[i];
            using (var shadow = new SolidBrush(Color.FromArgb(Theme.IsDark ? 120 : 40, 0, 0, 0)))
                g.FillRectangle(shadow, rect.X, rect.Y + Px(1), rect.Width, rect.Height + Px(1));
            PagePainter.DrawPage(g, entry, Renderer.BestCached(entry, RenderWidth(i)), rect, item => item != _editing, PreviewRedactions);
            DrawFields(g, i);
        }
        // A page still shown from a smaller render gets its sharp one, however the view got here.
        if (!_rendering && NextPageToRender() is not null) RequestRender();
        if (_textPick is { } pick && pick.Page < _pages.Count) DrawTextPick(g, pick.Page, pick.Lines);
        if (_creating is not null && _dragPage >= 0) DrawCreating(g);
        if (_selectionPage >= 0 && _selectionPage < _pages.Count && !_hideSelection) DrawSelection(g);
        if (_marquee is { } marquee) DrawMarquee(g, marquee);
        if (_picking is not null && !_hideSelection) DrawLoupe(g);
    }

    void DrawMarquee(Graphics g, Rectangle marquee)
    {
        using var fill = new SolidBrush(Color.FromArgb(28, Theme.Current.Accent));
        using var edge = new Pen(Theme.Current.Accent, 1);
        g.FillRectangle(fill, marquee);
        g.DrawRectangle(edge, marquee);
    }

    IEnumerable<FormField> FieldsOn(int page)
    {
        PageEntry entry = Document!.Pages[page];
        return entry.Source != 0 || entry.IsBlank ? [] : Document.Fields.Where(field => field.SourcePage == entry.SourcePage);
    }

    void DrawFields(Graphics g, int page)
    {
        var fields = FieldsOn(page).ToList();
        if (fields.Count == 0) return;
        GraphicsState state = g.Save();
        using Matrix matrix = PageMatrix(page);
        g.MultiplyTransform(matrix);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        foreach (FormField field in fields)
        {
            string value = Document!.FieldValues.GetValueOrDefault(field.Name, field.Value);
            PagePainter.DrawField(g, field, value, value != field.Value, ShowFields);
        }
        g.Restore(state);
    }

    FormField? FieldAt(int page, PointF point) =>
        ShowFields ? FieldsOn(page).FirstOrDefault(field => !field.ReadOnly && field.Bounds.Contains(point)) : null;

    void DrawTextPick(Graphics g, int page, IReadOnlyList<RectangleF> lines)
    {
        using var fill = new SolidBrush(Color.FromArgb(Theme.IsDark ? 90 : 70, Theme.Current.Accent));
        foreach (RectangleF line in lines) g.FillRectangle(fill, ToScreen(page, line));
    }

    void DrawCreating(Graphics g)
    {
        GraphicsState state = g.Save();
        using Matrix matrix = PageMatrix(_dragPage);
        g.MultiplyTransform(matrix);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (_marking is { } runs) DrawMarking(g, _creating!, runs);
        else PagePainter.DrawObject(g, _creating!);
        g.Restore(state);
    }

    void DrawSelection(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.None;
        using var pen = new Pen(Theme.Current.Accent, 1);
        foreach (PageObject item in _selection)
        {
            // The box as the object shows, turned with it.
            PointF[] outline = [.. Frame(item, Px(3)).Select(p => ToScreen(_selectionPage, p))];
            if (item.Rotation == 0) g.DrawPolygon(pen, outline);
            else
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.DrawPolygon(pen, outline);
                g.SmoothingMode = SmoothingMode.None;
            }
        }
        // A group also gets a dashed box around all of it.
        using var dashed = new Pen(Theme.Current.Accent, 1) { DashStyle = DashStyle.Dash };
        foreach (var group in _selection.Where(item => item.Group is not null).GroupBy(item => item.Group))
        {
            RectangleF box = ToScreen(_selectionPage, group.Select(ShapeGeometry.VisibleBounds).Aggregate(RectangleF.Union));
            box.Inflate(Px(7), Px(7));
            g.DrawRectangle(dashed, box.X, box.Y, box.Width, box.Height);
        }
        if (_selection.Count != 1 || _editing is not null) return;
        PageObject single = _selection[0];
        List<RectangleF> handles = Handles(single, _selectionPage);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        for (int i = 0; i < handles.Count; i++)
        {
            RectangleF handle = handles[i];
            if (ShapeGeometry.TurnsAsBox(single.Kind) && i == TurnHandle)
            {
                // The turn handle: a round knob on a short stem above the top edge.
                PointF top = new(handles[1].X + handles[1].Width / 2, handles[1].Y + handles[1].Height / 2);
                g.DrawLine(pen, top, new PointF(handle.X + handle.Width / 2, handle.Y + handle.Height / 2));
                g.FillEllipse(Brushes.White, handle);
                g.DrawEllipse(pen, handle);
                continue;
            }
            bool active = single.Kind == MarkKind.Spline && i == _activePoint;
            using (var fill = new SolidBrush(active ? Theme.Current.Accent : Color.White)) g.FillRectangle(fill, handle);
            g.DrawRectangle(pen, handle.X, handle.Y, handle.Width, handle.Height);
        }
        g.SmoothingMode = SmoothingMode.None;
    }

    const int TurnHandle = 8;

    /// <summary>The four corners of an object's box as it shows on the page, padded by some screen pixels.</summary>
    PointF[] Frame(PageObject item, int padPixels)
    {
        float pad = padPixels / Math.Max(0.01f, PixelsPerPoint);
        RectangleF b = RectangleF.Inflate(item.Bounds, pad, pad);
        return ShapeGeometry.ToPage(item, new(b.Left, b.Top), new(b.Right, b.Top), new(b.Right, b.Bottom), new(b.Left, b.Bottom));
    }

    /// <summary>
    /// Handles in screen space. Lines get one at each end, curves one on every point, notes none; anything else gets
    /// corners and edges of its box (turned with it, in its own order: top-left, top, top-right, left, right,
    /// bottom-left, bottom, bottom-right) and, when it can turn, a round turn handle above.
    /// </summary>
    List<RectangleF> Handles(PageObject item, int page)
    {
        float size = Px(HandleSize);
        RectangleF Box(PointF p) => new(p.X - size / 2, p.Y - size / 2, size, size);
        if (item.Kind == MarkKind.Note) return [];
        if (ShapeGeometry.IsLine(item.Kind) && item.Strokes.FirstOrDefault() is { Count: >= 2 } points)
        {
            // A curve's middle handle sits on the curve; dragging it bends the curve through the pointer.
            if (item.Kind == MarkKind.Curve && points.Count >= 3)
                return [Box(ToScreen(page, points[0])), Box(ToScreen(page, CurveMiddle(points))), Box(ToScreen(page, points[^1]))];
            if (item.Kind == MarkKind.Spline) return [.. points.Select(p => Box(ToScreen(page, p)))];
            return [Box(ToScreen(page, points[0])), Box(ToScreen(page, points[^1]))];
        }
        float pad = Px(3) / Math.Max(0.01f, PixelsPerPoint);
        RectangleF b = RectangleF.Inflate(item.Bounds, pad, pad);
        float cx = b.Left + b.Width / 2, cy = b.Top + b.Height / 2;
        PointF[] local = [new(b.Left, b.Top), new(cx, b.Top), new(b.Right, b.Top), new(b.Left, cy), new(b.Right, cy),
            new(b.Left, b.Bottom), new(cx, b.Bottom), new(b.Right, b.Bottom)];
        var handles = ShapeGeometry.ToPage(item, local).Select(p => Box(ToScreen(page, p))).ToList();
        if (ShapeGeometry.TurnsAsBox(item.Kind))
        {
            // Above the top edge in the object's own frame, so it stays above as the object turns.
            PointF knob = ShapeGeometry.ToPage(item, new PointF(cx, b.Top - Px(22) / Math.Max(0.01f, PixelsPerPoint)))[0];
            handles.Add(RectangleF.Inflate(Box(ToScreen(page, knob)), 1, 1));
        }
        return handles;
    }

    // Selection ---------------------------------------------------------------------------------

    /// <summary>Selects objects of a page; with <paramref name="withGroups"/>, the rest of their groups come too.</summary>
    public void Select(int page, IEnumerable<PageObject> objects, bool withGroups = true)
    {
        CommitTextEdit();
        _session = null;
        _activePoint = -1;
        _selection.Clear();
        _selection.AddRange(page >= 0 && withGroups ? WithGroups(page, objects) : objects);
        _selectionPage = _selection.Count == 0 ? -1 : page;
        Invalidate();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        if (_selection.Count > 0) Announce(SelectionText());
    }

    /// <summary>The objects with the rest of any group they belong to, in page order.</summary>
    IEnumerable<PageObject> WithGroups(int page, IEnumerable<PageObject> objects)
    {
        var chosen = objects.ToList();
        var groups = chosen.Where(item => item.Group is not null).Select(item => item.Group!.Value).ToHashSet();
        if (groups.Count == 0 || Document is null || page >= Document.Pages.Count) return chosen;
        return Document.Pages[page].Objects.Where(item => chosen.Contains(item) || (item.Group is { } group && groups.Contains(group) && !item.Hidden)).ToList();
    }

    /// <summary>An object and the others in its group, or just the object.</summary>
    List<PageObject> GroupOf(int page, PageObject item) =>
        item.Group is { } group ? [.. Document!.Pages[page].Objects.Where(other => other.Group == group)] : [item];

    public bool CanGroup => _selection.Count > 1 && !(_selection.All(item => item.Group is not null) && _selection.Select(item => item.Group).Distinct().Count() == 1);

    public bool CanUngroup => _selection.Any(item => item.Group is not null);

    /// <summary>Joins the selected objects into one group that selects and moves as one.</summary>
    public void GroupSelection()
    {
        if (!CanGroup) return;
        StartChange();
        var group = Guid.NewGuid();
        foreach (PageObject item in _selection) item.Group = group;
        FinishChange();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        Announce($"Grouped {_selection.Count} objects");
    }

    public void UngroupSelection()
    {
        if (!CanUngroup) return;
        StartChange();
        foreach (PageObject item in _selection) item.Group = null;
        FinishChange();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        Announce("Ungrouped");
    }

    string SelectionText() => _selection.Count switch
    {
        0 => "Nothing selected",
        1 => $"{ToolStyles.Describe(_selection[0])} selected",
        _ => $"{_selection.Count} objects selected",
    };

    // Screen readers ----------------------------------------------------------------------------

    protected override AccessibleObject CreateAccessibilityInstance() => new DescribedAccessible(this, () =>
        Document is null || _pages.Count == 0 ? "Opening the document."
        : $"Page {_currentPage + 1} of {_pages.Count}, {Math.Round(_zoom * 100)} %. {SelectionText()}. " +
          "Arrow keys move the selection. With a tool chosen, Enter places what it makes in the middle of the view.");

    /// <summary>Speaks a change for screen reader users working in the page area.</summary>
    void Announce(string text)
    {
        AccessibilityNotifyClients(AccessibleEvents.DescriptionChange, -1);
        if (Focused) ScreenReader.Say(this, text);
    }

    /// <summary>
    /// Enter with a tool chosen: puts what the tool makes in the middle of the page in view, so every tool works
    /// without dragging. Arrow keys then move it and the inspector sets its size.
    /// </summary>
    void PlaceWithKeyboard()
    {
        if (Document is null || _pages.Count == 0) return;
        int page = _currentPage;
        RectangleF onScreen = ScreenRect(page);
        RectangleF visible = RectangleF.Intersect(onScreen, new RectangleF(0, 0, ClientSize.Width, ClientSize.Height));
        RectangleF area = visible.IsEmpty ? onScreen : visible;
        PointF point = ToPagePoint(page, Point.Round(new PointF(area.X + area.Width / 2, area.Y + area.Height / 2)));
        if (PlacesOnClick(Tool))
        {
            PlaceOnClick(page, point);
            return;
        }
        if (Tool == EditTool.Draw)
        {
            Announce("The pen needs a mouse, pen or finger. Use a shape or text instead.");
            return;
        }
        if (NewObject?.Invoke(Tool) is not { } item) return;
        SizeF size = Tool switch
        {
            EditTool.Highlight or EditTool.Underline or EditTool.Strikeout => new SizeF(160, 14),
            EditTool.RedactText => new SizeF(160, 18),
            _ => new SizeF(120, 80),
        };
        var box = new RectangleF(point.X - size.Width / 2, point.Y - size.Height / 2, size.Width, size.Height);
        if (Tool is EditTool.Line or EditTool.Arrow) item.Strokes = [[new(box.Left, point.Y), new(box.Right, point.Y)]];
        else if (Tool == EditTool.Curve) item.Strokes = [[new(box.Left, point.Y), new(point.X, box.Top - box.Height / 2), new(box.Right, point.Y)]];
        else if (Tool == EditTool.Triangle) item.Strokes = [TriangleIn(box)];
        else if (Tool is EditTool.SelectText or EditTool.LiftRectangle or EditTool.LiftFreeform)
        {
            Announce("This tool needs a mouse, pen or finger.");
            return;
        }
        else if (Tool == EditTool.RedactFreeform) item.Strokes = [[new(box.Left, box.Top), new(box.Right, box.Top), new(box.Right, box.Bottom), new(box.Left, box.Bottom)]];
        if (item.Strokes.Count > 0) ShapeGeometry.Fit(item);
        else item.Bounds = box;
        StartChange();
        Document.Pages[page].Objects.Add(item);
        FinishChange();
        FinishTool();
        Select(page, [item]);
    }

    public void ClearSelection()
    {
        if (_selection.Count == 0) return;
        Select(-1, []);
    }

    PageObject? HitObject(int page, PointF point)
    {
        if (Document is null) return null;
        float tolerance = Px(HitTolerance) / PixelsPerPoint;
        var objects = Document.Pages[page].Objects;
        for (int i = objects.Count - 1; i >= 0; i--)
        {
            PageObject item = objects[i];
            if (item.Hidden) continue;
            if (Hits(item, point, tolerance)) return item;
        }
        return null;
    }

    static bool Hits(PageObject item, PointF p, float tolerance)
    {
        // A turned or flipped object is tested in its own frame.
        p = ShapeGeometry.ToLocal(item, p);
        switch (item.Kind)
        {
            case MarkKind.Curve or MarkKind.Spline when item.Strokes.FirstOrDefault() is { Count: >= 2 }:
                PointF[] curve = ShapeGeometry.PathPoints(item);
                return curve.Zip(curve.Skip(1)).Any(segment => Distance(p, segment.First, segment.Second) <= tolerance + ShapeGeometry.PenWidth(item) / 2);
            case MarkKind.Line or MarkKind.Arrow or MarkKind.Ink:
                return item.Strokes.Any(stroke => stroke.Zip(stroke.Skip(1)).Any(segment => Distance(p, segment.First, segment.Second) <= tolerance + ShapeGeometry.PenWidth(item) / 2));
            case MarkKind.Redaction:
                using (GraphicsPath path = PagePainter.RedactionPath(item)) return path.IsVisible(p);
            default:
                RectangleF bounds = item.Bounds;
                bounds.Inflate(tolerance, tolerance);
                return bounds.Contains(p);
        }
    }

    static float Distance(PointF p, PointF a, PointF b)
    {
        float dx = b.X - a.X, dy = b.Y - a.Y, lengthSquared = dx * dx + dy * dy;
        float t = lengthSquared == 0 ? 0 : Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lengthSquared, 0, 1);
        float x = a.X + t * dx - p.X, y = a.Y + t * dy - p.Y;
        return MathF.Sqrt(x * x + y * y);
    }

    // Changes -----------------------------------------------------------------------------------

    void StartChange()
    {
        _session = null;
        if (_changeStarted) return;
        _changeStarted = true;
        BeforeChange?.Invoke(this, EventArgs.Empty);
    }

    void FinishChange()
    {
        if (!_changeStarted) return;
        _changeStarted = false;
        Invalidate();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void DeleteSelection()
    {
        if (_selection.Count == 0 || Document is null) return;
        int count = _selection.Count;
        StartChange();
        foreach (PageObject item in _selection) Document.Pages[_selectionPage].Objects.Remove(item);
        FinishChange();
        ClearSelection();
        Announce($"Deleted {Core.Words.Count(count, "object", "objects")}");
    }

    public void DuplicateSelection()
    {
        if (_selection.Count == 0 || Document is null) return;
        StartChange();
        List<PageObject> copies = FreshCopies(_selection, 10);
        Document.Pages[_selectionPage].Objects.AddRange(copies);
        FinishChange();
        Select(_selectionPage, copies);
    }

    public void Copy() => _clipboard = [.. _selection.Select(item => item.Clone())];

    public void Paste()
    {
        if (_clipboard.Count == 0 || Document is null || Document.Pages.Count == 0) return;
        int page = _selectionPage >= 0 ? _selectionPage : _currentPage;
        StartChange();
        List<PageObject> copies = FreshCopies(_clipboard, 12);
        Document.Pages[page].Objects.AddRange(copies);
        FinishChange();
        Select(page, copies);
    }

    /// <summary>Copies with new ids, nudged along; copies of a group form a new group of their own.</summary>
    static List<PageObject> FreshCopies(IEnumerable<PageObject> items, float offset)
    {
        var groups = new Dictionary<Guid, Guid>();
        return [.. items.Select(item =>
        {
            PageObject copy = item.Clone();
            copy.Id = Guid.NewGuid();
            if (copy.Group is { } group)
            {
                if (!groups.TryGetValue(group, out Guid fresh)) groups[group] = fresh = Guid.NewGuid();
                copy.Group = fresh;
            }
            copy.Offset(offset, offset);
            return copy;
        })];
    }

    /// <summary>
    /// Applies a style change to the selection as one undo step. Changes that pass the same
    /// <paramref name="session"/> (a slider being dragged) add to the step the first one started.
    /// </summary>
    public void ChangeSelection(Action<PageObject> change, object? session = null)
    {
        if (_selection.Count == 0) return;
        bool continuing = session is not null && session == _session;
        if (!continuing)
        {
            StartChange();
            _session = session;
        }
        foreach (PageObject item in _selection) change(item);
        if (_selection.Any(item => item.Kind == MarkKind.Text)) FitTextHeights();
        if (!continuing)
        {
            FinishChange();
            return;
        }
        Invalidate();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Changes one object, selected or not, as one undo step.</summary>
    public void ChangeObject(PageObject item, Action<PageObject> change) => ChangeObjects([item], change);

    /// <summary>Changes some objects, selected or not, as one undo step.</summary>
    public void ChangeObjects(IEnumerable<PageObject> items, Action<PageObject> change)
    {
        StartChange();
        foreach (PageObject item in items) change(item);
        FinishChange();
    }

    /// <summary>Selects an object and scrolls it into view.</summary>
    public void Reveal(int page, PageObject item)
    {
        if (page < 0 || page >= _pages.Count) return;
        RectangleF rect = ToScreen(page, item.Bounds);
        if (rect.Top < 0 || rect.Bottom > ClientSize.Height)
        {
            AutoScrollPosition = new Point(-AutoScrollPosition.X, Math.Max(0, (int)(rect.Top - AutoScrollPosition.Y - ClientSize.Height / 3f)));
            AfterScroll();
        }
        Select(page, [item]);
    }

    /// <summary>Moves the selected objects to the front or back of their page.</summary>
    public void Arrange(bool toFront)
    {
        if (_selection.Count == 0 || Document is null) return;
        StartChange();
        var objects = Document.Pages[_selectionPage].Objects;
        foreach (PageObject item in _selection) objects.Remove(item);
        if (toFront) objects.AddRange(_selection); else objects.InsertRange(0, _selection);
        FinishChange();
    }

    /// <summary>Moves each selected object one place up (or down) the stack, past the object next to it.</summary>
    public void ArrangeStep(bool forward)
    {
        if (_selection.Count == 0 || Document is null) return;
        var objects = Document.Pages[_selectionPage].Objects;
        StartChange();
        // From the end they move towards, so each moves one place and none leapfrogs another.
        var indexes = forward ? Enumerable.Range(0, objects.Count).Reverse() : Enumerable.Range(0, objects.Count);
        foreach (int i in indexes.ToList())
        {
            int next = forward ? i + 1 : i - 1;
            if (next < 0 || next >= objects.Count) continue;
            if (_selection.Contains(objects[i]) && !_selection.Contains(objects[next])) (objects[i], objects[next]) = (objects[next], objects[i]);
        }
        FinishChange();
    }

    public bool CanDistribute => _selection.Count >= 3;

    /// <summary>Spaces three or more objects evenly between the outermost two: their middles, across or down.</summary>
    public void Distribute(bool across)
    {
        if (!CanDistribute) return;
        float Middle(PageObject item) => across ? item.X + item.Width / 2 : item.Y + item.Height / 2;
        var items = _selection.OrderBy(Middle).ToList();
        float first = Middle(items[0]), step = (Middle(items[^1]) - first) / (items.Count - 1);
        StartChange();
        for (int i = 1; i < items.Count - 1; i++)
        {
            float by = first + step * i - Middle(items[i]);
            items[i].Offset(across ? by : 0, across ? 0 : by);
        }
        FinishChange();
    }

    /// <summary>
    /// Puts a page's objects in a new order (top first), each in the group given or in none, as one undo step:
    /// what dragging layers in the layer list does. A group left with one object stops being a group.
    /// </summary>
    public void Restack(int page, IReadOnlyList<(PageObject Item, Guid? Group)> topFirst)
    {
        if (Document is null || page < 0 || page >= Document.Pages.Count) return;
        List<PageObject> objects = Document.Pages[page].Objects;
        if (topFirst.Count != objects.Count || topFirst.Any(entry => !objects.Contains(entry.Item))) return;
        StartChange();
        objects.Clear();
        foreach (var (item, group) in topFirst.Reverse())
        {
            item.Group = group;
            objects.Add(item);
        }
        foreach (PageObject single in objects.Where(item => item.Group is not null).GroupBy(item => item.Group).Where(g => g.Count() < 2).SelectMany(g => g).ToList())
            single.Group = null;
        FinishChange();
    }

    /// <summary>
    /// Lines up the selection: several objects against the edges or centres of the box around them, one object
    /// against its page.
    /// </summary>
    public void Align(AlignEdge edge)
    {
        if (_selection.Count == 0 || Document is null) return;
        PageEntry page = Document.Pages[_selectionPage];
        RectangleF area = _selection.Count == 1
            ? new RectangleF(0, 0, page.Width, page.Height)
            : _selection.Select(ShapeGeometry.VisibleBounds).Aggregate(RectangleF.Union);
        StartChange();
        foreach (PageObject item in _selection)
        {
            RectangleF b = ShapeGeometry.VisibleBounds(item);
            float dx = edge switch
            {
                AlignEdge.Left => area.Left - b.Left,
                AlignEdge.Centre => area.Left + (area.Width - b.Width) / 2 - b.Left,
                AlignEdge.Right => area.Right - b.Right,
                _ => 0,
            };
            float dy = edge switch
            {
                AlignEdge.Top => area.Top - b.Top,
                AlignEdge.Middle => area.Top + (area.Height - b.Height) / 2 - b.Top,
                AlignEdge.Bottom => area.Bottom - b.Bottom,
                _ => 0,
            };
            item.Offset(dx, dy);
        }
        FinishChange();
    }

    void FitTextHeights()
    {
        using Graphics g = CreateGraphics();
        foreach (PageObject item in _selection.Where(item => item.Kind == MarkKind.Text))
            item.Height = Math.Max(item.Height, PagePainter.TextHeight(g, item));
    }

    // Mouse -------------------------------------------------------------------------------------

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (_editor is not null && !_editor.Bounds.Contains(e.Location)) CommitTextEdit();
        Focus();
        _pressScreen = e.Location;
        if (e.Button == MouseButtons.Middle || (e.Button == MouseButtons.Left && (Tool == EditTool.Hand || _spaceDown)))
        {
            _drag = Drag.Pan;
            _panOrigin = new Point(-AutoScrollPosition.X, -AutoScrollPosition.Y);
            Cursor = Cursors.SizeAll;
            return;
        }
        if (_picking is not null)
        {
            if (e.Button == MouseButtons.Left) FinishPicking(e.Location);
            else CancelPicking();
            return;
        }
        if (e.Button == MouseButtons.Right && _textPick is not null)
        {
            ShowTextMenu(e.Location);
            return;
        }
        if (e.Button != MouseButtons.Left || Document is null) return;

        if (Tool == EditTool.SelectText)
        {
            _textPick = null;
            Invalidate();
            if (!ToPage(e.Location, out int textPage, out PointF from)) return;
            _drag = Drag.Text;
            _dragPage = textPage;
            _pressPage = _lastPage = from;
            if (e.Clicks == 2) PickText(from, from);
            return;
        }

        if (Tool == EditTool.Select)
        {
            if (_selection.Count == 1 && HandleAt(e.Location) is int handle and >= 0)
            {
                PageObject item = _selection[0];
                _dragPage = _selectionPage;
                _pressPage = ToPagePoint(_dragPage, e.Location);
                if (ShapeGeometry.TurnsAsBox(item.Kind) && handle == TurnHandle)
                {
                    _drag = Drag.Rotate;
                    _turnCentre = new PointF(item.X + item.Width / 2, item.Y + item.Height / 2);
                    _turnFrom = AngleOf(_turnCentre, _pressPage);
                    _turnStart = item.Rotation;
                    return;
                }
                if (item.Kind == MarkKind.Spline)
                {
                    _activePoint = handle;
                    Invalidate();
                    // A double-click on a point takes it out.
                    if (e.Clicks == 2) { RemoveCurvePoint(); return; }
                }
                _drag = Drag.Resize;
                _handle = handle;
                _resizeStart = item.Bounds;
                _resizeOriginal = item.Clone();
                return;
            }
            bool adding = (ModifierKeys & (Keys.Shift | Keys.Control)) != 0;
            if (!ToPage(e.Location, out int page, out PointF point))
            {
                StartMarquee(_pages.Count == 0 ? -1 : _currentPage, adding);
                return;
            }
            PageObject? hit = HitObject(page, point);
            _pressOnChosen = hit is not null && _selection.Count == 1 && _selection[0] == hit;
            if (hit is null && FieldAt(page, point) is { } clicked)
            {
                ClearSelection();
                FieldClicked?.Invoke(this, clicked);
                return;
            }
            if (hit is null)
            {
                StartMarquee(page, adding);
                return;
            }
            // Shift or Ctrl adds an object (and its group) to the selection, or takes it out.
            if (adding && page == _selectionPage)
                Select(page, _selection.Contains(hit) ? _selection.Except(GroupOf(page, hit)).ToList() : [.. _selection, hit]);
            else if (!_selection.Contains(hit))
                Select(page, [hit]);
            // A double-click inside a selected group picks out the one object under the pointer.
            if (e.Clicks == 2 && hit.Group is not null && _selection.Count > 1 && _selection.Contains(hit))
            {
                Select(page, [hit], withGroups: false);
                return;
            }
            if (e.Clicks == 2 && hit.Kind == MarkKind.Text) { BeginTextEdit(page, hit); return; }
            _drag = Drag.Move;
            _dragPage = page;
            _pressPage = _lastPage = point;
            return;
        }

        if (!ToPage(e.Location, out int target, out PointF start)) return;
        _dragPage = target;
        _pressPage = _lastPage = start;
        if (PlacesOnClick(Tool)) { PlaceOnClick(target, start); return; }
        _creating = NewObject?.Invoke(Tool);
        if (_creating is null) return;
        _creating.Bounds = new RectangleF(start, SizeF.Empty);
        if (Tool is EditTool.Line or EditTool.Arrow) _creating.Strokes = [[start, start]];
        if (Tool == EditTool.Curve) _creating.Strokes = [[start, start, start]];
        if (Tool is EditTool.Draw or EditTool.RedactFreeform or EditTool.LiftFreeform) _creating.Strokes = [[start]];
        if (Tool == EditTool.Draw)
        {
            _creating.Pressures = Pressure is float pressure ? [[pressure]] : [];
            _snapped = false;
            _holdAnchor = e.Location;
            _holdTimer.Stop();
            // A highlighter follows the lines of text it crosses, and the pointer's own path elsewhere.
            _marking = _creating.Pen == PenKind.Highlighter && IsOverText is not null ? [new HighlightRun(IsOverText(target, start), start)] : null;
        }
        _drag = Drag.Create;
    }

    // Picking and copying text --------------------------------------------------------------------

    void PickText(PointF from, PointF to)
    {
        IReadOnlyList<RectangleF>? lines = SelectText?.Invoke(_dragPage, from, to);
        _textPick = lines is { Count: > 0 } ? (_dragPage, from, to, lines) : null;
        Invalidate();
    }

    public bool HasTextPicked => _textPick is not null;

    public void CopyPickedText()
    {
        if (_textPick is not { } pick || TextBetween?.Invoke(pick.Page, pick.From, pick.To) is not { Length: > 0 } text) return;
        try
        {
            Clipboard.SetText(text);
            Announce("Copied the text.");
        }
        catch (System.Runtime.InteropServices.ExternalException) { Announce("Couldn't copy: another app is using the clipboard."); }
    }

    void ShowTextMenu(Point location)
    {
        ContextMenuStrip menu = Menus.Create();
        menu.Items.Add(Menus.Item("Copy", CopyPickedText, "Ctrl+C"));
        Menus.Show(menu, this, location);
    }

    /// <summary>A press on an empty part of the page: dragging from it draws a box that selects what it touches.</summary>
    void StartMarquee(int page, bool adding)
    {
        if (!adding) ClearSelection();
        if (page < 0) return;
        _drag = Drag.Marquee;
        _dragPage = page;
        _marqueeAdds = adding;
    }

    void UpdateMarquee(Point location)
    {
        if (_marquee is null && !PastDragThreshold(location)) return;
        _marquee = Rectangle.FromLTRB(Math.Min(_pressScreen.X, location.X), Math.Min(_pressScreen.Y, location.Y),
            Math.Max(_pressScreen.X, location.X), Math.Max(_pressScreen.Y, location.Y));
        Invalidate();
    }

    void FinishMarquee()
    {
        if (_marquee is not { } box || Document is null || _dragPage < 0) return;
        _marquee = null;
        PointF a = ToPagePoint(_dragPage, box.Location), b = ToPagePoint(_dragPage, new Point(box.Right, box.Bottom));
        var area = RectangleF.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
        var touched = Document.Pages[_dragPage].Objects.Where(item => !item.Hidden && ShapeGeometry.VisibleBounds(item).IntersectsWith(area)).ToList();
        if (_marqueeAdds && _dragPage == _selectionPage) touched = [.. _selection.Union(touched)];
        Select(touched.Count == 0 ? -1 : _dragPage, touched);
        Invalidate();
    }

    static bool PlacesOnClick(EditTool tool) =>
        tool is EditTool.Text or EditTool.Date or EditTool.Note or EditTool.Check or EditTool.Cross or EditTool.Image or EditTool.Signature;

    /// <summary>
    /// Places what a one-click tool makes: text starts at the click, anything else is centred on it. Either way
    /// it stays on the page.
    /// </summary>
    void PlaceOnClick(int page, PointF point)
    {
        PageObject? placed = Tool switch
        {
            EditTool.Image => ImageObject(),
            EditTool.Signature => CurrentSignature?.Invoke(),
            _ => NewObject?.Invoke(Tool),
        };
        if (placed is null) return;

        PageEntry entry = Document!.Pages[page];
        bool centred = Tool is not (EditTool.Text or EditTool.Date);
        placed.Offset(point.X - placed.X - (centred ? placed.Width / 2 : 0), point.Y - placed.Y - (centred ? placed.Height / 2 : 0));
        placed.Offset(Math.Max(0, -placed.X), Math.Max(0, -placed.Y));
        placed.Offset(Math.Min(0, entry.Width - placed.X - placed.Width), Math.Min(0, entry.Height - placed.Y - placed.Height));
        if (placed.Kind == MarkKind.Text)
        {
            using Graphics g = CreateGraphics();
            placed.Height = Math.Max(placed.Height, PagePainter.TextHeight(g, placed));
        }
        bool typing = Tool == EditTool.Text;
        FinishTool();
        if (typing)
        {
            // The box joins the page once something is typed in it, so an empty click leaves no trace.
            _selection.Clear();
            BeginTextEdit(page, placed, isNew: true);
            return;
        }
        StartChange();
        entry.Objects.Add(placed);
        FinishChange();
        Select(page, [placed]);
        Placed?.Invoke(this, placed);
    }

    PageObject? ImageObject()
    {
        if (PickImage?.Invoke() is not { } data) return null;
        PageObject? item = NewObject?.Invoke(EditTool.Image);
        if (item is null) return null;
        item.ImageData = data;
        if (ImageCache.Get(item) is { } image)
        {
            float width = Math.Min(240, image.Width * 72f / Math.Max(1, image.HorizontalResolution));
            item.Width = width;
            item.Height = width * image.Height / image.Width;
        }
        return item;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        switch (_drag)
        {
            case Drag.Pan:
                AutoScrollPosition = new Point(_panOrigin.X - (e.X - _pressScreen.X), _panOrigin.Y - (e.Y - _pressScreen.Y));
                AfterScroll();
                return;
            case Drag.Move:
                MoveSelection(e);
                return;
            case Drag.Resize:
                ResizeSelection(e);
                return;
            case Drag.Rotate:
                TurnByHandle(e);
                return;
            case Drag.Create:
                UpdateCreating(e);
                return;
            case Drag.Marquee:
                UpdateMarquee(e.Location);
                return;
            case Drag.Text:
                if (_textPick is null && !PastDragThreshold(e.Location)) return;
                _lastPage = ToPagePoint(_dragPage, e.Location);
                PickText(_pressPage, _lastPage);
                return;
        }
        if (_picking is not null)
        {
            _pickAt = e.Location;
            Invalidate();
            return;
        }
        UpdateCursor(e.Location);
    }

    bool PastDragThreshold(Point location) =>
        Math.Abs(location.X - _pressScreen.X) > SystemInformation.DragSize.Width || Math.Abs(location.Y - _pressScreen.Y) > SystemInformation.DragSize.Height;

    void MoveSelection(MouseEventArgs e)
    {
        if (!_changeStarted && !PastDragThreshold(e.Location)) return;
        StartChange();
        PointF point = ToPagePoint(_dragPage, e.Location);
        float dx = point.X - _lastPage.X, dy = point.Y - _lastPage.Y;
        foreach (PageObject item in _selection) item.Offset(dx, dy);
        _lastPage = point;
        Invalidate();
    }

    void ResizeSelection(MouseEventArgs e)
    {
        if (!_changeStarted && !PastDragThreshold(e.Location)) return;
        StartChange();
        PageObject item = _selection[0];
        PointF point = ToPagePoint(_dragPage, e.Location);
        if (ShapeGeometry.IsLine(item.Kind) && item.Strokes.FirstOrDefault() is { Count: >= 2 } points)
        {
            bool curve = item.Kind == MarkKind.Curve && points.Count >= 3;
            if (curve && _handle == 1) points[1] = BendThrough(points[0], point, points[^1]);
            else if (item.Kind == MarkKind.Spline && _handle < points.Count) points[_handle] = point;
            else points[_handle == 0 ? 0 : points.Count - 1] = point;
            ShapeGeometry.Fit(item);
            Invalidate();
            return;
        }
        if (_resizeOriginal is not { } original) return;
        // In the object's own frame, so a turned (or flipped, or on a turned page) object stretches along its sides.
        PointF from = ShapeGeometry.ToLocal(original, _pressPage), to = ShapeGeometry.ToLocal(original, point);
        float dx = to.X - from.X, dy = to.Y - from.Y;
        RectangleF start = _resizeStart;
        float left = start.Left, top = start.Top, right = start.Right, bottom = start.Bottom;
        if (_handle is 0 or 3 or 5) left += dx;
        if (_handle is 2 or 4 or 7) right += dx;
        if (_handle is 0 or 1 or 2) top += dy;
        if (_handle is 5 or 6 or 7) bottom += dy;
        if (ModifierKeys.HasFlag(Keys.Shift) && _handle is 0 or 2 or 5 or 7 && start.Width > 0)
        {
            float ratio = start.Height / start.Width, width = Math.Abs(right - left);
            if (_handle is 0 or 2) top = bottom - width * ratio; else bottom = top + width * ratio;
        }
        var local = RectangleF.FromLTRB(Math.Min(left, right), Math.Min(top, bottom), Math.Max(left, right), Math.Max(top, bottom));
        if (local.Width < 2 || local.Height < 2) return;
        // The new box's middle, put back where the old frame shows it, keeps the side not dragged in place.
        PointF middle = ShapeGeometry.ToPage(original, new PointF(local.X + local.Width / 2, local.Y + local.Height / 2))[0];
        item.Resize(new RectangleF(middle.X - local.Width / 2, middle.Y - local.Height / 2, local.Width, local.Height));
        Invalidate();
    }

    static double AngleOf(PointF centre, PointF point) => Math.Atan2(point.Y - centre.Y, point.X - centre.X) * 180 / Math.PI;

    /// <summary>Turning with the round handle; Shift keeps to steps of 15 degrees.</summary>
    void TurnByHandle(MouseEventArgs e)
    {
        if (!_changeStarted && !PastDragThreshold(e.Location)) return;
        StartChange();
        PageObject item = _selection[0];
        double angle = _turnStart + AngleOf(_turnCentre, ToPagePoint(_dragPage, e.Location)) - _turnFrom;
        if (ModifierKeys.HasFlag(Keys.Shift)) angle = Math.Round(angle / 15) * 15;
        item.Rotation = Normalize((float)angle);
        Invalidate();
    }

    /// <summary>An angle as -180 to 180 degrees.</summary>
    static float Normalize(float degrees)
    {
        degrees %= 360;
        if (degrees > 180) degrees -= 360;
        if (degrees <= -180) degrees += 360;
        return Math.Abs(degrees) < 0.01f ? 0 : degrees;
    }

    static bool Turnable(PageObject item) => item.Kind is not (MarkKind.Highlight or MarkKind.Underline or MarkKind.Strikeout or MarkKind.Note or MarkKind.Redaction);

    public bool CanTurn => _selection.Any(Turnable);

    /// <summary>
    /// Turns the selection about its middle: shapes, text and pictures keep their form and turn; lines and drawings
    /// have their points moved round.
    /// </summary>
    public void RotateSelection(float degrees)
    {
        var items = _selection.Where(Turnable).ToList();
        if (items.Count == 0) return;
        RectangleF area = items.Select(ShapeGeometry.VisibleBounds).Aggregate(RectangleF.Union);
        var pivot = new PointF(area.X + area.Width / 2, area.Y + area.Height / 2);
        using var turn = new Matrix();
        turn.RotateAt(degrees, pivot);
        StartChange();
        foreach (PageObject item in items)
        {
            if (ShapeGeometry.TurnsAsBox(item.Kind))
            {
                PointF[] middle = [new(item.X + item.Width / 2, item.Y + item.Height / 2)];
                float x = middle[0].X, y = middle[0].Y;
                turn.TransformPoints(middle);
                item.Offset(middle[0].X - x, middle[0].Y - y);
                item.Rotation = Normalize(item.Rotation + degrees);
            }
            else Transform(item, turn);
        }
        FinishChange();
    }

    /// <summary>Sets how far the chosen shapes, text and pictures are turned, each about its own middle.</summary>
    public void SetRotation(float degrees, object? session = null)
    {
        if (_selection.All(item => !ShapeGeometry.TurnsAsBox(item.Kind))) return;
        ChangeSelection(item => { if (ShapeGeometry.TurnsAsBox(item.Kind)) item.Rotation = Normalize(degrees); }, session);
    }

    /// <summary>Flips the selection across (left for right) or down (top for bottom), about its middle.</summary>
    public void FlipSelection(bool across)
    {
        var items = _selection.Where(Turnable).ToList();
        if (items.Count == 0) return;
        RectangleF area = items.Select(ShapeGeometry.VisibleBounds).Aggregate(RectangleF.Union);
        float pivotX = area.X + area.Width / 2, pivotY = area.Y + area.Height / 2;
        using var mirror = new Matrix(across ? -1 : 1, 0, 0, across ? 1 : -1, across ? 2 * pivotX : 0, across ? 0 : 2 * pivotY);
        StartChange();
        foreach (PageObject item in items)
        {
            if (ShapeGeometry.TurnsAsBox(item.Kind))
            {
                float x = item.X + item.Width / 2, y = item.Y + item.Height / 2;
                item.Offset(across ? 2 * (pivotX - x) : 0, across ? 0 : 2 * (pivotY - y));
                // A flip after a turn is the opposite turn after the flip.
                if (across) item.FlipX = !item.FlipX; else item.FlipY = !item.FlipY;
                item.Rotation = Normalize(-item.Rotation);
            }
            else Transform(item, mirror);
        }
        FinishChange();
    }

    /// <summary>Moves every point of a line, drawing or polygon through a transform, then fits its box.</summary>
    static void Transform(PageObject item, Matrix matrix)
    {
        foreach (List<PointF> stroke in item.Strokes)
        {
            PointF[] points = [.. stroke];
            matrix.TransformPoints(points);
            for (int i = 0; i < points.Length; i++) stroke[i] = points[i];
        }
        if (item.Strokes.Count > 0) ShapeGeometry.Fit(item);
    }

    // Curve points ---------------------------------------------------------------------------------------

    /// <summary>Puts a new point on a curve where it was clicked, between the two points it falls between.</summary>
    void AddCurvePoint(PageObject item, PointF at)
    {
        if (item.Strokes.FirstOrDefault() is not { Count: >= 2 } points) return;
        StartChange();
        // The older one-bend curve becomes a curve through points first.
        if (item.Kind == MarkKind.Curve && points.Count >= 3)
        {
            item.Kind = MarkKind.Spline;
            points = item.Strokes[0] = [points[0], CurveMiddle(points), points[^1]];
        }
        const int Steps = 16;
        PointF[] path = ShapeGeometry.SplinePoints(points, Steps);
        float Gap(PointF p) => (p.X - at.X) * (p.X - at.X) + (p.Y - at.Y) * (p.Y - at.Y);
        int nearest = 0;
        for (int i = 1; i < path.Length; i++)
            if (Gap(path[i]) < Gap(path[nearest])) nearest = i;
        int piece = Math.Clamp((nearest - 1) / Steps, 0, points.Count - 2);
        points.Insert(piece + 1, path[nearest]);
        ShapeGeometry.Fit(item);
        FinishChange();
        _activePoint = piece + 1;
        Invalidate();
        Announce("Added a point. Drag it to bend the curve; Delete takes it out.");
    }

    /// <summary>Takes out the curve point picked last; a curve keeps at least its two ends.</summary>
    void RemoveCurvePoint()
    {
        if (_selection is not [{ Kind: MarkKind.Spline } item] || item.Strokes.FirstOrDefault() is not { } points
            || _activePoint < 0 || _activePoint >= points.Count || points.Count <= 2) return;
        StartChange();
        points.RemoveAt(_activePoint);
        ShapeGeometry.Fit(item);
        FinishChange();
        _activePoint = -1;
        Invalidate();
        Announce("Removed the point.");
    }

    void UpdateCreating(MouseEventArgs e)
    {
        if (_creating is null) return;
        PointF point = ToPagePoint(_dragPage, e.Location);
        if (_snapped)
        {
            // Once snapped, the far end of a line or curve still follows the pen until it lifts.
            if (ShapeGeometry.IsLine(_creating.Kind) && _creating.Strokes.FirstOrDefault() is { Count: >= 2 } ends)
            {
                PointF bend = ends.Count >= 3 ? CurveMiddle(ends) : default;
                ends[^1] = point;
                if (ends.Count >= 3) ends[1] = BendThrough(ends[0], bend, point);
                ShapeGeometry.Fit(_creating);
                Invalidate();
            }
            return;
        }
        switch (Tool)
        {
            case EditTool.Line or EditTool.Arrow:
                if (ModifierKeys.HasFlag(Keys.Shift)) point = Snap45(_pressPage, point);
                _creating.Strokes[0][1] = point;
                _creating.FitToStrokes();
                break;
            case EditTool.Curve:
                if (ModifierKeys.HasFlag(Keys.Shift)) point = Snap45(_pressPage, point);
                // Drawn straight; the middle handle bends it afterwards.
                _creating.Strokes[0] = [_pressPage, new PointF((_pressPage.X + point.X) / 2, (_pressPage.Y + point.Y) / 2), point];
                ShapeGeometry.Fit(_creating);
                break;
            case EditTool.Draw when _marking is not null:
                ExtendMarking(point);
                break;
            case EditTool.Draw or EditTool.RedactFreeform or EditTool.LiftFreeform:
                if (Tool == EditTool.Draw && (Math.Abs(e.X - _holdAnchor.X) > Px(3) || Math.Abs(e.Y - _holdAnchor.Y) > Px(3)))
                {
                    _holdAnchor = e.Location;
                    _holdTimer.Stop();
                    _holdTimer.Start();
                }
                if (Math.Abs(point.X - _lastPage.X) + Math.Abs(point.Y - _lastPage.Y) < 0.8f) return;
                _creating.Strokes[0].Add(point);
                if (_creating.Pressures.Count == 1) _creating.Pressures[0].Add(Pressure ?? _creating.Pressures[0][^1]);
                _creating.FitToStrokes();
                break;
            default:
                float width = point.X - _pressPage.X, height = point.Y - _pressPage.Y;
                if (ModifierKeys.HasFlag(Keys.Shift) && Tool is EditTool.Rectangle or EditTool.Ellipse or EditTool.RedactRectangle or EditTool.RedactEllipse
                    or EditTool.Triangle or EditTool.SemiCircle or EditTool.Pie or EditTool.Heart or EditTool.Star or EditTool.Parallelogram)
                {
                    float side = Math.Max(Math.Abs(width), Math.Abs(height));
                    width = Math.Sign(width) * side;
                    height = Math.Sign(height) * side;
                }
                _creating.Bounds = RectangleF.FromLTRB(Math.Min(_pressPage.X, _pressPage.X + width), Math.Min(_pressPage.Y, _pressPage.Y + height),
                    Math.Max(_pressPage.X, _pressPage.X + width), Math.Max(_pressPage.Y, _pressPage.Y + height));
                if (Tool is EditTool.Highlight or EditTool.Underline or EditTool.Strikeout && _creating.Height < 4)
                    _creating.Height = Math.Max(_creating.Height, 12);
                if (Tool == EditTool.Triangle) _creating.Strokes = [TriangleIn(_creating.Bounds)];
                break;
        }
        _lastPage = point;
        Invalidate();
    }

    /// <summary>A highlighter stroke moving on: along the text while over it, along the pointer's path off it.</summary>
    void ExtendMarking(PointF point)
    {
        HighlightRun run = _marking![^1];
        bool over = IsOverText?.Invoke(_dragPage, point) ?? false;
        if (over != run.Text) _marking.Add(new HighlightRun(over, point));
        else if (over) run.To = point;
        else run.Path.Add(point);
    }

    /// <summary>The boxes of the text lines a highlighter run crossed.</summary>
    IReadOnlyList<RectangleF> MarkedLines(HighlightRun run) => SelectText?.Invoke(_dragPage, run.From, run.To) ?? [];

    void DrawMarking(Graphics g, PageObject pen, List<HighlightRun> runs)
    {
        using var fill = new SolidBrush(PagePainter.WithAlpha(pen.StrokeColor, pen.Opacity * 0.4f));
        foreach (HighlightRun run in runs)
        {
            if (run.Text) foreach (RectangleF line in MarkedLines(run)) g.FillRectangle(fill, line);
            else if (run.Path.Count >= 2) PagePainter.DrawPieces(g, pen, ShapeGeometry.InkPieces(pen, [run.Path], []));
        }
    }

    /// <summary>
    /// Adds what a highlighter stroke made: a highlight on each line of text it followed and the free parts as
    /// highlighter ink, grouped when there is more than one, so they move together.
    /// </summary>
    void CommitMarking(PageObject pen, List<HighlightRun> runs)
    {
        var made = new List<PageObject>();
        foreach (HighlightRun run in runs.Where(run => run.Text))
            foreach (RectangleF line in MarkedLines(run))
                made.Add(new PageObject { Kind = MarkKind.Highlight, Bounds = line, StrokeColor = pen.StrokeColor, Opacity = pen.Opacity });
        var free = runs.Where(run => !run.Text && run.Path.Count >= 2).Select(run => run.Path).ToList();
        if (free.Count > 0 && free.Sum(path => path.Count) >= 3)
        {
            PageObject ink = pen.Clone();
            ink.Id = Guid.NewGuid();
            ink.Strokes = free;
            ink.Pressures = [];
            ink.FitToStrokes();
            made.Add(ink);
        }
        if (made.Count == 0) { Invalidate(); return; }
        if (made.Count > 1)
        {
            var group = Guid.NewGuid();
            foreach (PageObject item in made) item.Group = group;
        }
        StartChange();
        Document!.Pages[_dragPage].Objects.AddRange(made);
        FinishChange();
    }

    static List<PointF> TriangleIn(RectangleF box) =>
        [new(box.X + box.Width / 2, box.Top), new(box.Right, box.Bottom), new(box.Left, box.Bottom)];

    /// <summary>Where a quadratic curve passes halfway along.</summary>
    static PointF CurveMiddle(List<PointF> points) =>
        new(0.25f * points[0].X + 0.5f * points[1].X + 0.25f * points[^1].X, 0.25f * points[0].Y + 0.5f * points[1].Y + 0.25f * points[^1].Y);

    /// <summary>The bend handle that makes a curve between two ends pass through <paramref name="middle"/>.</summary>
    static PointF BendThrough(PointF start, PointF middle, PointF end) =>
        new(2 * middle.X - (start.X + end.X) / 2, 2 * middle.Y - (start.Y + end.Y) / 2);

    /// <summary>A pen stroke held still: if it looks like a shape, it becomes that shape.</summary>
    void SnapHeldStroke()
    {
        _holdTimer.Stop();
        if (_drag != Drag.Create || _snapped || _marking is not null || _creating is not { Kind: MarkKind.Ink } ink) return;
        if (ShapeRecognizer.Recognize(ink) is not { } shape) return;
        _creating = shape;
        _snapped = true;
        Invalidate();
        Announce($"Snapped to {ToolStyles.Describe(shape).ToLowerInvariant()}.");
    }

    float? Pressure => Environment.TickCount64 - _penAt < 250 ? _penPressure : null;

    protected override void WndProc(ref Message m)
    {
        const int WM_POINTERUPDATE = 0x0245, WM_POINTERDOWN = 0x0246;
        if (m.Msg is WM_POINTERUPDATE or WM_POINTERDOWN && Platform.Native.PenPressure((uint)(m.WParam.ToInt64() & 0xFFFF)) is float pressure)
        {
            _penPressure = pressure;
            _penAt = Environment.TickCount64;
        }
        // Passed on either way, so Windows still turns pen input into the mouse messages the tools use.
        base.WndProc(ref m);
    }

    static PointF Snap45(PointF from, PointF to)
    {
        double angle = Math.Atan2(to.Y - from.Y, to.X - from.X), length = Math.Sqrt(Math.Pow(to.X - from.X, 2) + Math.Pow(to.Y - from.Y, 2));
        double snapped = Math.Round(angle / (Math.PI / 4)) * (Math.PI / 4);
        return new PointF(from.X + (float)(Math.Cos(snapped) * length), from.Y + (float)(Math.Sin(snapped) * length));
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        Drag drag = _drag;
        _drag = Drag.None;
        _holdTimer.Stop();
        if (drag == Drag.Create) CommitCreating();
        else if (drag == Drag.Text && _textPick is not null) Announce("Text picked. Press Ctrl+C to copy it.");
        // A click (no drag) on a curve that was already chosen puts a new point there.
        else if (drag == Drag.Move && !_changeStarted && _pressOnChosen && _selection is [{ Kind: MarkKind.Spline or MarkKind.Curve } curve]
            && ToPage(e.Location, out _, out PointF at)) AddCurvePoint(curve, at);
        else if (drag is Drag.Move or Drag.Resize or Drag.Rotate) FinishChange();
        else if (drag == Drag.Marquee) FinishMarquee();
        UpdateCursor(e.Location);
    }

    void CommitCreating()
    {
        PageObject? item = _creating;
        _creating = null;
        if (item is null || Document is null || _dragPage < 0) return;
        if (_marking is { } runs)
        {
            _marking = null;
            CommitMarking(item, runs);
            return;
        }
        bool snapped = _snapped;
        _snapped = false;
        bool freeform = item.Kind is MarkKind.Ink || (item.Kind == MarkKind.Redaction && item.RedactShape == RedactShape.Freeform)
            || Tool == EditTool.LiftFreeform;
        bool tooSmall = freeform && !snapped
            ? item.Strokes[0].Count < 3
            : ShapeGeometry.IsLine(item.Kind) ? item.Width + item.Height < 4 : item.Width < 3 || item.Height < 3;
        if (Tool is EditTool.LiftRectangle or EditTool.LiftFreeform)
        {
            if (tooSmall) Invalidate();
            else _ = LiftAsync(_dragPage, item, Tool == EditTool.LiftFreeform);
            return;
        }
        if (item.Pressures.Count > 0 && item.Pressures[0].Count != item.Strokes[0].Count) item.Pressures = [];
        List<PageObject> made = [item];
        if (Tool is EditTool.Highlight or EditTool.Underline or EditTool.Strikeout or EditTool.RedactText)
        {
            // Text tools follow the words between where the drag started and ended, line by line.
            IReadOnlyList<RectangleF>? lines = SelectText?.Invoke(_dragPage, _pressPage, _lastPage);
            if (lines is { Count: > 0 })
            {
                made = [.. lines.Select(line => { PageObject copy = item.Clone(); copy.Id = Guid.NewGuid(); copy.Bounds = line; return copy; })];
                tooSmall = false;
            }
            else if (Tool == EditTool.RedactText) tooSmall = true;
        }
        if (tooSmall) { Invalidate(); return; }
        StartChange();
        Document.Pages[_dragPage].Objects.AddRange(made);
        FinishChange();
        if (Tool is EditTool.Rectangle or EditTool.Ellipse or EditTool.Line or EditTool.Arrow or EditTool.Triangle or EditTool.SemiCircle
            or EditTool.Pie or EditTool.Curve or EditTool.Heart or EditTool.Star or EditTool.Parallelogram)
        {
            Select(_dragPage, [item]);
            FinishTool();
        }
    }

    /// <summary>
    /// Cuts an area of the page's own content out as a picture that can be moved: the area is covered with white
    /// and the picture of it is placed on top, selected, ready to drag. Objects on the page stay where they are.
    /// </summary>
    async Task LiftAsync(int page, PageObject area, bool freeform)
    {
        if (Document is null || page >= Document.Pages.Count) return;
        PageEntry entry = Document.Pages[page];
        RectangleF box = RectangleF.Intersect(area.Bounds, new RectangleF(0, 0, entry.Width, entry.Height));
        if (box.Width < 3 || box.Height < 3) { Invalidate(); return; }
        // About 216 dpi: sharp at the zooms people edit at, small enough to keep in the document.
        float scale = Math.Min(3, 5000f / Math.Max(entry.Width, entry.Height));
        Bitmap? render = RenderForLift is null ? null : await RenderForLift(entry, (int)(entry.Width * scale));
        if (Document is null || page >= Document.Pages.Count || Document.Pages[page] != entry) { render?.Dispose(); return; }
        byte[] picture;
        using (render)
        {
            int width = Math.Max(1, (int)Math.Ceiling(box.Width * scale)), height = Math.Max(1, (int)Math.Ceiling(box.Height * scale));
            using var whole = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(whole))
            {
                g.ScaleTransform(scale, scale);
                g.TranslateTransform(-box.X, -box.Y);
                PagePainter.DrawPaper(g, entry, render);
            }
            using var piece = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(piece))
            {
                if (freeform)
                {
                    // The outline cut smooth, everything outside it see-through.
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    using var outline = new GraphicsPath();
                    outline.AddPolygon([.. area.Strokes[0].Select(p => new PointF((p.X - box.X) * scale, (p.Y - box.Y) * scale))]);
                    using var texture = new TextureBrush(whole);
                    g.FillPath(texture, outline);
                }
                else g.DrawImageUnscaled(whole, 0, 0);
            }
            using var stream = new MemoryStream();
            piece.Save(stream, ImageFormat.Png);
            picture = stream.ToArray();
        }
        const int White = unchecked((int)0xFFFFFFFF);
        var cover = new PageObject { Kind = freeform ? MarkKind.Polygon : MarkKind.Rectangle, FillColor = White, StrokeColor = White, StrokeWidth = 0, Comment = "Lifted area" };
        if (freeform)
        {
            cover.Strokes = [[.. area.Strokes[0]]];
            cover.FitToStrokes();
        }
        else cover.Bounds = box;
        var lifted = new PageObject { Kind = MarkKind.Image, ImageData = picture, Bounds = box };
        // Kept together in the layers as one group; the picture alone is chosen, so dragging it leaves the cover.
        cover.Group = lifted.Group = Guid.NewGuid();
        StartChange();
        entry.Objects.Add(cover);
        entry.Objects.Add(lifted);
        FinishChange();
        FinishTool();
        Select(page, [lifted], withGroups: false);
        Announce("Lifted. Drag it where it should go.");
    }

    /// <summary>Tools that place one object hand back to Select; drawing and marking tools stay on.</summary>
    void FinishTool()
    {
        if (_tool is EditTool.Select or EditTool.Draw or EditTool.Highlight or EditTool.Underline or EditTool.Strikeout or EditTool.SelectText
            or EditTool.RedactRectangle or EditTool.RedactEllipse or EditTool.RedactFreeform or EditTool.RedactText)
            return;
        _tool = EditTool.Select;
        UpdateCursor(null);
        ToolFinished?.Invoke(this, EventArgs.Empty);
    }

    int HandleAt(Point location)
    {
        if (_selection.Count != 1 || _selectionPage < 0) return -1;
        var handles = Handles(_selection[0], _selectionPage);
        for (int i = 0; i < handles.Count; i++)
        {
            RectangleF box = handles[i];
            box.Inflate(Px(3), Px(3));
            if (box.Contains(location)) return i;
        }
        return -1;
    }

    void UpdateCursor(Point? location)
    {
        if (_picking is not null)
        {
            Cursor = Cursors.Cross;
            return;
        }
        Cursor = Tool switch
        {
            EditTool.Hand => Cursors.Hand,
            EditTool.Text or EditTool.Date or EditTool.RedactText or EditTool.SelectText => Cursors.IBeam,
            EditTool.Select => SelectCursor(location),
            _ => Cursors.Cross,
        };
        if (_spaceDown) Cursor = Cursors.Hand;
    }

    Cursor SelectCursor(Point? location)
    {
        if (location is not { } point || Document is null) return Cursors.Default;
        int handle = HandleAt(point);
        if (handle >= 0)
        {
            PageObject item = _selection[0];
            if (ShapeGeometry.IsLine(item.Kind)) return Cursors.SizeAll;
            if (ShapeGeometry.TurnsAsBox(item.Kind) && handle == TurnHandle) return Cursors.Hand;
            // Which way the handle pulls in the object's own frame (a corner always pulls on the diagonal, however
            // wide the box), turned and flipped with the object and its page.
            (int dx, int dy) = handle switch { 0 => (-1, -1), 1 => (0, -1), 2 => (1, -1), 3 => (-1, 0), 4 => (1, 0), 5 => (-1, 1), 6 => (0, 1), _ => (1, 1) };
            if (item.FlipX) dx = -dx;
            if (item.FlipY) dy = -dy;
            double angle = ((Math.Atan2(dy, dx) * 180 / Math.PI + item.Rotation + Document.Pages[_selectionPage].Rotation) % 180 + 180) % 180;
            return angle switch { < 22.5 or >= 157.5 => Cursors.SizeWE, < 67.5 => Cursors.SizeNWSE, < 112.5 => Cursors.SizeNS, _ => Cursors.SizeNESW };
        }
        if (!ToPage(point, out int page, out PointF p)) return Cursors.Default;
        return HitObject(page, p) is not null ? Cursors.SizeAll : FieldAt(page, p) is not null ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (e is HandledMouseEventArgs handled) handled.Handled = true;
        if (ModifierKeys.HasFlag(Keys.Control))
        {
            if (e.Delta > 0) ZoomIn(); else ZoomOut();
            return;
        }
        ScrollBy(-e.Delta * Px(WheelStep) / 120);
    }

    // Picking a colour --------------------------------------------------------------------------

    /// <summary>
    /// Lets the next click on the page choose a colour: a magnifier follows the pointer, a click picks what is
    /// under it, and Escape (or a right-click) gives up. The colour comes from the page as shown, objects and all.
    /// </summary>
    public void PickColor(Action<Color> picked)
    {
        CommitTextEdit();
        _picking = picked;
        _pickAt = null;
        Focus();
        UpdateCursor(null);
        Invalidate();
        Announce("Click the colour to use on the page. Escape cancels.");
    }

    Bitmap PickView()
    {
        // Taken again after a scroll or a zoom, so it always matches what is on screen.
        var of = (AutoScrollPosition, _zoom, ClientSize);
        if (_pickView is not null && _pickViewOf == of) return _pickView;
        _pickViewOf = of;
        _pickView?.Dispose();
        _pickView = new Bitmap(Math.Max(1, ClientSize.Width), Math.Max(1, ClientSize.Height));
        using Graphics g = Graphics.FromImage(_pickView);
        _hideSelection = true;
        try { OnPaint(new PaintEventArgs(g, ClientRectangle)); }
        finally { _hideSelection = false; }
        return _pickView;
    }

    Color PixelAt(Point point)
    {
        Bitmap view = PickView();
        return view.GetPixel(Math.Clamp(point.X, 0, view.Width - 1), Math.Clamp(point.Y, 0, view.Height - 1));
    }

    void FinishPicking(Point point)
    {
        Color color = Color.FromArgb(255, PixelAt(point));
        Action<Color>? picked = _picking;
        CancelPicking();
        picked?.Invoke(color);
        Announce($"Picked {color.R:X2}{color.G:X2}{color.B:X2}");
    }

    void CancelPicking()
    {
        if (_picking is null) return;
        _picking = null;
        _pickAt = null;
        _pickView?.Dispose();
        _pickView = null;
        UpdateCursor(null);
        Invalidate();
    }

    /// <summary>The magnifier: the pixels around the pointer enlarged in a circle ringed in the colour under it.</summary>
    void DrawLoupe(Graphics g)
    {
        if (_pickAt is not { } at || !ClientRectangle.Contains(at)) return;
        const int Span = 11;
        int cell = Px(9), size = Span * cell, gap = Px(20);
        var box = new Rectangle(at.X + gap, at.Y + gap, size, size);
        if (box.Right > ClientSize.Width) box.X = at.X - gap - size;
        if (box.Bottom + Px(28) > ClientSize.Height) box.Y = at.Y - gap - size - Px(28);
        Color color = PixelAt(at);

        GraphicsState state = g.Save();
        using (var circle = new GraphicsPath())
        {
            circle.AddEllipse(box);
            g.SetClip(circle);
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(PickView(), box, new Rectangle(at.X - Span / 2, at.Y - Span / 2, Span, Span), GraphicsUnit.Pixel);
            var middle = new Rectangle(box.X + Span / 2 * cell, box.Y + Span / 2 * cell, cell, cell);
            g.DrawRectangle(Pens.Black, Rectangle.Inflate(middle, 1, 1));
            g.DrawRectangle(Pens.White, middle);
        }
        g.Restore(state);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var ring = new Pen(color, Px(5))) g.DrawEllipse(ring, box);
        using (var edge = new Pen(Theme.Current.Line2, 1)) g.DrawEllipse(edge, Rectangle.Inflate(box, Px(3), Px(3)));
        string hex = $"{color.R:X2}{color.G:X2}{color.B:X2}";
        Size text = TextRenderer.MeasureText(hex, AppFonts.MonoSmall);
        var label = new Rectangle(box.X + (box.Width - text.Width - Px(12)) / 2, box.Bottom + Px(6), text.Width + Px(12), Px(22));
        Draw.Box(g, label, Px(6), Theme.Current.Raised, Theme.Current.Line2);
        Draw.Text(g, hex, AppFonts.MonoSmall, label, Theme.Current.Text, Draw.Centered);
    }

    // Text editing ------------------------------------------------------------------------------

    public void BeginTextEdit(int page, PageObject item, bool isNew = false)
    {
        CommitTextEdit();
        _editing = item;
        _editingIsNew = isNew;
        _selectionPage = page;
        _editorFont = new Font(PagePainter.FontFor(item).FontFamily, Math.Max(1, item.FontSize * PixelsPerPoint), PagePainter.FontFor(item).Style, GraphicsUnit.Pixel);
        _editor = new TextBox
        {
            Multiline = true,
            AcceptsReturn = true,
            AccessibleName = "Text on the page",
            AccessibleDescription = "Escape or Ctrl+Enter finishes.",
            WordWrap = true,
            BorderStyle = BorderStyle.None,
            Font = _editorFont,
            ForeColor = Color.FromArgb(item.StrokeColor),
            BackColor = item.FillColor >> 24 == 0 ? Color.White : Color.FromArgb(255, Color.FromArgb(item.FillColor)),
            Text = item.Text.Replace("\n", Environment.NewLine),
            TextAlign = item.Align switch { TextAlign.Center => HorizontalAlignment.Center, TextAlign.Right => HorizontalAlignment.Right, _ => HorizontalAlignment.Left },
        };
        _editor.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape || (e.KeyCode == Keys.Enter && e.Control)) { e.SuppressKeyPress = true; CommitTextEdit(); Focus(); }
        };
        _editor.TextChanged += (_, _) => PositionEditor();
        _editor.LostFocus += (_, _) => BeginInvoke(() => { if (_editor is not null && !_editor.Focused) CommitTextEdit(); });
        Controls.Add(_editor);
        PositionEditor();
        _editor.Focus();
        _editor.SelectAll();
        Invalidate();
    }

    void PositionEditor()
    {
        if (_editor is null || _editing is null || _selectionPage < 0 || _selectionPage >= _pages.Count) return;
        RectangleF rect = ToScreen(_selectionPage, _editing.Bounds);
        int inset = (int)(2 * PixelsPerPoint);
        int lines = Math.Max(1, _editor.Lines.Length + 1);
        int height = Math.Max((int)rect.Height, lines * _editorFont!.Height + inset * 2);
        _editor.SetBounds((int)rect.X + inset, (int)rect.Y + inset, Math.Max(20, (int)rect.Width - 2 * inset), height);
    }

    public void CommitTextEdit()
    {
        if (_editor is null || _editing is null) return;
        TextBox editor = _editor;
        PageObject item = _editing;
        bool isNew = _editingIsNew;
        _editor = null;
        _editing = null;
        string text = editor.Text.Replace("\r\n", "\n");
        Controls.Remove(editor);
        editor.Dispose();
        _editorFont?.Dispose();
        _editorFont = null;

        int page = _selectionPage;
        if (Document is null || page < 0) return;
        List<PageObject> objects = Document.Pages[page].Objects;
        bool empty = text.Trim().Length == 0;
        if (isNew && empty)
        {
            Select(-1, []);
            return;
        }
        if (empty || text != item.Text)
        {
            StartChange();
            if (empty) objects.Remove(item);
            else
            {
                item.Text = text;
                using Graphics g = CreateGraphics();
                item.Height = Math.Max(item.Height, PagePainter.TextHeight(g, item));
                if (isNew) objects.Add(item);
            }
            FinishChange();
        }
        Select(empty ? -1 : page, empty ? [] : [item]);
    }

    // Keyboard ----------------------------------------------------------------------------------

    protected override bool IsInputKey(Keys keyData) =>
        (keyData & Keys.KeyCode) is Keys.Up or Keys.Down or Keys.Left or Keys.Right or Keys.PageUp or Keys.PageDown or Keys.Home or Keys.End or Keys.Space or Keys.Enter
        || base.IsInputKey(keyData);

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        AccessibilityNotifyClients(AccessibleEvents.DescriptionChange, -1);
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        CancelPicking();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_picking is null) return;
        _pickAt = null;
        Invalidate();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Space && !_spaceDown) { _spaceDown = true; UpdateCursor(null); e.Handled = true; return; }
        bool hasSelection = _selection.Count > 0;
        float nudge = e.Shift ? 10 : 1;
        e.Handled = true;
        if (_picking is not null && e.KeyCode == Keys.Escape) { CancelPicking(); return; }
        if (e.KeyData == (Keys.Control | Keys.Shift | Keys.G)) { UngroupSelection(); return; }
        switch (e.KeyData & ~Keys.Shift)
        {
            case Keys.Delete or Keys.Back when _activePoint >= 0 && _selection is [{ Kind: MarkKind.Spline } curve] && curve.Strokes[0].Count > 2: RemoveCurvePoint(); break;
            case Keys.Delete or Keys.Back when hasSelection: DeleteSelection(); break;
            case Keys.Escape when _textPick is not null: _textPick = null; Invalidate(); break;
            case Keys.Escape: if (_drag == Drag.Create) { _creating = null; _marking = null; _drag = Drag.None; _holdTimer.Stop(); Invalidate(); } else ClearSelection(); break;
            case Keys.Control | Keys.C when _textPick is not null: CopyPickedText(); break;
            case Keys.Left when hasSelection: Nudge(-nudge, 0); break;
            case Keys.Right when hasSelection: Nudge(nudge, 0); break;
            case Keys.Up when hasSelection: Nudge(0, -nudge); break;
            case Keys.Down when hasSelection: Nudge(0, nudge); break;
            case Keys.Enter when Tool is not (EditTool.Select or EditTool.Hand): PlaceWithKeyboard(); break;
            case Keys.Enter when hasSelection && _selection.Count == 1 && _selection[0].Kind == MarkKind.Text: BeginTextEdit(_selectionPage, _selection[0]); break;
            case Keys.Down: ScrollBy(Px(LineStep)); break;
            case Keys.Up: ScrollBy(-Px(LineStep)); break;
            case Keys.PageDown: ScrollBy((int)(ClientSize.Height * 0.9)); break;
            case Keys.PageUp: ScrollBy(-(int)(ClientSize.Height * 0.9)); break;
            case Keys.Home: GoToPage(0); break;
            case Keys.End: GoToPage(_pages.Count - 1); break;
            case Keys.Control | Keys.D when hasSelection: DuplicateSelection(); break;
            case Keys.Control | Keys.C when hasSelection: Copy(); break;
            case Keys.Control | Keys.X when hasSelection: Copy(); DeleteSelection(); break;
            case Keys.Control | Keys.V: Paste(); break;
            case Keys.Control | Keys.G when hasSelection: GroupSelection(); break;
            case Keys.Control | Keys.A when Document is not null && Document.Pages.Count > 0:
                Select(_currentPage, Document.Pages[_currentPage].Objects.Where(o => !o.Hidden));
                break;
            case Keys.Control | Keys.Oemplus or Keys.Control | Keys.Add: ZoomIn(); break;
            case Keys.Control | Keys.OemMinus or Keys.Control | Keys.Subtract: ZoomOut(); break;
            case Keys.Control | Keys.D0 or Keys.Control | Keys.NumPad0: FitWidth(); break;
            default: e.Handled = false; break;
        }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.KeyCode == Keys.Space) { _spaceDown = false; UpdateCursor(PointToClient(MousePosition)); }
    }

    void Nudge(float dx, float dy)
    {
        StartChange();
        foreach (PageObject item in _selection) item.Offset(dx, dy);
        FinishChange();
    }

    void NativeTheme()
    {
        if (IsHandleCreated) Platform.Native.SetScrollbarTheme(Handle, Theme.IsDark);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeTheme();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _renderCancel.Cancel();
            _renderCancel.Dispose();
            _editorFont?.Dispose();
            _pickView?.Dispose();
            _holdTimer.Dispose();
        }
        base.Dispose(disposing);
    }
}
