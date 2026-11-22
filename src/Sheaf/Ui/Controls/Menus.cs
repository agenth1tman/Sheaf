using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Sheaf.Ui.Controls;

/// <summary>Menus in the theme's colours, with Geist type and rounded highlight rows.</summary>
internal static class Menus
{
    public static ContextMenuStrip Create()
    {
        var menu = new ContextMenuStrip
        {
            Renderer = new MenuRenderer(),
            Font = AppFonts.Body,
            Padding = new Padding(4),
        };
        menu.Opening += (_, _) => Fit(menu);
        MenuChrome.Attach(menu);
        return menu;
    }

    /// <summary>
    /// Readies a menu (and its submenus) to show: the theme's colours, and a column on the left only when an item
    /// has a picture or a tick, so the text of a plain menu starts near its edge.
    /// </summary>
    static void Fit(ToolStripDropDown menu)
    {
        menu.BackColor = MenuChrome.Background;
        var entries = menu.Items.OfType<ToolStripMenuItem>().ToList();
        if (menu is ToolStripDropDownMenu list)
        {
            // A menu holding a control (a slider, the colours) leaves the column out, so the control isn't pushed in
            // from the left; the control itself shows what is chosen.
            bool hosts = menu.Items.OfType<ToolStripControlHost>().Any();
            bool pictures = !hosts && entries.Any(item => item.Image is not null);
            list.ShowImageMargin = pictures;
            list.ShowCheckMargin = !hosts && !pictures && entries.Any(item => item.Checked || item.CheckOnClick);
        }
        foreach (ToolStripMenuItem item in entries.Where(item => item.HasDropDownItems))
        {
            ToolStripDropDown submenu = item.DropDown;
            submenu.Renderer = menu.Renderer;
            submenu.Font = menu.Font;
            submenu.Padding = menu.Padding;
            MenuChrome.Attach(submenu);
            Fit(submenu);
        }
    }

    static readonly ConditionalWeakTable<Control, StrongBox<long>> ClosedByOwner = [];

    /// <summary>
    /// Shows a menu at a point of its owner with a quick fade, as Windows' own menus open. A press on the owner
    /// while its menu is open closes the menu: that press (which closes it) doesn't open it again.
    /// </summary>
    public static void Show(ContextMenuStrip menu, Control owner, Point location)
    {
        if (ClosedByOwner.TryGetValue(owner, out StrongBox<long>? closed) && Environment.TickCount64 - closed.Value < 400)
        {
            closed.Value = 0;
            menu.Dispose();
            return;
        }
        menu.Closed += (_, e) =>
        {
            if (e.CloseReason == ToolStripDropDownCloseReason.AppClicked && !owner.IsDisposed && owner.ClientRectangle.Contains(owner.PointToClient(Cursor.Position)))
                ClosedByOwner.AddOrUpdate(owner, new StrongBox<long>(Environment.TickCount64));
        };
        if (Motion.Enabled)
        {
            menu.Opacity = 0;
            MenuChrome.SetOpacity(menu, 0);
            menu.Opened += Appear;
        }
        menu.Show(owner, location);

        void Appear(object? sender, EventArgs e)
        {
            menu.Opened -= Appear;
            Motion.Run(100, t =>
            {
                if (menu.IsDisposed || !menu.Visible) return;
                menu.Opacity = t;
                MenuChrome.SetOpacity(menu, t);
            }, Easing.Standard, () =>
            {
                if (menu.IsDisposed) return;
                menu.Opacity = 1;
                MenuChrome.SetOpacity(menu, 1);
            });
        }
    }

    /// <summary>A highlight that glides from row to row of a menu, and how much of it shows.</summary>
    sealed class Glide
    {
        public RectangleF At;
        public Rectangle Target;
        public double Shown;
        public Tween? Move;
    }

    static readonly ConditionalWeakTable<ToolStrip, Glide> Glides = [];

