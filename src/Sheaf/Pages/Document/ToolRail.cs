using Sheaf.Ui;
using Sheaf.Ui.Controls;

namespace Sheaf.Pages.Document;

/// <summary>
/// The column of tool buttons on the left of the canvas; the tool in use is tinted. Related tools share a
/// button (shapes, lines, lifting): it shows the one used last, and clicking it again or right-clicking it opens
/// the others beside it.
/// </summary>
internal sealed class ToolRail : Surface
{
    // About as long as Photoshop waits before a held tool opens its others.
    const int ButtonSize = 36, HoldMs = 250;
    ContextMenuStrip? _flyout;
    readonly List<(RailTool[] Family, AppButton Button)> _buttons = [];
    // The tool each shared button shows, kept across modes and documents' visits to them.
    readonly Dictionary<EditTool, RailTool> _shown = [];
    Divider? _divider;
    // On a short window: the tools that don't fit, offered by a button at the foot of the rail.
    readonly AppButton _overflow = new("", ButtonKind.Quiet, "more") { LogicalHeight = ButtonSize, Margin = Padding.Empty, AccessibleName = "More Tools", Visible = false };
    readonly List<RailTool[]> _hiddenFamilies = [];

    public ToolRail() : base(SurfaceRole.Panel)
    {
        Borders = AnchorStyles.Right;
        Padding = new Padding(6, 8, 6, 8);
        AccessibleRole = AccessibleRole.ToolBar;
        AccessibleName = "Tools";
        Tips.Shared.SetToolTip(_overflow, "More tools");
        _overflow.Click += (_, _) => ShowOverflow();
    }

    /// <summary>
    /// Places the buttons top to bottom: closer together when the rail is short, and when even that is too tall,
    /// the last ones go into a "more" button at the foot.
    /// </summary>
    void LayOut()
    {
        if (_buttons.Count == 0) return;
        int top = Padding.Top, available = ClientSize.Height - Padding.Bottom;
        int gap = LogicalToDeviceUnits(4), dividerSpace = _divider is null ? 0 : LogicalToDeviceUnits(9);
        int size = LogicalToDeviceUnits(ButtonSize);
        if (top + dividerSpace + _buttons.Count * (size + gap) > available)
        {
            size = LogicalToDeviceUnits(30);
            gap = LogicalToDeviceUnits(2);
        }
        int fits = Math.Max(1, (available - top - dividerSpace + gap) / (size + gap));
        bool overflow = fits < _buttons.Count;
        int shown = overflow ? Math.Max(1, fits - 1) : _buttons.Count;
        _hiddenFamilies.Clear();
        int y = top;
        for (int i = 0; i < _buttons.Count; i++)
        {
            var (family, button) = _buttons[i];
            if (i == 2 && _divider is not null)
            {
                _divider.SetBounds(LogicalToDeviceUnits(10), y + LogicalToDeviceUnits(4) - (gap == LogicalToDeviceUnits(2) ? LogicalToDeviceUnits(2) : 0), Width - 2 * LogicalToDeviceUnits(10), 1);
                y += dividerSpace;
            }
            button.Visible = i < shown;
            if (i >= shown)
            {
                _hiddenFamilies.Add(family);
                continue;
            }
            button.SetBounds((Width - size) / 2, y, size, size);
            y += size + gap;
        }
        _overflow.Visible = overflow;
        if (overflow)
        {
            _overflow.SetBounds((Width - size) / 2, y, size, size);
            _overflow.Selected = _hiddenFamilies.Any(family => _buttons.Any(pair => pair.Family == family && pair.Button.Selected));
        }
    }

    /// <summary>The tools that didn't fit on the rail, in a menu beside it.</summary>
    void ShowOverflow()
    {
        var menu = Menus.Create();
        foreach (RailTool[] family in _hiddenFamilies)
        {
            if (menu.Items.Count > 0) menu.Items.Add(new ToolStripSeparator());
            foreach (RailTool tool in family)
                menu.Items.Add(Menus.Item(tool.Name, () => ToolChosen?.Invoke(this, tool.Tool), KeyName(tool.Key),
                    isChecked: _buttons.Any(pair => pair.Family == family && pair.Button.Selected) && ShownOf(family) == tool));
        }
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        Menus.Show(menu, _overflow, new Point(_overflow.Width + LogicalToDeviceUnits(6), 0));
    }

    public event EventHandler<EditTool>? ToolChosen;

