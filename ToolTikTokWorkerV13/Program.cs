using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace ToolTikTokV11;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        // File picker helper chạy trong process Worker sạch, không tạo MainForm/IPC.
        // Mục đích: cách ly native Windows file dialog khỏi Worker automation.
        if (TryRunConfigFilePickerHelper(args))
            return;
        var options = StartupOptions.Parse(args);

        Mutex? workerMutex = null;

        if (options.Worker && !string.IsNullOrWhiteSpace(options.PipeName))
        {
            // Tương thích cả Worker cũ chưa có mutex:
            // nếu pipe của profile đã trả lời thì KHÔNG tạo form/Worker thứ hai.
            if (ExistingWorkerRespondsAsync(options, initialProbe: true).GetAwaiter().GetResult())
            {
                RunExistingWorkerLeaseProxyAsync(options).GetAwaiter().GetResult();
                return;
            }

            var mutexName = BuildWorkerMutexName(options);
            workerMutex = new Mutex(
                initiallyOwned: true,
                name: mutexName,
                createdNew: out var createdNew);

            if (!createdNew)
            {
                // Một Worker bản mới khác đang khởi động/đang chạy.
                // Chờ pipe của Worker đó rồi chuyển thành process lease proxy nhẹ.
                if (WaitForExistingWorkerPipeAsync(options, TimeSpan.FromSeconds(12)).GetAwaiter().GetResult())
                {
                    RunExistingWorkerLeaseProxyAsync(options).GetAwaiter().GetResult();
                }

                workerMutex.Dispose();
                return;
            }
        }

        try
        {
            if (options.ManagedMode)
                ManagedDataBootstrap.Ensure(options);

            using var form = new MainForm(options);
            using var ipc = options.Worker && !string.IsNullOrWhiteSpace(options.PipeName)
                ? new WorkerIpcServer(options.PipeName, form)
                : null;

            if (ipc is not null)
                form.Shown += (_, _) => ipc.Start();

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
        finally
        {
            if (workerMutex is not null)
            {
                try { workerMutex.ReleaseMutex(); } catch { }
                workerMutex.Dispose();
            }
        }
    }

    static bool TryRunConfigFilePickerHelper(string[] args)
    {
        var openMode = args.Any(a => string.Equals(a, "--config-picker-open", StringComparison.OrdinalIgnoreCase));
        var saveMode = args.Any(a => string.Equals(a, "--config-picker-save", StringComparison.OrdinalIgnoreCase));
        if (!openMode && !saveMode)
            return false;

        var resultFile = GetArgValue(args, "--result-file");
        if (string.IsNullOrWhiteSpace(resultFile))
            return true;

        try
        {
            string? selected = null;

            if (openMode)
            {
                using var dialog = new OpenFileDialog
                {
                    Title = "Nhập cấu hình V13",
                    Filter = "Gói cấu hình ZIP|*.zip|Tất cả file|*.*",
                    CheckFileExists = true,
                    Multiselect = false,
                    RestoreDirectory = true,
                    // Giữ giao diện File Dialog hiện đại của Windows; helper process riêng vẫn tránh treo Worker chính.
                    AutoUpgradeEnabled = true
                };

                if (dialog.ShowDialog() == DialogResult.OK)
                    selected = dialog.FileName;
            }
            else
            {
                using var dialog = new SaveFileDialog
                {
                    Title = "Xuất cấu hình V13",
                    Filter = "Gói cấu hình ZIP|*.zip",
                    FileName = $"TikTok_V13_Config_{DateTime.Now:yyyyMMdd_HHmm}.zip",
                    AddExtension = true,
                    DefaultExt = "zip",
                    RestoreDirectory = true,
                    AutoUpgradeEnabled = true
                };

                if (dialog.ShowDialog() == DialogResult.OK)
                    selected = dialog.FileName;
            }

            if (!string.IsNullOrWhiteSpace(selected))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(resultFile))!);
                File.WriteAllText(resultFile, selected, new UTF8Encoding(false));
            }
        }
        catch (Exception ex)
        {
            try
            {
                File.WriteAllText(resultFile + ".error", ex.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }

        return true;
    }

    static string? GetArgValue(string[] args, string name)
    {
        for (var i = 0; i + 1 < args.Length; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }

        return null;
    }

    static string BuildWorkerMutexName(StartupOptions options)
    {
        var identity =
            ((options.ProfileName ?? "") + "|" + (options.ProfilePath ?? ""))
            .Trim()
            .ToUpperInvariant();

        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(identity)));

        return @"Local\ToolTikTokV13_Worker_" + hash[..24];
    }

    static async Task<bool> WaitForExistingWorkerPipeAsync(
        StartupOptions options,
        TimeSpan timeout)
    {
        var end = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < end)
        {
            if (await ExistingWorkerRespondsAsync(options, initialProbe: false))
                return true;

            await Task.Delay(180);
        }

        return false;
    }

    static async Task<bool> ExistingWorkerRespondsAsync(
        StartupOptions options,
        bool initialProbe)
    {
        // Probe đầu nhiều lần để tránh đúng lúc Worker cũ đang bận một lệnh IPC.
        var attempts = initialProbe ? 10 : 1;

        for (var attempt = 0; attempt < attempts; attempt++)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(
                    ".",
                    options.PipeName,
                    PipeDirection.InOut,
                    PipeOptions.Asynchronous);

                using var cts = new CancellationTokenSource(
                    TimeSpan.FromMilliseconds(initialProbe ? 450 : 300));

                await pipe.ConnectAsync(cts.Token);

                using var reader = new StreamReader(
                    pipe,
                    Encoding.UTF8,
                    false,
                    4096,
                    leaveOpen: true);

                using var writer = new StreamWriter(
                    pipe,
                    new UTF8Encoding(false),
                    4096,
                    leaveOpen: true)
                {
                    AutoFlush = true
                };

                await writer.WriteLineAsync("ping");
                var response = await reader.ReadLineAsync(cts.Token);

                if (string.Equals(
                        response,
                        "pong",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch
            {
                // Worker chưa sẵn sàng hoặc pipe đang bận; thử lại.
            }

            if (attempt + 1 < attempts)
                await Task.Delay(120);
        }

        return false;
    }

    static async Task RunExistingWorkerLeaseProxyAsync(StartupOptions options)
    {
        // Manager vừa spawn process này nhưng profile đã có Worker thật.
        // Giữ process nhẹ sống để Manager không tưởng Worker vừa thoát;
        // mọi IPC vẫn đi thẳng tới Worker thật qua pipe cũ.
        var misses = 0;

        while (misses < 4)
        {
            await Task.Delay(1500);

            if (await ExistingWorkerRespondsAsync(options, initialProbe: false))
                misses = 0;
            else
                misses++;
        }
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
        foreach (var file in Directory.EnumerateFiles(
                     source,
                     "*",
                     SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (!File.Exists(target))
                File.Copy(file, target, false);
        }
    }
}
