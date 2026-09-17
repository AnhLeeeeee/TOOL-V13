PATCH_VERSION_MANIFEST_SELF_HEAL_AND_SAFE_PUBLISH

Copy de len thu muc goc source ToolTikTokWorkerV13.

Thay doi:
1) ManagerForm.DashboardUpdate.cs
   - Tai version.json/versions.json voi cache-buster + no-cache.
   - Kiem tra body rong/NUL/0x00 truoc JsonSerializer.
   - Retry 2 lan, log UPDATE_MANIFEST_RETRY, thong bao loi ro rang neu manifest bi hong.

2) _BAT_PHU/SYNC_VERSION.ps1
   - Ghi JSON qua file tam, parse/verify lai truoc khi replace file chinh.
   - Tao *.lastgood khi file hien tai hop le.
   - Neu file chinh loi, thu doc *.lastgood de bao toan lich su.
   - Chan publish neu JSON rong/co NUL/khong parse duoc.

3) version.json + versions.json
   - Khoi phuc manifest hop le, them V14.1.4 voi SHA256 release hien tai.
