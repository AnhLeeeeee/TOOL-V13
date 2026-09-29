using System.Text;
using System.Text.Json;
using ToolTikTokV11.Services;

namespace ToolTikTokV11;

public sealed partial class MainForm
{
    readonly object _videoUploadSync = new();
    CancellationTokenSource? _videoUploadCts;
    Task? _videoUploadTask;
    TikTokVideoUploadProgress _videoUploadProgress = new(
        false,
        "IDLE",
        "",
        "",
        "Chưa chạy",
        false,
        false,
        false,
        false,
        false,
        "");

    bool IsVideoUploadRunning
    {
        get
        {
            lock (_videoUploadSync)
                return _videoUploadTask is { IsCompleted: false } && _videoUploadProgress.Running;
        }
    }

    bool IsVideoOperationRunning => IsVideoDeleteRunning || IsVideoUploadRunning;

    string BuildManagedVideoUploadStatusResponse()
    {
        lock (_videoUploadSync)
            return JsonSerializer.Serialize(_videoUploadProgress);
    }

    sealed class ManagedVideoUploadRequest
    {
        public string Username { get; set; } = "";
        public string VideoPath { get; set; } = "";
        public string Caption { get; set; } = "";
        public bool StudioDeleteFallback { get; set; }
        public bool StudioCleanupOnly { get; set; }
    }

    async Task<string> StartManagedVideoUploadAsync(string commandPayload)
    {
        if (IsManagerEmergencyStopActive()) return "emergency_stopped";
        if (IsVideoUploadRunning) return "already_running";
        if (IsVideoDeleteRunning) return "video_delete_running";
        if (IsMessageReplyRunning) return "message_reply_running";
        if (_engine.Running)
        {
            _engine.Stop("Chuẩn bị đăng video TikTok");
            if (!await _engine.WaitForStopAsync(TimeSpan.FromSeconds(20)))
                return "automation_running";
        }
        if (!_chrome.Connected) return "chrome_not_connected";
        if (string.IsNullOrWhiteSpace(commandPayload)) return "invalid_payload";

        ManagedVideoUploadRequest request;
        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(commandPayload));
            request = JsonSerializer.Deserialize<ManagedVideoUploadRequest>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidOperationException("Payload đăng video TikTok không hợp lệ.");
        }
        catch (Exception ex)
        {
            _log.Warn("[VIDEO_UPLOAD_PAYLOAD] " + ex.Message);
            return "invalid_payload";
        }

        if (!request.StudioCleanupOnly
            && (string.IsNullOrWhiteSpace(request.VideoPath) || !File.Exists(request.VideoPath)))
            return "video_not_found";

        if (!await _chrome.EnsureTikTokIdentitySessionReadyAsync())
            return "not_logged_in";

        var cts = new CancellationTokenSource();
        lock (_videoUploadSync)
        {
            try { _videoUploadCts?.Dispose(); } catch { }
            _videoUploadCts = cts;
            _videoUploadProgress = new TikTokVideoUploadProgress(
                true,
                "STARTING",
                request.VideoPath,
                "",
                "Đang khởi động đăng video...",
                false,
                false,
                false,
                false,
                false,
                "");
        }

        _videoUploadTask = Task.Run(async () =>
        {
            try
            {
                var result = request.StudioCleanupOnly
                    ? await _chrome.CleanupTikTokStudioOldPostsKeepingFirstAsync(
                        progress =>
                        {
                            lock (_videoUploadSync)
                                _videoUploadProgress = progress;
                        },
                        cts.Token)
                    : await _chrome.UploadTikTokVideoAsync(
                        request.Username,
                        request.VideoPath,
                        request.Caption,
                        request.StudioDeleteFallback,
                        progress =>
                        {
                            lock (_videoUploadSync)
                                _videoUploadProgress = progress;
                        },
                        cts.Token);

                lock (_videoUploadSync)
                {
                    _videoUploadProgress = new TikTokVideoUploadProgress(
                        true,
                        result.Ok ? "FINALIZING" : result.Posted ? "FINALIZING_PARTIAL" : "FINALIZING_ERROR",
                        result.VideoPath,
                        result.PostedHref,
                        result.Ok ? result.Message : result.Error,
                        false,
                        result.Ok,
                        result.Posted,
                        result.PrivacyUpdated,
                        result.ProfileVerified,
                        result.Error)
                    {
                        DeleteFallbackAttempted = result.DeleteFallbackAttempted,
                        DeleteFallbackSucceeded = result.DeleteFallbackSucceeded,
                        DeleteFallbackDeletedCount = result.DeleteFallbackDeletedCount,
                        DeleteFallbackRemainingCount = result.DeleteFallbackRemainingCount,
                        DeleteFallbackError = result.DeleteFallbackError
                    };
                }
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                lock (_videoUploadSync)
                {
                    _videoUploadProgress = _videoUploadProgress with
                    {
                        Running = true,
                        Stage = "FINALIZING_STOPPED",
                        Message = "Đã dừng đăng video; đang nhường profile về luồng hiện tại...",
                        Completed = false,
                        Ok = false,
                        Error = "Đã dừng theo yêu cầu."
                    };
                }
                _log.Warn("[VIDEO_UPLOAD_STOPPED] operation cancelled");
            }
            catch (Exception ex)
            {
                _log.Warn("[VIDEO_UPLOAD_RUN] " + ex.Message);
                lock (_videoUploadSync)
                {
                    _videoUploadProgress = _videoUploadProgress with
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
                try
                {
                    using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                    await _chrome.CleanupTikTokVideoOperationAsync(cleanupCts.Token);
                }
                catch (Exception ex)
                {
                    _log.Warn("[VIDEO_UPLOAD_CLEANUP] " + ex.Message);
                }

                lock (_videoUploadSync)
                {
                    var finalStage = _videoUploadProgress.Stage switch
                    {
                        "FINALIZING" => "COMPLETED",
                        "FINALIZING_PARTIAL" => "PARTIAL",
                        "FINALIZING_STOPPED" => "STOPPED",
                        "FINALIZING_ERROR" => "ERROR",
                        _ => _videoUploadProgress.Ok ? "COMPLETED" : _videoUploadProgress.Posted ? "PARTIAL" : "ERROR"
                    };
                    _videoUploadProgress = _videoUploadProgress with
                    {
                        Running = false,
                        Stage = finalStage,
                        Completed = true
                    };
                }

                _log.Info("[VIDEO_UPLOAD_RELEASED] busy=false");
            }
        });

        return "started";
    }

    string StopManagedVideoUpload()
    {
        lock (_videoUploadSync)
        {
            if (_videoUploadTask is null || _videoUploadTask.IsCompleted)
                return "not_running";

            try { _videoUploadCts?.Cancel(); } catch { }
            _videoUploadProgress = _videoUploadProgress with
            {
                Stage = "STOPPING",
                Message = "Đang dừng sau thao tác hiện tại..."
            };
            return "stopping";
        }
    }
}
