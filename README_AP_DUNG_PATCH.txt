PATCH V13.5 - BO QUA SUA HO SO NEU 1 TRONG 2 DIEU KIEN DUNG

Ap dung sau cac patch ten/anh va tu dang nhap V13.5 truoc do.

File thay doi:
  V115Core/Services/ChromeController.cs

Logic moi (OR):

1) TREN TRANG HO SO:
   - Doc ten hien tai.
   - Neu ten trung BAT KY ten nao trong danh sach ten da cau hinh:
       + Bo qua toan bo buoc Sua ho so.
       + Khong doi ten/anh/tieu su nua.
       + Cho phep Manager ghi DONE vao Excel.
       + Khong mo popup Sua ho so.

HOAC

2) NEU TEN KHONG TRUNG VA PHAI MO SUA HO SO DE KIEM TRA:
   - Neu phat hien dong:
       "Ban co the tiep tuc thay doi biet danh sau ..."
     (co ho tro chu co dau/khong dau va ban tieng Anh thong dung)
       + Bo qua ngay, khong sua bat ky truong nao.
       + Danh dau bo qua profile trong PHIEN HIEN TAI de khong lap lai/reload lien tuc.
       + KHONG ghi DONE vao Excel vi day la cooldown TikTok.

Chi can 1 trong 2 dieu kien dung la bo qua buoc sua ho so.

Cach ap dung:
  1. Dong Manager/Worker neu dang chay.
  2. Chep thu muc V115Core trong patch vao thu muc source V13.5 va cho phep ghi de.
  3. Build/tao ban cai lai nhu binh thuong.

Patch nay khong sua Manager, log 500 dong, version, man giam sat Chrome, hay logic LIVE.