    public void SetTools(IReadOnlyList<RailTool[]> families)
    {
        SuspendLayout();
        foreach (var (_, button) in _buttons) button.Dispose();
        _buttons.Clear();
        Controls.Clear();
        _divider?.Dispose();
        _divider = null;
        for (int i = 0; i < families.Count; i++)
        {
            RailTool[] family = families[i];
            if (i == 2)
            {
                _divider = new Divider { Margin = Padding.Empty };
                _divider.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
                Controls.Add(_divider);
            }
            RailTool tool = ShownOf(family);
            var button = new AppButton("", ButtonKind.Quiet, tool.Icon)
            {
                LogicalHeight = ButtonSize,
                Margin = Padding.Empty,
                TabStop = i == 0,
                HasMore = family.Length > 1,
            };
            // Holding the button down opens the others, as in Photoshop; the click that ends the hold does nothing more.
            bool held = false;
            var hold = new System.Windows.Forms.Timer { Interval = HoldMs };
            hold.Tick += (_, _) =>
            {
                hold.Stop();
                held = true;
                ShowFamily(family, button, held: true);
            };
            if (family.Length > 1)
            {
                button.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) { held = false; hold.Start(); } };
                button.MouseLeave += (_, _) => { if (!held) hold.Stop(); };
                // Still holding, the pointer slides over the others; letting go over one picks it, as in Photoshop.
                button.MouseMove += (_, e) =>
                {
                    if (held && _flyout is { Visible: true } menu) Menus.GlideTo(menu, ItemUnder(menu, button.PointToScreen(e.Location)));
                };
                button.MouseUp += (_, e) =>
                {
                    hold.Stop();
                    if (held && _flyout is { Visible: true } menu && ItemUnder(menu, button.PointToScreen(e.Location)) is { } item)
                    {
                        menu.Close();
                        item.PerformClick();
                    }
                };
            }
            button.Disposed += (_, _) => hold.Dispose();
            button.Click += (_, _) =>
            {
                if (held) { held = false; return; }
                if (family.Length > 1 && button.Selected) ShowFamily(family, button);
                else ToolChosen?.Invoke(this, ShownOf(family).Tool);
            };
            button.MouseUp += (_, e) => { if (e.Button == MouseButtons.Right && family.Length > 1) ShowFamily(family, button); };
            Describe(button, family, tool);
            Controls.Add(button);
            _buttons.Add((family, button));
        }
        Controls.Add(_overflow);
        LayOut();
        ResumeLayout(true);
    }

    RailTool ShownOf(RailTool[] family) => _shown.GetValueOrDefault(family[0].Tool) ?? family[0];

    static string? KeyName(Keys key) => key == Keys.None ? null : key.ToString();

    static void Describe(AppButton button, RailTool[] family, RailTool tool)
    {
        button.Icon = tool.Icon;
        button.AccessibleName = tool.Name;
        button.AccessibleDescription = family.Length > 1 ? "Press again for " + string.Join(", ", family.Where(other => other != tool).Select(other => other.Name.ToLowerInvariant())) : null;
        string name = KeyName(tool.Key) is { } key ? $"{tool.Name} ({key})" : tool.Name;
        Tips.Shared.SetToolTip(button, family.Length > 1 ? $"{name}\nHold, click again or right-click for more" : name);
        button.Invalidate();
    }

    static ToolStripItem? ItemUnder(ContextMenuStrip menu, Point screen) =>
        menu.Items.OfType<ToolStripMenuItem>().FirstOrDefault(item => item.Enabled && menu.RectangleToScreen(item.Bounds).Contains(screen));

    /// <summary>The tools that share a button, in a menu beside it.</summary>
    void ShowFamily(RailTool[] family, AppButton button, bool held = false)
    {
        var menu = Menus.Create();
        _flyout = menu;
        menu.Closed += (_, _) => { if (_flyout == menu) _flyout = null; };
        RailTool shown = ShownOf(family);
        foreach (RailTool tool in family)
            menu.Items.Add(Menus.Item(tool.Name, () => ToolChosen?.Invoke(this, tool.Tool), KeyName(tool.Key), isChecked: tool == shown));
        if (held)
        {
            // Opened by holding: say that letting go over a tool picks it.
            menu.Items.Add(new ToolStripSeparator());
            var hint = new Menus.SlideHint("Slide to one and let go");
            menu.Items.Add(hint);
            menu.Opened += (_, _) => hint.Start();
        }
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        Menus.Show(menu, button, new Point(button.Width + LogicalToDeviceUnits(6), 0));
    }

    public void Show(EditTool tool)
    {
        foreach (var (family, button) in _buttons)
        {
            RailTool? match = family.FirstOrDefault(railTool => railTool.Tool == tool);
            if (match is not null && family.Length > 1 && ShownOf(family) != match)
            {
                _shown[family[0].Tool] = match;
                Describe(button, family, match);
            }
            button.Selected = match is not null;
        }
        if (_overflow.Visible) _overflow.Selected = _hiddenFamilies.Any(family => family.Any(railTool => railTool.Tool == tool));
    }

    public IEnumerable<RailTool> Tools => _buttons.SelectMany(pair => pair.Family);

    public RailTool? FindByKey(Keys key) => Tools.FirstOrDefault(tool => tool.Key == key && key != Keys.None);

    protected override void OnResize(EventArgs eventargs)
    {
        base.OnResize(eventargs);
        LayOut();
    }
}
