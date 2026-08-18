using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ToolTikTokV12.Services;

public sealed record TikTokAccountPoolItem(
    string Id,
    string Username,
    string Password,
    string TotpSecret,
    string Note,
    string AssignedProfile,
    int SourceRow)
{
    public bool IsAssigned => !string.IsNullOrWhiteSpace(AssignedProfile);
    public override string ToString()
    {
        var noteText = string.IsNullOrWhiteSpace(Note) ? "" : $" — {Note.Trim()}";
        return $"Dòng {SourceRow}: {Username}{noteText}" + (IsAssigned ? $"  [đã dùng: {AssignedProfile}]" : "");
    }
}

public sealed record TikTokAccountImportResult(int Added, int Updated, int Skipped, int TotalRows);

public sealed class TikTokAccountPoolService
{
    sealed class StoredCatalog
    {
        public int Version { get; set; } = 2;
        public string SourceFilePath { get; set; } = "";
        public List<StoredItem> Accounts { get; set; } = new();
    }

    sealed class StoredItem
    {
        public string Id { get; set; } = "";
        public string Username { get; set; } = "";
        public string PasswordProtected { get; set; } = "";
        public string TotpSecretProtected { get; set; } = "";
        public string Note { get; set; } = "";
        public string AssignedProfile { get; set; } = "";
        public int SourceRow { get; set; }
    }

    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ToolTikTok-V13.5-AccountPool-v1");
    readonly string _path;
    readonly object _cacheGate = new();
    StoredCatalog? _catalogCache;
    List<TikTokAccountPoolItem>? _itemsCache;
    DateTime _catalogCacheWriteUtc = DateTime.MinValue;
    long _catalogCacheLength = -1;
    HashSet<string>? _identityDoneCache;
    string _identityDoneCachePath = "";
    DateTime _identityDoneCacheWriteUtc = DateTime.MinValue;
    long _identityDoneCacheLength = -1;

    public TikTokAccountPoolService(string baseDir)
    {
        _path = Path.Combine(Path.GetFullPath(baseDir), "tiktok_accounts_pool.json");
    }

    public string CatalogPath => _path;
    public string CurrentSourcePath => LoadStoredCatalog().SourceFilePath ?? "";

    static (DateTime WriteUtc, long Length) FileFingerprint(string path)
    {
        var info = new FileInfo(path);
        return (info.LastWriteTimeUtc, info.Length);
    }

    public HashSet<string> GetIdentityDoneUsernames()
    {
        var path = CurrentSourcePath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var fingerprint = FileFingerprint(path);
        lock (_cacheGate)
        {
            if (_identityDoneCache is not null
                && path.Equals(_identityDoneCachePath, StringComparison.OrdinalIgnoreCase)
                && fingerprint.WriteUtc == _identityDoneCacheWriteUtc
                && fingerprint.Length == _identityDoneCacheLength)
                return new HashSet<string>(_identityDoneCache, StringComparer.OrdinalIgnoreCase);
        }

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rows = ReadSourceRows(path);
        for (var i = 1; i < rows.Count; i++)
        {
            var row = rows[i];
            var user = row.Count > 0 ? (row[0] ?? "").Trim() : "";
            var done = row.Count > 5 ? (row[5] ?? "").Trim() : "";
            if (user.Length == 0) continue;
            if (done.Equals("DONE", StringComparison.OrdinalIgnoreCase)
                || done.Equals("YES", StringComparison.OrdinalIgnoreCase)
                || done.Equals("TRUE", StringComparison.OrdinalIgnoreCase)
                || done == "1")
                result.Add(user);
        }

        fingerprint = FileFingerprint(path);
        lock (_cacheGate)
        {
            _identityDoneCache = new HashSet<string>(result, StringComparer.OrdinalIgnoreCase);
            _identityDoneCachePath = path;
            _identityDoneCacheWriteUtc = fingerprint.WriteUtc;
            _identityDoneCacheLength = fingerprint.Length;
        }
        return result;
    }

    public bool IsIdentityDone(string username)
        => GetIdentityDoneUsernames().Contains((username ?? "").Trim());

    public void MarkIdentityDone(string username)
    {
        username = (username ?? "").Trim();
        if (username.Length == 0) throw new InvalidOperationException("Username trống; không thể ghi DONE.");
        var path = CurrentSourcePath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new InvalidOperationException("Kho tài khoản chưa có file Excel nguồn; không thể ghi DONE bền vững.");
        var rows = ReadSourceRows(path);
        for (var i = 1; i < rows.Count; i++)
        {
            var row = rows[i];
            var user = row.Count > 0 ? (row[0] ?? "").Trim() : "";
            if (!user.Equals(username, StringComparison.OrdinalIgnoreCase)) continue;
            SetIdentityDoneCell(path, i + 1, "DONE");
            var fingerprint = FileFingerprint(path);
            lock (_cacheGate)
            {
                if (_identityDoneCache is not null && path.Equals(_identityDoneCachePath, StringComparison.OrdinalIgnoreCase))
                {
                    _identityDoneCache.Add(username);
                    _identityDoneCacheWriteUtc = fingerprint.WriteUtc;
                    _identityDoneCacheLength = fingerprint.Length;
                }
                else
                {
                    // Chưa có snapshot đầy đủ trong RAM: để lần đọc kế tiếp nạp lại toàn bộ cột DONE.
                    _identityDoneCache = null;
                    _identityDoneCachePath = "";
                    _identityDoneCacheWriteUtc = DateTime.MinValue;
                    _identityDoneCacheLength = -1;
                }
            }
            return;
        }
        throw new InvalidOperationException($"Không tìm thấy tài khoản {username} trong file Excel đang dùng để ghi DONE.");
    }