    /// <summary>
    /// Moves the gliding highlight to a row (or fades it out for none): for a flyout opened by holding its button,
    /// it shows which tool letting go will pick, following the pointer as it slides.
    /// </summary>
    public static void GlideTo(ToolStripDropDown menu, ToolStripItem? item)
    {
        Glide glide = Glides.GetOrCreateValue(menu);
        if (item is null)
        {
            if (glide.Target.IsEmpty) return;
            glide.Target = Rectangle.Empty;
            double from = glide.Shown;
            Motion.Run(90, t => { glide.Shown = from * (1 - t); menu.Invalidate(); });
            return;
        }
        int inset = menu.LogicalToDeviceUnits(2);
        var target = new Rectangle(item.Bounds.X + inset, item.Bounds.Y, item.Bounds.Width - 2 * inset, item.Bounds.Height);
        if (target == glide.Target) return;
        bool appearing = glide.Target.IsEmpty;
        glide.Target = target;
        glide.Move?.Cancel();
        if (appearing)
        {
            glide.At = target;
            double from = glide.Shown;
            glide.Move = Motion.Run(110, t => { glide.Shown = from + (1 - from) * t; menu.Invalidate(); });
            return;
        }
        RectangleF start = glide.At;
        glide.Move = Motion.Run(130, t =>
        {
            float f = (float)t;
            glide.At = new RectangleF(start.X + (target.X - start.X) * f, start.Y + (target.Y - start.Y) * f, start.Width + (target.Width - start.Width) * f, start.Height + (target.Height - start.Height) * f);
            glide.Shown = 1;
            menu.Invalidate();
        }, Easing.Out);
    }

    static bool Gliding(ToolStrip? strip) => strip is not null && Glides.TryGetValue(strip, out Glide? glide) && glide.Shown > 0;

    /// <summary>Just under a button, where its menu opens.</summary>
    public static Point Below(Control owner) => new(0, owner.Height + owner.LogicalToDeviceUnits(4));

    public static ToolStripMenuItem Item(string text, Action action, string? shortcut = null, bool isChecked = false)
    {
        var item = new ToolStripMenuItem(text) { ShortcutKeyDisplayString = shortcut, Checked = isChecked, Padding = new Padding(4, 7, 8, 7) };
        item.Click += (_, _) => action();
        return item;
    }

    sealed class MenuRenderer() : ToolStripProfessionalRenderer(new MenuColors())
    {
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            e.Graphics.Clear(MenuChrome.Background);
            if (!Glides.TryGetValue(e.ToolStrip, out Glide? glide) || glide.Shown <= 0) return;
            // The gliding highlight: an accent tint with a bar at its left, under the row letting go would pick.
            int alpha = (int)Math.Round(255 * Math.Clamp(glide.Shown, 0, 1));
            Rectangle at = Rectangle.Round(glide.At);
            Draw.Box(e.Graphics, at, e.ToolStrip.LogicalToDeviceUnits(5), Color.FromArgb(alpha * Theme.Current.AccentSoft.A / 255, Theme.Current.AccentSoft));
            int bar = e.ToolStrip.LogicalToDeviceUnits(3), inset = e.ToolStrip.LogicalToDeviceUnits(8);
            Draw.Box(e.Graphics, new Rectangle(at.X + e.ToolStrip.LogicalToDeviceUnits(3), at.Y + inset, bar, at.Height - 2 * inset), bar / 2f, Color.FromArgb(alpha, Theme.Current.Accent));
        }

