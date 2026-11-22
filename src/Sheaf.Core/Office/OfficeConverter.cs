using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Sheaf.Core.Office;

public enum Conversion { WordToPdf, ExcelToPdf, PowerPointToPdf, PdfToWord }

public enum ConverterApp { MicrosoftOffice, LibreOffice, BuiltIn }

/// <summary>One file to convert and where the result goes.</summary>
public sealed record ConversionJob(string Input, string Output);

/// <summary>
/// Converts documents with Microsoft Office through COM automation or LibreOffice in headless mode when one is
/// installed, and otherwise with Sheaf's own converters (<see cref="BuiltIn"/>), so it always works.
/// </summary>
public static class OfficeConverter
{
    static readonly TimeSpan LibreOfficeTimeout = TimeSpan.FromMinutes(3);
    static readonly Lazy<string?> SofficePath = new(FindLibreOffice);

    /// <summary>The best converter on this PC: Microsoft Office, then LibreOffice, then the built-in one.</summary>
    public static ConverterApp AppFor(Conversion conversion)
    {
        if (HasOfficeApp(OfficeProgId(conversion))) return ConverterApp.MicrosoftOffice;
        return SofficePath.Value is null ? ConverterApp.BuiltIn : ConverterApp.LibreOffice;
    }

    public static bool HasOfficeApp(string progId) => Type.GetTypeFromProgID(progId) is not null;

    public static string? LibreOfficePath => SofficePath.Value;

    /// <summary>
    /// Converts each job in turn with <paramref name="app"/> (or the best one installed). Returns one entry per job:
    /// null when it worked, otherwise the error.
    /// </summary>
    public static Task<IReadOnlyList<Exception?>> ConvertAsync(
        Conversion conversion, IReadOnlyList<ConversionJob> jobs, IProgress<int>? progress, CancellationToken cancel, ConverterApp? app = null) =>
        (app ?? AppFor(conversion)) switch
        {
            ConverterApp.MicrosoftOffice => ConvertWithOfficeAsync(conversion, jobs, progress, cancel),
            ConverterApp.LibreOffice => Task.Run(() => ConvertWithLibreOffice(conversion, jobs, progress, cancel), cancel),
            _ => Task.Run(() => ConvertBuiltIn(conversion, jobs, progress, cancel), cancel),
        };

    // Built in ---------------------------------------------------------------------------------

    static IReadOnlyList<Exception?> ConvertBuiltIn(Conversion conversion, IReadOnlyList<ConversionJob> jobs, IProgress<int>? progress, CancellationToken cancel)
    {
        var results = new Exception?[jobs.Count];
        for (int i = 0; i < jobs.Count; i++)
        {
            cancel.ThrowIfCancellationRequested();
            ConversionJob job = jobs[i];
            string temp = TempOutput(job.Output);
            results[i] = Try(() =>
            {
                Action<string, string, CancellationToken> convert = conversion switch
                {
                    Conversion.WordToPdf => BuiltIn.WordToPdf.Convert,
                    Conversion.ExcelToPdf => BuiltIn.ExcelToPdf.Convert,
                    Conversion.PowerPointToPdf => BuiltIn.PowerPointToPdf.Convert,
                    _ => BuiltIn.PdfToWord.Convert,
                };
                convert(job.Input, temp, cancel);
                MoveIntoPlace(temp, job.Output);
            });
            if (results[i] is not null) DeleteQuietly(temp);
            progress?.Report(i + 1);
        }
        return results;
    }

    public static string OfficeAppName(Conversion conversion) => conversion switch
    {
        Conversion.ExcelToPdf => "Excel",
        Conversion.PowerPointToPdf => "PowerPoint",
        _ => "Word",
    };

    static string OfficeProgId(Conversion conversion) => $"{OfficeAppName(conversion)}.Application";

    // Microsoft Office -------------------------------------------------------------------------

