namespace ToolTikTokManagerV13;

public sealed partial class ManagerForm
{
    // Bốn tùy chọn Tin nhắn đã được chốt theo luồng sử dụng:
    // 1) Luôn chấp nhận yêu cầu.
    // 2) Luôn trả lời ngay sau khi chấp nhận.
    // 3) Luôn bỏ qua người đã trả lời.
    // 4) Chạy thủ công: chỉ xử lý các yêu cầu có tại thời điểm bấm Chạy.
    //
    // File này chỉ khóa/hide UI bốn checkbox. Logic Tin nhắn hiện có vẫn được dùng nguyên vẹn.
    static readonly string[] FixedMessageReplyOptionTexts =
    {
        "Chấp nhận yêu cầu",
        "Trả lời ngay sau khi chấp nhận",
        "Bỏ qua người đã trả lời",
        "Chỉ xử lý yêu cầu có lúc bấm Chạy"
    };

    static readonly HashSet<IntPtr> FixedMessageReplyDialogHandles = new();
    bool _fixedMessageReplyDefaultsNormalized;

    static ManagerForm()
    {
        Application.Idle += (_, _) =>
        {
            try
            {
                foreach (Form form in Application.OpenForms)
                {
                    if (form is ManagerForm manager)
                        manager.NormalizeFixedMessageReplyDefaults();

                    if (!string.Equals(form.Text, "Tin nhắn TikTok", StringComparison.Ordinal))
                        continue;

                    ApplyFixedMessageReplyOptionsToDialog(form);
                }
            }
            catch
            {
                // UI hook chỉ là lớp cố định tùy chọn. Không được làm ảnh hưởng Manager.
            }
        };
    }

    void NormalizeFixedMessageReplyDefaults()
    {
        if (_fixedMessageReplyDefaultsNormalized) return;
        _fixedMessageReplyDefaultsNormalized = true;

        try
        {
            var state = LoadMessageReplyToolState();
            var changed =
                !state.AcceptRequests ||
                !state.ReplyAfterAccept ||
                !state.SkipAlreadyReplied ||
                !state.OnlyInitialRequests;

            state.AcceptRequests = true;
            state.ReplyAfterAccept = true;
            state.SkipAlreadyReplied = true;
            state.OnlyInitialRequests = true;

            if (changed)
            {
                SaveMessageReplyToolState(state);
                _log.Info("[MESSAGE_REPLY_FIXED_OPTIONS] Đã cố định 4 tùy chọn Tin nhắn = true.");
            }
        }
        catch (Exception ex)
        {
            _log.Warn("[MESSAGE_REPLY_FIXED_OPTIONS] Không chuẩn hóa được cấu hình: " + ex.Message);
        }
    }

    static void ApplyFixedMessageReplyOptionsToDialog(Form form)
    {
        if (form.IsDisposed || !form.IsHandleCreated) return;

        var changed = false;
        foreach (var control in EnumerateControls(form))
        {
            if (control is CheckBox checkBox
                && FixedMessageReplyOptionTexts.Contains(checkBox.Text, StringComparer.Ordinal))
            {
                // Quan trọng: set Checked=true trước khi ẩn vì code gốc dùng trực tiếp .Checked
                // khi SaveUiState và khi tạo request gửi sang Worker.
                if (!checkBox.Checked)
                {
                    checkBox.Checked = true;
                    changed = true;
                }

                checkBox.Visible = false;
                checkBox.TabStop = false;
            }
            else if (control is Label label
                     && string.Equals(label.Text, "Tùy chọn", StringComparison.Ordinal))
            {
                label.Visible = false;
            }
        }

        if (changed)
        {
            try { form.PerformLayout(); } catch { }
        }

        // Ghi nhận handle chỉ để log/debug nội bộ nếu cần sau này.
        FixedMessageReplyDialogHandles.Add(form.Handle);
    }

    static IEnumerable<Control> EnumerateControls(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var descendant in EnumerateControls(child))
                yield return descendant;
        }
    }
}