        // The border is the frame's (MenuChrome), drawn smooth and round behind the menu; a submenu without one
        // still gets a plain edge.
        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            if (e.ToolStrip is ToolStripDropDown drop && MenuChrome.Has(drop)) return;
            using var pen = new Pen(Theme.Current.Line2);
            e.Graphics.DrawRectangle(pen, 0, 0, e.AffectedBounds.Width - 1, e.AffectedBounds.Height - 1);
        }

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled || Gliding(e.ToolStrip)) return;
            int inset = e.Item.Owner?.LogicalToDeviceUnits(2) ?? 2;
            var bounds = new Rectangle(inset, 0, e.Item.Width - 2 * inset, e.Item.Height);
            Draw.Box(e.Graphics, bounds, e.Item.Owner?.LogicalToDeviceUnits(5) ?? 5, Theme.IsDark ? Theme.Current.Active : Theme.Current.Hover);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = !e.Item.Enabled ? Theme.Current.Disabled
                : e.Item is ToolStripLabel ? Theme.Current.Text3
                : e.Item is ToolStripMenuItem { ShortcutKeyDisplayString: { } shortcut } && e.Text == shortcut ? Theme.Current.Text3
                : Theme.Current.Text;
            // Centred in the row's full height, rather than where the menu's layout puts the line.
            Rectangle box = e.TextRectangle;
            e.TextRectangle = new Rectangle(box.X, 0, box.Width, e.Item.Height);
            e.TextFormat = (e.TextFormat & ~TextFormatFlags.Bottom) | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            Rectangle box = e.ImageRectangle;
            int size = Math.Min(box.Width, box.Height) * 7 / 8;
            Icons.Draw(e.Graphics, "check", new Rectangle(box.X + (box.Width - size) / 2, box.Y + (box.Height - size) / 2, size, size), Theme.Current.AccentText, 2.2f);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            using var pen = new Pen(Theme.Current.Line);
            int y = e.Item.Height / 2, inset = e.Item.Owner?.LogicalToDeviceUnits(10) ?? 10;
            e.Graphics.DrawLine(pen, inset, y, e.Item.Width - inset, y);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            // A chevron, like the rest of the app, rather than the classic solid triangle.
            Rectangle box = e.ArrowRectangle;
            int size = Math.Min(box.Height, e.Item?.Owner?.LogicalToDeviceUnits(12) ?? 12);
            Icons.Draw(e.Graphics, "chevron-right", new Rectangle(box.X + (box.Width - size) / 2, box.Y + (box.Height - size) / 2, size, size), Theme.Current.Text2, 2f);
        }
    }

    sealed class MenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Theme.Current.Panel;
        public override Color MenuBorder => Theme.Current.Line2;
        public override Color MenuItemBorder => Color.Transparent;
        public override Color MenuItemSelected => Theme.Current.Hover;
        public override Color SeparatorDark => Theme.Current.Line;
        public override Color SeparatorLight => Theme.Current.Line;
        public override Color ImageMarginGradientBegin => Theme.Current.Panel;
        public override Color ImageMarginGradientMiddle => Theme.Current.Panel;
        public override Color ImageMarginGradientEnd => Theme.Current.Panel;
    }
}

/// <summary>A button that shows the current choice and opens a menu of the others.</summary>
internal sealed class DropDown<T> : AppButton where T : notnull
{
    (string Label, T Value)[] _options;
    T _value;

    public DropDown(string name, params (string Label, T Value)[] options) : base(options[0].Label, ButtonKind.Secondary)
    {
        _options = options;
        _value = options[0].Value;
        AccessibleName = name;
        Chevron = true;
        Click += (_, _) => Open();
    }

    public event EventHandler? Changed;

    /// <summary>Replaces the choices (the value stays when it is still one of them).</summary>
    public void SetOptions(params (string Label, T Value)[] options)
    {
        _options = options;
        Value = _value;
    }

    public T Value
    {
        get => _value;
        set
        {
            int index = Array.FindIndex(_options, option => EqualityComparer<T>.Default.Equals(option.Value, value));
            if (index < 0) return;
            _value = value;
            Text = _options[index].Label;
        }
    }

    void Open()
    {
        var menu = Menus.Create();
        foreach (var (label, value) in _options)
            menu.Items.Add(Menus.Item(label, () =>
            {
                bool changed = !EqualityComparer<T>.Default.Equals(_value, value);
                Value = value;
                if (changed) Changed?.Invoke(this, EventArgs.Empty);
            }, isChecked: EqualityComparer<T>.Default.Equals(_value, value)));
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        Menus.Show(menu, this, Menus.Below(this));
    }
}

/// <summary>
/// A toolbar button that shows a number ("2 pt", "75 %") and opens a slider to set it, with presets below.
/// The value changes as the slider moves; everything done while it is open shares one <see cref="Session"/>, so
/// it can be undone as one step.
/// </summary>
internal sealed class SliderDrop : AppButton
{
    readonly float _maximum, _step;
    readonly Func<float, string> _format;
    readonly float[] _presets;
    float _value;

    public SliderDrop(string name, float minimum, float maximum, float step, Func<float, string> format, params float[] presets) : base(format(minimum), ButtonKind.Secondary)
    {
        Unit = format(maximum).Split(' ').LastOrDefault(part => part is "pt" or "%") ?? "";
        Minimum = minimum;
        _maximum = maximum;
        _step = step;
        _format = format;
        _presets = presets;
        _value = minimum;
        AccessibleName = name;
        Chevron = true;
        Click += (_, _) => Open();
    }

    public event EventHandler? Changed;

    /// <summary>The unit typed values are in ("pt" or "%"), shown beside the box in the open menu.</summary>
    public string Unit { get; }

    /// <summary>The lowest value offered; it can rise for objects that need a little (a line can't be zero wide).</summary>
    public float Minimum { get; set; }

    /// <summary>Set while the slider is open; changes made in one opening carry the same object.</summary>
    public object? Session { get; private set; }

