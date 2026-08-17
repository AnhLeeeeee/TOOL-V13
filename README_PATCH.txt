PATCH V13.5.4 - Dashboard chỉ hiện profile đang mở / gạt hiện tất cả

Thay đổi:
- Mặc định tab Tổng quan chỉ hiển thị các profile đang mở trong Manager.
- Nút gạt ở góc phải header:
  + Hồ sơ đang mở: chỉ hiện profile có tab đang mở.
  + Tất cả hồ sơ: hiện toàn bộ profile trong catalog như trước.
- Summary ở chế độ mặc định hiển thị "Profiles đang mở: x/y".
- Chỉ lọc giao diện Dashboard; không dừng, đóng, mở hoặc thay đổi Worker/Chrome.

File thay đổi:
ToolTikTokManagerV13/ManagerForm.DashboardUpdate.cs

Cách áp dụng:
Giải nén nội dung ZIP trực tiếp vào thư mục source V13.5.4 và chọn Replace.
Sau đó build lại bằng TAO_BAN_CAI_V13_5.bat.
