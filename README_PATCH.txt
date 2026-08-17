PATCH V13.5 - VIEW CHROME: KHOI PHUC HANH VI CU + FIX AUTO-OPEN

Muc tieu:
- Khoi phuc dung hanh vi View cu: Manager restore -> maximize -> foreground.
- Khong de Worker tu restore/resize cua so Chrome nua.
- Chi fix truong hop Chrome duoc nut Bat dau tu dong mo: CDP CONNECTED nhung status chua co ChromeWindowHandle.

Cach xu ly moi:
1. Bam View.
2. Neu snapshot da co HWND: dung ngay logic View cu.
3. Neu HWND = 0 nhung Chrome CONNECTED: Worker chi do lai PID/HWND theo profilePath + CDP port, KHONG thay doi kich thuoc/foreground.
4. Manager nhan HWND va dung ChromeMonitorWindowActions.RestoreMaximizeAndActivate nhu ban cu.
5. Neu top-level window vua bi Chrome tao lai, refresh HWND 1 lan roi thu lai.

File thay doi:
- ToolTikTokManagerV13/ManagerForm.cs
- ToolTikTokWorkerV13/MainForm.Managed.cs
- V115Core/Services/ChromeController.cs

Patch nay giu nguyen Dashboard/Auto Update va Chrome OOM recovery dang co.
