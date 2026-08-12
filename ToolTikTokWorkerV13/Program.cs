namespace ToolTikTokV11;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var options = StartupOptions.Parse(args);
        if (options.ManagedMode)
            ManagedDataBootstrap.Ensure(options);

        using var form = new MainForm(options);
        using var ipc = options.Worker && !string.IsNullOrWhiteSpace(options.PipeName)
            ? new WorkerIpcServer(options.PipeName, form)
            : null;
        if (ipc is not null) form.Shown += (_, _) => ipc.Start();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            if (!form.IsDisposed)
            {
                try { form.BeginInvoke(new Action(form.Close)); } catch { }
            }
        };
        Application.Run(form);
    }
}

static class ManagedDataBootstrap
{
    public static void Ensure(StartupOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ProfileName))
            throw new InvalidOperationException("Managed worker thiếu --profile.");
        if (string.IsNullOrWhiteSpace(options.ProfilePath))
            throw new InvalidOperationException("Managed worker thiếu --profile-path.");
        if (!Directory.Exists(options.ProfilePath))
            Directory.CreateDirectory(options.ProfilePath);

        var dataRoot = string.IsNullOrWhiteSpace(options.DataRoot)
            ? Path.Combine(AppContext.BaseDirectory, "profiles", options.ProfileName)
            : Path.GetFullPath(options.DataRoot);
        Directory.CreateDirectory(dataRoot);

        var defaults = Path.Combine(AppContext.BaseDirectory, "defaults");
        if (!Directory.Exists(defaults)) return;
        CopyTreeMissing(defaults, dataRoot);
    }

    static void CopyTreeMissing(string source, string destination)
    {
        // V13.4: chỉ tạo thư mục khi thực sự có file mặc định cần copy.
        // Tránh nhân bản các thư mục legacy rỗng vào mọi profile trên VM.
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (!File.Exists(target)) File.Copy(file, target, false);
        }
    }
}
