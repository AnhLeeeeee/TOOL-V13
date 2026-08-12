using System.IO.Pipes;
using System.Text;

namespace ToolTikTokV11;

public sealed class WorkerIpcServer : IDisposable
{
    readonly string _pipeName;
    readonly MainForm _form;
    readonly CancellationTokenSource _cts = new();
    Task? _loop;

    public WorkerIpcServer(string pipeName, MainForm form)
    {
        _pipeName = pipeName;
        _form = form;
    }

    public void Start() => _loop ??= Task.Run(() => LoopAsync(_cts.Token));

    async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(
                    _pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync(ct);
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
                var command = await reader.ReadLineAsync(ct) ?? "";
                var response = await _form.HandleManagedCommandAsync(command);
                await writer.WriteLineAsync(response);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (ObjectDisposedException) when (ct.IsCancellationRequested) { break; }
            catch
            {
                if (!ct.IsCancellationRequested) await Task.Delay(100, ct);
            }
        }
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { _loop?.Wait(500); } catch { }
        _cts.Dispose();
    }
}
