namespace ToolTikTokManagerV13;

public sealed partial class ManagerForm
{
    Button? _autoReplacementManualControlButton;
    bool _autoReplacementManualControlInitialized;

    void InitializeAutoReplacementManualControl()
    {
        if (_autoReplacementManualControlInitialized)
            return;

        _autoReplacementManualControlInitialized = true;

        var toolbar =
            EnumerateAutoReplacementManualControls(this)
                .OfType<FlowLayoutPanel>()
                .FirstOrDefault(panel =>
                    panel.Controls
                        .OfType<Button>()
                        .Any(button =>
                            button.Text.Equals(
                                "Dừng tất cả",
                                StringComparison.OrdinalIgnoreCase)));

        if (toolbar is null)
        {
            _log.Warn(
                "[AUTO_REPLACE_MANUAL_UI] Không tìm thấy toolbar để thêm nút Dừng Tự bù.");
            return;
        }

        _autoReplacementManualControlButton =
            new Button
            {
                AutoSize = true,
                Height = 34,
                MinimumSize = new Size(126, 34),
                Margin = new Padding(5, 3, 5, 3),
                FlatStyle = FlatStyle.Flat,
                UseVisualStyleBackColor = false
            };

        _autoReplacementManualControlButton.FlatAppearance.BorderSize = 1;

        _autoReplacementManualControlButton.Click +=
            (_, _) => ToggleAutoReplacementFromUi();

        toolbar.Controls.Add(
            _autoReplacementManualControlButton);

        // Đặt ngay sau nút "Tự động: BAN + ... + Bù" nếu tìm thấy.
        // Nếu không tìm thấy thì giữ vị trí cuối toolbar, vẫn hoạt động bình thường.
        try
        {
            if (_autoCloseToolbarButton is not null
                && !_autoCloseToolbarButton.IsDisposed
                && _autoCloseToolbarButton.Parent == toolbar)
            {
                var autoCloseIndex =
                    toolbar.Controls.GetChildIndex(
                        _autoCloseToolbarButton);

                toolbar.Controls.SetChildIndex(
                    _autoReplacementManualControlButton,
                    Math.Min(
                        toolbar.Controls.Count - 1,
                        autoCloseIndex + 1));
            }
        }
        catch { }

        UpdateAutoReplacementManualControlButton();

        _log.Info(
            $"[AUTO_REPLACE_MANUAL_UI_READY] enabled={_autoCloseSettings.OpenReplacementAfterAutoClose}");
    }

    static IEnumerable<Control>
        EnumerateAutoReplacementManualControls(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;

            foreach (var nested
                     in EnumerateAutoReplacementManualControls(child))
            {
                yield return nested;
            }
        }
    }

    void ToggleAutoReplacementFromUi()
    {
        if (_autoCloseSettings.OpenReplacementAfterAutoClose)
        {
            StopAutoReplacementFromUi();
        }
        else
        {
            StartAutoReplacementFromUi();
        }
    }

    void StopAutoReplacementFromUi()
    {
        var clearedPending = 0;

        // Chặn queue đang chạy trước, sau đó mới xóa backlog.
        // Profile bù nào đã đi sâu vào một lần CREATE_PROFILE thì để lượt đó
        // kết thúc an toàn; tuyệt đối không khởi chạy lượt tiếp theo.
        _autoReplacementSessionArmed = false;

        lock (_autoReplacementQueueLock)
        {
            clearedPending =
                _autoReplacementQueue.Count;

            _autoReplacementQueue.Clear();

            try
            {
                SaveAutoReplacementQueueUnsafe();
            }
            catch (Exception ex)
            {
                _log.Warn(
                    $"[AUTO_REPLACE_MANUAL_STOP_SAVE_WARN] error={ex.Message}");
            }
        }

        _autoCloseSettings.OpenReplacementAfterAutoClose = false;

        try
        {
            // SaveAutoCloseSettings lưu trạng thái qua lần mở Tool sau
            // và NotifyAutoReplacementSettingsChanged() sẽ làm queue dừng.
            SaveAutoCloseSettings();
        }
        catch (Exception ex)
        {
            _log.Warn(
                $"[AUTO_REPLACE_MANUAL_STOP_SETTINGS_WARN] error={ex.Message}");
        }

        UpdateAutoReplacementManualControlButton();

        _log.Warn(
            $"[AUTO_REPLACE_MANUAL_STOP] clearedPending={clearedPending} armed=false");

        WriteAutoActivityLog(
            action: "TỰ BÙ",
            result: "ĐÃ DỪNG THỦ CÔNG",
            detail:
                $"Người dùng bấm Dừng Tự bù; đã xóa {clearedPending} suất bù đang chờ. "
                + "Các profile đang chạy giữ nguyên.");
    }

    void StartAutoReplacementFromUi()
    {
        _autoCloseSettings.OpenReplacementAfterAutoClose = true;

        try
        {
            // Chỉ bật quyền Tự bù.
            // Không tự khôi phục queue cũ vì queue đã được xóa khi Dừng.
            // Suất bù mới chỉ xuất hiện sau lần Tự đóng tiếp theo.
            SaveAutoCloseSettings();
        }
        catch (Exception ex)
        {
            _log.Warn(
                $"[AUTO_REPLACE_MANUAL_START_SETTINGS_WARN] error={ex.Message}");
        }

        UpdateAutoReplacementManualControlButton();

        _log.Info(
            "[AUTO_REPLACE_MANUAL_START] enabled=true pending=0 wait_for_next_auto_close");

        WriteAutoActivityLog(
            action: "TỰ BÙ",
            result: "ĐÃ BẬT THỦ CÔNG",
            detail:
                "Tự bù đã bật lại. Tool chỉ xử lý các suất bù phát sinh mới.");
    }

    void UpdateAutoReplacementManualControlButton()
    {
        if (_autoReplacementManualControlButton is null
            || _autoReplacementManualControlButton.IsDisposed)
        {
            return;
        }

        if (_autoCloseSettings.OpenReplacementAfterAutoClose)
        {
            // Tự bù đang bật -> nút thể hiện hành động DỪNG.
            _autoReplacementManualControlButton.Text =
                "■ Dừng Tự bù";

            _autoReplacementManualControlButton.BackColor =
                Color.FromArgb(253, 236, 236);

            _autoReplacementManualControlButton.ForeColor =
                Color.FromArgb(185, 42, 42);

            _autoReplacementManualControlButton.FlatAppearance.BorderColor =
                Color.FromArgb(224, 112, 112);
        }
        else
        {
            // Tự bù đang dừng -> nút thể hiện hành động BẬT.
            _autoReplacementManualControlButton.Text =
                "▶ Bật Tự bù";

            _autoReplacementManualControlButton.BackColor =
                Color.FromArgb(232, 247, 236);

            _autoReplacementManualControlButton.ForeColor =
                Color.FromArgb(32, 122, 60);

            _autoReplacementManualControlButton.FlatAppearance.BorderColor =
                Color.FromArgb(112, 184, 128);
        }
    }
}
