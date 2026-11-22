using Sheaf.Core;
using Sheaf.Core.IO;
using Sheaf.Platform;

namespace Sheaf.Tools;

/// <summary>
/// A tool that works on a list of files. A tool builds its own option controls (once, on the UI thread),
/// checks them, then does the work through a <see cref="ToolRun"/>. The workspace around it (file list,
/// output folder, primary button, result line) is shared and lives in <c>ToolView</c>.
/// To add a tool: subclass this, then list it in <see cref="ToolCatalog"/>.
/// </summary>
internal abstract class Tool(ToolInfo info)
{
    Control? _options;

    public ToolInfo Info { get; } = info;

    /// <summary>The tool's settings, or null when it has none.</summary>
    public Control? Options => _options ??= BuildOptions();

    /// <summary>Why the tool can't run on this PC (a converter isn't installed), or null.</summary>
    public virtual string? Unavailable => null;

    protected virtual Control? BuildOptions() => null;

    /// <summary>Checks the options and shows any problem next to its field. Returns the first bad field to focus.</summary>
    public virtual Control? Validate() => null;

    /// <summary>Runs on the UI thread: read option values first, then move the work off it with Task.Run.</summary>
    public abstract Task<ToolResult> RunAsync(ToolRun run);
}

/// <param name="Detail">Extra fact for the result line, e.g. "4.2 MB → 1.3 MB, 69 % smaller".</param>
internal sealed record ToolResult(IReadOnlyList<string> Outputs, IReadOnlyList<string> Failures, string? Detail = null)
{
    public static ToolResult Single(string output, string? detail = null) => new([output], [], detail);
}

/// <summary>How far a run has got: the share of the work done, and the share it will reach when the current step ends.</summary>
internal readonly record struct RunShare(double Done, double Next);

/// <summary>Everything one run of a tool needs: the files, where output goes, progress and cancellation.</summary>
internal sealed class ToolRun(IReadOnlyList<SourceFile> files, OutputPlanner output, IProgress<string> progress, IProgress<RunShare> share, CancellationToken cancel)
{
    // The part of the run the current file covers, so a tool's own steps (pages) move the progress line within it.
    (double Start, double End) _span = (0, 1);

    public IReadOnlyList<SourceFile> Files { get; } = files;
    public OutputPlanner Output { get; } = output;
    public CancellationToken Cancel { get; } = cancel;

    public void Report(string text) => progress.Report(text);

    /// <summary>Reports text and that <paramref name="done"/> of <paramref name="total"/> steps are finished.</summary>
    public void Report(string text, int done, int total)
    {
        progress.Report(text);
        double size = _span.End - _span.Start;
        share.Report(new RunShare(_span.Start + size * Math.Min(done, total) / total, _span.Start + size * Math.Min(done + 1, total) / total));
    }

    /// <summary>
    /// Does <paramref name="work"/> for each file in turn on a worker thread. A file that fails is recorded
    /// and the others still run; cancelling stops before the next file.
    /// </summary>
    public Task<ToolResult> EachFileAsync(string busyText, Func<SourceFile, IEnumerable<string>> work) => Task.Run(() =>
    {
        var outputs = new List<string>();
        var failures = new List<string>();
        for (int i = 0; i < Files.Count; i++)
        {
            Cancel.ThrowIfCancellationRequested();
            _span = (0, 1);
            if (Files.Count > 1) Report($"{busyText} {i + 1} of {Files.Count}…", i, Files.Count);
            _span = ((double)i / Files.Count, (double)(i + 1) / Files.Count);
            try
            {
                outputs.AddRange(work(Files[i]));
            }
            catch (ToolException ex)
            {
                failures.Add(ex.Message);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ActivityLog.Error($"processing {Files[i].Name}", ex);
                failures.Add($"“{Files[i].Name}” couldn’t be processed: {ex.Message}");
            }
        }
        _span = (0, 1);
        return new ToolResult(outputs, failures);
    }, Cancel);
}

/// <summary>Decides where new files go: next to the original, or in the folder chosen in Settings.</summary>
internal sealed class OutputPlanner(AppSettings settings)
{
    public string FolderFor(SourceFile source) =>
        settings.SaveNextToOriginal || !Directory.Exists(settings.OutputFolder) ? source.Folder : settings.OutputFolder;

    /// <summary>"report.pdf" with suffix "compressed" becomes "report-compressed.pdf" (or "… (2).pdf").</summary>
    public string FileFor(SourceFile source, string suffix, string extension = ".pdf") =>
        OutputNames.UniqueFile(FolderFor(source), $"{source.BaseName}-{suffix}", extension);

    public string NewFile(SourceFile nextTo, string baseName, string extension = ".pdf") =>
        OutputNames.UniqueFile(FolderFor(nextTo), baseName, extension);

    public string NewFolder(SourceFile source, string suffix) =>
        OutputNames.CreateUniqueFolder(FolderFor(source), $"{source.BaseName}-{suffix}");
}
