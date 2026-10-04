namespace CommentVisibilityMonitor;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "CommentCheckData");
        Directory.CreateDirectory(dataDir);

        Application.ThreadException += (_, e) =>
            DiagnosticExporter.AppendCrash(dataDir, "Application.ThreadException", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            DiagnosticExporter.AppendCrash(
                dataDir,
                "AppDomain.UnhandledException",
                e.ExceptionObject as Exception,
                e.ExceptionObject?.ToString());
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            DiagnosticExporter.AppendCrash(dataDir, "TaskScheduler.UnobservedTaskException", e.Exception);
            e.SetObserved();
        };

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