    /// <summary>The Office process this conversion started, if any. Only that process is ever stopped or quit.</summary>
    sealed class OfficeSession
    {
        public int? StartedProcessId { get; set; }
    }

    /// <summary>
    /// Office automation can block forever on a question it asks (a crash-recovery prompt, first-run setup).
    /// Word's question before it opens a PDF is turned off for the run (<see cref="OfficeRegistry"/>), since a hidden
    /// Word shows it nowhere. For the rest a watchdog checks every few seconds: when the Office instance Sheaf
    /// started shows a dialog, or runs past its time, or the user cancels, Sheaf stops that instance and says why.
    /// An Office window the user already had open is never touched.
    /// </summary>
    static async Task<IReadOnlyList<Exception?>> ConvertWithOfficeAsync(
        Conversion conversion, IReadOnlyList<ConversionJob> jobs, IProgress<int>? progress, CancellationToken cancel)
    {
        string app = OfficeAppName(conversion);
        string[] inputs = [.. jobs.Select(job => job.Input)];
        // A file Office was stopped on before would otherwise open with a question nobody sees.
        Try(() => OfficeRegistry.ForgetDisabled(app, inputs));
        IDisposable? quiet = null;
        if (conversion == Conversion.PdfToWord) Try(() => quiet = OfficeRegistry.QuietPdfPrompt());
        try
        {
            IReadOnlyList<Exception?> results = await WatchOfficeAsync(conversion, jobs, progress, cancel);
            Try(() => OfficeRegistry.ForgetDisabled(app, inputs));
            return results;
        }
        finally
        {
            quiet?.Dispose();
        }
    }

    static async Task<IReadOnlyList<Exception?>> WatchOfficeAsync(
        Conversion conversion, IReadOnlyList<ConversionJob> jobs, IProgress<int>? progress, CancellationToken cancel)
    {
        var session = new OfficeSession();
        Task<IReadOnlyList<Exception?>> work = RunOnStaThread(() => ConvertWithOffice(conversion, jobs, progress, session, cancel));
        DateTime deadline = DateTime.UtcNow + OfficeTimeoutPerFile * jobs.Count;
        string? question = null;
        while (!work.IsCompleted && !cancel.IsCancellationRequested && DateTime.UtcNow < deadline && question is null)
        {
            await Task.WhenAny(work, Task.Delay(WatchdogInterval, CancellationToken.None));
            if (session.StartedProcessId is int id) question = OfficeDialogs.FindQuestion(id);
        }
        if (work.IsCompleted) return await work;

        string app = $"Microsoft {OfficeAppName(conversion)}";
        var stalled = new ToolException(question is null
            ? $"{app} didn’t finish in time, so Sheaf stopped it. Open {OfficeAppName(conversion)} once to finish any setup it asks for, then try again."
            : $"{app} stopped to ask: “{question}” Open the file in {OfficeAppName(conversion)}, answer there, then try again.");
        if (session.StartedProcessId is null)
        {
            // Office was already open and belongs to the user; leave it alone and abandon the blocked thread.
            cancel.ThrowIfCancellationRequested();
            return [.. jobs.Select(_ => (Exception?)stalled)];
        }

        StopStartedOffice(session, conversion);
        IReadOnlyList<Exception?> results;
        try
        {
            results = await work;
        }
        catch (COMException)
        {
            // Stopped while it was still starting up, before any file was tried.
            results = [.. jobs.Select(_ => (Exception?)stalled)];
        }
        cancel.ThrowIfCancellationRequested();
        return [.. results.Select(error => error is null ? null : (Exception)stalled)];
    }

    static readonly TimeSpan OfficeTimeoutPerFile = TimeSpan.FromMinutes(2);
    static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(2);

