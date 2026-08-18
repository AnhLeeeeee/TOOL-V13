namespace ToolTikTokV11.Models;

public sealed class TikTokMessageReplyOptions
{
    public string[] Messages { get; set; } = Array.Empty<string>();
    public bool AcceptRequests { get; set; } = true;
    public bool ReplyAfterAccept { get; set; } = true;
    public bool SkipAlreadyReplied { get; set; } = true;
    public bool OnlyInitialRequests { get; set; } = true;
    public int DelayMinMs { get; set; } = 1500;
    public int DelayMaxMs { get; set; } = 3500;
    public int RetryCount { get; set; } = 2;
    public string HistoryPath { get; set; } = "";

    // Dùng cho chế độ tự động xen giữa luồng LIVE. Worker lưu URL hiện tại trước
    // khi vào /messages và cố quay lại đúng URL đó trước khi Manager resume LIVE.
    public bool ReturnToPreviousPage { get; set; }
    public int ReturnPageSettleMs { get; set; } = 1200;

    // Auto LIVE dùng fail-open: chỉ cần một bước xử lý request lỗi là bỏ phiên Tin nhắn
    // ngay, chạy finally quay về LIVE rồi resume automation. Manual mặc định vẫn retry/tiếp tục như cũ.
    public bool AbortOnAnyError { get; set; }
}

public sealed record TikTokMessageReplyProgress(
    bool Running,
    string Stage,
    int RequestsFound,
    int Processed,
    int Accepted,
    int Replied,
    int Skipped,
    int Failed,
    string CurrentUser,
    string Message,
    bool Completed,
    bool Cancelled);

public sealed record TikTokMessageReplyRunResult(
    int RequestsFound,
    int Processed,
    int Accepted,
    int Replied,
    int Skipped,
    int Failed,
    bool Cancelled,
    string Message);
