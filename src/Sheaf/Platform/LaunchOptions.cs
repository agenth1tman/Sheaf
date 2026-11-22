namespace Sheaf.Platform;

/// <summary>
/// Command line: <c>Sheaf.exe [files…] [--tool &lt;id&gt;] [--sandbox &lt;folder&gt;]</c>.
/// PDFs open in the reader, other files in the tool that converts them; --tool opens a tool with the files.
/// </summary>
internal sealed record LaunchOptions(IReadOnlyList<string> Files, string? ToolId, string? SandboxFolder)
{
    public static LaunchOptions Parse(IReadOnlyList<string> args)
    {
        var files = new List<string>();
        string? tool = null, sandbox = null;
        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];
            if (IsFlag(arg, "tool") && i + 1 < args.Count) tool = args[++i];
            else if (IsFlag(arg, "sandbox") && i + 1 < args.Count) sandbox = args[++i];
            else if (!arg.StartsWith('-')) files.Add(Path.GetFullPath(arg));
        }
        return new LaunchOptions(files, tool, sandbox);
    }

    /// <summary>Arguments to hand to an already running copy: files are absolute, so they survive the hop.</summary>
    public IEnumerable<string> ForwardedArguments()
    {
        if (ToolId is not null)
        {
            yield return "--tool";
            yield return ToolId;
        }
        foreach (string file in Files) yield return file;
    }

    static bool IsFlag(string arg, string name) =>
        arg.Equals("--" + name, StringComparison.OrdinalIgnoreCase) || arg.Equals("/" + name, StringComparison.OrdinalIgnoreCase);
}
