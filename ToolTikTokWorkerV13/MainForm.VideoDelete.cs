using System.Text;
using System.Text.Json;
using ToolTikTokV11.Services;

namespace ToolTikTokV11;

public sealed partial class MainForm
{
    readonly object _videoDeleteSync = new();
    CancellationTokenSource? _videoDeleteCts;
    Task? _videoDeleteTask;
    TikTokVideoDeleteProgress _videoDeleteProgress = new(
        false,
        "IDLE",
        0,
        0,
        -1,
        "Chưa chạy",
        false,
        false,
        false,
        "");

    bool IsVideoDeleteRunning
    {
        get
        {
            lock (_videoDeleteSync)
                return _videoDeleteTask is { IsCompleted: false } && _videoDeleteProgress.Running;
        }
    }

    string BuildManagedVideoDeleteStatusResponse()
    {
        lock (_videoDeleteSync)
            return JsonSerializer.Serialize(_videoDeleteProgress);
    }

    async Task<string> StartManagedVideoDeleteAsync(string commandPayload)
    {
        if (IsManagerEmergencyStopActive()) return "emergency_stopped";
        if (IsVideoDeleteRunning) return "already_running";
        if (IsVideoUploadRunning) return "video_upload_running";
        if (IsMessageReplyRunning) return "message_reply_running";
        if (_engine.Running)
        {
            _engine.Stop("Chuẩn bị xóa video TikTok");
            if (!await _engine.WaitForStopAsync(TimeSpan.FromSeconds(20)))
                return "automation_running";
        }
        if (!_chrome.Connected) return "chrome_not_connected";
        if (string.IsNullOrWhiteSpace(commandPayload)) return "invalid_payload";

        ManagedVideoDeleteRequest request;
        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(commandPayload));
            request = JsonSerializer.Deserialize<ManagedVideoDeleteRequest>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidOperationException("Payload xóa video TikTok không hợp lệ.");
        }
        catch (Exception ex)
        {
            _log.Warn("[VIDEO_DELETE_PAYLOAD] " + ex.Message);
            return "invalid_payload";
        }

        if (!await _chrome.EnsureTikTokIdentitySessionReadyAsync())
            return "not_logged_in";

        var cts = new CancellationTokenSource();
        lock (_videoDeleteSync)
        {
            try { _videoDeleteCts?.Dispose(); } catch { }
            _videoDeleteCts = cts;
            _videoDeleteProgress = new TikTokVideoDeleteProgress(
                true,
                "STARTING",
                0,
                0,
                -1,
                "Đang khởi động xử lý xóa video/bài cũ...",
                false,
                false,
                false,
                "");
        }

        _videoDeleteTask = Task.Run(async () =>
        {
            try
            {
                var result = await _chrome.DeleteTikTokProfilePostsAsync(
                    request.Username,
                    request.DeleteMode,
                    progress =>
                    {
                        lock (_videoDeleteSync)
                            _videoDeleteProgress = progress;
                    },
                    cts.Token);

                lock (_videoDeleteSync)
                {
                    _videoDeleteProgress = new TikTokVideoDeleteProgress(
                        true,
                        result.Ok ? "FINALIZING" : "FINALIZING_ERROR",
                        result.InitialCount,
                        result.DeletedCount,
                        result.RemainingCount,
                        result.Ok ? result.Message : result.Error,
                        false,
                        result.Ok,
                        result.VerifiedEmpty,
                        result.Error);
                }
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                lock (_videoDeleteSync)
                {
                    _videoDeleteProgress = _videoDeleteProgress with
                    {
                        Running = true,
                        Stage = "FINALIZING_STOPPED",
                        Message = "Đã dừng xử lý xóa video; đang dọn trạng thái...",
                        Completed = false,
                        Ok = false,
                        Error = "Đã dừng theo yêu cầu."
                    };
                }
                _log.Warn("[VIDEO_DELETE_STOPPED] operation cancelled");
            }
            catch (Exception ex)
            {
                _log.Warn("[VIDEO_DELETE_RUN] " + ex.Message);
                lock (_videoDeleteSync)
                {
                    _videoDeleteProgress = _videoDeleteProgress with
                    {
                        Running = true,
                        Stage = "FINALIZING_ERROR",
                        Message = ex.Message,
                        Completed = false,
                        Ok = false,
                        Error = ex.Message
                    };
                }
            }
            finally
            {
                // Best-effort cleanup: lỗi/timeout không được để popup Xóa giữ Chrome ở trạng thái kẹt,
                // đồng thời cờ VideoDeleteRunning phải luôn được hạ để Manager/Auto Run lấy lại profile.
                try
                {
                    using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                    await _chrome.CleanupTikTokVideoOperationAsync(cleanupCts.Token);
                }
                catch (Exception ex)
                {
                    _log.Warn("[VIDEO_DELETE_CLEANUP] " + ex.Message);
                }

                lock (_videoDeleteSync)
                {
                    var finalStage = _videoDeleteProgress.Stage switch
                    {
                        "FINALIZING" => "COMPLETED",
                        "FINALIZING_STOPPED" => "STOPPED",
                        "FINALIZING_ERROR" => "ERROR",
                        _ => _videoDeleteProgress.Ok ? "COMPLETED" : "ERROR"
                    };
                    _videoDeleteProgress = _videoDeleteProgress with
                    {
                        Running = false,
                        Stage = finalStage,
                        Completed = true
                    };
                }

                _log.Info("[VIDEO_DELETE_RELEASED] busy=false");
            }
        });

        return "started";
    }

    string StopManagedVideoDelete()
    {
        lock (_videoDeleteSync)
        {
            if (_videoDeleteTask is null || _videoDeleteTask.IsCompleted)
                return "not_running";

            try { _videoDeleteCts?.Cancel(); } catch { }
            _videoDeleteProgress = _videoDeleteProgress with
            {
                Stage = "STOPPING",
                Message = "Đang dừng sau thao tác hiện tại..."
            };
            return "stopping";
        }
    }
}
