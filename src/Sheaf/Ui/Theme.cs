namespace Sheaf.Ui;

internal enum ThemeMode { Dark, Light }

/// <summary>Every colour of one theme. Controls read these while painting, so switching theme is a repaint.</summary>
internal sealed record Palette(
    Color Chrome, Color Panel, Color Raised, Color Hover, Color Active, Color Line, Color Line2, Color Canvas,
    Color Text, Color Text2, Color Text3, Color Disabled,
    Color Accent, Color AccentHover, Color AccentPressed, Color AccentSoft, Color AccentText,
    Color Danger, Color DangerText, Color Success, Color Warning);

/// <summary>
/// The design system: graphite surfaces, one accent for the primary action, selection and focus, Geist type.
/// Status colours appear only on status words. Sizes are logical (96 DPI) pixels.
/// </summary>
internal static class Theme
{
    static readonly Color AccentBase = Hex(0x2563EB);

    public static readonly Palette Dark = new(
        Chrome: Hex(0x0E0E10), Panel: Hex(0x17171A), Raised: Hex(0x1F1F23), Hover: Hex(0x26262B), Active: Hex(0x2E2E35),
        Line: Hex(0x28282D), Line2: Hex(0x3A3A42), Canvas: Hex(0x0B0B0D),
        Text: Hex(0xEDEDEF), Text2: Hex(0xA1A1AA), Text3: Hex(0x71717A), Disabled: Hex(0x55555D),
        Accent: AccentBase, AccentHover: Hex(0x3B74EE), AccentPressed: Hex(0x1D55D0), AccentSoft: Color.FromArgb(61, AccentBase),
        AccentText: Hex(0x7EA6F6), Danger: Hex(0xE5484D), DangerText: Hex(0xFF7A7F), Success: Hex(0x3DD68C), Warning: Hex(0xF5B841));

    public static readonly Palette Light = new(
        Chrome: Hex(0xE7E7EA), Panel: Hex(0xFFFFFF), Raised: Hex(0xF4F4F5), Hover: Hex(0xEFEFF2), Active: Hex(0xE4E4E9),
        Line: Hex(0xE4E4E7), Line2: Hex(0xD1D1D8), Canvas: Hex(0xE2E2E6),
        Text: Hex(0x141416), Text2: Hex(0x52525B), Text3: Hex(0x80808A), Disabled: Hex(0xA9A9B2),
        Accent: AccentBase, AccentHover: Hex(0x1D55D0), AccentPressed: Hex(0x1A4AB5), AccentSoft: Color.FromArgb(31, AccentBase),
        AccentText: Hex(0x1D4ED8), Danger: Hex(0xD92D20), DangerText: Hex(0xC4271C), Success: Hex(0x15803D), Warning: Hex(0xA16207));

    public static Palette Current { get; private set; } = Dark;

    public static bool IsDark => Current == Dark;

    public static event EventHandler? Changed;

    public static void Use(ThemeMode mode)
    {
        Palette next = mode == ThemeMode.Light ? Light : Dark;
        if (next == Current) return;
        Current = next;
        Changed?.Invoke(null, EventArgs.Empty);
    }

    // Radii
    public const int RadiusSmall = 4;
    public const int Radius = 6;
    public const int RadiusLarge = 8;

    // Heights and widths of the frame
    public const int ControlHeight = 32;
    public const int TitleBarHeight = 40;
    public const int HeaderHeight = 48;
    public const int ContextBarHeight = 40;
    public const int StatusBarHeight = 28;
    public const int RailWidth = 48;
    public const int PagesPanelWidth = 188;
    public const int InspectorWidth = 288;
    public const int SidePanelWidth = 320;
    public const int Gutter = 32;
    // Small enough for a 1280 × 720 screen at 150 % (or 1366 × 768 at 175 %): the editor folds its side panels away
    // and its bars go to icons as the window narrows.
    public const int MinimumWindowWidth = 780;
    public const int MinimumWindowHeight = 480;

    /// <summary>Colours of what people draw on pages, offered first in colour pickers.</summary>
    public static readonly Color[] InkColors =
    [
        Hex(0x18181B), Hex(0xE5484D), Hex(0xF97316), Hex(0xEAB308), Hex(0x22C55E), Hex(0x0EA5E9), Hex(0x2563EB), Hex(0xFFFFFF),
    ];

    /// <summary>Spoken names of <see cref="InkColors"/>, in the same order.</summary>
    public static readonly string[] InkColorNames = ["Black", "Red", "Orange", "Yellow", "Green", "Sky blue", "Blue", "White"];

    public static Color Hex(int rgb) => Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);

    public static Color Blend(Color from, Color to, double amount)
    {
        double t = Math.Clamp(amount, 0, 1);
        return Color.FromArgb(
            (int)Math.Round(from.A + (to.A - from.A) * t),
            (int)Math.Round(from.R + (to.R - from.R) * t),
            (int)Math.Round(from.G + (to.G - from.G) * t),
            (int)Math.Round(from.B + (to.B - from.B) * t));
    }
}
