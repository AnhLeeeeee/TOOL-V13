using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ToolTikTokV12.Services;

namespace CommentVisibilityMonitor;

// Self-contained bulk username change. Independent of CMT/BAN observer and Manager/Worker.
internal sealed class IdChangeForm : Form
{
    readonly string _dataDir;
    readonly Action<string> _log;
    readonly IdChangeExcelStore _store = new();
    readonly ObserverChromeSession _session;
    readonly Label _fileInfo = new() { Dock=DockStyle.Top, Height=30, AutoEllipsis=true, Padding=new Padding(12,5,12,2), Text="Chưa mở Excel" };
    readonly Label _progress = new() { Dock=DockStyle.Top, Height=32, Padding=new Padding(12,6,12,2), Text="Chưa chạy" };
    readonly DataGridView _grid = new() { Dock=DockStyle.Fill, ReadOnly=true, AllowUserToAddRows=false, AllowUserToDeleteRows=false, RowHeadersVisible=false, AutoGenerateColumns=false, SelectionMode=DataGridViewSelectionMode.FullRowSelect, MultiSelect=false, BackgroundColor=Color.White, AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill };
    readonly Button _open = new() { Text="Mở Excel", AutoSize=true, Height=34 };
    readonly Button _reload = new() { Text="Tải lại", AutoSize=true, Height=34 };
    readonly Button _start = new() { Text="Bắt đầu đổi ID", AutoSize=true, Height=34 };
    readonly Button _stop = new() { Text="Dừng", AutoSize=true, Height=34, Enabled=false };
    readonly Button _close = new() { Text="Đóng", AutoSize=true, Height=34 };
    readonly RadioButton _prefixMode = new() { Text="Tiền tố + ngẫu nhiên", AutoSize=true, Checked=true };
    readonly RadioButton _randomMode = new() { Text="Ngẫu nhiên toàn bộ", AutoSize=true };
    readonly TextBox _prefix = new() { Text="anh", Width=96 };
    readonly NumericUpDown _randomLength = new() { Minimum=6, Maximum=16, Value=8, Width=64 };
    readonly NumericUpDown _loginInterval = new() { Minimum=0, Maximum=3600, Value=180, Increment=30, Width=75 };
    readonly Label _hint = new() { AutoSize=true, Text="Giới hạn toàn máy: ít nhất 120 giây", ForeColor=Color.DimGray };
    readonly Button _preview = new() { Text="Sinh thử", AutoSize=true };
    string _sourcePath="";
    List<IdChangeAccountRow> _rows=new();
    CancellationTokenSource? _cts;
    bool _running;
    DateTime _lastLoginAnchorUtc=DateTime.MinValue;
    string SettingsPath => Path.Combine(_dataDir,"id_change_settings.json");
    string SourcePath => Path.Combine(_dataDir,"id_change_source.txt");
    sealed record UiSettings(string Source, bool RandomAll, string Prefix, int RandomLength, int LoginSeconds);

