namespace ToolTikTokManagerV13;

public sealed partial class ManagerForm
{
    // Mọi thao tác đọc/ghi Kho tài khoản dùng cho Auto Profile/BAN đi qua một hàng đợi nền.
    // Mục tiêu: không nén/ghi XLSX trên WinForms UI thread và không để hai thao tác cùng sửa file.
    readonly SemaphoreSlim _accountPoolBackgroundIoGate = new(1, 1);

    async Task RunAccountPoolIoAsync(Action action, CancellationToken ct)
    {
        await _accountPoolBackgroundIoGate.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            await Task.Run(action);
            ct.ThrowIfCancellationRequested();
        }
        finally
        {
            _accountPoolBackgroundIoGate.Release();
        }
    }

    async Task<T> RunAccountPoolIoAsync<T>(Func<T> action, CancellationToken ct)
    {
        await _accountPoolBackgroundIoGate.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            var result = await Task.Run(action);
            ct.ThrowIfCancellationRequested();
            return result;
        }
        finally
        {
            _accountPoolBackgroundIoGate.Release();
        }
    }
}
