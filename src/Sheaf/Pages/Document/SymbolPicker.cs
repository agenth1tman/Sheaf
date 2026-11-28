using System.Drawing.Text;
using Sheaf.Core.Editing;
using Sheaf.Ui;
using Sheaf.Ui.Controls;

namespace Sheaf.Pages.Document;

/// <summary>
/// The symbols maths and science text needs, in a grid under a heading per kind: Greek letters, operators, sets and
/// logic, arrows, and units. A click types one into the text box; they are drawn in <see cref="MathText.SymbolFont"/>,
/// which has them all, on screen and in the saved PDF.
/// </summary>
internal sealed class SymbolPicker : Control, IItemHost
{
    const int Columns = 12, Cell = 30, Inset = 8, LabelHeight = 22;

    static readonly (string Heading, string Symbols)[] Groups =
    [
        ("Greek", "αβγδεζηθικλμνξπρστυφχψωΓΔΘΛΞΠΣΦΨΩ"),
        ("Operators", "±∓×÷·≈≠≡≤≥≪≫∝∞√∛∑∏∫∮∂∇∆"),
        ("Sets and logic", "∈∉⊂⊃⊆∪∩∀∃∴∵ℝℕℤℚℂ"),
        ("Arrows", "→←↔↑↓⇒⇐⇔"),
        ("Units and geometry", "°′″ℏÅµΩ∠⊥∥≅∼½¼¾"),
    ];

    static readonly string[] Names = Groups.SelectMany(group => group.Symbols.Select(c => c.ToString())).ToArray();

    readonly Font _font;
    int _hover = -1;

    SymbolPicker()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true);
        _font = new Font(MathText.SymbolFont, 12f);
        int height = Inset * 2 + Groups.Sum(group => LabelHeight + Rows(group.Symbols.Length) * Cell);
        Size = new Size(LogicalToDeviceUnits(Inset * 2 + Columns * Cell), LogicalToDeviceUnits(height));
        AccessibleRole = AccessibleRole.List;
        AccessibleName = "Symbols";
        TabStop = true;
    }

    event EventHandler<string>? Picked;

    /// <summary>
    /// Shows the grid under <paramref name="owner"/>; a click on a symbol passes it to <paramref name="picked"/>, and
    /// <paramref name="closed"/> runs once the grid has gone.
    /// </summary>
    public static void Show(Control owner, Action<string> picked, Action? closed = null)
    {
        var menu = Menus.Create();
        var grid = new SymbolPicker();
        grid.Picked += (_, symbol) => { menu.Close(); picked(symbol); };
        menu.Items.Add(new ToolStripControlHost(grid) { AutoSize = false, Size = grid.Size, Padding = Padding.Empty, Margin = Padding.Empty });
        menu.Closed += (_, _) => owner.BeginInvoke(() =>
        {
            menu.Dispose();
            closed?.Invoke();
        });
        Menus.Show(menu, owner, Menus.Below(owner));
        grid.Focus();
    }

    static int Rows(int count) => (count + Columns - 1) / Columns;

    public override Size GetPreferredSize(Size proposedSize) => Size;

    /// <summary>Where a symbol's cell is: in the rows under its group's heading.</summary>
    Rectangle CellAt(int index)
    {
        int top = Inset, start = 0;
        foreach (var (_, symbols) in Groups)
        {
            top += LabelHeight;
            if (index < start + symbols.Length)
            {
                int i = index - start;
                return new Rectangle(LogicalToDeviceUnits(Inset + i % Columns * Cell), LogicalToDeviceUnits(top + i / Columns * Cell), LogicalToDeviceUnits(Cell), LogicalToDeviceUnits(Cell));
            }
            top += Rows(symbols.Length) * Cell;
            start += symbols.Length;
        }
        return Rectangle.Empty;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(MenuChrome.Background);
        int top = Inset;
        foreach (var (heading, symbols) in Groups)
        {
            Draw.Text(g, heading, AppFonts.Small, new Rectangle(LogicalToDeviceUnits(Inset + 2), LogicalToDeviceUnits(top), Width, LogicalToDeviceUnits(LabelHeight)), Theme.Current.Text3);
            top += LabelHeight + Rows(symbols.Length) * Cell;
        }
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var ink = new SolidBrush(Theme.Current.Text);
        using var centred = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        for (int i = 0; i < Names.Length; i++)
        {
            Rectangle cell = CellAt(i);
            if (i == _hover) Draw.Box(g, Rectangle.Inflate(cell, -LogicalToDeviceUnits(1), -LogicalToDeviceUnits(1)), LogicalToDeviceUnits(5), Theme.Current.Hover);
            g.DrawString(Names[i], _font, ink, cell, centred);
        }
        if (Focused && ShowFocusCues && _hover >= 0) Draw.FocusRing(g, CellAt(_hover), LogicalToDeviceUnits(5), LogicalToDeviceUnits(2));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int index = Enumerable.Range(0, Names.Length).FirstOrDefault(i => CellAt(i).Contains(e.Location), -1);
        if (index == _hover) return;
        _hover = index;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = -1;
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (_hover >= 0) Picked?.Invoke(this, Names[_hover]);
    }

    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        int current = Math.Max(0, _hover);
        int next = e.KeyCode switch
        {
            Keys.Left => current - 1,
            Keys.Right => current + 1,
            Keys.Up => current - Columns,
            Keys.Down => current + Columns,
            _ => int.MinValue,
        };
        if (next != int.MinValue)
        {
            _hover = Math.Clamp(next, 0, Names.Length - 1);
            ItemHostAccessible.ItemFocused(this, _hover);
            Invalidate();
            e.Handled = true;
        }
        else if (e.KeyCode is Keys.Enter or Keys.Space && _hover >= 0)
        {
            Picked?.Invoke(this, Names[_hover]);
            e.Handled = true;
        }
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new ItemHostAccessible(this, this, AccessibleRole.ListItem);

    int IItemHost.ItemCount => Names.Length;
    string IItemHost.ItemName(int index) => Names[index];
    Rectangle IItemHost.ItemBounds(int index) => CellAt(index);
    bool IItemHost.IsItemSelected(int index) => index == _hover;
    int IItemHost.FocusedItem => _hover;
    void IItemHost.ActivateItem(int index) => Picked?.Invoke(this, Names[index]);

    protected override void Dispose(bool disposing)
    {
        if (disposing) _font.Dispose();
        base.Dispose(disposing);
    }
}