    public float Value
    {
        get => _value;
        set
        {
            _value = Math.Clamp(value, Minimum, _maximum);
            Text = _format(_value);
            AccessibleDescription = Text;
        }
    }

    void Set(float value)
    {
        float before = _value;
        Value = value;
        if (_value != before) Changed?.Invoke(this, EventArgs.Empty);
    }

    void Open()
    {
        Session = new object();
        var menu = Menus.Create();
        menu.ShowImageMargin = false;
        int steps = (int)Math.Round((_maximum - Minimum) / _step);
        var panel = new SliderPanel(AccessibleName ?? "", steps, (int)Math.Round((_value - Minimum) / _step), Unit, index => Minimum + index * _step);
        panel.ValueChanged += (_, index) => Set(Minimum + index * _step);
        panel.Typed += (_, value) => Set((float)(Math.Round(value / _step) * _step));
        menu.Items.Add(new ToolStripControlHost(panel) { AutoSize = false, Size = panel.Size, Padding = Padding.Empty, Margin = Padding.Empty });
        var presets = _presets.Where(preset => preset >= Minimum && preset <= _maximum).ToList();
        if (presets.Count > 0) menu.Items.Add(new ToolStripSeparator());
        foreach (float preset in presets)
            menu.Items.Add(Menus.Item(_format(preset), () => Set(preset), isChecked: Math.Abs(preset - _value) < _step / 2));
        menu.Closed += (_, _) =>
        {
            Session = null;
            BeginInvoke(menu.Dispose);
        };
        Menus.Show(menu, this, Menus.Below(this));
    }

    /// <summary>The slider in the open menu: its name, a box to type the value in, and the slider itself.</summary>
    sealed class SliderPanel : Panel
    {
        readonly Slider _slider;
        readonly InputBox _box = new();
        readonly Func<int, float> _valueAt;
        readonly string _name, _unit;
        bool _syncing;

        public SliderPanel(string name, int steps, int value, string unit, Func<int, float> valueAt)
        {
            _name = name;
            _unit = unit;
            _valueAt = valueAt;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            Size = new Size(LogicalToDeviceUnits(248), LogicalToDeviceUnits(72));
            _slider = new Slider { Minimum = 0, Maximum = steps, AccessibleName = name };
            _slider.SetBounds(LogicalToDeviceUnits(10), LogicalToDeviceUnits(40), Width - LogicalToDeviceUnits(20), LogicalToDeviceUnits(26));
            _slider.Value = value;
            _slider.ValueChanged += (_, _) =>
            {
                ShowValue();
                ValueChanged?.Invoke(this, _slider.Value);
            };
            int unitWidth = unit.Length == 0 ? 0 : LogicalToDeviceUnits(22);
            _box.Caption = $"{name}, typed";
            _box.Box.Font = AppFonts.MonoSmall;
            _box.Box.TextAlign = HorizontalAlignment.Right;
            _box.Box.MaxLength = 6;
            _box.SetBounds(Width - LogicalToDeviceUnits(12) - unitWidth - LogicalToDeviceUnits(64), LogicalToDeviceUnits(6), LogicalToDeviceUnits(64), LogicalToDeviceUnits(28));
            _box.EnterPressed += (_, _) => Commit();
            _box.Box.Leave += (_, _) => Commit();
            _box.Box.KeyPress += (_, e) =>
            {
                if (!char.IsControl(e.KeyChar) && !char.IsAsciiDigit(e.KeyChar) && e.KeyChar is not '.' and not ',') e.Handled = true;
            };
            Controls.Add(_slider);
            Controls.Add(_box);
            ShowValue();
        }

        public event EventHandler<int>? ValueChanged;

        /// <summary>A value typed in the box and confirmed (Enter, or leaving the box).</summary>
        public event EventHandler<float>? Typed;

        void ShowValue()
        {
            if (_box.Box.Focused && !_syncing) return;
            _box.Text = _valueAt(_slider.Value).ToString("0.#", CultureInfo.CurrentCulture);
            Invalidate();
        }

