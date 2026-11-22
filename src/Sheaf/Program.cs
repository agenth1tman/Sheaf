using Sheaf.Core.Fonts;
using Sheaf.Platform;
using Sheaf.Shell;
using Sheaf.Ui;

namespace Sheaf;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        var launch = LaunchOptions.Parse(args);
        if (launch.SandboxFolder is not null) AppPaths.UseSandbox(launch.SandboxFolder);

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ReportCrash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportCrash(e.ExceptionObject as Exception);

        using SingleInstance instance = SingleInstance.Claim();
        if (!instance.IsFirst && instance.SendToFirst(launch.ForwardedArguments())) return;
        Task fonts = Task.Run(() => AppFonts.Body);
        EmbeddedFonts.UseForPdfOutput();
        AppSettings settings = SettingsStore.Load();
        Theme.Use(settings.Theme);
        ActivityLog.Write($"Started {Application.ProductVersion}{(AppPaths.IsSandbox ? " in sandbox " + AppPaths.DataFolder : "")}");
        fonts.Wait();
        Task.Run(AppPaths.CleanOldWork);

        using var form = new MainWindow(settings, launch);
        if (instance.IsFirst) instance.Listen(received => form.BeginInvoke(() => form.BringToFront(received)));
        Application.Run(form);
    }

    static void ReportCrash(Exception? ex)
    {
        ActivityLog.Error("unhandled", ex ?? new Exception("Unknown error"));
        MessageBox.Show(
            $"Something went wrong: {ex?.Message}\n\nThe details are in {AppPaths.LogFile}.",
            "Sheaf", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}
