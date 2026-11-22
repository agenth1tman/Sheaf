using Sheaf.Core.Editing;
using Sheaf.Ui;
using Sheaf.Ui.Controls;

namespace Sheaf.Pages.Document;

/// <summary>
/// The pens side by side, like the pen tray of a notes app: each shows a short stroke drawn in its own style and
/// the colour in use, so the difference is seen before drawing. The pen in use sits raised with an accent bar.
/// </summary>
internal sealed class PenPicker : Control, IItemHost
{
    const int TileWidth = 38, TileHeight = 30, Gap = 2;

    static readonly (PenKind Pen, string Name, string About)[] Pens =
    [
        (PenKind.Pen, "Pen", "A steady line"),
        (PenKind.Pencil, "Pencil", "Grainy, like graphite"),
        (PenKind.Marker, "Marker", "Bold and solid"),
        (PenKind.Highlighter, "Highlighter", "Wide and see-through; follows the lines of text it crosses"),
        (PenKind.Brush, "Brush", "Swells and tapers"),
        (PenKind.Crayon, "Crayon", "Waxy, with the paper showing through"),
    ];

    // The same id every time, so the pencil's and crayon's grain in the samples doesn't change as they repaint.
    static readonly Guid SampleId = new("5b4f0f3e-2b8a-4f8e-9c55-31a4d6c0a1e7");

    readonly Fade _hover;
    int _hoverIndex = -1;
    PenKind _value;
    Color _ink = Color.Black;

    public PenPicker()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        _hover = new Fade(this);
        TabStop = true;
        AccessibleName = "Pen type";
        AccessibleRole = AccessibleRole.List;
        Margin = new Padding(0, 1, 6, 0);
        Size = new Size(Pens.Length * (TileWidth + Gap), TileHeight + 2);
    }

    public event EventHandler? Changed;

    public PenKind Value
    {
        get => _value;
        set
        {
            if (_value == value) return;
            _value = value;
            Invalidate();
        }
    }

    /// <summary>The colour the samples are drawn in: the colour in use.</summary>
    public Color InkColor
    {
        get => _ink;
        set
        {
            if (_ink == value) return;
            _ink = value;
            Invalidate();
        }
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        Size = new Size(Pens.Length * LogicalToDeviceUnits(TileWidth + Gap), LogicalToDeviceUnits(TileHeight + 2));
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Size = new Size(Pens.Length * LogicalToDeviceUnits(TileWidth + Gap), LogicalToDeviceUnits(TileHeight + 2));
    }

    Rectangle Tile(int index) => new(index * LogicalToDeviceUnits(TileWidth + Gap), LogicalToDeviceUnits(1), LogicalToDeviceUnits(TileWidth), LogicalToDeviceUnits(TileHeight));

    int IndexAt(Point point)
    {
        for (int i = 0; i < Pens.Length; i++)
            if (Tile(i).Contains(point)) return i;
        return -1;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        Palette p = Theme.Current;
        g.Clear(Draw.BackgroundOf(this));
        int radius = LogicalToDeviceUnits(Theme.Radius);
        for (int i = 0; i < Pens.Length; i++)
        {
            Rectangle tile = Tile(i);
            bool chosen = Pens[i].Pen == _value;
            if (chosen) Draw.Box(g, tile, radius, p.Active);
            else if (i == _hoverIndex) Draw.Box(g, tile, radius, Theme.Blend(Color.FromArgb(0, p.Hover), p.Hover, _hover.Value));
            DrawSample(g, Pens[i].Pen, Rectangle.Inflate(tile, -LogicalToDeviceUnits(7), -LogicalToDeviceUnits(8)));
            if (chosen)
            {
                // The accent bar under the pen in use, as a notes app marks the pen in hand.
                int bar = LogicalToDeviceUnits(2), inset = LogicalToDeviceUnits(10);
                Draw.Box(g, new Rectangle(tile.X + inset, tile.Bottom - bar - LogicalToDeviceUnits(2), tile.Width - 2 * inset, bar), bar / 2f, p.Accent);
            }
            if (Focused && ShowFocusCues && chosen) Draw.FocusRing(g, tile, radius, LogicalToDeviceUnits(2));
        }
    }

    /// <summary>A little wave drawn with the pen itself, through the same passes it draws on the page.</summary>
    void DrawSample(Graphics g, PenKind pen, Rectangle area)
    {
        var points = new List<PointF>();
        for (int i = 0; i <= 24; i++)
        {
            float t = i / 24f;
            points.Add(new PointF(area.X + t * area.Width, area.Y + area.Height / 2f - (float)Math.Sin(t * Math.PI * 1.6) * area.Height * 0.38f));
        }
        // A dark ink would make the highlighter sample a grey smudge; it shows in yellow until a colour is chosen.
        Color ink = pen == PenKind.Highlighter && _ink.GetBrightness() < 0.25f ? Theme.Hex(0xFACC15) : _ink;
        float width = LogicalToDeviceUnits(2) * (pen == PenKind.Highlighter ? 0.75f : 1);
        var sample = new PageObject { Id = SampleId, Kind = MarkKind.Ink, Pen = pen, StrokeColor = ink.ToArgb(), StrokeWidth = width, Strokes = [points] };
        var state = g.Save();
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        PagePainter.DrawPieces(g, sample, ShapeGeometry.InkPieces(sample));
        g.Restore(state);
    }

    void Choose(int index)
    {
        if (index < 0 || index >= Pens.Length) return;
        bool changed = Pens[index].Pen != _value;
        Value = Pens[index].Pen;
        ItemHostAccessible.ItemFocused(this, index);
        if (changed) Changed?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int index = IndexAt(e.Location);
        if (index == _hoverIndex) return;
        _hoverIndex = index;
        _hover.Set(0);
        _hover.To(index >= 0 ? 1 : 0);
        Tips.Shared.SetToolTip(this, index >= 0 ? $"{Pens[index].Name}\n{Pens[index].About}" : null);
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hoverIndex = -1;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left) Choose(IndexAt(e.Location));
    }

    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Left or Keys.Right || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        int current = Array.FindIndex(Pens, entry => entry.Pen == _value);
        if (e.KeyCode == Keys.Left) Choose(Math.Max(0, current - 1));
        else if (e.KeyCode == Keys.Right) Choose(Math.Min(Pens.Length - 1, current + 1));
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    protected override AccessibleObject CreateAccessibilityInstance() => new ItemHostAccessible(this, this, AccessibleRole.ListItem);

    int IItemHost.ItemCount => Pens.Length;
    string IItemHost.ItemName(int index) => $"{Pens[index].Name}: {Pens[index].About}";
    Rectangle IItemHost.ItemBounds(int index) => Tile(index);
    bool IItemHost.IsItemSelected(int index) => Pens[index].Pen == _value;
    int IItemHost.FocusedItem => Array.FindIndex(Pens, entry => entry.Pen == _value);
    void IItemHost.ActivateItem(int index) => Choose(index);
}