        void Commit()
        {
            string text = _box.Text.Trim().Replace(',', '.');
            if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value)) { _syncing = true; ShowValue(); _syncing = false; return; }
            Typed?.Invoke(this, value);
            // The slider follows the typed value (as near as its steps allow).
            int nearest = Enumerable.Range(_slider.Minimum, _slider.Maximum - _slider.Minimum + 1).MinBy(index => Math.Abs(_valueAt(index) - value));
            _syncing = true;
            _slider.Value = nearest;
            ShowValue();
            _syncing = false;
        }

        public override Color BackColor
        {
            get => MenuChrome.Background;
            set { }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(MenuChrome.Background);
            var line = new Rectangle(LogicalToDeviceUnits(12), LogicalToDeviceUnits(6), Width - LogicalToDeviceUnits(24), LogicalToDeviceUnits(28));
            Draw.Text(e.Graphics, _name, AppFonts.Small, line, Theme.Current.Text2);
            if (_unit.Length > 0) Draw.Text(e.Graphics, _unit, AppFonts.MonoSmall, line, Theme.Current.Text3, Draw.Line | TextFormatFlags.Right);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            BeginInvoke(() => _slider.Focus());
        }
    }
}

/// <summary>
/// A swatch button. Its menu offers the ink colours, the colours picked by hand lately, <see cref="PickFromPage"/>
/// when the page is there to pick from, and Custom Colour… for anything else. With <see cref="AllowNone"/> the
/// first swatch is "no colour" (fully transparent).
/// </summary>
internal sealed class ColorButton : AppButton
{
    Color _color;

    public ColorButton(string name, Color color, bool showHex = true) : base(showHex ? Hex(color) : "", ButtonKind.Secondary)
    {
        _color = color;
        ShowHex = showHex;
        AccessibleName = name;
        Font = AppFonts.Mono;
        Click += (_, _) => Open();
    }

    bool ShowHex { get; }

    public bool AllowNone { get; init; }

    /// <summary>Starts picking a colour from the page; the callback gets the colour clicked. Null hides the choice.</summary>
    public Action<Action<Color>>? PickFromPage { get; set; }

    public event EventHandler? Changed;

    public Color Color
    {
        get => _color;
        set
        {
            _color = value;
            if (ShowHex) Text = Hex(value);
            AccessibleDescription = Hex(value);
            Invalidate();
        }
    }

    static string Hex(Color color) => color.A == 0 ? "None" : $"{color.R:X2}{color.G:X2}{color.B:X2}";

    public override Size GetPreferredSize(Size proposedSize)
    {
        Size size = base.GetPreferredSize(proposedSize);
        return ShowHex ? new Size(size.Width + LogicalToDeviceUnits(22), size.Height) : new Size(size.Height, size.Height);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        int swatch = LogicalToDeviceUnits(14);
        int x = ShowHex ? Width - LogicalToDeviceUnits(10) - swatch : (Width - swatch) / 2;
        var box = new Rectangle(x, (Height - swatch) / 2, swatch, swatch);
        Palette.DrawSwatch(e.Graphics, box, LogicalToDeviceUnits(3), _color);
    }

