# SERVER VERSION POLICY CONTRACT

## Chế độ an toàn áp dụng từ V14.2.9+

- `HighestVersionEver` local luôn được đọc/ghi ở Manager và Worker, kể cả khi API version-policy chưa cấu hình.
- `versionControl.enabled=true` trong `device_access_policy.json` giúp các build cũ đã có `VersionRollbackGuard` cũng kích hoạt guard khi nhận policy mới.
- `policyUrl` có thể để trống: khi đó hệ thống chạy **local-only**, bản thấp hơn `HighestVersionEver` bị chặn và không phụ thuộc server.
- Khi cần cho phép rollback đúng máy/đúng phiên bản, chỉ cần cấu hình `policyUrl` tới API server; server cấp `allow_specific`/`allow_all_old`. Client không hạ mốc local.

## Trạng thái tắt hoàn toàn

Khi `device_access_policy.json` có:

```json
"versionControl": {
  "enabled": false,
  "policyUrl": "",
  "failClosedOnDowngrade": true
}
```

Với build cũ chưa nhận patch "local always-on", `enabled=false` vẫn có thể bypass guard. Vì vậy policy phát hành nên giữ `versionControl.enabled=true`. Với build mới, local `HighestVersionEver` luôn hoạt động; cờ này chỉ còn quyết định có dùng lớp server ngoại lệ rollback hay không.


Client patch này dùng 2 tầng:

1. `device_access_policy.json` (bootstrap hiện có trên GitHub) chỉ cho client biết API version-control nằm ở đâu.
2. API trên server trả policy riêng theo `DeviceId`.

## 1) Bật version-control trong device_access_policy.json

```json
{
  "enabled": true,
  "policyVersion": "2",
  "enforceAllowList": false,
  "allowedDeviceIds": [],
  "blockedDeviceIds": [],
  "blockedUpdateDeviceIds": [],
  "blockMessage": "Thiết bị này chưa được cấp quyền sử dụng Tool.",
  "versionControl": {
    "enabled": true,
    "policyUrl": "https://YOUR-SERVER/api/tool/version-policy",
    "failClosedOnDowngrade": true
  }
}
```

`policyUrl` có thể thay đổi trên GitHub mà không cần build lại client.
Nên dùng HTTPS.

## 2) Request client gửi lên server

Method: `POST`
Content-Type: `application/json`

```json
{
  "deviceId": "TT-ABCDE-12345-ABCDE-12345",
  "currentVersion": "14.2.7",
  "localHighestVersion": "14.2.7",
  "action": "startup"
}
```

`action` có thể là:
- `startup`: lúc Manager/Worker khởi động.
- `update_check`: khi mở/refresh danh sách phiên bản.
- `install_check`: ngay trước khi tải/cài một bản cũ.

Server nên lưu `highestVersionEver` theo `deviceId`. Khi nhận `currentVersion` cao hơn mốc đang lưu thì nâng mốc server lên, không tự hạ mốc.

## 3) Response server

### A. Chặn toàn bộ bản cũ

```json
{
  "enabled": true,
  "mode": "deny",
  "allowedVersions": [],
  "highestVersionEver": "14.2.7",
  "allowCurrentVersion": true,
  "message": "",
  "policyVersion": "1"
}
```

Nếu client đang chạy một bản thấp hơn `highestVersionEver`, server nên trả `allowCurrentVersion: false`.

### B. Cho phép tất cả bản cũ

```json
{
  "enabled": true,
  "mode": "allow_all_old",
  "allowedVersions": [],
  "highestVersionEver": "14.2.7",
  "allowCurrentVersion": true,
  "message": "",
  "policyVersion": "1"
}
```

UI sẽ hiển thị toàn bộ bản cũ có trong `versions.json` (trừ bản withdrawn/allowInstall=false khi tới bước cài).

### C. Chỉ cho phép một bản cụ thể

Ví dụ chỉ cho máy này hạ về `14.2.5`:

```json
{
  "enabled": true,
  "mode": "allow_specific",
  "allowedVersions": ["14.2.5"],
  "highestVersionEver": "14.2.7",
  "allowCurrentVersion": true,
  "message": "",
  "policyVersion": "1"
}
```

UI chỉ hiện V14.2.5 trong nhóm các bản thấp hơn bản hiện tại. Có thể đưa nhiều version vào mảng nếu sau này cần.

## 4) Quy tắc server nên áp dụng cho allowCurrentVersion

- Nếu `currentVersion >= highestVersionEver`: `true`.
- Nếu thấp hơn và mode=`deny`: `false`.
- Nếu thấp hơn và mode=`allow_all_old`: `true`.
- Nếu thấp hơn và mode=`allow_specific`: chỉ `true` khi `currentVersion` có trong `allowedVersions`.

Client vẫn có fallback local `HighestVersionEver`. Server chỉ cấp ngoại lệ; client không hạ mốc local khi được rollback.

## 5) Lưu ý về các EXE cũ đã phát hành

Một build cũ được phát hành trước khi có patch này không chứa code gọi Version Policy API. Client mới có thể chặn/cho phép việc CÀI build đó từ updater, nhưng sau khi đã downgrade sang binary cũ thì binary đó không thể tự nhận lệnh thu hồi quyền mới từ server.

Muốn quản lý/revoke cả các bản rất cũ sau khi đã cài, cần một launcher/license component luôn được giữ ở phiên bản mới hoặc buộc các build cần quản lý phải chứa VersionRollbackGuard này.
