namespace CommentVisibilityMonitor;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        // Comment Check uses one dedicated Observer profile + one fixed CDP port.
        // Running two monitor instances would make both processes fight for the
        // same ObserverChrome directory, so reject duplicates at process level.
        using var singleInstance = new Mutex(
            true,
            @"Local\ToolTikTok.CommentVisibilityMonitor.ObserverV1",
            out var createdNew);

        if (!createdNew)
        {
            MessageBox.Show(
                "CHECK CMT đang chạy ở một cửa sổ khác. Hãy dùng cửa sổ đang mở thay vì chạy thêm một bản nữa.",
                "Check CMT",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

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
        try
        {
            Application.Run(new MainForm());
        }
        finally
        {
            try { singleInstance.ReleaseMutex(); } catch { }
        }
    }
}
