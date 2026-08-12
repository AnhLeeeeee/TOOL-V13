namespace ToolTikTokV11.Models;

/// <summary>
/// V13: trạng thái ô nhập là nguồn quyết định runtime duy nhất cho việc bỏ qua/chuyển LIVE.
/// Ảnh vùng quét V12.5 vẫn có thể còn trong file cấu hình cũ để tương thích dữ liệu,
/// nhưng không còn tham gia quyết định của AutomationEngine.
/// </summary>
public sealed class InputGuardSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Chuỗi phải xuất hiện trong placeholder/trạng thái rỗng bình thường trước khi tool click.
    /// Dùng Contains không phân biệt hoa/thường để chịu được biến thể như "Nhập...".
    /// </summary>
    public string NormalPlaceholderText { get; set; } = "Nhập";

    /// <summary>Số lần đọc DOM liên tiếp trước khi xác nhận trạng thái bất thường.</summary>
    public int ConfirmReads { get; set; } = 2;

    /// <summary>Khoảng chờ giữa các lần xác nhận DOM.</summary>
    public int ConfirmDelayMs { get; set; } = 150;

    /// <summary>
    /// Giữ hành vi tăng số lần ArrowDown khi lỗi còn tồn tại sau chuyển LIVE,
    /// tương đương cơ chế "Lỗi LT" trước đây. Giới hạn 1..4.
    /// </summary>
    public int ConsecutiveMax { get; set; } = 3;
}
