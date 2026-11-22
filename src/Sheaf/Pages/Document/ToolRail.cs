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

    public ToolRail() : base(SurfaceRole.Panel)
    {
        Borders = AnchorStyles.Right;
        Padding = new Padding(6, 8, 6, 8);
        AccessibleRole = AccessibleRole.ToolBar;
        AccessibleName = "Tools";
    }

    public event EventHandler<EditTool>? ToolChosen;

    public void SetTools(IReadOnlyList<RailTool[]> families)
    {
        SuspendLayout();
        foreach (var (_, button) in _buttons) button.Dispose();
        _buttons.Clear();
        Controls.Clear();
        int y = Padding.Top;
        for (int i = 0; i < families.Count; i++)
        {
            RailTool[] family = families[i];
            if (i == 2)
            {
                var divider = new Divider { Margin = Padding.Empty };
                divider.SetBounds(LogicalToDeviceUnits(10), y + LogicalToDeviceUnits(4), Width - 2 * LogicalToDeviceUnits(10), 1);
                divider.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
                Controls.Add(divider);
                y += LogicalToDeviceUnits(9);
            }
            RailTool tool = ShownOf(family);
            var button = new AppButton("", ButtonKind.Quiet, tool.Icon)
            {
                LogicalHeight = ButtonSize,
                Margin = Padding.Empty,
                TabStop = i == 0,
                HasMore = family.Length > 1,
            };
            button.Location = new Point((Width - LogicalToDeviceUnits(ButtonSize)) / 2, y);
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
            y += LogicalToDeviceUnits(ButtonSize + 4);
        }
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
            menu.Items.Add(new ToolStripLabel("Slide to one and let go") { Font = AppFonts.Small, Padding = new Padding(4, 4, 4, 6), Enabled = true });
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
    }

    public IEnumerable<RailTool> Tools => _buttons.SelectMany(pair => pair.Family);

    public RailTool? FindByKey(Keys key) => Tools.FirstOrDefault(tool => tool.Key == key && key != Keys.None);

    protected override void OnResize(EventArgs eventargs)
    {
        base.OnResize(eventargs);
        foreach (var (_, button) in _buttons) button.Left = (Width - button.Width) / 2;
    }
}