    void Pick(Color color, bool byHand)
    {
        Color = color;
        if (byHand) Platform.RecentColors.Add(color);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    void Open()
    {
        var menu = Menus.Create();
        menu.ShowImageMargin = true;
        var palette = new Palette(_color, AllowNone, Platform.RecentColors.All);
        palette.Picked += (_, color) => { menu.Close(); Pick(color, byHand: false); };
        menu.Items.Add(new ToolStripControlHost(palette) { AutoSize = false, Size = palette.Size, Padding = Padding.Empty, Margin = Padding.Empty });
        menu.Items.Add(new ToolStripSeparator());
        if (PickFromPage is { } pickFromPage)
        {
            var eyedropper = Menus.Item("Pick From Page", () => pickFromPage(color => Pick(color, byHand: true)));
            eyedropper.Image = IconImage("eyedropper");
            menu.Items.Add(eyedropper);
        }
        menu.Items.Add(Menus.Item("Custom Colour…", () =>
        {
            if (ColorPickerDialog.Pick(FindForm(), _color) is { } picked) Pick(picked, byHand: true);
        }));
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        Menus.Show(menu, this, Menus.Below(this));
    }

    Bitmap IconImage(string name)
    {
        int size = LogicalToDeviceUnits(16);
        var image = new Bitmap(size, size);
        using Graphics g = Graphics.FromImage(image);
        Icons.Draw(g, name, new Rectangle(0, 0, size, size), Theme.Current.Text2);
        return image;
    }

    /// <summary>The swatches: ink colours (and "no colour"), then the colours picked by hand lately.</summary>
    sealed class Palette : Panel, IItemHost
    {
        const int Columns = 5, Cell = 28, Inset = 6, LabelHeight = 22;
        readonly Color _current;
        readonly Color[] _colors;
        readonly int _baseCount;
        int _hover = -1;

        public Palette(Color current, bool allowNone, IReadOnlyList<Color> recent)
        {
            _current = current;
            Color[] inks = allowNone ? [Color.Transparent, .. Theme.InkColors] : Theme.InkColors;
            _baseCount = inks.Length;
            _colors = [.. inks, .. recent];
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            int height = Rows(_baseCount) * Cell + (recent.Count > 0 ? LabelHeight + Rows(recent.Count) * Cell : 0) + 2 * Inset;
            Size = new Size(LogicalToDeviceUnits(Columns * Cell + 2 * Inset), LogicalToDeviceUnits(height));
            AccessibleRole = AccessibleRole.List;
            AccessibleName = "Colours";
        }

        static int Rows(int count) => (count + Columns - 1) / Columns;

        public event EventHandler<Color>? Picked;

        public override Size GetPreferredSize(Size proposedSize) => Size;

        protected override AccessibleObject CreateAccessibilityInstance() => new ItemHostAccessible(this, this, AccessibleRole.ListItem);

        int IItemHost.ItemCount => _colors.Length;

        string IItemHost.ItemName(int index)
        {
            Color color = _colors[index];
            if (color.A == 0) return "No colour";
            int known = index < _baseCount ? Array.FindIndex(Theme.InkColors, ink => ink.ToArgb() == color.ToArgb()) : -1;
            string name = known >= 0 ? Theme.InkColorNames[known] : $"{color.R:X2}{color.G:X2}{color.B:X2}";
            return index >= _baseCount ? $"Recent, {name}" : name;
        }

        Rectangle IItemHost.ItemBounds(int index) => CellAt(index);
        bool IItemHost.IsItemSelected(int index) => IsCurrent(index);
        int IItemHost.FocusedItem => _hover;
        void IItemHost.ActivateItem(int index) => Picked?.Invoke(this, _colors[index]);

        bool IsCurrent(int index) => _colors[index].ToArgb() == _current.ToArgb() || (_colors[index].A == 0 && _current.A == 0);

        Rectangle CellAt(int index)
        {
            bool recent = index >= _baseCount;
            int i = recent ? index - _baseCount : index;
            int top = Inset + (recent ? Rows(_baseCount) * Cell + LabelHeight : 0) + i / Columns * Cell;
            return new Rectangle(LogicalToDeviceUnits(Inset + i % Columns * Cell + 3), LogicalToDeviceUnits(top + 3), LogicalToDeviceUnits(Cell - 6), LogicalToDeviceUnits(Cell - 6));
        }

        /// <summary>A colour swatch; "no colour" is white with a red diagonal.</summary>
        public static void DrawSwatch(Graphics g, Rectangle box, float radius, Color color)
        {
            if (color.A > 0)
            {
                Draw.Box(g, box, radius, color, Theme.Current.Line2);
                return;
            }
            Draw.Box(g, box, radius, Color.White, Theme.Current.Line2);
            SmoothingMode old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(Theme.Current.Danger, 1.5f);
            g.DrawLine(pen, box.Left + 2, box.Bottom - 2, box.Right - 2, box.Top + 2);
            g.SmoothingMode = old;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(MenuChrome.Background);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (_colors.Length > _baseCount)
            {
                int top = LogicalToDeviceUnits(Inset + Rows(_baseCount) * Cell);
                Draw.Text(g, "Recent", AppFonts.Small, new Rectangle(LogicalToDeviceUnits(Inset + 3), top, Width, LogicalToDeviceUnits(LabelHeight)), Theme.Current.Text3);
            }
            for (int i = 0; i < _colors.Length; i++)
            {
                Rectangle cell = CellAt(i);
                if (IsCurrent(i) || i == _hover)
                    Draw.Box(g, Rectangle.Inflate(cell, LogicalToDeviceUnits(3), LogicalToDeviceUnits(3)), LogicalToDeviceUnits(6), Color.Transparent, IsCurrent(i) ? Theme.Current.Accent : Theme.Current.Line2, LogicalToDeviceUnits(2));
                DrawSwatch(g, cell, LogicalToDeviceUnits(4), _colors[i]);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int index = Enumerable.Range(0, _colors.Length).FirstOrDefault(i => Rectangle.Inflate(CellAt(i), 3, 3).Contains(e.Location), -1);
            if (index != _hover) { _hover = index; Invalidate(); }
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
            if (_hover >= 0) Picked?.Invoke(this, _colors[_hover]);
        }
    }
}
