using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Sheaf.Platform;

internal static partial class Native
{
    public const int AnyProcess = -1;
    public const int WM_NCCALCSIZE = 0x0083;
    public const int WM_NCHITTEST = 0x0084;
    public const int HTTRANSPARENT = -1, HTCLIENT = 1, HTCAPTION = 2;
    public const int HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;
    const int WM_SETREDRAW = 0x000B;
    const uint RDW_INVALIDATE = 0x0001, RDW_ERASE = 0x0004, RDW_ALLCHILDREN = 0x0080, RDW_UPDATENOW = 0x0100, RDW_FRAME = 0x0400;
    const uint SPI_GETCLIENTAREAANIMATION = 0x1042;
    const int SM_CXSIZEFRAME = 32, SM_CXPADDEDBORDER = 92;

    [StructLayout(LayoutKind.Sequential)]
    public struct Margins(int left, int right, int top, int bottom)
    {
        public int Left = left, Right = right, Top = top, Bottom = bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NcCalcSizeParams
    {
        public Rect Proposed, Before, Client;
        public IntPtr Position;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AllowSetForegroundWindow(int processId);

    [StructLayout(LayoutKind.Sequential)]
    struct PointerPenInfo
    {
        public uint PointerType, PointerId, FrameId, PointerFlags;
        public IntPtr SourceDevice, Target;
        public int PixelX, PixelY, HimetricX, HimetricY, PixelRawX, PixelRawY, HimetricRawX, HimetricRawY;
        public uint Time, HistoryCount;
        public int InputData;
        public uint KeyStates;
        public ulong PerformanceCount;
        public int ButtonChange;
        public uint PenFlags, PenMask, Pressure, Rotation;
        public int TiltX, TiltY;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetPointerType(uint pointerId, out uint pointerType);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetPointerPenInfo(uint pointerId, out PointerPenInfo info);

    /// <summary>How hard a stylus presses (0 to 1), or null when the pointer isn't a pen that reports it.</summary>
    public static float? PenPressure(uint pointerId)
    {
        const uint Pen = 3, PressureReported = 1;
        if (!GetPointerType(pointerId, out uint type) || type != Pen) return null;
        if (!GetPointerPenInfo(pointerId, out PointerPenInfo info) || (info.PenMask & PressureReported) == 0) return null;
        return Math.Clamp(info.Pressure / 1024f, 0, 1);
    }

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    private static partial IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RedrawWindow(IntPtr window, IntPtr updateRect, IntPtr updateRegion, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfo(uint action, uint param, [MarshalAs(UnmanagedType.Bool)] out bool value, uint winIni);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetricsForDpi(int index, uint dpi);

    [LibraryImport("uxtheme.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SetWindowTheme(IntPtr window, string? appName, string? idList);

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmExtendFrameIntoClientArea(IntPtr window, ref Margins margins);

    /// <summary>Waits for the desktop compositor's next frame: what animations pace themselves on.</summary>
    [LibraryImport("dwmapi.dll")]
    public static partial int DwmFlush();

    /// <summary>Makes sleeps and timers precise to a millisecond until <see cref="TimeEndPeriod"/>.</summary>
    [LibraryImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    public static partial uint TimeBeginPeriod(uint milliseconds);

    [LibraryImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    public static partial uint TimeEndPeriod(uint milliseconds);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    /// <summary>Dark or light caption for a window that keeps the standard title bar (dialogs).</summary>
    public static void SetDarkTitleBar(IntPtr window, bool dark)
    {
        const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        int value = dark ? 1 : 0;
        DwmSetWindowAttribute(window, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
    }

    /// <summary>Windows' "Show animations in Windows" setting.</summary>
    public static bool AnimationsEnabled => !SystemParametersInfo(SPI_GETCLIENTAREAANIMATION, 0, out bool on, 0) || on;

    /// <summary>The invisible sizing border Windows keeps around a resizable window, in device pixels.</summary>
    public static int ResizeBorder(int dpi) => GetSystemMetricsForDpi(SM_CXSIZEFRAME, (uint)dpi) + GetSystemMetricsForDpi(SM_CXPADDEDBORDER, (uint)dpi);

    public static void SetScrollbarTheme(IntPtr window, bool dark) => SetWindowTheme(window, dark ? "DarkMode_Explorer" : "Explorer", null);

    /// <summary>Pauses drawing of a control and its children until <see cref="ResumeDrawing"/>, so a tab switch paints once.</summary>
    public static void PauseDrawing(Control control) => SendMessage(control.Handle, WM_SETREDRAW, 0, 0);

    /// <summary>
    /// Lets a paused control draw again without painting it now (it is hidden). Turning drawing back on also
    /// shows a window, so it is hidden again: left showing, it would sit unlaid-out over its neighbours.
    /// </summary>
    public static void EnableDrawing(Control control)
    {
        SendMessage(control.Handle, WM_SETREDRAW, 1, 0);
        if (!control.Visible) ShowWindow(control.Handle, 0);
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(IntPtr window, int command);

    public static void ResumeDrawing(Control control)
    {
        SendMessage(control.Handle, WM_SETREDRAW, 1, 0);
        RedrawWindow(control.Handle, 0, 0, RDW_INVALIDATE | RDW_ERASE | RDW_FRAME | RDW_ALLCHILDREN | RDW_UPDATENOW);
    }

    public static void OpenWithShell(string path) =>
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();

    public static void ShowInExplorer(string path)
    {
        if (Directory.Exists(path)) OpenWithShell(path);
        else Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true })?.Dispose();
    }
}