    static IReadOnlyList<Exception?> ConvertWithOffice(Conversion conversion, IReadOnlyList<ConversionJob> jobs,
        IProgress<int>? progress, OfficeSession session, CancellationToken cancel)
    {
        var results = new Exception?[jobs.Count];
        string processName = OfficeProcessName(conversion);
        var running = OfficeProcessIds(processName);
        dynamic app = Activator.CreateInstance(Type.GetTypeFromProgID(OfficeProgId(conversion))!)!;
        session.StartedProcessId = OfficeProcessIds(processName).Except(running).Cast<int?>().FirstOrDefault();
        try
        {
            PrepareOfficeApp(app, conversion);
            for (int i = 0; i < jobs.Count; i++)
            {
                results[i] = cancel.IsCancellationRequested
                    ? new OperationCanceledException(cancel)
                    : Try(() => ConvertOneWithOffice(app, conversion, jobs[i]));
                progress?.Report(i + 1);
            }
        }
        finally
        {
            if (session.StartedProcessId is not null) Try(() => app.Quit());
            Try(() => Marshal.FinalReleaseComObject(app));
        }
        return results;
    }

    static string OfficeProcessName(Conversion conversion) => conversion switch
    {
        Conversion.ExcelToPdf => "EXCEL",
        Conversion.PowerPointToPdf => "POWERPNT",
        _ => "WINWORD",
    };

    static HashSet<int> OfficeProcessIds(string processName)
    {
        Process[] processes = Process.GetProcessesByName(processName);
        var ids = processes.Select(process => process.Id).ToHashSet();
        foreach (Process process in processes) process.Dispose();
        return ids;
    }

    static void StopStartedOffice(OfficeSession session, Conversion conversion)
    {
        if (session.StartedProcessId is not int id) return;
        Try(() =>
        {
            using Process process = Process.GetProcessById(id);
            process.Kill();
            process.WaitForExit(3000);
        });
        // Otherwise its next start would take this for a crash and ask about the file, a question nobody sees.
        Try(() => OfficeRegistry.ForgetSession(OfficeAppName(conversion), id));
    }

    static void PrepareOfficeApp(dynamic app, Conversion conversion)
    {
        if (conversion == Conversion.PowerPointToPdf) return; // PowerPoint refuses to hide its window; files open without one.
        app.Visible = false;
        if (conversion == Conversion.ExcelToPdf) app.DisplayAlerts = false;
        else app.DisplayAlerts = 0; // wdAlertsNone
    }

    static void ConvertOneWithOffice(dynamic app, Conversion conversion, ConversionJob job)
    {
        const int WordPdf = 17, WordDocx = 16, ExcelPdf = 0, PowerPointPdf = 32, MsoTrue = -1, MsoFalse = 0;
        string temp = TempOutput(job.Output);
        dynamic? document = null;
        try
        {
            switch (conversion)
            {
                case Conversion.WordToPdf:
                    document = app.Documents.Open(job.Input, false, true, false);
                    document.ExportAsFixedFormat(temp, WordPdf);
                    document.Close(0);
                    break;
                case Conversion.PdfToWord:
                    document = app.Documents.Open(job.Input, false, true, false);
                    document.SaveAs2(temp, WordDocx);
                    document.Close(0);
                    break;
                case Conversion.ExcelToPdf:
                    document = app.Workbooks.Open(job.Input, 0, true);
                    document.ExportAsFixedFormat(ExcelPdf, temp);
                    document.Close(false);
                    break;
                case Conversion.PowerPointToPdf:
                    document = app.Presentations.Open(job.Input, MsoTrue, MsoFalse, MsoFalse);
                    document.SaveAs(temp, PowerPointPdf);
                    document.Close();
                    break;
            }
            MoveIntoPlace(temp, job.Output);
        }
        catch (COMException ex)
        {
            throw new ToolException($"{OfficeAppName(conversion)} couldn’t convert “{Path.GetFileName(job.Input)}”. " +
                "Check that it opens in Office and isn’t password-protected.", ex);
        }
        finally
        {
            if (document is not null) Marshal.FinalReleaseComObject(document);
            DeleteQuietly(temp);
        }
    }

