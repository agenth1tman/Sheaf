using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Sheaf.Ui.Controls;

/// <summary>
/// Rounded corners and a soft shadow for a menu. Windows 10 can't round a menu's corners itself, and a window
/// region alone leaves them jagged, so the menu is cut to a slightly rounder shape and a see-through window
/// just behind it draws the smooth corners, the border and the shadow. Both fade together.
/// </summary>
internal sealed partial class MenuChrome : IDisposable
{
    const int Radius = 8, ShadowSize = 14;
    static readonly ConditionalWeakTable<ToolStripDropDown, MenuChrome> Attached = [];

    readonly ToolStripDropDown _menu;
    readonly Backdrop _backdrop = new();
    byte _alpha = 255;

    MenuChrome(ToolStripDropDown menu)
    {
        _menu = menu;
        menu.DropShadowEnabled = false;
        menu.Opened += (_, _) => Place();
        menu.LocationChanged += (_, _) => { if (menu.Visible) Place(); };
        menu.SizeChanged += (_, _) => { if (menu.Visible) Place(); };
        menu.Closed += (_, _) => _backdrop.Hide();
        menu.Disposed += (_, _) => Dispose();
    }

    /// <summary>The background a menu is painted with: a step lighter than the panels in dark mode, white in light.</summary>
    public static Color Background => Theme.IsDark ? Theme.Current.Raised : Theme.Current.Panel;

    public static bool Has(ToolStripDropDown menu) => Attached.TryGetValue(menu, out _);

    public static void Attach(ToolStripDropDown menu)
    {
        if (!Attached.TryGetValue(menu, out _)) Attached.Add(menu, new MenuChrome(menu));
    }

    /// <summary>Fades the menu's frame and shadow with the menu (0 to 1).</summary>
    public static void SetOpacity(ToolStripDropDown menu, double opacity)
    {
        if (!Attached.TryGetValue(menu, out MenuChrome? chrome)) return;
        chrome._alpha = (byte)Math.Round(255 * Math.Clamp(opacity, 0, 1));
        if (menu.Visible) chrome._backdrop.SetAlpha(chrome._alpha);
    }

    int Px(int logical) => _menu.LogicalToDeviceUnits(logical);

    void Place()
    {
        if (_menu.IsDisposed || !_menu.IsHandleCreated || _menu.Width <= 0) return;
        // The menu is cut a pixel inside the frame, and a touch rounder, so the frame's smooth border shows all the
        // way round and the menu's own square corner pixels never cover it.
        int cut = 2 * Px(Radius) + 2;
        IntPtr region = CreateRoundRectRgn(1, 1, _menu.Width - 1, _menu.Height - 1, cut, cut);
        if (SetWindowRgn(_menu.Handle, region, true) == 0) DeleteObject(region);
        Paint();
    }

