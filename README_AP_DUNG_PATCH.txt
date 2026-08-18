PATCH HỢP NHẤT TIN NHẮN TIKTOK - V13.5

Mục đích:
- Khắc phục regression ở bản PATCH_FIX_O_NHAP_VA_NUT_GUI_TIKTOK_V13_5: phần click trực tiếp nickname request đã bị mất khi chép đè file.
- Giữ lại toàn bộ các sửa mới nhất trong cùng một file:
  1) Đọc tên request bằng p[data-e2e="dm-new-conversation-nickname"]
  2) Click trực tiếp span/p nickname + fallback pointer/mouse/ancestor
  3) Sau Accept, tìm lại đúng nickname ở danh sách Tin nhắn chính
  4) Ô nhập: div[data-e2e="dm-new-input-editor"] [contenteditable="true"][role="textbox"]
  5) Nút Gửi: nhận diện SVG path fill="#FE2C55" với path M30.488...
  6) Giữ log OPEN_REQUEST_USER_CLICK / OPEN_ACCEPTED_CHAT_SELECTOR / COMPOSER_SELECTOR / SEND_BUTTON_SELECTOR

Cách áp dụng:
- Tắt Manager/Worker trước khi chép.
- Chép thư mục V115Core trong patch đè vào source V13.5.
- Build/tạo bản cài lại như bình thường.
- Patch này nên được chép SAU CÙNG so với các patch Tin nhắn trước đó.

Log kỳ vọng ở bước mở request:
[OPEN_REQUEST_USER_CLICK] mode=0 ...
[OPEN_REQUEST_USER_CLICK_OK] ...

Sau Accept:
[OPEN_ACCEPTED_CHAT_SELECTOR] ... ok=True
[OPEN_ACCEPTED_CHAT_COMPOSER] ... ready=True
[COMPOSER_SELECTOR] ... ok=True
[SEND_BUTTON_SELECTOR] ... ok=True
[SEND] => CLICK NÚT GỬI OK