    static Task<T> RunOnStaThread<T>(Func<T> work)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { completion.SetResult(work()); }
            catch (Exception ex) { completion.SetException(ex); }
        }) { IsBackground = true, Name = "Office conversion" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    // LibreOffice ------------------------------------------------------------------------------

    static IReadOnlyList<Exception?> ConvertWithLibreOffice(
        Conversion conversion, IReadOnlyList<ConversionJob> jobs, IProgress<int>? progress, CancellationToken cancel)
    {
        var results = new Exception?[jobs.Count];
        string work = Path.Combine(Path.GetTempPath(), "Sheaf", "libreoffice-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            for (int i = 0; i < jobs.Count; i++)
            {
                cancel.ThrowIfCancellationRequested();
                results[i] = Try(() => ConvertOneWithLibreOffice(conversion, jobs[i], work, cancel));
                progress?.Report(i + 1);
            }
        }
        finally
        {
            Try(() => Directory.Delete(work, recursive: true));
        }
        return results;
    }

    static void ConvertOneWithLibreOffice(Conversion conversion, ConversionJob job, string work, CancellationToken cancel)
    {
        string outDir = Path.Combine(work, "out");
        Directory.CreateDirectory(outDir);
        var start = new ProcessStartInfo(SofficePath.Value!) { UseShellExecute = false, CreateNoWindow = true };
        foreach (string argument in LibreOfficeArguments(conversion, job.Input, work, outDir))
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start) ?? throw new ToolException("LibreOffice didn’t start.");
        using (cancel.Register(() => Try(() => process.Kill(entireProcessTree: true))))
        {
            if (!process.WaitForExit(LibreOfficeTimeout))
            {
                Try(() => process.Kill(entireProcessTree: true));
                throw new ToolException($"LibreOffice took too long to convert “{Path.GetFileName(job.Input)}”.");
            }
        }
        cancel.ThrowIfCancellationRequested();

        string produced = Path.Combine(outDir, Path.GetFileNameWithoutExtension(job.Input) + Path.GetExtension(job.Output));
        if (!File.Exists(produced))
            throw new ToolException($"LibreOffice couldn’t convert “{Path.GetFileName(job.Input)}”.");
        MoveIntoPlace(produced, job.Output);
    }

    static IEnumerable<string> LibreOfficeArguments(Conversion conversion, string input, string work, string outDir)
    {
        yield return "-env:UserInstallation=" + new Uri(Path.Combine(work, "profile")).AbsoluteUri;
        yield return "--headless";
        yield return "--norestore";
        yield return "--nolockcheck";
        if (conversion == Conversion.PdfToWord)
        {
            yield return "--infilter=writer_pdf_import";
            yield return "--convert-to";
            yield return "docx:MS Word 2007 XML";
        }
        else
        {
            yield return "--convert-to";
            yield return "pdf";
        }
        yield return "--outdir";
        yield return outDir;
        yield return input;
    }

    static string? FindLibreOffice()
    {
        string? installPath = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\LibreOffice\UNO\InstallPath", null, null) as string;
        string[] candidates =
        [
            installPath is null ? "" : Path.Combine(installPath, "soffice.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LibreOffice", "program", "soffice.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "LibreOffice", "program", "soffice.exe"),
        ];
        return candidates.FirstOrDefault(path => path.Length > 0 && File.Exists(path));
    }

    // Shared -----------------------------------------------------------------------------------

    static string TempOutput(string output)
    {
        string folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "Sheaf")).FullName;
        return Path.Combine(folder, Guid.NewGuid().ToString("N") + Path.GetExtension(output));
    }

    static void MoveIntoPlace(string produced, string output)
    {
        if (!File.Exists(produced))
            throw new ToolException($"The converter didn’t produce “{Path.GetFileName(output)}”.");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.Move(produced, output, overwrite: true);
    }

    static Exception? Try(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    static void DeleteQuietly(string path) => Try(() => File.Delete(path));
}
