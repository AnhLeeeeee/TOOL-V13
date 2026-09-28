using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace ToolTikTokV11.Services;

public sealed class CdpClient : IAsyncDisposable
{
    readonly ClientWebSocket _ws = new();
    readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    readonly CancellationTokenSource _cts = new();
    readonly SemaphoreSlim _sendGate = new(1, 1);
    long _id;
    long _beforeUnloadAutoAcceptedCount;
    Task? _reader;
    volatile bool _sessionLost;
    Exception? _terminalFailure;

    public bool Connected => !_sessionLost && _ws.State == WebSocketState.Open;
    public bool AutoAcceptBeforeUnload { get; set; }
    public long BeforeUnloadAutoAcceptedCount => Interlocked.Read(ref _beforeUnloadAutoAcceptedCount);

    public async Task ConnectAsync(string webSocketUrl, CancellationToken ct = default)
    {
        await _ws.ConnectAsync(new Uri(webSocketUrl), ct);
        _reader = Task.Run(ReadLoopAsync);
    }

    public async Task<JsonElement> CallAsync(string method, object? parameters = null, CancellationToken ct = default)
    {
        if (!Connected)
            throw _terminalFailure ?? new InvalidOperationException("[CDP_SESSION_LOST] CDP chưa kết nối.");
        var id = Interlocked.Increment(ref _id);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { id, method, @params = parameters ?? new { } });
        try
        {
            await _sendGate.WaitAsync(ct);
            try
            {
                await _ws.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Text, true, ct);
            }
            finally
            {
                _sendGate.Release();
            }
        }
        catch (Exception ex)
        {
            MarkSessionLost(ex);
            throw;
        }
        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
        return await tcs.Task.ConfigureAwait(false);
    }


    async Task SendNoWaitAsync(string method, object? parameters = null)
    {
        if (!Connected) return;
        var id = Interlocked.Increment(ref _id);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { id, method, @params = parameters ?? new { } });
        try
        {
            await _sendGate.WaitAsync(_cts.Token);
            try
            {
                if (!Connected) return;
                await _ws.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Text, true, _cts.Token);
            }
            finally
            {
                _sendGate.Release();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!_cts.IsCancellationRequested) MarkSessionLost(ex);
        }
    }

    void TryAutoAcceptBeforeUnload(JsonElement root)
    {
        if (!AutoAcceptBeforeUnload) return;
        if (!root.TryGetProperty("method", out var methodEl)
            || !string.Equals(methodEl.GetString(), "Page.javascriptDialogOpening", StringComparison.Ordinal))
            return;
        if (!root.TryGetProperty("params", out var p)) return;
        var type = p.TryGetProperty("type", out var typeEl) ? (typeEl.GetString() ?? "") : "";
        if (!string.Equals(type, "beforeunload", StringComparison.OrdinalIgnoreCase)) return;

        Interlocked.Increment(ref _beforeUnloadAutoAcceptedCount);
        _ = Task.Run(() => SendNoWaitAsync("Page.handleJavaScriptDialog", new { accept = true }));
    }

    void MarkSessionLost(Exception? cause = null)
    {
        if (_sessionLost) return;
        _sessionLost = true;
        _terminalFailure = cause is InvalidOperationException invalid && invalid.Message.Contains("[CDP_SESSION_LOST]", StringComparison.Ordinal)
            ? invalid
            : new InvalidOperationException("[CDP_SESSION_LOST] Phiên CDP/WebSocket đã đóng hoặc không còn phản hồi.", cause);
        foreach (var pending in _pending.Values) pending.TrySetException(_terminalFailure);
        _pending.Clear();
    }

    async Task ReadLoopAsync()
    {
        var buffer = new byte[64 * 1024];
        var ms = new MemoryStream();
        try
        {
            while (!_cts.IsCancellationRequested && _ws.State == WebSocketState.Open)
            {
                ms.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        MarkSessionLost(new WebSocketException("CDP WebSocket đã đóng bởi Chrome."));
                        return;
                    }
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                using var doc = JsonDocument.Parse(ms.ToArray());
                var root = doc.RootElement;
                if (root.TryGetProperty("id", out var idEl) && _pending.TryRemove(idEl.GetInt64(), out var tcs))
                {
                    if (root.TryGetProperty("error", out var err))
                        tcs.TrySetException(new InvalidOperationException(err.ToString()));
                    else if (root.TryGetProperty("result", out var res))
                        tcs.TrySetResult(res.Clone());
                    else tcs.TrySetResult(default);
                }
                else
                {
                    // beforeunload là dialog native của Chrome, không nằm trong DOM TikTok.
                    // Khi VIDEO đã arm guard, xử lý ngay ở tầng CDP để Page.navigate không
                    // bị treo chờ người dùng tự bấm "Rời khỏi".
                    TryAutoAcceptBeforeUnload(root);
                }
            }
            if (!_cts.IsCancellationRequested) MarkSessionLost();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!_cts.IsCancellationRequested) MarkSessionLost(ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        foreach (var p in _pending.Values) p.TrySetCanceled();
        _pending.Clear();
        try
        {
            if (_ws.State == WebSocketState.Open)
            {
                using var closeCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(800));
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", closeCts.Token);
            }
        }
        catch { }
        if (_reader is not null)
        {
            try { await Task.WhenAny(_reader, Task.Delay(800)); } catch { }
        }
        try { _ws.Abort(); } catch { }
        _ws.Dispose();
        _sendGate.Dispose();
        _cts.Dispose();
    }
}