    public List<TikTokAccountPoolItem> Load()
    {
        try
        {
            LoadStoredCatalog();
            lock (_cacheGate)
                return _itemsCache is null ? new List<TikTokAccountPoolItem>() : new List<TikTokAccountPoolItem>(_itemsCache);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Không đọc được kho tài khoản TikTok.", ex);
        }
    }

    public void Save(IEnumerable<TikTokAccountPoolItem> items)
    {
        var stored = LoadStoredCatalog();
        SaveCatalog(items, stored.SourceFilePath);
    }

    // Mở một file mới = thay thế toàn bộ dữ liệu kho bằng dữ liệu của file đó.
    // Chỉ giữ trạng thái gán profile cho username trùng nhau; không nối chồng dữ liệu file cũ.
    public TikTokAccountImportResult ImportExcel(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new FileNotFoundException("Không tìm thấy file tài khoản.", path);

        path = Path.GetFullPath(path);
        var rows = ReadSourceRows(path);
        var parsed = ParseRows(rows);
        var existing = Load();
        var byUsername = existing
            .GroupBy(x => x.Username, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        // Từ V13.5 patch PROFILE_IN_EXCEL: cột E là nguồn lưu lâu dài cho "Profile đã gán".
        // Với file cũ chưa có cột E, chỉ lần nhập đầu tiên sẽ lấy trạng thái gán đang có trong JSON
        // để backfill sang Excel. Sau khi cột E đã tồn tại, Excel là nguồn sự thật: xóa E rồi Tải lại
        // cũng đồng nghĩa bỏ gán profile.
        var hasAssignmentColumn = HasAssignmentColumn(rows);
        var replaced = new List<TikTokAccountPoolItem>();
        foreach (var item in parsed)
        {
            if (byUsername.TryGetValue(item.Username, out var old))
            {
                var assigned = hasAssignmentColumn ? item.AssignedProfile : old.AssignedProfile;
                replaced.Add(item with { Id = old.Id, AssignedProfile = assigned });
            }
            else
            {
                replaced.Add(item with { Id = Guid.NewGuid().ToString("N") });
            }
        }

        // Luôn chuẩn hóa cột E và ghi trạng thái gán hiện tại vào file nguồn trước khi lưu catalog.
        // Nhờ vậy cài lại/cập nhật Tool không làm mất lịch sử tài khoản đã gán profile nào.
        SyncAssignmentsToSource(path, replaced);
        SaveCatalog(replaced, path);
        var skipped = Math.Max(0, rows.Count - replaced.Count - 1);
        return new TikTokAccountImportResult(replaced.Count, 0, skipped, rows.Count);
    }

    public TikTokAccountImportResult ReloadCurrentExcel()
    {
        var path = CurrentSourcePath;
        if (string.IsNullOrWhiteSpace(path))
            return new TikTokAccountImportResult(0, 0, 0, 0);
        if (!File.Exists(path))
            throw new FileNotFoundException("File Excel đang dùng không còn tồn tại.", path);
        return ImportExcel(path);
    }

    public void Assign(string accountId, string profileName)
    {
        var items = Load();
        var index = items.FindIndex(x => x.Id.Equals(accountId, StringComparison.OrdinalIgnoreCase));
        if (index < 0) throw new InvalidOperationException("Dòng tài khoản đã chọn không còn tồn tại trong kho.");
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i].AssignedProfile.Equals(profileName, StringComparison.OrdinalIgnoreCase))
                items[i] = items[i] with { AssignedProfile = "" };
        }
        items[index] = items[index] with { AssignedProfile = profileName };
        SyncAssignmentsToCurrentSource(items);
        Save(items);
    }

    public void ReleaseByProfile(string profileName)
    {
        var items = Load();
        var changed = false;
        for (var i = 0; i < items.Count; i++)
        {
            if (!items[i].AssignedProfile.Equals(profileName, StringComparison.OrdinalIgnoreCase)) continue;
            items[i] = items[i] with { AssignedProfile = "" };
            changed = true;
        }
        if (changed)
        {
            SyncAssignmentsToCurrentSource(items);
            Save(items);
        }
    }

    public void ReleaseAccount(string accountId)
    {
        var items = Load();
        var index = items.FindIndex(x => x.Id.Equals(accountId, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return;
        items[index] = items[index] with { AssignedProfile = "" };
        SyncAssignmentsToCurrentSource(items);
        Save(items);
    }

    public void RenameAssignedProfile(string oldName, string newName)
    {
        var items = Load();
        var changed = false;
        for (var i = 0; i < items.Count; i++)
        {
            if (!items[i].AssignedProfile.Equals(oldName, StringComparison.OrdinalIgnoreCase)) continue;
            items[i] = items[i] with { AssignedProfile = newName };
            changed = true;
        }
        if (changed)
        {
            SyncAssignmentsToCurrentSource(items);
            Save(items);
        }
    }

    public void Delete(string accountId)
    {
        var items = Load();
        var current = items.FirstOrDefault(x => x.Id.Equals(accountId, StringComparison.OrdinalIgnoreCase));
        if (current is null) return;

        var sourcePath = CurrentSourcePath;
        if (!string.IsNullOrWhiteSpace(sourcePath))
            ClearSourceRow(sourcePath, current.SourceRow);

        items.RemoveAll(x => x.Id.Equals(accountId, StringComparison.OrdinalIgnoreCase));
        Save(items);
    }

    public void Upsert(TikTokAccountPoolItem item)
    {
        var items = Load();
        var index = items.FindIndex(x => x.Id.Equals(item.Id, StringComparison.OrdinalIgnoreCase));
        var normalized = item with
        {
            Id = string.IsNullOrWhiteSpace(item.Id) ? Guid.NewGuid().ToString("N") : item.Id,
            Username = item.Username.Trim(),
            TotpSecret = TikTokAuthService.NormalizeTotpSecret(item.TotpSecret),
            Note = (item.Note ?? "").Trim(),
            SourceRow = item.SourceRow <= 1 ? NextSourceRow(items) : item.SourceRow
        };

        var sourcePath = CurrentSourcePath;
        if (!string.IsNullOrWhiteSpace(sourcePath))
            WriteSourceRow(sourcePath, normalized);

        if (index >= 0) items[index] = normalized;
        else items.Add(normalized);
        Save(items);
    }

    StoredCatalog LoadStoredCatalog()
    {
        lock (_cacheGate)
        {
            if (!File.Exists(_path))
            {
                _catalogCache ??= new StoredCatalog();
                _itemsCache ??= new List<TikTokAccountPoolItem>();
                _catalogCacheWriteUtc = DateTime.MinValue;
                _catalogCacheLength = -1;
                return _catalogCache;
            }

            var fingerprint = FileFingerprint(_path);
            if (_catalogCache is not null
                && fingerprint.WriteUtc == _catalogCacheWriteUtc
                && fingerprint.Length == _catalogCacheLength)
                return _catalogCache;

            var loaded = JsonSerializer.Deserialize<StoredCatalog>(File.ReadAllText(_path)) ?? new StoredCatalog();
            _catalogCache = loaded;
            _itemsCache = ToItems(loaded);
            _catalogCacheWriteUtc = fingerprint.WriteUtc;
            _catalogCacheLength = fingerprint.Length;
            return loaded;
        }
    }

    static List<TikTokAccountPoolItem> ToItems(StoredCatalog stored)
    {
        return stored.Accounts
            .Where(x => !string.IsNullOrWhiteSpace(x.Username))
            .Select(x => new TikTokAccountPoolItem(
                string.IsNullOrWhiteSpace(x.Id) ? Guid.NewGuid().ToString("N") : x.Id,
                x.Username.Trim(),
                Unprotect(x.PasswordProtected),
                TikTokAuthService.NormalizeTotpSecret(Unprotect(x.TotpSecretProtected)),
                (x.Note ?? "").Trim(),
                (x.AssignedProfile ?? "").Trim(),
                x.SourceRow <= 0 ? 1 : x.SourceRow))
            .ToList();
    }

    void SaveCatalog(IEnumerable<TikTokAccountPoolItem> items, string? sourcePath)
    {
        var catalog = new StoredCatalog
        {
            Version = 2,
            SourceFilePath = string.IsNullOrWhiteSpace(sourcePath) ? "" : Path.GetFullPath(sourcePath),
            Accounts = items
                .Where(x => !string.IsNullOrWhiteSpace(x.Username))
                .Select(x => new StoredItem
                {
                    Id = string.IsNullOrWhiteSpace(x.Id) ? Guid.NewGuid().ToString("N") : x.Id,
                    Username = x.Username.Trim(),
                    PasswordProtected = Protect(x.Password ?? ""),
                    TotpSecretProtected = Protect(TikTokAuthService.NormalizeTotpSecret(x.TotpSecret)),
                    Note = (x.Note ?? "").Trim(),
                    AssignedProfile = (x.AssignedProfile ?? "").Trim(),
                    SourceRow = x.SourceRow <= 0 ? 1 : x.SourceRow
                }).ToList()
        };
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        AtomicWrite(_path, JsonSerializer.Serialize(catalog, JsonOptions));
        var fingerprint = FileFingerprint(_path);
        lock (_cacheGate)
        {
            _catalogCache = catalog;
            _itemsCache = ToItems(catalog);
            _catalogCacheWriteUtc = fingerprint.WriteUtc;
            _catalogCacheLength = fingerprint.Length;
        }
    }

    static int NextSourceRow(List<TikTokAccountPoolItem> items)
        => items.Count == 0 ? 2 : Math.Max(2, items.Max(x => x.SourceRow) + 1);

    static List<List<string>> ReadSourceRows(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".xlsx" => ReadXlsx(path),
            ".csv" or ".txt" => ReadDelimited(path),
            _ => throw new InvalidOperationException("Chỉ hỗ trợ file .xlsx, .csv hoặc .txt.")
        };
    }

    static void WriteSourceRow(string path, TikTokAccountPoolItem item)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("File Excel đang dùng không còn tồn tại.", path);
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".xlsx") UpdateXlsxRow(path, item.SourceRow, item.Username, item.Password, item.TotpSecret, item.Note, item.AssignedProfile);
        else if (ext is ".csv" or ".txt") UpdateDelimitedRow(path, item.SourceRow, item.Username, item.Password, item.TotpSecret, item.Note, item.AssignedProfile);
        else throw new InvalidOperationException("Chỉ hỗ trợ file .xlsx, .csv hoặc .txt.");
    }

    static void ClearSourceRow(string path, int sourceRow)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("File Excel đang dùng không còn tồn tại.", path);
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".xlsx") { UpdateXlsxRow(path, sourceRow, "", "", "", "", ""); SetIdentityDoneCell(path, sourceRow, ""); }
        else if (ext is ".csv" or ".txt") { UpdateDelimitedRow(path, sourceRow, "", "", "", "", ""); SetIdentityDoneCell(path, sourceRow, ""); }
        else throw new InvalidOperationException("Chỉ hỗ trợ file .xlsx, .csv hoặc .txt.");
    }

    static void UpdateDelimitedRow(string path, int sourceRow, string username, string password, string totp, string note, string assignedProfile)
    {
        var lines = File.ReadAllLines(path, Encoding.UTF8).ToList();
        var separator = lines.Take(5).SelectMany(x => new[] { ',', ';', '\t' }.Select(c => (c, count: x.Count(ch => ch == c))))
            .GroupBy(x => x.c).Select(g => (c: g.Key, score: g.Sum(x => x.count))).OrderByDescending(x => x.score).FirstOrDefault().c;
        if (separator == '\0') separator = ',';
        if (lines.Count == 0) lines.Add("Tài khoản" + separator + "Mật khẩu" + separator + "2FA" + separator + "Ghi chú" + separator + "Profile đã gán" + separator + "Tên/ảnh DONE");
        var header = SplitDelimited(lines[0], separator);
        while (header.Count < 6) header.Add("");
        header[4] = "Profile đã gán";
        header[5] = "Tên/ảnh DONE";
        lines[0] = string.Join(separator, header.Select(x => EscapeDelimited(x, separator)));

        while (lines.Count < sourceRow) lines.Add("");
        var cells = SplitDelimited(lines[sourceRow - 1], separator);
        while (cells.Count < 6) cells.Add("");
        cells[0] = username; cells[1] = password; cells[2] = totp; cells[3] = note; cells[4] = assignedProfile;
        lines[sourceRow - 1] = string.Join(separator, cells.Select(x => EscapeDelimited(x, separator)));
        AtomicWrite(path, string.Join(Environment.NewLine, lines));
    }

    static string EscapeDelimited(string value, char separator)
    {
        value ??= "";
        if (!value.Contains(separator) && !value.Contains('"') && !value.Contains('\r') && !value.Contains('\n')) return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    static void UpdateXlsxRow(string path, int sourceRow, string username, string password, string totp, string note, string assignedProfile)
    {
        try
        {
            using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var memory = new MemoryStream();
            source.CopyTo(memory);
            memory.Position = 0;
            string sheetName;
            XDocument sheetDoc;
            using (var zip = new ZipArchive(memory, ZipArchiveMode.Update, leaveOpen: true))
            {
                var sheetEntry = ResolveFirstSheet(zip) ?? throw new InvalidOperationException("File Excel không có worksheet.");
                sheetName = sheetEntry.FullName;
                using (var stream = sheetEntry.Open()) sheetDoc = XDocument.Load(stream);
                sheetEntry.Delete();

                XNamespace ns = sheetDoc.Root?.Name.Namespace ?? "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
                var sheetData = sheetDoc.Descendants(ns + "sheetData").FirstOrDefault()
                    ?? throw new InvalidOperationException("Worksheet không có sheetData.");
                var row = sheetData.Elements(ns + "row")
                    .FirstOrDefault(r => int.TryParse(r.Attribute("r")?.Value, out var n) && n == sourceRow);
                if (row is null)
                {
                    row = new XElement(ns + "row", new XAttribute("r", sourceRow));
                    sheetData.Add(row);
                }

                SetInlineCell(row, ns, "A" + sourceRow, username);
                SetInlineCell(row, ns, "B" + sourceRow, password);
                SetInlineCell(row, ns, "C" + sourceRow, totp);
                SetInlineCell(row, ns, "D" + sourceRow, note);
                SetInlineCell(row, ns, "E" + sourceRow, assignedProfile);
                EnsureAssignmentHeader(sheetData, ns);

                var orderedCells = row.Elements(ns + "c").OrderBy(c => ColumnIndex(c.Attribute("r")?.Value ?? "A1")).ToList();
                row.Elements(ns + "c").Remove();
                row.Add(orderedCells);

                var orderedRows = sheetData.Elements(ns + "row")
                    .OrderBy(r => int.TryParse(r.Attribute("r")?.Value, out var n) ? n : int.MaxValue).ToList();
                sheetData.Elements(ns + "row").Remove();
                sheetData.Add(orderedRows);

                var newEntry = zip.CreateEntry(sheetName, CompressionLevel.Optimal);
                using var outStream = newEntry.Open();
                sheetDoc.Save(outStream);
            }

            memory.Position = 0;
            var temp = path + ".tooltmp";
            using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None)) memory.CopyTo(output);
            ReplaceFileFromTemp(temp, path);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidOperationException("File Excel không hợp lệ hoặc đang được lưu dở.", ex);
        }
    }

    static void SetInlineCell(XElement row, XNamespace ns, string reference, string value)
    {
        var cell = row.Elements(ns + "c").FirstOrDefault(c =>
            string.Equals(c.Attribute("r")?.Value, reference, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrEmpty(value))
        {
            cell?.Remove();
            return;
        }
        cell ??= new XElement(ns + "c", new XAttribute("r", reference));
        if (cell.Parent is null) row.Add(cell);
        cell.RemoveNodes();
        cell.SetAttributeValue("t", "inlineStr");
        XNamespace xml = XNamespace.Xml;
        cell.Add(new XElement(ns + "is", new XElement(ns + "t", new XAttribute(xml + "space", "preserve"), value)));
    }

    static List<TikTokAccountPoolItem> ParseRows(List<List<string>> rows)
    {
        var result = new List<TikTokAccountPoolItem>();
        if (rows.Count == 0) return result;

        // V13.5+: Kho tài khoản dùng A-E như cũ; cột F được dành riêng cho trạng thái
        // đổi Tên/ảnh tự động (DONE). A = tài khoản, B = mật khẩu, C = 2FA,
        // D = ghi chú, E = Profile đã gán, F = Tên/ảnh DONE.
        var firstNonEmpty = rows.FindIndex(r => r.Any(c => !string.IsNullOrWhiteSpace(c)));
        if (firstNonEmpty < 0) return result;

        const int userCol = 0;
        const int passCol = 1;
        const int totpCol = 2;
        const int noteCol = 3;
        const int assignedCol = 4;
        var start = firstNonEmpty + 1;

        for (var rowIndex = start; rowIndex < rows.Count; rowIndex++)
        {
            var row = rows[rowIndex];
            string Get(int col) => col >= 0 && col < row.Count ? (row[col] ?? "").Trim() : "";
            var user = Get(userCol);
            if (user.Length == 0) continue;
            var pass = Get(passCol);
            var totp = TikTokAuthService.NormalizeTotpSecret(Get(totpCol));
            var note = Get(noteCol);
            var assigned = Get(assignedCol);
            result.Add(new TikTokAccountPoolItem("", user, pass, totp, note, assigned, rowIndex + 1));
        }
        return result;
    }

    static int FindHeader(List<string> headers, params string[] names)
    {
        for (var i = 0; i < headers.Count; i++)
        {
            foreach (var name in names)
                if (headers[i].Equals(NormalizeHeader(name), StringComparison.OrdinalIgnoreCase)) return i;
        }
        return -1;
    }

    static string NormalizeHeader(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var normalized = value.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var ch in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetterOrDigit(ch) ? ch : ' ');
        }
        return Regex.Replace(sb.ToString().Normalize(NormalizationForm.FormC), @"\s+", " ").Trim();
    }

    static bool HasAssignmentColumn(List<List<string>> rows)
    {
        var firstNonEmpty = rows.FindIndex(r => r.Any(c => !string.IsNullOrWhiteSpace(c)));
        if (firstNonEmpty < 0) return false;
        var header = rows[firstNonEmpty];
        if (header.Count <= 4) return false;
        var normalized = NormalizeHeader(header[4]);
        return normalized.Contains("profile", StringComparison.OrdinalIgnoreCase);
    }

    void SyncAssignmentsToCurrentSource(IEnumerable<TikTokAccountPoolItem> items)
    {
        var path = CurrentSourcePath;
        if (string.IsNullOrWhiteSpace(path)) return;
        if (!File.Exists(path)) throw new FileNotFoundException("File Excel đang dùng không còn tồn tại.", path);
        SyncAssignmentsToSource(path, items);
    }

    static void SyncAssignmentsToSource(string path, IEnumerable<TikTokAccountPoolItem> items)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var snapshot = items.Where(x => x.SourceRow > 1).ToList();
        if (ext == ".xlsx")
            UpdateXlsxAssignments(path, snapshot);
        else if (ext is ".csv" or ".txt")
            UpdateDelimitedAssignments(path, snapshot);
        else
            throw new InvalidOperationException("Chỉ hỗ trợ file .xlsx, .csv hoặc .txt.");
    }

    static void UpdateDelimitedAssignments(string path, IReadOnlyList<TikTokAccountPoolItem> items)
    {
        var lines = File.ReadAllLines(path, Encoding.UTF8).ToList();
        var separator = lines.Take(5).SelectMany(x => new[] { ',', ';', '\t' }.Select(c => (c, count: x.Count(ch => ch == c))))
            .GroupBy(x => x.c).Select(g => (c: g.Key, score: g.Sum(x => x.count))).OrderByDescending(x => x.score).FirstOrDefault().c;
        if (separator == '\0') separator = ',';
        if (lines.Count == 0) lines.Add("");

        var header = SplitDelimited(lines[0], separator);
        while (header.Count < 6) header.Add("");
        header[4] = "Profile đã gán";
        header[5] = "Tên/ảnh DONE";
        lines[0] = string.Join(separator, header.Select(x => EscapeDelimited(x, separator)));

        foreach (var item in items)
        {
            while (lines.Count < item.SourceRow) lines.Add("");
            var cells = SplitDelimited(lines[item.SourceRow - 1], separator);
            while (cells.Count < 6) cells.Add("");
            cells[4] = item.AssignedProfile ?? "";
            lines[item.SourceRow - 1] = string.Join(separator, cells.Select(x => EscapeDelimited(x, separator)));
        }
        AtomicWrite(path, string.Join(Environment.NewLine, lines));
    }

    static void UpdateXlsxAssignments(string path, IReadOnlyList<TikTokAccountPoolItem> items)
    {
        try
        {
            using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var memory = new MemoryStream();
            source.CopyTo(memory);
            memory.Position = 0;
            string sheetName;
            XDocument sheetDoc;
            using (var zip = new ZipArchive(memory, ZipArchiveMode.Update, leaveOpen: true))
            {
                var sheetEntry = ResolveFirstSheet(zip) ?? throw new InvalidOperationException("File Excel không có worksheet.");
                sheetName = sheetEntry.FullName;
                using (var stream = sheetEntry.Open()) sheetDoc = XDocument.Load(stream);
                sheetEntry.Delete();

                XNamespace ns = sheetDoc.Root?.Name.Namespace ?? "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
                var sheetData = sheetDoc.Descendants(ns + "sheetData").FirstOrDefault()
                    ?? throw new InvalidOperationException("Worksheet không có sheetData.");

                EnsureAssignmentHeader(sheetData, ns);
                foreach (var item in items)
                {
                    var row = sheetData.Elements(ns + "row")
                        .FirstOrDefault(r => int.TryParse(r.Attribute("r")?.Value, out var n) && n == item.SourceRow);
                    if (row is null)
                    {
                        row = new XElement(ns + "row", new XAttribute("r", item.SourceRow));
                        sheetData.Add(row);
                    }
                    SetInlineCell(row, ns, "E" + item.SourceRow, item.AssignedProfile ?? "");
                    var orderedCells = row.Elements(ns + "c").OrderBy(c => ColumnIndex(c.Attribute("r")?.Value ?? "A1")).ToList();
                    row.Elements(ns + "c").Remove();
                    row.Add(orderedCells);
                }

                var orderedRows = sheetData.Elements(ns + "row")
                    .OrderBy(r => int.TryParse(r.Attribute("r")?.Value, out var n) ? n : int.MaxValue).ToList();
                sheetData.Elements(ns + "row").Remove();
                sheetData.Add(orderedRows);

                var newEntry = zip.CreateEntry(sheetName, CompressionLevel.Optimal);
                using var outStream = newEntry.Open();
                sheetDoc.Save(outStream);
            }

            memory.Position = 0;
            var temp = path + ".tooltmp";
            using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None)) memory.CopyTo(output);
            ReplaceFileFromTemp(temp, path);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidOperationException("File Excel không hợp lệ hoặc đang được lưu dở.", ex);
        }
    }

    static void SetIdentityDoneCell(string path, int sourceRow, string value)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".csv" or ".txt")
        {
            var lines = File.ReadAllLines(path, Encoding.UTF8).ToList();
            var separator = lines.Take(5).SelectMany(x => new[] { ',', ';', '\t' }.Select(c => (c, count: x.Count(ch => ch == c))))
                .GroupBy(x => x.c).Select(g => (c: g.Key, score: g.Sum(x => x.count))).OrderByDescending(x => x.score).FirstOrDefault().c;
            if (separator == '\0') separator = ',';
            if (lines.Count == 0) lines.Add("");
            var header = SplitDelimited(lines[0], separator);
            while (header.Count < 6) header.Add("");
            header[4] = "Profile đã gán";
            header[5] = "Tên/ảnh DONE";
            lines[0] = string.Join(separator, header.Select(x => EscapeDelimited(x, separator)));
            while (lines.Count < sourceRow) lines.Add("");
            var cells = SplitDelimited(lines[sourceRow - 1], separator);
            while (cells.Count < 6) cells.Add("");
            cells[5] = value ?? "";
            lines[sourceRow - 1] = string.Join(separator, cells.Select(x => EscapeDelimited(x, separator)));
            AtomicWrite(path, string.Join(Environment.NewLine, lines));
            return;
        }
        if (ext != ".xlsx") throw new InvalidOperationException("Chỉ hỗ trợ file .xlsx, .csv hoặc .txt.");

        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var memory = new MemoryStream();
        source.CopyTo(memory);
        memory.Position = 0;
        string sheetName;
        XDocument sheetDoc;
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Update, leaveOpen: true))
        {
            var sheetEntry = ResolveFirstSheet(zip) ?? throw new InvalidOperationException("File Excel không có worksheet.");
            sheetName = sheetEntry.FullName;
            using (var stream = sheetEntry.Open()) sheetDoc = XDocument.Load(stream);
            sheetEntry.Delete();
            XNamespace ns = sheetDoc.Root?.Name.Namespace ?? "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            var sheetData = sheetDoc.Descendants(ns + "sheetData").FirstOrDefault()
                ?? throw new InvalidOperationException("Worksheet không có sheetData.");
            EnsureAssignmentHeader(sheetData, ns);
            var row = sheetData.Elements(ns + "row").FirstOrDefault(r => int.TryParse(r.Attribute("r")?.Value, out var n) && n == sourceRow);
            if (row is null) { row = new XElement(ns + "row", new XAttribute("r", sourceRow)); sheetData.Add(row); }
            SetInlineCell(row, ns, "F" + sourceRow, value ?? "");
            var orderedCells = row.Elements(ns + "c").OrderBy(c => ColumnIndex(c.Attribute("r")?.Value ?? "A1")).ToList();
            row.Elements(ns + "c").Remove();
            row.Add(orderedCells);
            var newEntry = zip.CreateEntry(sheetName, CompressionLevel.Optimal);
            using var outStream = newEntry.Open();
            sheetDoc.Save(outStream);
        }
        memory.Position = 0;
        var temp = path + ".tooltmp";
        using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None)) memory.CopyTo(output);
        ReplaceFileFromTemp(temp, path);
    }

    static void EnsureAssignmentHeader(XElement sheetData, XNamespace ns)
    {
        var headerRow = sheetData.Elements(ns + "row")
            .FirstOrDefault(r => int.TryParse(r.Attribute("r")?.Value, out var n) && n == 1);
        if (headerRow is null)
        {
            headerRow = new XElement(ns + "row", new XAttribute("r", 1));
            sheetData.AddFirst(headerRow);
        }
        SetInlineCell(headerRow, ns, "E1", "Profile đã gán");
        SetInlineCell(headerRow, ns, "F1", "Tên/ảnh DONE");
        var orderedCells = headerRow.Elements(ns + "c").OrderBy(c => ColumnIndex(c.Attribute("r")?.Value ?? "A1")).ToList();
        headerRow.Elements(ns + "c").Remove();
        headerRow.Add(orderedCells);
    }

    static List<List<string>> ReadDelimited(string path)
    {
        var lines = File.ReadAllLines(path, Encoding.UTF8);
        var separator = lines.Take(5).SelectMany(x => new[] { ',', ';', '\t' }.Select(c => (c, count: x.Count(ch => ch == c))))
            .GroupBy(x => x.c).Select(g => (c: g.Key, score: g.Sum(x => x.count))).OrderByDescending(x => x.score).FirstOrDefault().c;
        if (separator == '\0') separator = ',';
        return lines.Select(line => SplitDelimited(line, separator)).ToList();
    }

    static List<string> SplitDelimited(string line, char separator)
    {
        var cells = new List<string>();
        var sb = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else quoted = !quoted;
            }
            else if (ch == separator && !quoted) { cells.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(ch);
        }
        cells.Add(sb.ToString());
        return cells;
    }

    static List<List<string>> ReadXlsx(string path)
    {
        // Excel thường giữ file đang mở. Đọc bằng FileShare.ReadWrite/Delete và chép vào RAM
        // để người dùng không phải đóng Excel trước khi bấm Nhập.
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var memory = new MemoryStream();
        source.CopyTo(memory);
        memory.Position = 0;
        using var zip = new ZipArchive(memory, ZipArchiveMode.Read, leaveOpen: false);

        var sharedStrings = ReadSharedStrings(zip);
        var sheetEntry = ResolveFirstSheet(zip) ?? throw new InvalidOperationException("File Excel không có worksheet.");
        using var stream = sheetEntry.Open();
        var doc = XDocument.Load(stream);
        XNamespace ns = doc.Root?.Name.Namespace ?? "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var rows = new List<List<string>>();
        foreach (var row in doc.Descendants(ns + "row"))
        {
            // Giữ đúng số dòng Excel để thao tác Sửa/Xóa ghi ngược đúng dòng nguồn.
            var excelRow = int.TryParse(row.Attribute("r")?.Value, out var parsedRow) && parsedRow > 0
                ? parsedRow
                : rows.Count + 1;
            while (rows.Count < excelRow - 1) rows.Add(new List<string> { "", "", "", "", "", "" });

            // Đọc A-F; F = trạng thái DONE của luồng Tên/ảnh tự động.
            var values = new string[6];
            foreach (var cell in row.Elements(ns + "c"))
            {
                var reference = cell.Attribute("r")?.Value ?? "A1";
                var col = ColumnIndex(reference);
                if (col < 0 || col > 5) continue;

                var type = cell.Attribute("t")?.Value ?? "";
                var raw = cell.Element(ns + "v")?.Value ?? "";
                string value;
                if (type == "s" && int.TryParse(raw, out var sharedIndex) && sharedIndex >= 0 && sharedIndex < sharedStrings.Count)
                    value = sharedStrings[sharedIndex];
                else if (type == "inlineStr")
                    value = string.Concat(cell.Descendants(ns + "t").Select(x => x.Value));
                else value = raw;
                values[col] = value;
            }
            if (rows.Count == excelRow - 1) rows.Add(values.ToList());
            else rows[excelRow - 1] = values.ToList();
        }
        return rows;
    }

    static List<string> ReadSharedStrings(ZipArchive zip)
    {
        var entry = zip.GetEntry("xl/sharedStrings.xml");
        if (entry is null) return new List<string>();
        using var stream = entry.Open();
        var doc = XDocument.Load(stream);
        XNamespace ns = doc.Root?.Name.Namespace ?? "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        return doc.Descendants(ns + "si")
            .Select(si => string.Concat(si.Descendants(ns + "t").Select(t => t.Value)))
            .ToList();
    }

    static ZipArchiveEntry? ResolveFirstSheet(ZipArchive zip)
    {
        var direct = zip.GetEntry("xl/worksheets/sheet1.xml");
        if (direct is not null) return direct;
        return zip.Entries
            .Where(e => e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase) && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    static int ColumnIndex(string reference)
    {
        var index = 0;
        foreach (var ch in reference)
        {
            if (!char.IsLetter(ch)) break;
            index = index * 26 + (char.ToUpperInvariant(ch) - 'A' + 1);
        }
        return Math.Max(0, index - 1);
    }

    static string Protect(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var raw = Encoding.UTF8.GetBytes(value);
        return Convert.ToBase64String(ProtectedData.Protect(raw, Entropy, DataProtectionScope.CurrentUser));
    }

    static string Unprotect(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var raw = Convert.FromBase64String(value);
        return Encoding.UTF8.GetString(ProtectedData.Unprotect(raw, Entropy, DataProtectionScope.CurrentUser));
    }

    static void AtomicWrite(string path, string content)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, content, new UTF8Encoding(false));
        ReplaceFileFromTemp(temp, path);
    }

    static void ReplaceFileFromTemp(string temp, string path)
    {
        // Ưu tiên replace nguyên tử khi file không bị Excel giữ handle.
        try
        {
            File.Move(temp, path, true);
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Excel thường cho chia sẻ quyền đọc/ghi nhưng không cho FILE_SHARE_DELETE.
            // Vì vậy rename/replace sẽ báo Access denied dù ta vẫn có thể ghi vào chính file.
            // File tạm đã hoàn chỉnh nên fallback là truncate + copy bytes vào handle hiện tại,
            // không cần rename/delete file nguồn.
            try
            {
                using var input = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                input.CopyTo(output);
                output.Flush(true);
                output.Close();
                input.Close();
                try { File.Delete(temp); } catch { }
                return;
            }
            catch (Exception writeEx) when (writeEx is IOException or UnauthorizedAccessException)
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                throw new IOException(
                    "Không thể ghi vào file Excel. File đang bị Excel/chương trình khác khóa quyền ghi hoặc file/thư mục đang ở chế độ chỉ đọc. " +
                    "Hãy kiểm tra file không phải Read-only; nếu Excel đang mở file ở chế độ Protected View/Read-only thì bật Edit rồi thử lại.",
                    writeEx);
            }
        }
    }
}