    void Paint()
    {
        int shadow = Px(ShadowSize), radius = Px(Radius);
        Rectangle menu = _menu.Bounds, outer = Rectangle.Inflate(menu, shadow, shadow);
        using var picture = new Bitmap(outer.Width, outer.Height, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(picture))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // Pixel centres on the half, so the 1 px border lands on one row of pixels, crisp along the sides.
            g.PixelOffsetMode = PixelOffsetMode.Half;
            var body = new Rectangle(shadow, shadow, menu.Width, menu.Height);
            // A soft shadow: rings that get darker towards the menu, a little lower than it, as light from above casts.
            int strength = Theme.IsDark ? 9 : 5;
            for (int ring = shadow; ring > 0; ring -= 2)
            {
                var spread = Rectangle.Inflate(body, ring - shadow / 3, ring - shadow / 3);
                spread.Offset(0, shadow / 4);
                using GraphicsPath path = Draw.RoundRect(spread, radius + ring);
                using var brush = new SolidBrush(Color.FromArgb(strength, 0, 0, 0));
                g.FillPath(brush, path);
            }
            using (GraphicsPath path = Draw.RoundRect(body, radius))
            using (var fill = new SolidBrush(Background))
                g.FillPath(fill, path);
            using (GraphicsPath edge = Draw.RoundRect(new RectangleF(body.X + 0.5f, body.Y + 0.5f, body.Width - 1, body.Height - 1), radius))
            using (var pen = new Pen(Theme.Current.Line2))
                g.DrawPath(pen, edge);
        }
        _backdrop.Show(picture, outer.Location, _alpha, _menu.Handle);
    }

    public void Dispose()
    {
        Attached.Remove(_menu);
        _backdrop.DestroyHandle();
    }

    /// <summary>The see-through window behind the menu: layered, click-through, never active, kept just under it.</summary>
    sealed class Backdrop : NativeWindow
    {
        public void Show(Bitmap picture, Point at, byte alpha, IntPtr above)
        {
            if (Handle == IntPtr.Zero)
            {
                const int Popup = unchecked((int)0x80000000), Layered = 0x80000, Transparent = 0x20, NoActivate = 0x8000000, ToolWindow = 0x80, Topmost = 0x8;
                CreateHandle(new CreateParams { Style = Popup, ExStyle = Layered | Transparent | NoActivate | ToolWindow | Topmost });
            }
            IntPtr screen = GetDC(IntPtr.Zero), memory = CreateCompatibleDC(screen), bitmap = picture.GetHbitmap(Color.FromArgb(0)), old = SelectObject(memory, bitmap);
            try
            {
                var where = new Pair { X = at.X, Y = at.Y };
                var size = new Pair { X = picture.Width, Y = picture.Height };
                var source = new Pair();
                var blend = new BlendFunction { SourceConstantAlpha = alpha, AlphaFormat = 1 };
                UpdateLayeredWindow(Handle, screen, ref where, ref size, memory, ref source, 0, ref blend, 2);
            }
            finally
            {
                SelectObject(memory, old);
                DeleteObject(bitmap);
                DeleteDC(memory);
                ReleaseDC(IntPtr.Zero, screen);
            }
            const uint NoSize = 0x1, NoMove = 0x2, NoActivateFlag = 0x10, ShowWindowFlag = 0x40;
            // Just under the menu, which stays on top of it.
            SetWindowPos(Handle, above, 0, 0, 0, 0, NoSize | NoMove | NoActivateFlag | ShowWindowFlag);
        }

        /// <summary>Changes only how see-through it is, keeping the picture it has.</summary>
        public void SetAlpha(byte alpha)
        {
            if (Handle == IntPtr.Zero) return;
            var blend = new BlendFunction { SourceConstantAlpha = alpha, AlphaFormat = 1 };
            UpdateLayeredAlpha(Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, ref blend, 2);
        }

        public void Hide()
        {
            if (Handle != IntPtr.Zero) ShowWindow(Handle, 0);
        }
    }

    /// <summary>A POINT or a SIZE: two ints.</summary>
    [StructLayout(LayoutKind.Sequential)]
    struct Pair
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BlendFunction
    {
        public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat;
    }

    [LibraryImport("gdi32.dll")]
    private static partial IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);

    [LibraryImport("user32.dll")]
    private static partial int SetWindowRgn(IntPtr window, IntPtr region, [MarshalAs(UnmanagedType.Bool)] bool redraw);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(IntPtr handle);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetDC(IntPtr window);

    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(IntPtr window, IntPtr dc);

    [LibraryImport("gdi32.dll")]
    private static partial IntPtr CreateCompatibleDC(IntPtr dc);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteDC(IntPtr dc);

    [LibraryImport("gdi32.dll")]
    private static partial IntPtr SelectObject(IntPtr dc, IntPtr handle);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UpdateLayeredWindow(IntPtr window, IntPtr screen, ref Pair at, ref Pair size, IntPtr source, ref Pair sourceAt, int key, ref BlendFunction blend, int flags);

    [LibraryImport("user32.dll", EntryPoint = "UpdateLayeredWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UpdateLayeredAlpha(IntPtr window, IntPtr screen, IntPtr at, IntPtr size, IntPtr source, IntPtr sourceAt, int key, ref BlendFunction blend, int flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(IntPtr window, int command);
}