    public IdChangeForm(string dataDir, Action<string> log)
    {
        _dataDir=dataDir;
        _log=log;
        Directory.CreateDirectory(dataDir);
        _session=new ObserverChromeSession(49336,Path.Combine(dataDir,"IdChangeChrome"));
        Text="CHECK — ĐỔI TIKTOK ID HÀNG LOẠT";
        Width=1040; Height=680; MinimumSize=new Size(820,530);
        StartPosition=FormStartPosition.CenterParent; Font=new Font("Segoe UI",9F);
        BackColor=Color.FromArgb(246,248,251);
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name="row",HeaderText="Dòng",FillWeight=9 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name="old",HeaderText="Tài khoản cũ",FillWeight=24 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name="new",HeaderText="Tài khoản mới",FillWeight=24 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name="state",HeaderText="Đổi ID",FillWeight=13 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name="candidate",HeaderText="ID dự kiến",FillWeight=20 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name="note",HeaderText="Ghi chú BAN",FillWeight=10 });
        _grid.ColumnHeadersHeight=34; _grid.RowTemplate.Height=28;

        var config=new FlowLayoutPanel { Dock=DockStyle.Top, Height=87, Padding=new Padding(10,8,10,4), WrapContents=true, BackColor=Color.White };
        config.Controls.AddRange(new Control[] {
            _prefixMode,_randomMode,new Label {Text="  Phần đầu:",AutoSize=true,Margin=new Padding(8,5,1,2)},
            _prefix,new Label {Text="  Ký tự:",AutoSize=true,Margin=new Padding(8,5,1,2)},
            _randomLength,_preview,new Label {Text="  Chờ đăng nhập:",AutoSize=true,Margin=new Padding(10,5,1,2)},
            _loginInterval,new Label {Text="giây",AutoSize=true,Margin=new Padding(1,5,1,2)},_hint
        });
        var bottom=new FlowLayoutPanel { Dock=DockStyle.Bottom, Height=56, Padding=new Padding(12,8,12,7), BackColor=Color.White,WrapContents=false };
        bottom.Controls.AddRange(new Control[]{_open,_reload,_start,_stop,_close});
        Controls.Add(_grid);Controls.Add(_progress);Controls.Add(_fileInfo);Controls.Add(config);Controls.Add(bottom);
        _open.Click+=(_,_)=>OpenExcel();
        _reload.Click+=(_,_)=>ReloadExcel(true);
        _start.Click+=async (_,_)=>await StartAsync();
        _stop.Click+=(_,_)=>_cts?.Cancel();
        _close.Click+=(_,_)=>Close();
        _preview.Click+=(_,_)=>{
            try { MessageBox.Show(this,"Ví dụ: @"+GenerateId(),"ID được sinh thử",MessageBoxButtons.OK,MessageBoxIcon.Information); }
            catch(Exception ex){MessageBox.Show(this,ex.Message,"Cấu hình chưa hợp lệ");}
        };
        _prefixMode.CheckedChanged+=(_,_)=>UpdateMode();
        _randomMode.CheckedChanged+=(_,_)=>UpdateMode();
        _prefix.TextChanged+=(_,_)=>SaveSettings();
        _randomLength.ValueChanged+=(_,_)=>SaveSettings();
        _loginInterval.ValueChanged+=(_,_)=>SaveSettings();
        FormClosing+=(_,e)=>{if(_running){e.Cancel=true;_cts?.Cancel();_progress.Text="Đang dừng an toàn...";}};
        FormClosed+=async (_,_)=>{try { await _session.DisposeAsync(); } catch { } _cts?.Dispose();};
        Shown+=(_,_)=>LoadSettings();
        UpdateMode();
    }
    void UpdateMode(){_prefix.Enabled=_prefixMode.Checked; SaveSettings();}
    void SaveSettings()
    {
        if (!IsHandleCreated) return;
        try
        {
            var s=new UiSettings(_sourcePath,_randomMode.Checked,_prefix.Text,(int)_randomLength.Value,(int)_loginInterval.Value);
            var temp=SettingsPath+".tmp";
            File.WriteAllText(temp,JsonSerializer.Serialize(s));
            File.Move(temp,SettingsPath,true);
        }
        catch { }
    }
    void LoadSettings()
    {
        try
        {
            if(File.Exists(SettingsPath))
            {
                var s=JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(SettingsPath));
                if(s is not null)
                {
                    _randomMode.Checked=s.RandomAll;_prefixMode.Checked=!s.RandomAll;
                    _prefix.Text=s.Prefix;
                    _randomLength.Value=Math.Clamp(s.RandomLength,(int)_randomLength.Minimum,(int)_randomLength.Maximum);
                    _loginInterval.Value=Math.Clamp(s.LoginSeconds,(int)_loginInterval.Minimum,(int)_loginInterval.Maximum);
                    _sourcePath=s.Source;
                }
            }
            else if(File.Exists(SourcePath)) _sourcePath=File.ReadAllText(SourcePath).Trim();
            if(File.Exists(_sourcePath))ReloadExcel(false);
        }
        catch { }
        UpdateMode();
    }
    void OpenExcel()
    {
        if(_running)return;
        using var dialog=new OpenFileDialog{Filter="Excel (*.xlsx)|*.xlsx",Title="Chọn kho tài khoản Đổi TikTok ID",CheckFileExists=true};
        if(File.Exists(_sourcePath)){dialog.InitialDirectory=Path.GetDirectoryName(_sourcePath);dialog.FileName=Path.GetFileName(_sourcePath);}
        if(dialog.ShowDialog(this)!=DialogResult.OK)return;
        try
        {
            _sourcePath=Path.GetFullPath(dialog.FileName);
            // Read-only open: checkpoint writes will create the backup on demand.
            _rows=_store.Open(_sourcePath).ToList();
            SaveSettings(); RefreshGrid();
        }
        catch(Exception ex){MessageBox.Show(this,ex.Message,"Lỗi mở Excel",MessageBoxButtons.OK,MessageBoxIcon.Warning);}
    }
    void ReloadExcel(bool showError)
    {
        try
        {
            if(string.IsNullOrWhiteSpace(_sourcePath)||!File.Exists(_sourcePath))
            {
                if(showError)MessageBox.Show(this,"Hãy mở Excel trước.");
                return;
            }
            _rows=_store.Reload(_sourcePath).ToList();RefreshGrid();
        }
        catch(Exception ex){if(showError)MessageBox.Show(this,ex.Message,"Lỗi Excel",MessageBoxButtons.OK,MessageBoxIcon.Warning);}
    }
    void RefreshGrid()
    {
        _grid.Rows.Clear();
        foreach(var x in _rows)
        {
            var n=_grid.Rows.Add(x.SourceRow,x.Username,x.NewUsername,x.State,x.Candidate,x.Note);
            _grid.Rows[n].Tag=x.SourceRow;
        }
        _fileInfo.Text=string.IsNullOrWhiteSpace(_sourcePath)?"Chưa chọn Excel":Path.GetFileName(_sourcePath)+$"  •  {_rows.Count} tài khoản  •  Giữ nguyên TK/MK/2FA và Ghi chú BAN";
        _progress.Text=$"Đã đổi: {_rows.Count(x=>IsDone(x))}/{_rows.Count}  |  Cần xác minh: {_rows.Count(x=>x.State.Equals("need_verify",StringComparison.OrdinalIgnoreCase))}  |  Chưa xử lý: {_rows.Count(x=>ShouldTry(x))}";
    }
    static bool IsDone(IdChangeAccountRow x)=>x.State.Equals("done",StringComparison.OrdinalIgnoreCase)&&!string.IsNullOrWhiteSpace(x.NewUsername);
    static bool ShouldTry(IdChangeAccountRow x)
        => !IsDone(x) && string.IsNullOrWhiteSpace(x.NewUsername)
           && !x.Note.Trim().Equals("ban",StringComparison.OrdinalIgnoreCase)
           && !x.State.Equals("done",StringComparison.OrdinalIgnoreCase)
           && !x.State.Equals("need_verify",StringComparison.OrdinalIgnoreCase)
           && !x.State.Equals("cooldown",StringComparison.OrdinalIgnoreCase);
    void UpdateRow(int rowNo, string newId, string state, string candidate)
    {
        var idx=_rows.FindIndex(x=>x.SourceRow==rowNo);
        if(idx>=0)_rows[idx]=_rows[idx] with {NewUsername=newId,State=state,Candidate=candidate};
        var row=_grid.Rows.Cast<DataGridViewRow>().FirstOrDefault(x=>x.Tag is int value&&value==rowNo);
        if(row is null)return;
        row.Cells["new"].Value=newId;row.Cells["state"].Value=state;row.Cells["candidate"].Value=candidate;
        row.Cells["state"].Style.BackColor=state=="done"?Color.Honeydew:state=="need_verify"?Color.MistyRose:Color.FromArgb(255,248,225);
        try{_grid.CurrentCell=row.Cells["old"];_grid.FirstDisplayedScrollingRowIndex=Math.Max(0,row.Index-2);}catch{}
    }
    string GenerateId()
    {
        const string chars="abcdefghijklmnopqrstuvwxyz0123456789";
        string random(int n){var b=new StringBuilder(n);for(int i=0;i<n;i++)b.Append(chars[RandomNumberGenerator.GetInt32(chars.Length)]);return b.ToString();}
        var prefix=_randomMode.Checked?"":_prefix.Text.Trim().ToLowerInvariant();
        if(prefix.Length>0 && !Regex.IsMatch(prefix,"^[a-z][a-z0-9._]*$") )throw new InvalidOperationException("Tiền tố chỉ được chứa chữ cái thường, số, dấu _ hoặc . và bắt đầu bằng chữ.");
        if(prefix.StartsWith("user",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Tiền tố bắt đầu bằng 'user' bị cấm.");
        var n=(int)_randomLength.Value;
        var result=_randomMode.Checked?"a"+random(n-1):prefix+random(n);
        if(result.StartsWith("user",StringComparison.OrdinalIgnoreCase)||result.Length>24 || result.Length<5)
            throw new InvalidOperationException("ID phải dài 5–24 ký tự và không bắt đầu bằng user.");
        return result;
    }
    void SetRunning(bool running)
    {
        _running=running;
        _open.Enabled=!running;_reload.Enabled=!running;_start.Enabled=!running;_stop.Enabled=running;_close.Enabled=!running;
        _prefixMode.Enabled=!running;_randomMode.Enabled=!running;_prefix.Enabled=!running&&_prefixMode.Checked;_randomLength.Enabled=!running;
        // Keep time interval editable during the run. Applies to next LOGIN.
        _loginInterval.Enabled=true;
    }
    async Task WaitForSpacingAsync(CancellationToken ct)
    {
        while(true)
        {
            ct.ThrowIfCancellationRequested();
            var seconds=(int)_loginInterval.Value;
            var anchor=_lastLoginAnchorUtc;
            var global=TikTokGlobalLoginSubmitGate.ReadLastSubmitUtc();
            if(global>anchor)anchor=global;
            var wanted=anchor==DateTime.MinValue?DateTime.MinValue:anchor.AddSeconds(Math.Max(120,seconds));
            var remaining=wanted-DateTime.UtcNow;
            if(remaining<=TimeSpan.Zero)return;
            _progress.Text=$"Chờ đăng nhập tiếp theo: {remaining:hh\\:mm\\:ss}  |  Tùy chỉnh: {seconds}s  |  Giới hạn chung: 120s";
            await Task.Delay(850,ct);
        }
    }
    async Task StartAsync()
    {
        if(_running)return;
        var access=RemotePolicyShadowReader.Evaluate("tool_access");
        if(!access.Allowed)
        {
            MessageBox.Show(this,"Remote Policy đang chặn quyền dùng tool trên thiết bị này.","Truy cập bị chặn",MessageBoxButtons.OK,MessageBoxIcon.Warning);
            return;
        }
        if(string.IsNullOrWhiteSpace(_sourcePath)||!File.Exists(_sourcePath)) { MessageBox.Show(this,"Chọn Excel trước.");return; }
        try{_ = GenerateId(); _rows=_store.Reload(_sourcePath).ToList(); RefreshGrid();}
        catch(Exception ex){MessageBox.Show(this,ex.Message,"Không thể bắt đầu",MessageBoxButtons.OK,MessageBoxIcon.Warning);return;}
        var pending=_rows.Where(ShouldTry).OrderBy(x=>x.SourceRow).ToList();
        if(pending.Count==0){MessageBox.Show(this,"Không còn dòng có thể tự đổi. DONE/NEED_VERIFY/COOLDOWN/BAN được bỏ qua.");return;}
        if(MessageBox.Show(this,$"Sẽ xử lý tối đa {pending.Count} tài khoản theo thứ tự Excel. Mỗi tài khoản thử tối đa 5 ID.\n\nHãy đảm bảo bạn có quyền quản lý các tài khoản này và đã sao lưu Excel. Bắt đầu?", "Xác nhận Đổi ID",MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes)return;
        _cts?.Dispose();_cts=new CancellationTokenSource(); var ct=_cts.Token;
        SetRunning(true);
        _log($"[ID_CHANGE_RUN_BEGIN] accounts={pending.Count} file={Path.GetFileName(_sourcePath)} interval={_loginInterval.Value}");
        try
        {
            for(int index=0;index<pending.Count;index++)
            {
                var account=pending[index];
                ct.ThrowIfCancellationRequested();
                _progress.Text=$"[{index+1}/{pending.Count}] Đang chuẩn bị @{account.Username}";
                if(string.IsNullOrEmpty(account.Password))
                {
                    _store.WriteOutcome(_sourcePath,account.SourceRow,account.Username,"missing_password");
                    UpdateRow(account.SourceRow,"","missing_password","");continue;
                }
                await WaitForSpacingAsync(ct);
                var attemptStartedUtc=DateTime.UtcNow;
                _lastLoginAnchorUtc=attemptStartedUtc;
                ToolTikTokV11.Services.TikTokStartupResult login;
                try {login=await _session.LoginAsync(account.Username,account.Password,account.TotpSecret,ct);}
                catch(OperationCanceledException){throw;}
                catch(Exception ex)
                {
                    _store.WriteOutcome(_sourcePath,account.SourceRow,account.Username,"login_error");
                    UpdateRow(account.SourceRow,"","login_error","");_log($"[ID_CHANGE_LOGIN_ERROR] row={account.SourceRow} {ex.Message}");continue;
                }
                var lastSubmit=TikTokGlobalLoginSubmitGate.ReadLastSubmitUtc();
                if(lastSubmit>_lastLoginAnchorUtc)_lastLoginAnchorUtc=lastSubmit;
                if(!login.LoggedIn)
                {
                    var state=login.State is "CAPTCHA_REQUIRED" or "TOTP_REQUIRED" or "LOGIN_POLICY_BLOCKED" ? "verification_required" : "login_failed";
                    _store.WriteOutcome(_sourcePath,account.SourceRow,account.Username,state);
                    UpdateRow(account.SourceRow,"",state,"");
                    _log($"[ID_CHANGE_LOGIN_NOT_READY] row={account.SourceRow} state={login.State}");
                    if(state=="verification_required")
                    {
                        MessageBox.Show(this,"TikTok yêu cầu xác minh/OTP hoặc chặn đăng nhập. Đã dừng toàn bộ lượt để tránh đăng nhập liên tục.","Dừng an toàn",MessageBoxButtons.OK,MessageBoxIcon.Warning);
                        break;
                    }
                    continue;
                }
                string current="";
                try {current=await _session.ReadOwnUsernameForIdAsync(ct);}
                catch(Exception ex){_log($"[ID_CHANGE_ACCOUNT_PROBE_FAIL] row={account.SourceRow} {ex.Message}");}
                if(string.IsNullOrWhiteSpace(current))
                {
                    _store.WriteOutcome(_sourcePath,account.SourceRow,account.Username,"account_unknown");
                    UpdateRow(account.SourceRow,"","account_unknown","");
                    _log($"[ID_CHANGE_ACCOUNT_UNKNOWN] expected={account.Username} actual=<empty>");
                    MessageBox.Show(this,"Không đọc được TikTok ID thực tế từ hồ sơ đang đăng nhập.\nĐã dừng để tránh đổi nhầm tài khoản; đây KHÔNG phải account_mismatch.","Không xác minh được tài khoản",MessageBoxButtons.OK,MessageBoxIcon.Warning);
                    break;
                }
                if(!string.Equals(current,account.Username.Trim().TrimStart('@'),StringComparison.OrdinalIgnoreCase))
                {
                    _store.WriteOutcome(_sourcePath,account.SourceRow,account.Username,"account_mismatch");
                    UpdateRow(account.SourceRow,"","account_mismatch","");
                    _log($"[ID_CHANGE_WRONG_ACCOUNT_BLOCKED] expected={account.Username} actual={current}");
                    // Login might have retained a stale or unexpected session: don't continue automated mutations.
                    MessageBox.Show(this,"Tài khoản thực tế không khớp Excel. Đã dừng toàn bộ lượt để tránh đổi nhầm ID.","Dừng an toàn",MessageBoxButtons.OK,MessageBoxIcon.Warning);
                    break;
                }
                var attempted=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var finished=false;
                for(int n=1;n<=5 && !finished;n++)
                {
                    ct.ThrowIfCancellationRequested();
                    var candidate=GenerateId();
                    if(!attempted.Add(candidate)){n--;continue;}
                    _progress.Text=$"[{index+1}/{pending.Count}] @{account.Username} → @{candidate} (thử {n}/5)";
                    IdChangeAttemptResult stage;
                    try{stage=await _session.PrepareIdChangeAsync(account.Username,candidate,ct);}
                    catch(OperationCanceledException){throw;}
                    catch(Exception ex){stage=new("MANUAL_REQUIRED",ex.Message);}
                    if(stage.State=="UNAVAILABLE")continue;
                    if(stage.State!="READY_TO_SAVE")
                    {
                        var state=stage.State=="COOLDOWN"?"cooldown":stage.State=="RATE_LIMIT"?"rate_limit":stage.State=="ACCOUNT_MISMATCH"?"account_mismatch":stage.State=="ACCOUNT_UNKNOWN"?"account_unknown":"manual_required";
                        _store.WriteOutcome(_sourcePath,account.SourceRow,account.Username,state);
                        UpdateRow(account.SourceRow,"",state,"");
                        _log($"[ID_CHANGE_STAGE_STOP] row={account.SourceRow} state={stage.State} info={stage.Detail}");
                        finished=true;
                        if(stage.State is "ACCOUNT_MISMATCH" or "ACCOUNT_UNKNOWN" or "RATE_LIMIT")
                        {
                            MessageBox.Show(this,"TikTok xuất hiện lỗi danh tính/giới hạn thao tác. Đã dừng lượt đổi hàng loạt.","Dừng an toàn",MessageBoxButtons.OK,MessageBoxIcon.Warning);
                            return;
                        }
                        break;
                    }
                    // Persistent checkpoint BEFORE clicking save; protects a crash/network loss at the dangerous boundary.
                    _store.WritePending(_sourcePath,account.SourceRow,account.Username,candidate);
                    UpdateRow(account.SourceRow,"","need_verify",candidate);
                    IdChangeAttemptResult saved;
                    try {saved=await _session.CommitIdChangeAsync(candidate,ct);}
                    catch(OperationCanceledException){throw;}
                    catch(Exception ex){saved=new("NEED_VERIFY",ex.Message);}
                    if(saved.State=="DONE")
                    {
                        _store.WriteDone(_sourcePath,account.SourceRow,account.Username,candidate);
                        UpdateRow(account.SourceRow,candidate,"done","");
                        _log($"[ID_CHANGE_DONE] row={account.SourceRow} original={account.Username} new={candidate}");
                    }
                    else
                    {
                        _log($"[ID_CHANGE_NEED_VERIFY] row={account.SourceRow} candidate={candidate} detail={saved.Detail}");
                        MessageBox.Show(this,$"Dòng {account.SourceRow} đã gửi/chuẩn bị gửi lệnh lưu nhưng chưa xác minh được.\nID dự kiến: @{candidate}\n\nĐã dừng lượt để bạn kiểm tra thủ công, không đổi tiếp.","Cần xác minh",MessageBoxButtons.OK,MessageBoxIcon.Warning);
                        return;
                    }
                    finished=true;
                }
                if(!finished)
                {
                    _store.WriteOutcome(_sourcePath,account.SourceRow,account.Username,"unavailable_5");
                    UpdateRow(account.SourceRow,"","unavailable_5","");
                }
                RefreshProgress(index+1,pending.Count);
            }
        }
        catch(OperationCanceledException){_log("[ID_CHANGE_CANCELLED] user requested stop");}
        catch(Exception ex)
        {
            _log("[ID_CHANGE_RUN_FATAL] "+ex);
            MessageBox.Show(this,ex.Message,"Đổi ID dừng do lỗi",MessageBoxButtons.OK,MessageBoxIcon.Warning);
        }
        finally{SetRunning(false);RefreshGrid();_log("[ID_CHANGE_RUN_END]");}
    }
    void RefreshProgress(int processed,int total)
        =>_progress.Text=$"Tiến độ: {processed}/{total}  |  Đã đổi: {_rows.Count(IsDone)}  |  Cần kiểm tra: {_rows.Count(x=>x.State=="need_verify")}";
}
