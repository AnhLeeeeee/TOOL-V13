PATCH V13.5 - FIX DASHBOARD / VIEW CHROME

Sửa lỗi:
- Dashboard hiển thị Chrome=CONNECTED, profile RUNNING nhưng bấm View lại báo Chrome chưa mở.

Nguyên nhân:
- Status polling không chủ động dò lại ChromeWindowHandle để tránh làm chậm Manager.
- Nút View cũ lại chỉ kiểm tra handle trong snapshot nên có thể nhận 0 dù Chrome đang chạy.

Cách sửa:
- Thêm command Worker: view_chrome.
- Khi bấm View, Manager gọi đúng Worker của profile.
- Worker dò/restore HWND theo profilePath + CDP port bằng RestoreManagedWindow.
- Nếu chưa tìm thấy, Manager reconnect nhẹ một lần và thử lại.
- Không tự mở Chrome mới và không đụng profile khác.

File thay đổi:
- ToolTikTokManagerV13/ManagerForm.cs
- ToolTikTokWorkerV13/MainForm.Managed.cs

Cách áp dụng:
1. Tắt Tool.
2. Giải nén ZIP trực tiếp vào root source V13.5.
3. Chọn Replace.
4. Chạy BUILD_V13.bat hoặc TAO_BAN_CAI_V13_5.bat.
