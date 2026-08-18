$ErrorActionPreference = 'Stop'

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$candidates = @(
    (Join-Path $here 'ToolTikTokManagerV13\ManagerForm.MessageReply.cs'),
    (Join-Path (Split-Path -Parent $here) 'ToolTikTokManagerV13\ManagerForm.MessageReply.cs'),
    (Join-Path (Get-Location) 'ToolTikTokManagerV13\ManagerForm.MessageReply.cs')
) | Select-Object -Unique

$target = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $target) {
    Write-Host ''
    Write-Host 'KHONG TIM THAY SOURCE:' -ForegroundColor Red
    Write-Host 'ToolTikTokManagerV13\ManagerForm.MessageReply.cs'
    Write-Host ''
    Write-Host 'Hay dat thu muc patch nay vao thu muc goc source V13.5.6 roi chay lai.'
    Read-Host 'Nhan Enter de dong'
    exit 1
}

Write-Host "Dang sua: $target" -ForegroundColor Cyan
$text = [IO.File]::ReadAllText($target)
$original = $text

function Replace-Once([string]$inputText, [string]$old, [string]$new, [string]$label) {
    $idx = $inputText.IndexOf($old, [StringComparison]::Ordinal)
    if ($idx -lt 0) {
        throw "Khong tim thay khoi code: $label. Source co the da khac ban V13.5.6."
    }
    return $inputText.Substring(0, $idx) + $new + $inputText.Substring($idx + $old.Length)
}

# 1) Tao host cuon cho TOAN BO cua so Tin nhan.
$old = @'
        ModernDialog.Apply(form, fixedDialog: false);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(16),
            BackColor = ModernDialog.Canvas
        };
'@
$new = @'
        ModernDialog.Apply(form, fixedDialog: false);

        // Host cuon toan bo noi dung. Tren man hinh thap / DPI lon, nguoi dung co the
        // cuon tu phan nhap tin nhan xuong danh sach tai khoan va cac nut ben duoi.
        var scrollHost = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = ModernDialog.Canvas
        };

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(16),
            BackColor = ModernDialog.Canvas
        };
'@
$text = Replace-Once $text $old $new 'scrollHost/root'

# 2) Khong ep config vao 230px nua. Moi phan co chieu cao that, tab tai khoan giu 320px.
$pattern = [regex]::Escape('        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));') + "\r?\n" +
           [regex]::Escape('        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));') + "\r?\n" +
           '(?:        //.*\r?\n)?' +
           '        root\.RowStyles\.Add\(new RowStyle\(SizeType\.Absolute, (?:230|340)F\)\);\r?\n' +
           [regex]::Escape('        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));') + "\r?\n" +
           [regex]::Escape('        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));') + "\r?\n" +
           [regex]::Escape('        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));')
$replacement = @'
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 320F));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
'@
$newText = [regex]::Replace($text, $pattern, $replacement, 1)
if ($newText -eq $text) { throw 'Khong tim thay khoi RowStyles cua root.' }
$text = $newText

# 3) Config tu tang chieu cao; hang nhap noi dung luon co 130px.
$old = @'
        var config = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 5,
            Margin = new Padding(0, 0, 0, 10)
        };
'@
$new = @'
        var config = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 4,
            RowCount = 5,
            Margin = new Padding(0, 0, 0, 10)
        };
'@
$text = Replace-Once $text $old $new 'config AutoSize'

$text = Replace-Once $text `
    '        config.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));' `
    '        config.RowStyles.Add(new RowStyle(SizeType.Absolute, 130F));' `
    'config row nhap tin nhan'

# 4) Dam bao textbox khong bi co ve 0px.
$old = @'
            AcceptsReturn = true,
            Dock = DockStyle.Fill,
            Text = state.MessagesText
'@
$new = @'
            AcceptsReturn = true,
            Dock = DockStyle.Fill,
            MinimumSize = new Size(0, 110),
            Text = state.MessagesText
'@
$text = Replace-Once $text $old $new 'messages MinimumSize'

# 5) Danh sach tai khoan van co chieu cao de xem duoc khi cuon xuong.
$old = @'
        var tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0),
            Padding = new Point(14, 5)
        };
'@
$new = @'
        var tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            MinimumSize = new Size(0, 280),
            Margin = new Padding(0),
            Padding = new Point(14, 5)
        };
'@
$text = Replace-Once $text $old $new 'tabs MinimumSize'

# 6) Dua root vao scrollHost thay vi gan thang vao Form.
$text = Replace-Once $text `
    '        form.Controls.Add(root);' `
    "        scrollHost.Controls.Add(root);`r`n        form.Controls.Add(scrollHost);" `
    'gan scrollHost vao form'

if ($text -eq $original) { throw 'Khong co thay doi nao duoc ap dung.' }

$backup = "$target.bak_scroll"
if (-not (Test-Path $backup)) {
    [IO.File]::WriteAllText($backup, $original, (New-Object Text.UTF8Encoding($true)))
}
[IO.File]::WriteAllText($target, $text, (New-Object Text.UTF8Encoding($true)))

Write-Host ''
Write-Host 'PATCH OK.' -ForegroundColor Green
Write-Host 'Da sua cua so Tin nhan TikTok thanh CUON TOAN TRANG.'
Write-Host 'File backup:' $backup
Write-Host ''
Write-Host 'Tiep theo: build/tao ban cap nhat V13.5.6 nhu binh thuong.'
Read-Host 'Nhan Enter de dong'
