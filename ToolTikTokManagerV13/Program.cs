using ToolTikTokV12.Utils;

namespace ToolTikTokManagerV13;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        ManagerProcessDiagnostics.Append(
            $"[MANAGER_PROCESS_MAIN_START] time={DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} pid={Environment.ProcessId} app={AppVersionInfo.Display}");

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            ManagerProcessDiagnostics.Append(
                $"[MANAGER_UNHANDLED_EXCEPTION] pid={Environment.ProcessId} terminating={e.IsTerminating} exception={ManagerProcessDiagnostics.OneLine(e.ExceptionObject?.ToString())}");
        };

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            ManagerProcessDiagnostics.Append(
                $"[MANAGER_PROCESS_EXIT] time={DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} pid={Environment.ProcessId} exitCode={Environment.ExitCode}");
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            ManagerProcessDiagnostics.Append(
                $"[MANAGER_UNOBSERVED_TASK_EXCEPTION] pid={Environment.ProcessId} observed={e.Observed} exception={ManagerProcessDiagnostics.ExceptionOneLine(e.Exception)}");
        };

        Application.ApplicationExit += (_, _) =>
        {
            ManagerProcessDiagnostics.Append(
                $"[MANAGER_APPLICATION_EXIT] time={DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} pid={Environment.ProcessId}");
        };

        ApplicationConfiguration.Initialize();

        try
        {
            Application.Run(new ManagerForm());
            ManagerProcessDiagnostics.Append(
                $"[MANAGER_APPLICATION_RUN_RETURNED] time={DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} pid={Environment.ProcessId}");
        }
        catch (Exception ex)
        {
            ManagerProcessDiagnostics.Append(
                $"[MANAGER_MAIN_FATAL_EXCEPTION] time={DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} pid={Environment.ProcessId} exception={ManagerProcessDiagnostics.ExceptionOneLine(ex)}");
            throw;
        }
        finally
        {
            ManagerProcessDiagnostics.Append(
                $"[MANAGER_MAIN_FINALLY] time={DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} pid={Environment.ProcessId}");
        }
    }
}
