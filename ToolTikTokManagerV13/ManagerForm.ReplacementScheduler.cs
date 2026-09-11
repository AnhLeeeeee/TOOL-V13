using System.Threading;

namespace ToolTikTokManagerV13;

public sealed partial class ManagerForm
{
    // V13.8.6-style AutoClose không dùng TIME scheduler/slot 30 phút.
    // Chỉ giữ gate của luồng Tự bù vì AutoReplacement hiện tại vẫn dùng nó
    // để tránh hai lần tạo/lấy PRF bù chạy chồng nhau. Gate này KHÔNG chặn AutoClose.
    readonly SemaphoreSlim _autoReplacementOperationGate = new(1, 1);


    // Compatibility cho LoginBan của các bản mới hơn. V13.8.6-style không còn
    // hàng đợi TIME nên không có gì cần hủy.
    void CancelTimeReplacementForBan(string profileName, string detail)
    {
    }

    async Task<IDisposable> EnterReplacementOperationAsync(
        string profileName,
        string reason)
    {
        var waited = _autoReplacementOperationGate.CurrentCount == 0;
        if (waited)
        {
            _log.Info(
                $"[REPLACEMENT_OPERATION_WAIT] profile={profileName} reason={reason} detail=another_replacement_running");
        }

        await _autoReplacementOperationGate.WaitAsync();

        _log.Info(
            $"[REPLACEMENT_OPERATION_ENTER] profile={profileName} reason={reason} waited={waited}");

        return new ReplacementOperationLease(
            _autoReplacementOperationGate,
            () => _log.Info(
                $"[REPLACEMENT_OPERATION_EXIT] profile={profileName} reason={reason}"));
    }

    sealed class ReplacementOperationLease : IDisposable
    {
        readonly SemaphoreSlim _gate;
        readonly Action _onDispose;
        int _disposed;

        public ReplacementOperationLease(SemaphoreSlim gate, Action onDispose)
        {
            _gate = gate;
            _onDispose = onDispose;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            try { _onDispose(); } catch { }
            _gate.Release();
        }
    }
}
