CAP NHAT V13.3 -> V13.4 XPATH ONLY / VM OPTIMIZED

1. Dong Tool TikTok, Worker va Chrome do tool quan ly.
2. Backup thu muc source V13.3 neu muon.
3. Giai nen ZIP patch nay.
4. Copy TOAN BO noi dung patch vao THU MUC GOC SOURCE V13.3.
5. Chon Replace the files in the destination.
6. Chay AP_DUNG_CAP_NHAT_V13_4.bat de xoa 2 file legacy:
   - V115Core\Services\TesseractOcr.cs
   - V115Core\Services\ImageMatcher.cs
7. Chay BUILD_V13.bat.
8. Neu BUILD OK, chay RUN_V13_DEV.bat.

Patch KHONG xoa/doi:
- TikTokProfiles
- profiles
- profiles.json
- chrome_profile / cookie / dang nhap TikTok
- ProfilePath / DataRoot / CDP port

Thay doi V13.4:
- Viewer chi doc bang XPath/DOM, khong OCR fallback.
- Go Tesseract va image decode/crop khong con dung.
- Giu nguyen logic Viewer threshold/confirm/recovery, 8 buoc, InputGuard,
  Live cu DOM, ArrowDown, F5, retry/recovery, VM Safe/VM Max va log V13.3.
