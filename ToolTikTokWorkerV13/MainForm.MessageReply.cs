using System.Text;
using System.Text.Json;
using ToolTikTokV11.Models;

namespace ToolTikTokV11;

public sealed partial class MainForm
{
    readonly object _messageReplySync = new();
    CancellationTokenSource? _messageReplyCts;
    Task? _messageReplyTask;
    TikTokMessageReplyProgress _messageReplyProgress = new(
        false, "IDLE", 0, 0, 0, 0, 0, 0, "", "Chưa chạy", false, false);
    readonly List<string> _messageReplyJournal = new();
    const int MaxMessageReplyJournalLines = 500;

    void AppendMessageReplyJournal(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {message}";
        lock (_messageReplySync)
        {
            _messageReplyJournal.Add(line);
            if (_messageReplyJournal.Count > MaxMessageReplyJournalLines)
                _messageReplyJournal.RemoveRange(0, _messageReplyJournal.Count - MaxMessageReplyJournalLines);
        }
    }

    string BuildManagedMessageReplyLogResponse()
    {
        lock (_messageReplySync)
            return JsonSerializer.Serialize(_messageReplyJournal.ToArray());
    }

    bool IsMessageReplyRunning
    {
        get
        {
            lock (_messageReplySync)
                return _messageReplyTask is { IsCompleted: false } && _messageReplyProgress.Running;
        }
    }

    string BuildManagedMessageReplyStatusResponse()
    {
        lock (_messageReplySync)
            return JsonSerializer.Serialize(_messageReplyProgress);
    }

    async Task<string> StartManagedMessageReplyAsync(string commandPayload)
    {
        if (IsMessageReplyRunning) return "already_running";
        // Chế độ tự động sẽ PAUSE automation LIVE trước khi xử lý tin nhắn.
        // Chỉ chặn khi engine vẫn đang chạy thực sự; engine đang Paused thì an toàn để điều hướng /messages.
        if (_engine.Running && !_engine.Paused) return "automation_running";
        if (!_chrome.Connected) return "chrome_not_connected";
        if (!await _chrome.IsTikTokSessionActiveAsync()) return "not_logged_in";
        if (string.IsNullOrWhiteSpace(commandPayload)) return "invalid_payload";

        TikTokMessageReplyOptions options;
        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(commandPayload));
            options = JsonSerializer.Deserialize<TikTokMessageReplyOptions>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidOperationException("Payload tin nhắn không hợp lệ.");
        }
        catch (Exception ex)
        {
            _log.Warn("[MESSAGE_REPLY_PAYLOAD] " + ex.Message);
            return "invalid_payload";
        }

        options.HistoryPath = Path.Combine(_baseDir, "message_reply_history.json");
        var cts = new CancellationTokenSource();
        lock (_messageReplySync)
        {
            try { _messageReplyCts?.Dispose(); } catch { }
            _messageReplyCts = cts;
            _messageReplyJournal.Clear();
            _messageReplyProgress = new TikTokMessageReplyProgress(
                true, "STARTING", 0, 0, 0, 0, 0, 0, "", "Đang khởi động xử lý tin nhắn...", false, false);
        }
        AppendMessageReplyJournal("[STARTING] Khởi động module Tin nhắn TikTok.");

        _messageReplyTask = Task.Run(async () =>
        {
            try
            {
                TikTokMessageReplyProgress? terminalProgress = null;
                var result = await _chrome.ProcessTikTokMessageRequestsAsync(options, progress =>
                {
                    lock (_messageReplySync)
                    {
                        if (options.ReturnToPreviousPage && progress.Completed)
                        {
                            // Controller còn phải chạy finally để quay lại URL LIVE. Không báo Completed
                            // sớm cho Manager, tránh Manager Resume engine trong lúc Chrome vẫn đang điều hướng.
                            terminalProgress = progress;
                            _messageReplyProgress = progress with
                            {
                                Running = true,
                                Stage = "RETURNING_LIVE",
                                Message = "Đã xử lý xong Tin nhắn; đang quay lại trang LIVE trước khi tiếp tục...",
                                Completed = false
                            };
                        }
                        else
                        {
                            _messageReplyProgress = progress;
                        }
                    }
                }, cts.Token, AppendMessageReplyJournal);

                if (options.ReturnToPreviousPage)
                {
                    lock (_messageReplySync)
                    {
                        var final = terminalProgress ?? _messageReplyProgress;
                        _messageReplyProgress = final with
                        {
                            Running = false,
                            Completed = true,
                            Cancelled = result.Cancelled
                        };
                    }
                    AppendMessageReplyJournal("[RETURNING_LIVE_DONE] Đã hoàn tất bước quay lại trang trước; Manager có thể Resume LIVE.");
                }
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                AppendMessageReplyJournal("[STOPPED] Đã dừng xử lý tin nhắn theo yêu cầu.");
                lock (_messageReplySync)
                    _messageReplyProgress = _messageReplyProgress with
                    {
                        Running = false,
                        Stage = "STOPPED",
                        Message = "Đã dừng xử lý tin nhắn.",
                        Completed = true,
                        Cancelled = true
                    };
            }
            catch (Exception ex)
            {
                _log.Warn("[MESSAGE_REPLY_RUN] " + ex.Message);
                AppendMessageReplyJournal("[ERROR] " + ex.Message);
                lock (_messageReplySync)
                    _messageReplyProgress = _messageReplyProgress with
                    {
                        Running = false,
                        Stage = "ERROR",
                        Failed = _messageReplyProgress.Failed + 1,
                        Message = ex.Message,
                        Completed = true
                    };
            }
            finally
            {
                lock (_messageReplySync)
                {
                    if (_messageReplyProgress.Running)
                        _messageReplyProgress = _messageReplyProgress with { Running = false, Completed = true };
                }
            }
        });

        return "started";
    }

    string StopManagedMessageReply()
    {
        lock (_messageReplySync)
        {
            if (_messageReplyTask is null || _messageReplyTask.IsCompleted) return "not_running";
            try { _messageReplyCts?.Cancel(); } catch { }
            _messageReplyProgress = _messageReplyProgress with
            {
                Stage = "STOPPING",
                Message = "Đang dừng sau thao tác hiện tại..."
            };
            _messageReplyJournal.Add($"{DateTime.Now:HH:mm:ss.fff} [STOPPING] Đang dừng sau thao tác hiện tại...");
            if (_messageReplyJournal.Count > MaxMessageReplyJournalLines)
                _messageReplyJournal.RemoveRange(0, _messageReplyJournal.Count - MaxMessageReplyJournalLines);
            return "stopping";
        }
    }
}
