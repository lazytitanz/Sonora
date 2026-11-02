namespace Sonora.TestPlayer;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ReportUnhandled("UI thread", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            ReportUnhandled("background thread", e.ExceptionObject as Exception ?? new InvalidOperationException(e.ExceptionObject?.ToString()));
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            System.Diagnostics.Debug.WriteLine($"[Program] Unobserved task exception: {e.Exception}");
            e.SetObserved();
        };

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm(Environment.GetCommandLineArgs().Skip(1).ToArray()));
    }

    private static void ReportUnhandled(string where, Exception exception)
    {
        try
        {
            MessageBox.Show(
                $"An unhandled error occurred on the {where}.\n\n{MainForm.DescribeException(exception)}",
                "Sonora Test Player",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Program] Could not show unhandled error dialog: {ex}");
        }
    }
}
