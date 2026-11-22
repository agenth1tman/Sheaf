using System.Runtime.InteropServices;
using System.Text;

namespace Sheaf.Core.Office;

/// <summary>Finds a message box that an Office process is showing, so Sheaf can say what it asked.</summary>
internal static class OfficeDialogs
{
    const string DialogClass = "#32770";
    const string TextClass = "Static";

    delegate bool WindowVisitor(IntPtr window, IntPtr state);

    [DllImport("user32.dll")]
    static extern bool EnumWindows(WindowVisitor visitor, IntPtr state);

    [DllImport("user32.dll")]
    static extern bool EnumChildWindows(IntPtr parent, WindowVisitor visitor, IntPtr state);

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetClassName(IntPtr window, StringBuilder name, int capacity);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetWindowText(IntPtr window, StringBuilder text, int capacity);

    /// <summary>The longest line of text in a visible dialog of <paramref name="processId"/>, or null when none is open.</summary>
    public static string? FindQuestion(int processId)
    {
        string? question = null;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out uint owner);
            if (owner == processId && IsWindowVisible(window) && ClassOf(window) == DialogClass)
                question = LongestText(window);
            return question is null;
        }, IntPtr.Zero);
        return question;
    }

    static string? LongestText(IntPtr dialog)
    {
        string? longest = null;
        EnumChildWindows(dialog, (child, _) =>
        {
            if (ClassOf(child) != TextClass) return true;
            var text = new StringBuilder(1024);
            GetWindowText(child, text, text.Capacity);
            string line = text.ToString().Trim();
            if (line.Length > (longest?.Length ?? 0)) longest = line;
            return true;
        }, IntPtr.Zero);
        return longest;
    }

    static string ClassOf(IntPtr window)
    {
        var name = new StringBuilder(64);
        GetClassName(window, name, name.Capacity);
        return name.ToString();
    }
}
