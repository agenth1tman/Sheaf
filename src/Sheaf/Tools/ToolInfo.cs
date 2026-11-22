using Sheaf.Core.IO;

namespace Sheaf.Tools;

internal enum ToolCategory { Organize, EditAndReview, Convert, ProtectAndOptimize }

/// <param name="Id">Stable key used in settings and on the command line (--tool merge).</param>
/// <param name="Icon">Name of an icon in <c>Ui.Icons</c>.</param>
/// <param name="ActionText">The primary button, naming the action: "Merge PDF".</param>
/// <param name="BusyText">Shown while it runs: "Merging…".</param>
/// <param name="MinimumFiles">Files needed before the action can run.</param>
/// <param name="DocumentMode">Set for tools that open a PDF in the editor instead of a file list ("edit", "redact").</param>
/// <param name="FileBadge">Short file-type label shown instead of an icon on Home ("DOC", "JPG").</param>
internal sealed record ToolInfo(
    string Id,
    string Title,
    string Summary,
    ToolCategory Category,
    string Icon,
    FileKind Accepts,
    string ActionText,
    string BusyText,
    int MinimumFiles = 1,
    string? DocumentMode = null,
    string? FileBadge = null)
{
    public static string CategoryName(ToolCategory category) => category switch
    {
        ToolCategory.Organize => "Organize",
        ToolCategory.EditAndReview => "Edit and review",
        ToolCategory.Convert => "Convert",
        _ => "Protect and optimize",
    };
}
