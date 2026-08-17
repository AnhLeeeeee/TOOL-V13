PATCH DONG BO VERSION TU DONG - TOOL TIKTOK V13.5
=================================================

MUC TIEU
- Chi con 1 nguon version: VERSION.txt
- Manager, Worker, Dashboard, cac man hinh hien thi, Assembly/FileVersion va Inno Setup deu lay cung version.
- version.json duoc dong bo tu VERSION.txt.
- Sau khi tao Setup, SHA-256 cua Setup duoc tinh lai va ghi vao version.json.

CACH AP DUNG
1. Tat Tool/Visual Studio neu dang mo file source.
2. Giai nen PATCH nay vao THU MUC GOC source V13.5.
3. Chon Replace/ghi de cac file trung ten.
4. Tu nay muon tang version, chi sua duy nhat file VERSION.txt.
   Vi du:
      13.5.4
   thanh:
      13.5.5
5. Chay TAO_BAN_CAI_V13_5.bat de publish ban may khach.
6. Chay TAO_SETUP_V13_5_AUTO_FIND_INNO.bat de tao Setup.
   Script se tu dong cap nhat version + SHA-256 vao version.json.

LUU Y
- Ten nhanh V13.5 trong ten ZIP, thu muc cai va ten Setup duoc GIU NGUYEN co chu y de cap nhat de len may khach cu va khong tach du lieu profile.
- Cac comment lich su nhu "V13.4.1 da them..." co the van con trong source; chung khong con duoc dung lam version hien tai tren giao dien/runtime.
- Cac chuoi ma hoa dang ToolTikTok-V13.5-... KHONG duoc doi theo version vi doi chung co the lam du lieu tai khoan da luu khong giai ma duoc.
- Patch nay khong thay doi Logger, nen khong ghi de ban va gioi han log 500 dong da ap dung truoc do.

VERSION BAN DAU TRONG PATCH: 13.5.4
