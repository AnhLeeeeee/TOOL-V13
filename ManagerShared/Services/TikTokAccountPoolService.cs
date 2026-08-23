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
        return $"Dòng {SourceRow}: {Username}{noteText}"
            + (IsAssigned ? $"  [đã dùng: {AssignedProfile}]" : "");
    }
}

public sealed record TikTokAccountImportResult(
    int Added,
    int Updated,
    int Skipped,
    int TotalRows);

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

    sealed record SourceColumnLayout(
        int HeaderRow,
        int User,
        int Password,
        int Totp,
        int Note,
        int Assigned,
        int IdentityDone,
        int SuspiciousNote,
        int SuspiciousAssigned,
        int SuspiciousIdentity);

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    static readonly byte[] Entropy =
        Encoding.UTF8.GetBytes("ToolTikTok-V13.5-AccountPool-v1");

    readonly string _path;

    public TikTokAccountPoolService(string baseDir)
    {
        _path = Path.Combine(
            Path.GetFullPath(baseDir),
            "tiktok_accounts_pool.json");
    }

    public string CatalogPath => _path;

    public string CurrentSourcePath
        => LoadStoredCatalog().SourceFilePath ?? "";

    public HashSet<string> GetIdentityDoneUsernames()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var path = CurrentSourcePath;

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return result;

        var rows = ReadSourceRows(path);
        var columns = ResolveSourceColumns(rows, allocateManagedColumns: false);

        if (columns.HeaderRow < 0 || columns.IdentityDone < 0)
            return result;

        for (var i = columns.HeaderRow + 1; i < rows.Count; i++)
        {
            var row = rows[i];
            var user = GetCell(row, columns.User).Trim();
            var done = GetCell(row, columns.IdentityDone).Trim();

            if (user.Length == 0) continue;
            if (IsDoneValue(done)) result.Add(user);
        }

        return result;
    }

    public bool IsIdentityDone(string username)
        => GetIdentityDoneUsernames().Contains((username ?? "").Trim());

    public void MarkIdentityDone(string username)
    {
        username = (username ?? "").Trim();

        if (username.Length == 0)
            throw new InvalidOperationException(
                "Username trống; không thể ghi DONE.");

        var path = CurrentSourcePath;

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new InvalidOperationException(
                "Kho tài khoản chưa có file Excel nguồn; không thể ghi DONE bền vững.");

        var rows = ReadSourceRows(path);
        var columns = ResolveSourceColumns(rows, allocateManagedColumns: false);

        if (columns.HeaderRow < 0)
            throw new InvalidOperationException(
                "Không tìm thấy hàng tiêu đề trong file Excel.");

        for (var i = columns.HeaderRow + 1; i < rows.Count; i++)
        {
            var user = GetCell(rows[i], columns.User).Trim();

            if (!user.Equals(username, StringComparison.OrdinalIgnoreCase))
                continue;

            SetIdentityDoneCell(path, i + 1, "DONE");
            return;
        }

        throw new InvalidOperationException(
            $"Không tìm thấy tài khoản {username} trong file Excel đang dùng để ghi DONE.");
    }

    public List<TikTokAccountPoolItem> Load()
    {
        try
        {
            return ToItems(LoadStoredCatalog());
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Không đọc được kho tài khoản TikTok.",
                ex);
        }
    }

    public void Save(IEnumerable<TikTokAccountPoolItem> items)
    {
        var stored = LoadStoredCatalog();
        SaveCatalog(items, stored.SourceFilePath);
    }

    // Excel linh hoạt:
    // - Tự tìm Tài khoản / Mật khẩu / 2FA theo tiêu đề, fallback A/B/C.
    // - Không mặc định D/E/F là cột của Tool.
    // - Email, Ngày tạo và các cột riêng được giữ nguyên.
    // - Ghi chú, Profile đã gán, Tên/ảnh DONE được thêm ở bên phải nếu chưa có.
    public TikTokAccountImportResult ImportExcel(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new FileNotFoundException(
                "Không tìm thấy file tài khoản.",
                path);

        path = Path.GetFullPath(path);

        var oldSourcePath = CurrentSourcePath;
        var rows = ReadSourceRows(path);
        var columns = ResolveSourceColumns(
            rows,
            allocateManagedColumns: false);

        var parsed = ParseRows(rows, columns);
        var existing = Load();

        var byUsername = existing
            .GroupBy(x => x.Username, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.First(),
                StringComparer.OrdinalIgnoreCase);

        var sameSource = SamePath(oldSourcePath, path);

        // Chỉ backfill trạng thái gán từ JSON nếu vẫn là chính file nguồn cũ
        // và file chưa có cột Profile hợp lệ.
        // File Excel MỚI không được kế thừa gán profile từ file cũ.
        var preserveLegacyAssignment =
            sameSource
            && columns.Assigned < 0
            && columns.SuspiciousAssigned < 0;

        var replaced = new List<TikTokAccountPoolItem>();

        foreach (var item in parsed)
        {
            if (byUsername.TryGetValue(item.Username, out var old))
            {
                var assigned = columns.Assigned >= 0
                    ? item.AssignedProfile
                    : preserveLegacyAssignment
                        ? old.AssignedProfile
                        : "";

                replaced.Add(item with
                {
                    Id = old.Id,
                    AssignedProfile = assigned
                });
            }
            else
            {
                replaced.Add(item with
                {
                    Id = Guid.NewGuid().ToString("N")
                });
            }
        }

        // Tự thêm các cột Tool sang bên phải và ghi Profile đã gán vào đó.
        // Không đè Email / Ngày tạo / cột dữ liệu khác.
        SyncAssignmentsToSource(path, replaced);

        SaveCatalog(replaced, path);

        var dataStart = columns.HeaderRow >= 0
            ? columns.HeaderRow + 1
            : 1;

        var skipped = Math.Max(
            0,
            rows.Count - replaced.Count - dataStart);

        return new TikTokAccountImportResult(
            replaced.Count,
            0,
            skipped,
            rows.Count);
    }

    public TikTokAccountImportResult ReloadCurrentExcel()
    {
        var path = CurrentSourcePath;

        if (string.IsNullOrWhiteSpace(path))
            return new TikTokAccountImportResult(0, 0, 0, 0);

        if (!File.Exists(path))
            throw new FileNotFoundException(
                "File Excel đang dùng không còn tồn tại.",
                path);

        return ImportExcel(path);
    }

    public void Assign(string accountId, string profileName)
    {
        var items = Load();

        var index = items.FindIndex(x =>
            x.Id.Equals(
                accountId,
                StringComparison.OrdinalIgnoreCase));

        if (index < 0)
            throw new InvalidOperationException(
                "Dòng tài khoản đã chọn không còn tồn tại trong kho.");

        for (var i = 0; i < items.Count; i++)
        {
            if (items[i].AssignedProfile.Equals(
                    profileName,
                    StringComparison.OrdinalIgnoreCase))
            {
                items[i] = items[i] with
                {
                    AssignedProfile = ""
                };
            }
        }

        items[index] = items[index] with
        {
            AssignedProfile = profileName
        };

        SyncAssignmentsToCurrentSource(items);
        Save(items);
    }

    public void ReleaseByProfile(string profileName)
    {
        var items = Load();
        var changed = false;

        for (var i = 0; i < items.Count; i++)
        {
            if (!items[i].AssignedProfile.Equals(
                    profileName,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            items[i] = items[i] with
            {
                AssignedProfile = ""
            };

            changed = true;
        }

        if (!changed) return;

        SyncAssignmentsToCurrentSource(items);
        Save(items);
    }

    public void ReleaseAccount(string accountId)
    {
        var items = Load();

        var index = items.FindIndex(x =>
            x.Id.Equals(
                accountId,
                StringComparison.OrdinalIgnoreCase));

        if (index < 0) return;

        items[index] = items[index] with
        {
            AssignedProfile = ""
        };

        SyncAssignmentsToCurrentSource(items);
        Save(items);
    }

    public void RenameAssignedProfile(
        string oldName,
        string newName)
    {
        var items = Load();
        var changed = false;

        for (var i = 0; i < items.Count; i++)
        {
            if (!items[i].AssignedProfile.Equals(
                    oldName,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            items[i] = items[i] with
            {
                AssignedProfile = newName
            };

            changed = true;
        }

        if (!changed) return;

        SyncAssignmentsToCurrentSource(items);
        Save(items);
    }

    public void Delete(string accountId)
    {
        var items = Load();

        var current = items.FirstOrDefault(x =>
            x.Id.Equals(
                accountId,
                StringComparison.OrdinalIgnoreCase));

        if (current is null) return;

        // Giữ nguyên file Excel nguồn.
        items.RemoveAll(x =>
            x.Id.Equals(
                accountId,
                StringComparison.OrdinalIgnoreCase));

        Save(items);
    }

    public void Upsert(TikTokAccountPoolItem item)
    {
        var items = Load();

        var index = items.FindIndex(x =>
            x.Id.Equals(
                item.Id,
                StringComparison.OrdinalIgnoreCase));

        var normalized = item with
        {
            Id = string.IsNullOrWhiteSpace(item.Id)
                ? Guid.NewGuid().ToString("N")
                : item.Id,

            Username = item.Username.Trim(),

            TotpSecret =
                TikTokAuthService.NormalizeTotpSecret(
                    item.TotpSecret),

            Note = (item.Note ?? "").Trim(),

            SourceRow = item.SourceRow <= 1
                ? NextSourceRow(items)
                : item.SourceRow
        };

        var sourcePath = CurrentSourcePath;

        if (!string.IsNullOrWhiteSpace(sourcePath))
            WriteSourceRow(sourcePath, normalized);

        if (index >= 0)
            items[index] = normalized;
        else
            items.Add(normalized);

        Save(items);
    }

    StoredCatalog LoadStoredCatalog()
    {
        if (!File.Exists(_path))
            return new StoredCatalog();

        return JsonSerializer.Deserialize<StoredCatalog>(
                   File.ReadAllText(_path))
               ?? new StoredCatalog();
    }

    static List<TikTokAccountPoolItem> ToItems(
        StoredCatalog stored)
    {
        return stored.Accounts
            .Where(x => !string.IsNullOrWhiteSpace(x.Username))
            .Select(x => new TikTokAccountPoolItem(
                string.IsNullOrWhiteSpace(x.Id)
                    ? Guid.NewGuid().ToString("N")
                    : x.Id,

                x.Username.Trim(),

                Unprotect(x.PasswordProtected),

                TikTokAuthService.NormalizeTotpSecret(
                    Unprotect(x.TotpSecretProtected)),

                (x.Note ?? "").Trim(),

                (x.AssignedProfile ?? "").Trim(),

                x.SourceRow <= 0
                    ? 1
                    : x.SourceRow))
            .ToList();
    }

    void SaveCatalog(
        IEnumerable<TikTokAccountPoolItem> items,
        string? sourcePath)
    {
        var catalog = new StoredCatalog
        {
            Version = 2,

            SourceFilePath =
                string.IsNullOrWhiteSpace(sourcePath)
                    ? ""
                    : Path.GetFullPath(sourcePath),

            Accounts = items
                .Where(x =>
                    !string.IsNullOrWhiteSpace(x.Username))
                .Select(x => new StoredItem
                {
                    Id = string.IsNullOrWhiteSpace(x.Id)
                        ? Guid.NewGuid().ToString("N")
                        : x.Id,

                    Username = x.Username.Trim(),

                    PasswordProtected =
                        Protect(x.Password ?? ""),

                    TotpSecretProtected =
                        Protect(
                            TikTokAuthService.NormalizeTotpSecret(
                                x.TotpSecret)),

                    Note = (x.Note ?? "").Trim(),

                    AssignedProfile =
                        (x.AssignedProfile ?? "").Trim(),

                    SourceRow = x.SourceRow <= 0
                        ? 1
                        : x.SourceRow
                })
                .ToList()
        };

        Directory.CreateDirectory(
            Path.GetDirectoryName(_path)!);

        AtomicWrite(
            _path,
            JsonSerializer.Serialize(
                catalog,
                JsonOptions));
    }

    static int NextSourceRow(
        List<TikTokAccountPoolItem> items)
        => items.Count == 0
            ? 2
            : Math.Max(
                2,
                items.Max(x => x.SourceRow) + 1);

    static List<List<string>> ReadSourceRows(string path)
    {
        var ext = Path
            .GetExtension(path)
            .ToLowerInvariant();

        return ext switch
        {
            ".xlsx" => ReadXlsx(path),
            ".csv" or ".txt" => ReadDelimited(path),

            _ => throw new InvalidOperationException(
                "Chỉ hỗ trợ file .xlsx, .csv hoặc .txt.")
        };
    }

    static void WriteSourceRow(
        string path,
        TikTokAccountPoolItem item)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException(
                "File Excel đang dùng không còn tồn tại.",
                path);

        var ext = Path
            .GetExtension(path)
            .ToLowerInvariant();

        if (ext == ".xlsx")
        {
            UpdateXlsxRow(
                path,
                item.SourceRow,
                item.Username,
                item.Password,
                item.TotpSecret,
                item.Note,
                item.AssignedProfile);
        }
        else if (ext is ".csv" or ".txt")
        {
            UpdateDelimitedRow(
                path,
                item.SourceRow,
                item.Username,
                item.Password,
                item.TotpSecret,
                item.Note,
                item.AssignedProfile);
        }
        else
        {
            throw new InvalidOperationException(
                "Chỉ hỗ trợ file .xlsx, .csv hoặc .txt.");
        }
    }

    static void UpdateDelimitedRow(
        string path,
        int sourceRow,
        string username,
        string password,
        string totp,
        string note,
        string assignedProfile)
    {
        var lines = File
            .ReadAllLines(path, Encoding.UTF8)
            .ToList();

        var separator = DetectSeparator(lines);

        if (lines.Count == 0)
            lines.Add("Tài khoản" + separator
                + "Mật khẩu" + separator
                + "2FA");

        var rows = lines
            .Select(line =>
                SplitDelimited(line, separator))
            .ToList();

        var columns = ResolveSourceColumns(
            rows,
            allocateManagedColumns: true);

        EnsureDelimitedManagedHeaders(
            lines,
            separator,
            rows,
            columns);

        while (lines.Count < sourceRow)
            lines.Add("");

        var cells = SplitDelimited(
            lines[sourceRow - 1],
            separator);

        EnsureCellCount(
            cells,
            MaxManagedColumn(columns) + 1);

        cells[columns.User] = username;
        cells[columns.Password] = password;
        cells[columns.Totp] = totp;

        if (columns.Note >= 0)
            cells[columns.Note] = note;

        cells[columns.Assigned] = assignedProfile;

        lines[sourceRow - 1] =
            string.Join(
                separator,
                cells.Select(x =>
                    EscapeDelimited(x, separator)));

        AtomicWrite(
            path,
            string.Join(
                Environment.NewLine,
                lines));
    }

    static string EscapeDelimited(
        string value,
        char separator)
    {
        value ??= "";

        if (!value.Contains(separator)
            && !value.Contains('"')
            && !value.Contains('\r')
            && !value.Contains('\n'))
        {
            return value;
        }

        return "\""
            + value.Replace("\"", "\"\"")
            + "\"";
    }

    static void UpdateXlsxRow(
        string path,
        int sourceRow,
        string username,
        string password,
        string totp,
        string note,
        string assignedProfile)
    {
        try
        {
            var rowsBeforeWrite = ReadXlsx(path);

            var columns = ResolveSourceColumns(
                rowsBeforeWrite,
                allocateManagedColumns: true);

            using var source = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            using var memory = new MemoryStream();
            source.CopyTo(memory);
            memory.Position = 0;

            string sheetName;
            XDocument sheetDoc;

            using (var zip = new ZipArchive(
                       memory,
                       ZipArchiveMode.Update,
                       leaveOpen: true))
            {
                var sheetEntry =
                    ResolveFirstSheet(zip)
                    ?? throw new InvalidOperationException(
                        "File Excel không có worksheet.");

                sheetName = sheetEntry.FullName;

                using (var stream = sheetEntry.Open())
                    sheetDoc = XDocument.Load(stream);

                sheetEntry.Delete();

                XNamespace ns =
                    sheetDoc.Root?.Name.Namespace
                    ?? "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

                var sheetData =
                    sheetDoc
                        .Descendants(ns + "sheetData")
                        .FirstOrDefault()
                    ?? throw new InvalidOperationException(
                        "Worksheet không có sheetData.");

                EnsureXlsxManagedHeaders(
                    sheetData,
                    ns,
                    rowsBeforeWrite,
                    columns);

                var row = GetOrCreateRow(
                    sheetData,
                    ns,
                    sourceRow);

                SetInlineCell(
                    row,
                    ns,
                    ColumnName(columns.User)
                        + sourceRow,
                    username);

                SetInlineCell(
                    row,
                    ns,
                    ColumnName(columns.Password)
                        + sourceRow,
                    password);

                SetInlineCell(
                    row,
                    ns,
                    ColumnName(columns.Totp)
                        + sourceRow,
                    totp);

                if (columns.Note >= 0)
                {
                    SetInlineCell(
                        row,
                        ns,
                        ColumnName(columns.Note)
                            + sourceRow,
                        note);
                }

                SetInlineCell(
                    row,
                    ns,
                    ColumnName(columns.Assigned)
                        + sourceRow,
                    assignedProfile);

                ReorderCells(row, ns);
                ReorderRows(sheetData, ns);

                var newEntry =
                    zip.CreateEntry(
                        sheetName,
                        CompressionLevel.Optimal);

                using var outStream =
                    newEntry.Open();

                sheetDoc.Save(outStream);
            }

            memory.Position = 0;

            var temp = path + ".tooltmp";

            using (var output = new FileStream(
                       temp,
                       FileMode.Create,
                       FileAccess.Write,
                       FileShare.None))
            {
                memory.CopyTo(output);
            }

            ReplaceFileFromTemp(
                temp,
                path);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidOperationException(
                "File Excel không hợp lệ hoặc đang được lưu dở.",
                ex);
        }
    }

    static void SetInlineCell(
        XElement row,
        XNamespace ns,
        string reference,
        string value)
    {
        var cell = row
            .Elements(ns + "c")
            .FirstOrDefault(c =>
                string.Equals(
                    c.Attribute("r")?.Value,
                    reference,
                    StringComparison.OrdinalIgnoreCase));

        if (string.IsNullOrEmpty(value))
        {
            cell?.Remove();
            return;
        }

        cell ??= new XElement(
            ns + "c",
            new XAttribute("r", reference));

        if (cell.Parent is null)
            row.Add(cell);

        cell.RemoveNodes();
        cell.SetAttributeValue(
            "t",
            "inlineStr");

        XNamespace xml = XNamespace.Xml;

        cell.Add(
            new XElement(
                ns + "is",
                new XElement(
                    ns + "t",
                    new XAttribute(
                        xml + "space",
                        "preserve"),
                    value)));
    }

    static List<TikTokAccountPoolItem> ParseRows(
        List<List<string>> rows,
        SourceColumnLayout columns)
    {
        var result =
            new List<TikTokAccountPoolItem>();

        if (rows.Count == 0
            || columns.HeaderRow < 0)
        {
            return result;
        }

        for (var rowIndex =
                 columns.HeaderRow + 1;
             rowIndex < rows.Count;
             rowIndex++)
        {
            var row = rows[rowIndex];

            var user =
                GetCell(row, columns.User)
                    .Trim();

            if (user.Length == 0)
                continue;

            var pass =
                GetCell(
                    row,
                    columns.Password)
                    .Trim();

            var totp =
                TikTokAuthService
                    .NormalizeTotpSecret(
                        GetCell(
                            row,
                            columns.Totp));

            var note =
                columns.Note >= 0
                    ? GetCell(
                            row,
                            columns.Note)
                        .Trim()
                    : "";

            var assigned =
                columns.Assigned >= 0
                    ? GetCell(
                            row,
                            columns.Assigned)
                        .Trim()
                    : "";

            result.Add(
                new TikTokAccountPoolItem(
                    "",
                    user,
                    pass,
                    totp,
                    note,
                    assigned,
                    rowIndex + 1));
        }

        return result;
    }

    static SourceColumnLayout ResolveSourceColumns(
        List<List<string>> rows,
        bool allocateManagedColumns)
    {
        var headerRow =
            rows.FindIndex(r =>
                r.Any(c =>
                    !string.IsNullOrWhiteSpace(c)));

        if (headerRow < 0)
        {
            var emptyNoteColumn = allocateManagedColumns ? 3 : -1;
            var emptyAssignedColumn = allocateManagedColumns ? 4 : -1;
            var emptyDoneColumn = allocateManagedColumns ? 5 : -1;

            return new SourceColumnLayout(
                0,
                0,
                1,
                2,
                emptyNoteColumn,
                emptyAssignedColumn,
                emptyDoneColumn,
                -1,
                -1,
                -1);
        }

        var headers = rows[headerRow];

        var user = FindHeader(
            headers,
            "tài khoản",
            "tai khoan",
            "username",
            "user",
            "account",
            "tiktok");

        if (user < 0)
            user = 0;

        var password = FindHeader(
            headers,
            "mật khẩu",
            "mat khau",
            "password",
            "pass",
            "passwd");

        if (password < 0)
            password = 1;

        var totp = FindHeader(
            headers,
            "2fa",
            "totp",
            "secret 2fa",
            "2fa secret",
            "totp secret",
            "mã 2fa",
            "ma 2fa");

        if (totp < 0)
            totp = 2;

        var noteCandidates = FindHeaders(
            headers,
            "ghi chú",
            "ghi chu",
            "note",
            "notes");

        var note = ChooseNoteColumn(
            rows,
            headerRow,
            noteCandidates,
            out var suspiciousNote);

        var assignedCandidates = FindHeaders(
            headers,
            "profile đã gán",
            "profile da gan",
            "assigned profile",
            "profile assigned");

        var assigned = ChooseAssignmentColumn(
            rows,
            headerRow,
            assignedCandidates,
            out var suspiciousAssigned);

        var doneCandidates = FindHeaders(
            headers,
            "tên ảnh done",
            "ten anh done",
            "tên/ảnh done",
            "ten/anh done",
            "identity done",
            "name avatar done");

        var identityDone =
            ChooseIdentityDoneColumn(
                rows,
                headerRow,
                doneCandidates,
                out var suspiciousDone);

        if (allocateManagedColumns)
        {
            var lastUsed =
                Math.Max(
                    2,
                    LastUsedColumn(rows));

            if (note < 0)
                note = ++lastUsed;
            else
                lastUsed =
                    Math.Max(lastUsed, note);

            if (assigned < 0)
                assigned = ++lastUsed;
            else
                lastUsed =
                    Math.Max(lastUsed, assigned);

            if (identityDone < 0
                || identityDone == assigned
                || identityDone == note)
            {
                identityDone = ++lastUsed;
            }
        }

        return new SourceColumnLayout(
            headerRow,
            user,
            password,
            totp,
            note,
            assigned,
            identityDone,
            suspiciousNote,
            suspiciousAssigned,
            suspiciousDone);
    }

    static int ChooseNoteColumn(
        List<List<string>> rows,
        int headerRow,
        IReadOnlyList<int> candidates,
        out int suspicious)
    {
        suspicious = -1;

        if (candidates.Count == 0)
            return -1;

        foreach (var col in candidates.Reverse())
        {
            if (LooksLikeEmailColumn(
                    rows,
                    headerRow,
                    col))
            {
                suspicious = col;
                continue;
            }

            if (LooksLikeDateColumn(
                    rows,
                    headerRow,
                    col))
            {
                suspicious = col;
                continue;
            }

            return col;
        }

        return -1;
    }

    static int ChooseAssignmentColumn(
        List<List<string>> rows,
        int headerRow,
        IReadOnlyList<int> candidates,
        out int suspicious)
    {
        suspicious = -1;

        if (candidates.Count == 0)
            return -1;

        // Ưu tiên cột bên phải vì patch mới luôn thêm cột quản lý về cuối.
        foreach (var col in candidates.Reverse())
        {
            if (LooksLikeDateColumn(
                    rows,
                    headerRow,
                    col))
            {
                suspicious = col;
                continue;
            }

            if (LooksLikeEmailColumn(
                    rows,
                    headerRow,
                    col))
            {
                suspicious = col;
                continue;
            }

            return col;
        }

        return -1;
    }

    static int ChooseIdentityDoneColumn(
        List<List<string>> rows,
        int headerRow,
        IReadOnlyList<int> candidates,
        out int suspicious)
    {
        suspicious = -1;

        if (candidates.Count == 0)
            return -1;

        foreach (var col in candidates.Reverse())
        {
            if (LooksLikeIdentityStatusColumn(
                    rows,
                    headerRow,
                    col))
            {
                return col;
            }

            suspicious = col;
        }

        return -1;
    }

    static bool LooksLikeEmailColumn(
        List<List<string>> rows,
        int headerRow,
        int col)
    {
        var values = DataValues(
            rows,
            headerRow,
            col);

        if (values.Count == 0)
            return false;

        var emailLike =
            values.Count(v =>
                v.Contains('@')
                && v.IndexOf('@') > 0
                && v.LastIndexOf('.') >
                   v.IndexOf('@') + 1);

        return emailLike >=
            Math.Max(
                1,
                (int)Math.Ceiling(
                    values.Count * 0.70));
    }

    static bool LooksLikeDateColumn(
        List<List<string>> rows,
        int headerRow,
        int col)
    {
        var values = DataValues(
            rows,
            headerRow,
            col);

        if (values.Count == 0)
            return false;

        var dateLike =
            values.Count(v =>
                DateTime.TryParseExact(
                    v,
                    "yyyyMMdd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out _)
                || DateTime.TryParseExact(
                    v,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out _)
                || DateTime.TryParseExact(
                    v,
                    "yyyy/MM/dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out _));

        return dateLike >=
            Math.Max(
                1,
                (int)Math.Ceiling(
                    values.Count * 0.70));
    }

    static bool LooksLikeIdentityStatusColumn(
        List<List<string>> rows,
        int headerRow,
        int col)
    {
        var values = DataValues(
            rows,
            headerRow,
            col);

        if (values.Count == 0)
            return true;

        return values.All(IsDoneValue);
    }

    static List<string> DataValues(
        List<List<string>> rows,
        int headerRow,
        int col)
    {
        return rows
            .Skip(headerRow + 1)
            .Select(r =>
                GetCell(r, col).Trim())
            .Where(v => v.Length > 0)
            .Take(50)
            .ToList();
    }

    static bool IsDoneValue(string? value)
    {
        value = (value ?? "").Trim();

        return value.Equals(
                   "DONE",
                   StringComparison.OrdinalIgnoreCase)
            || value.Equals(
                "YES",
                StringComparison.OrdinalIgnoreCase)
            || value.Equals(
                "TRUE",
                StringComparison.OrdinalIgnoreCase)
            || value == "1";
    }

    static int FindHeader(
        List<string> headers,
        params string[] names)
    {
        var found =
            FindHeaders(headers, names);

        return found.Count == 0
            ? -1
            : found[0];
    }

    static List<int> FindHeaders(
        List<string> headers,
        params string[] names)
    {
        var targets = names
            .Select(NormalizeHeader)
            .Where(x => x.Length > 0)
            .ToHashSet(
                StringComparer.OrdinalIgnoreCase);

        var result = new List<int>();

        for (var i = 0; i < headers.Count; i++)
        {
            var normalized =
                NormalizeHeader(headers[i]);

            if (targets.Contains(normalized))
                result.Add(i);
        }

        return result;
    }

    static string NormalizeHeader(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        var normalized =
            value
                .Trim()
                .ToLowerInvariant()
                .Normalize(
                    NormalizationForm.FormD);

        var sb = new StringBuilder();

        foreach (var ch in normalized)
        {
            if (CharUnicodeInfo
                    .GetUnicodeCategory(ch)
                == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            sb.Append(
                char.IsLetterOrDigit(ch)
                    ? ch
                    : ' ');
        }

        return Regex
            .Replace(
                sb
                    .ToString()
                    .Normalize(
                        NormalizationForm.FormC),
                @"\s+",
                " ")
            .Trim();
    }

    static int LastUsedColumn(
        List<List<string>> rows)
    {
        var last = -1;

        foreach (var row in rows)
        {
            for (var i =
                     row.Count - 1;
                 i >= 0;
                 i--)
            {
                if (string.IsNullOrWhiteSpace(row[i]))
                    continue;

                last = Math.Max(
                    last,
                    i);

                break;
            }
        }

        return last;
    }

    static string GetCell(
        List<string> row,
        int col)
    {
        return col >= 0
               && col < row.Count
            ? row[col] ?? ""
            : "";
    }

    static bool SamePath(
        string? left,
        string? right)
    {
        if (string.IsNullOrWhiteSpace(left)
            || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        try
        {
            var a =
                Path
                    .GetFullPath(left)
                    .TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar);

            var b =
                Path
                    .GetFullPath(right)
                    .TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar);

            return a.Equals(
                b,
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    void SyncAssignmentsToCurrentSource(
        IEnumerable<TikTokAccountPoolItem> items)
    {
        var path = CurrentSourcePath;

        if (string.IsNullOrWhiteSpace(path))
            return;

        if (!File.Exists(path))
            throw new FileNotFoundException(
                "File Excel đang dùng không còn tồn tại.",
                path);

        SyncAssignmentsToSource(
            path,
            items);
    }

    static void SyncAssignmentsToSource(
        string path,
        IEnumerable<TikTokAccountPoolItem> items)
    {
        var ext = Path
            .GetExtension(path)
            .ToLowerInvariant();

        var snapshot = items
            .Where(x => x.SourceRow > 1)
            .ToList();

        if (ext == ".xlsx")
        {
            UpdateXlsxAssignments(
                path,
                snapshot);
        }
        else if (ext is ".csv" or ".txt")
        {
            UpdateDelimitedAssignments(
                path,
                snapshot);
        }
        else
        {
            throw new InvalidOperationException(
                "Chỉ hỗ trợ file .xlsx, .csv hoặc .txt.");
        }
    }

    static void UpdateDelimitedAssignments(
        string path,
        IReadOnlyList<TikTokAccountPoolItem> items)
    {
        var lines =
            File
                .ReadAllLines(path, Encoding.UTF8)
                .ToList();

        var separator =
            DetectSeparator(lines);

        if (lines.Count == 0)
            lines.Add("Tài khoản" + separator
                + "Mật khẩu" + separator
                + "2FA");

        var rows =
            lines
                .Select(line =>
                    SplitDelimited(
                        line,
                        separator))
                .ToList();

        var columns =
            ResolveSourceColumns(
                rows,
                allocateManagedColumns: true);

        EnsureDelimitedManagedHeaders(
            lines,
            separator,
            rows,
            columns);

        foreach (var item in items)
        {
            while (lines.Count < item.SourceRow)
                lines.Add("");

            var cells =
                SplitDelimited(
                    lines[item.SourceRow - 1],
                    separator);

            EnsureCellCount(
                cells,
                MaxManagedColumn(columns) + 1);

            cells[columns.Assigned] =
                item.AssignedProfile ?? "";

            lines[item.SourceRow - 1] =
                string.Join(
                    separator,
                    cells.Select(x =>
                        EscapeDelimited(
                            x,
                            separator)));
        }

        AtomicWrite(
            path,
            string.Join(
                Environment.NewLine,
                lines));
    }

    static void UpdateXlsxAssignments(
        string path,
        IReadOnlyList<TikTokAccountPoolItem> items)
    {
        try
        {
            var rowsBeforeWrite =
                ReadXlsx(path);

            var columns =
                ResolveSourceColumns(
                    rowsBeforeWrite,
                    allocateManagedColumns: true);

            using var source =
                new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite
                    | FileShare.Delete);

            using var memory =
                new MemoryStream();

            source.CopyTo(memory);
            memory.Position = 0;

            string sheetName;
            XDocument sheetDoc;

            using (var zip =
                   new ZipArchive(
                       memory,
                       ZipArchiveMode.Update,
                       leaveOpen: true))
            {
                var sheetEntry =
                    ResolveFirstSheet(zip)
                    ?? throw new InvalidOperationException(
                        "File Excel không có worksheet.");

                sheetName =
                    sheetEntry.FullName;

                using (var stream =
                       sheetEntry.Open())
                {
                    sheetDoc =
                        XDocument.Load(stream);
                }

                sheetEntry.Delete();

                XNamespace ns =
                    sheetDoc.Root?.Name.Namespace
                    ?? "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

                var sheetData =
                    sheetDoc
                        .Descendants(
                            ns + "sheetData")
                        .FirstOrDefault()
                    ?? throw new InvalidOperationException(
                        "Worksheet không có sheetData.");

                EnsureXlsxManagedHeaders(
                    sheetData,
                    ns,
                    rowsBeforeWrite,
                    columns);

                foreach (var item in items)
                {
                    var row =
                        GetOrCreateRow(
                            sheetData,
                            ns,
                            item.SourceRow);

                    SetInlineCell(
                        row,
                        ns,
                        ColumnName(
                            columns.Assigned)
                        + item.SourceRow,
                        item.AssignedProfile
                        ?? "");

                    ReorderCells(
                        row,
                        ns);
                }

                ReorderRows(
                    sheetData,
                    ns);

                var newEntry =
                    zip.CreateEntry(
                        sheetName,
                        CompressionLevel.Optimal);

                using var outStream =
                    newEntry.Open();

                sheetDoc.Save(outStream);
            }

            memory.Position = 0;

            var temp =
                path + ".tooltmp";

            using (var output =
                   new FileStream(
                       temp,
                       FileMode.Create,
                       FileAccess.Write,
                       FileShare.None))
            {
                memory.CopyTo(output);
            }

            ReplaceFileFromTemp(
                temp,
                path);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidOperationException(
                "File Excel không hợp lệ hoặc đang được lưu dở.",
                ex);
        }
    }

    static void SetIdentityDoneCell(
        string path,
        int sourceRow,
        string value)
    {
        var ext =
            Path
                .GetExtension(path)
                .ToLowerInvariant();

        if (ext is ".csv" or ".txt")
        {
            var lines =
                File
                    .ReadAllLines(
                        path,
                        Encoding.UTF8)
                    .ToList();

            var separator =
                DetectSeparator(lines);

            if (lines.Count == 0)
                lines.Add(
                    "Tài khoản"
                    + separator
                    + "Mật khẩu"
                    + separator
                    + "2FA");

            var rows =
                lines
                    .Select(line =>
                        SplitDelimited(
                            line,
                            separator))
                    .ToList();

            var columns =
                ResolveSourceColumns(
                    rows,
                    allocateManagedColumns: true);

            EnsureDelimitedManagedHeaders(
                lines,
                separator,
                rows,
                columns);

            while (lines.Count < sourceRow)
                lines.Add("");

            var cells =
                SplitDelimited(
                    lines[sourceRow - 1],
                    separator);

            EnsureCellCount(
                cells,
                MaxManagedColumn(columns) + 1);

            cells[columns.IdentityDone] =
                value ?? "";

            lines[sourceRow - 1] =
                string.Join(
                    separator,
                    cells.Select(x =>
                        EscapeDelimited(
                            x,
                            separator)));

            AtomicWrite(
                path,
                string.Join(
                    Environment.NewLine,
                    lines));

            return;
        }

        if (ext != ".xlsx")
            throw new InvalidOperationException(
                "Chỉ hỗ trợ file .xlsx, .csv hoặc .txt.");

        var rowsBeforeWrite =
            ReadXlsx(path);

        var columnsXlsx =
            ResolveSourceColumns(
                rowsBeforeWrite,
                allocateManagedColumns: true);

        using var source =
            new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite
                | FileShare.Delete);

        using var memory =
            new MemoryStream();

        source.CopyTo(memory);
        memory.Position = 0;

        string sheetName;
        XDocument sheetDoc;

        using (var zip =
               new ZipArchive(
                   memory,
                   ZipArchiveMode.Update,
                   leaveOpen: true))
        {
            var sheetEntry =
                ResolveFirstSheet(zip)
                ?? throw new InvalidOperationException(
                    "File Excel không có worksheet.");

            sheetName =
                sheetEntry.FullName;

            using (var stream =
                   sheetEntry.Open())
            {
                sheetDoc =
                    XDocument.Load(stream);
            }

            sheetEntry.Delete();

            XNamespace ns =
                sheetDoc.Root?.Name.Namespace
                ?? "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

            var sheetData =
                sheetDoc
                    .Descendants(ns + "sheetData")
                    .FirstOrDefault()
                ?? throw new InvalidOperationException(
                    "Worksheet không có sheetData.");

            EnsureXlsxManagedHeaders(
                sheetData,
                ns,
                rowsBeforeWrite,
                columnsXlsx);

            var row =
                GetOrCreateRow(
                    sheetData,
                    ns,
                    sourceRow);

            SetInlineCell(
                row,
                ns,
                ColumnName(
                    columnsXlsx.IdentityDone)
                + sourceRow,
                value ?? "");

            ReorderCells(row, ns);
            ReorderRows(sheetData, ns);

            var newEntry =
                zip.CreateEntry(
                    sheetName,
                    CompressionLevel.Optimal);

            using var outStream =
                newEntry.Open();

            sheetDoc.Save(outStream);
        }

        memory.Position = 0;

        var temp =
            path + ".tooltmp";

        using (var output =
               new FileStream(
                   temp,
                   FileMode.Create,
                   FileAccess.Write,
                   FileShare.None))
        {
            memory.CopyTo(output);
        }

        ReplaceFileFromTemp(
            temp,
            path);
    }

    static void EnsureDelimitedManagedHeaders(
        List<string> lines,
        char separator,
        List<List<string>> rows,
        SourceColumnLayout columns)
    {
        while (lines.Count <= columns.HeaderRow)
            lines.Add("");

        var header =
            SplitDelimited(
                lines[columns.HeaderRow],
                separator);

        EnsureCellCount(
            header,
            MaxManagedColumn(columns) + 1);

        RestoreSuspiciousHeaders(
            header,
            rows,
            columns);

        if (columns.User >= 0
            && string.IsNullOrWhiteSpace(
                header[columns.User]))
        {
            header[columns.User] =
                "Tài khoản";
        }

        if (columns.Password >= 0
            && string.IsNullOrWhiteSpace(
                header[columns.Password]))
        {
            header[columns.Password] =
                "Mật khẩu";
        }

        if (columns.Totp >= 0
            && string.IsNullOrWhiteSpace(
                header[columns.Totp]))
        {
            header[columns.Totp] =
                "2FA";
        }

        header[columns.Note] =
            "Ghi chú";

        header[columns.Assigned] =
            "Profile đã gán";

        header[columns.IdentityDone] =
            "Tên/ảnh DONE";

        lines[columns.HeaderRow] =
            string.Join(
                separator,
                header.Select(x =>
                    EscapeDelimited(
                        x,
                        separator)));
    }

    static void EnsureXlsxManagedHeaders(
        XElement sheetData,
        XNamespace ns,
        List<List<string>> rows,
        SourceColumnLayout columns)
    {
        var headerNumber =
            Math.Max(
                1,
                columns.HeaderRow + 1);

        var headerRow =
            GetOrCreateRow(
                sheetData,
                ns,
                headerNumber);

        RestoreSuspiciousXlsxHeaders(
            headerRow,
            ns,
            rows,
            columns,
            headerNumber);

        SetInlineCell(
            headerRow,
            ns,
            ColumnName(columns.Note)
            + headerNumber,
            "Ghi chú");

        SetInlineCell(
            headerRow,
            ns,
            ColumnName(columns.Assigned)
            + headerNumber,
            "Profile đã gán");

        SetInlineCell(
            headerRow,
            ns,
            ColumnName(columns.IdentityDone)
            + headerNumber,
            "Tên/ảnh DONE");

        ReorderCells(
            headerRow,
            ns);
    }

    static void RestoreSuspiciousHeaders(
        List<string> header,
        List<List<string>> rows,
        SourceColumnLayout columns)
    {
        if (columns.SuspiciousNote >= 0)
        {
            EnsureCellCount(
                header,
                columns.SuspiciousNote + 1);

            header[columns.SuspiciousNote] =
                GuessLegacyHeader(
                    rows,
                    columns.HeaderRow,
                    columns.SuspiciousNote);
        }

        if (columns.SuspiciousAssigned >= 0)
        {
            EnsureCellCount(
                header,
                columns.SuspiciousAssigned + 1);

            header[columns.SuspiciousAssigned] =
                GuessLegacyHeader(
                    rows,
                    columns.HeaderRow,
                    columns.SuspiciousAssigned);
        }

        if (columns.SuspiciousIdentity >= 0)
        {
            EnsureCellCount(
                header,
                columns.SuspiciousIdentity + 1);

            header[columns.SuspiciousIdentity] =
                GuessLegacyHeader(
                    rows,
                    columns.HeaderRow,
                    columns.SuspiciousIdentity);
        }
    }

    static void RestoreSuspiciousXlsxHeaders(
        XElement headerRow,
        XNamespace ns,
        List<List<string>> rows,
        SourceColumnLayout columns,
        int headerNumber)
    {
        foreach (var col in new[]
                 {
                     columns.SuspiciousNote,
                     columns.SuspiciousAssigned,
                     columns.SuspiciousIdentity
                 })
        {
            if (col < 0) continue;

            SetInlineCell(
                headerRow,
                ns,
                ColumnName(col)
                + headerNumber,
                GuessLegacyHeader(
                    rows,
                    columns.HeaderRow,
                    col));
        }
    }

    static string GuessLegacyHeader(
        List<List<string>> rows,
        int headerRow,
        int col)
    {
        if (LooksLikeEmailColumn(
                rows,
                headerRow,
                col))
        {
            return "Email";
        }

        if (LooksLikeDateColumn(
                rows,
                headerRow,
                col))
        {
            return "Ngày tạo";
        }

        return "Dữ liệu cũ";
    }

    static XElement GetOrCreateRow(
        XElement sheetData,
        XNamespace ns,
        int sourceRow)
    {
        var row =
            sheetData
                .Elements(ns + "row")
                .FirstOrDefault(r =>
                    int.TryParse(
                        r.Attribute("r")?.Value,
                        out var n)
                    && n == sourceRow);

        if (row is not null)
            return row;

        row = new XElement(
            ns + "row",
            new XAttribute(
                "r",
                sourceRow));

        sheetData.Add(row);
        return row;
    }

    static void ReorderCells(
        XElement row,
        XNamespace ns)
    {
        var orderedCells =
            row
                .Elements(ns + "c")
                .OrderBy(c =>
                    ColumnIndex(
                        c.Attribute("r")?.Value
                        ?? "A1"))
                .ToList();

        row
            .Elements(ns + "c")
            .Remove();

        row.Add(orderedCells);
    }

    static void ReorderRows(
        XElement sheetData,
        XNamespace ns)
    {
        var orderedRows =
            sheetData
                .Elements(ns + "row")
                .OrderBy(r =>
                    int.TryParse(
                        r.Attribute("r")?.Value,
                        out var n)
                        ? n
                        : int.MaxValue)
                .ToList();

        sheetData
            .Elements(ns + "row")
            .Remove();

        sheetData.Add(orderedRows);
    }

    static void EnsureCellCount(
        List<string> cells,
        int count)
    {
        while (cells.Count < count)
            cells.Add("");
    }

    static int MaxManagedColumn(
        SourceColumnLayout columns)
    {
        return new[]
        {
            columns.User,
            columns.Password,
            columns.Totp,
            columns.Note,
            columns.Assigned,
            columns.IdentityDone
        }.Max();
    }

    static char DetectSeparator(
        IReadOnlyList<string> lines)
    {
        var best =
            lines
                .Take(5)
                .SelectMany(x =>
                    new[]
                    {
                        ',',
                        ';',
                        '\t'
                    }.Select(c =>
                        (
                            c,
                            count:
                            x.Count(ch =>
                                ch == c)
                        )))
                .GroupBy(x => x.c)
                .Select(g =>
                    (
                        c: g.Key,
                        score:
                        g.Sum(x =>
                            x.count)
                    ))
                .OrderByDescending(x =>
                    x.score)
                .FirstOrDefault();

        return best.c == '\0'
            ? ','
            : best.c;
    }

    static List<List<string>> ReadDelimited(
        string path)
    {
        var lines =
            File.ReadAllLines(
                path,
                Encoding.UTF8);

        var separator =
            DetectSeparator(lines);

        return lines
            .Select(line =>
                SplitDelimited(
                    line,
                    separator))
            .ToList();
    }

    static List<string> SplitDelimited(
        string line,
        char separator)
    {
        var cells =
            new List<string>();

        var sb =
            new StringBuilder();

        var quoted = false;

        for (var i = 0;
             i < line.Length;
             i++)
        {
            var ch =
                line[i];

            if (ch == '"')
            {
                if (quoted
                    && i + 1 < line.Length
                    && line[i + 1] == '"')
                {
                    sb.Append('"');
                    i++;
                }
                else
                {
                    quoted = !quoted;
                }
            }
            else if (ch == separator
                     && !quoted)
            {
                cells.Add(
                    sb.ToString());

                sb.Clear();
            }
            else
            {
                sb.Append(ch);
            }
        }

        cells.Add(
            sb.ToString());

        return cells;
    }

    static List<List<string>> ReadXlsx(
        string path)
    {
        using var source =
            new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite
                | FileShare.Delete);

        using var memory =
            new MemoryStream();

        source.CopyTo(memory);
        memory.Position = 0;

        using var zip =
            new ZipArchive(
                memory,
                ZipArchiveMode.Read,
                leaveOpen: false);

        var sharedStrings =
            ReadSharedStrings(zip);

        var sheetEntry =
            ResolveFirstSheet(zip)
            ?? throw new InvalidOperationException(
                "File Excel không có worksheet.");

        using var stream =
            sheetEntry.Open();

        var doc =
            XDocument.Load(stream);

        XNamespace ns =
            doc.Root?.Name.Namespace
            ?? "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

        var rows =
            new List<List<string>>();

        foreach (var row in
                 doc.Descendants(ns + "row"))
        {
            var excelRow =
                int.TryParse(
                    row.Attribute("r")?.Value,
                    out var parsedRow)
                && parsedRow > 0
                    ? parsedRow
                    : rows.Count + 1;

            while (rows.Count
                   < excelRow - 1)
            {
                rows.Add(
                    new List<string>());
            }

            var values =
                new List<string>();

            foreach (var cell in
                     row.Elements(ns + "c"))
            {
                var reference =
                    cell.Attribute("r")?.Value
                    ?? "A1";

                var col =
                    ColumnIndex(reference);

                if (col < 0)
                    continue;

                EnsureCellCount(
                    values,
                    col + 1);

                var type =
                    cell.Attribute("t")?.Value
                    ?? "";

                var raw =
                    cell.Element(ns + "v")?.Value
                    ?? "";

                string value;

                if (type == "s"
                    && int.TryParse(
                        raw,
                        out var sharedIndex)
                    && sharedIndex >= 0
                    && sharedIndex
                    < sharedStrings.Count)
                {
                    value =
                        sharedStrings[sharedIndex];
                }
                else if (type == "inlineStr")
                {
                    value =
                        string.Concat(
                            cell
                                .Descendants(
                                    ns + "t")
                                .Select(x =>
                                    x.Value));
                }
                else
                {
                    value = raw;
                }

                values[col] =
                    value;
            }

            if (rows.Count
                == excelRow - 1)
            {
                rows.Add(values);
            }
            else
            {
                rows[excelRow - 1] =
                    values;
            }
        }

        return rows;
    }

    static List<string> ReadSharedStrings(
        ZipArchive zip)
    {
        var entry =
            zip.GetEntry(
                "xl/sharedStrings.xml");

        if (entry is null)
            return new List<string>();

        using var stream =
            entry.Open();

        var doc =
            XDocument.Load(stream);

        XNamespace ns =
            doc.Root?.Name.Namespace
            ?? "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

        return doc
            .Descendants(ns + "si")
            .Select(si =>
                string.Concat(
                    si
                        .Descendants(
                            ns + "t")
                        .Select(t =>
                            t.Value)))
            .ToList();
    }

    static ZipArchiveEntry? ResolveFirstSheet(
        ZipArchive zip)
    {
        var direct =
            zip.GetEntry(
                "xl/worksheets/sheet1.xml");

        if (direct is not null)
            return direct;

        return zip.Entries
            .Where(e =>
                e.FullName.StartsWith(
                    "xl/worksheets/sheet",
                    StringComparison.OrdinalIgnoreCase)
                && e.FullName.EndsWith(
                    ".xml",
                    StringComparison.OrdinalIgnoreCase))
            .OrderBy(
                e => e.FullName,
                StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    static int ColumnIndex(
        string reference)
    {
        var index = 0;

        foreach (var ch in reference)
        {
            if (!char.IsLetter(ch))
                break;

            index =
                index * 26
                + (char.ToUpperInvariant(ch)
                   - 'A'
                   + 1);
        }

        return Math.Max(
            0,
            index - 1);
    }

    static string ColumnName(
        int zeroBasedIndex)
    {
        if (zeroBasedIndex < 0)
            throw new ArgumentOutOfRangeException(
                nameof(zeroBasedIndex));

        var value =
            zeroBasedIndex + 1;

        var sb =
            new StringBuilder();

        while (value > 0)
        {
            value--;

            sb.Insert(
                0,
                (char)(
                    'A'
                    + value % 26));

            value /= 26;
        }

        return sb.ToString();
    }


    public sealed record TikTokAccountAutoState(
        string Status,
        string Step,
        string Note)
    {
        public bool IsEmpty =>
            string.IsNullOrWhiteSpace(Status)
            && string.IsNullOrWhiteSpace(Step)
            && string.IsNullOrWhiteSpace(Note);

        public bool IsReady =>
            string.Equals(
                (Status ?? "").Trim(),
                "READY",
                StringComparison.OrdinalIgnoreCase);

        public bool IsPausedOrError
        {
            get
            {
                var s = (Status ?? "").Trim();

                if (s.Length == 0)
                    return false;

                return s.StartsWith(
                           "PAUSED",
                           StringComparison.OrdinalIgnoreCase)
                    || s.StartsWith(
                        "ERROR",
                        StringComparison.OrdinalIgnoreCase)
                    || s.StartsWith(
                        "FAILED",
                        StringComparison.OrdinalIgnoreCase)
                    || s.StartsWith(
                        "STOPPED",
                        StringComparison.OrdinalIgnoreCase)
                    || s.Contains(
                        "CAPTCHA",
                        StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    sealed record AutoColumnLayout(
        int HeaderRow,
        int Status,
        int Step,
        int Note);

    public void EnsureAutoColumns()
    {
        var path = CurrentSourcePath;

        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException(
                "Kho tài khoản chưa chọn file Excel nguồn.");

        if (!File.Exists(path))
            throw new FileNotFoundException(
                "File Excel đang dùng không còn tồn tại.",
                path);

        var existingRows = ReadSourceRows(path);
        var existingColumns = ResolveAutoColumns(
            existingRows,
            allocateManagedColumns: false);

        if (existingColumns.HeaderRow >= 0
            && existingColumns.Status >= 0
            && existingColumns.Step >= 0
            && existingColumns.Note >= 0)
        {
            return;
        }

        var ext = Path
            .GetExtension(path)
            .ToLowerInvariant();

        if (ext == ".xlsx")
        {
            EnsureXlsxAutoColumns(path);
            return;
        }

        if (ext is ".csv" or ".txt")
        {
            EnsureDelimitedAutoColumns(path);
            return;
        }

        throw new InvalidOperationException(
            "Chỉ hỗ trợ file .xlsx, .csv hoặc .txt.");
    }

    public Dictionary<string, TikTokAccountAutoState> LoadAutoStates()
    {
        var result =
            new Dictionary<string, TikTokAccountAutoState>(
                StringComparer.OrdinalIgnoreCase);

        var path = CurrentSourcePath;

        if (string.IsNullOrWhiteSpace(path)
            || !File.Exists(path))
        {
            return result;
        }

        var rows = ReadSourceRows(path);
        var columns = ResolveAutoColumns(
            rows,
            allocateManagedColumns: false);

        if (columns.HeaderRow < 0
            || columns.Status < 0
            || columns.Step < 0
            || columns.Note < 0)
        {
            return result;
        }

        foreach (var account in Load())
        {
            if (string.IsNullOrWhiteSpace(account.Id)
                || account.SourceRow <= 0)
            {
                continue;
            }

            var rowIndex =
                account.SourceRow - 1;

            if (rowIndex < 0
                || rowIndex >= rows.Count)
            {
                continue;
            }

            var row = rows[rowIndex];

            result[account.Id] =
                new TikTokAccountAutoState(
                    GetCell(
                        row,
                        columns.Status).Trim(),
                    GetCell(
                        row,
                        columns.Step).Trim(),
                    GetCell(
                        row,
                        columns.Note).Trim());
        }

        return result;
    }

    public void SetAutoState(
        string accountId,
        string status,
        string step,
        string note)
    {
        accountId =
            (accountId ?? "").Trim();

        if (accountId.Length == 0)
            throw new InvalidOperationException(
                "AccountId trống; không thể ghi trạng thái +auto.");

        var account =
            Load().FirstOrDefault(x =>
                x.Id.Equals(
                    accountId,
                    StringComparison.OrdinalIgnoreCase));

        if (account is null)
            throw new InvalidOperationException(
                "Không tìm thấy tài khoản trong Kho để ghi trạng thái +auto.");

        if (account.SourceRow <= 0)
            throw new InvalidOperationException(
                "Tài khoản không có dòng Excel hợp lệ để ghi trạng thái +auto.");

        var path = CurrentSourcePath;

        if (string.IsNullOrWhiteSpace(path)
            || !File.Exists(path))
        {
            throw new InvalidOperationException(
                "Kho tài khoản chưa có file Excel nguồn để ghi trạng thái +auto.");
        }

        var ext = Path
            .GetExtension(path)
            .ToLowerInvariant();

        if (ext == ".xlsx")
        {
            SetXlsxAutoState(
                path,
                account.SourceRow,
                status ?? "",
                step ?? "",
                note ?? "");

            return;
        }

        if (ext is ".csv" or ".txt")
        {
            SetDelimitedAutoState(
                path,
                account.SourceRow,
                status ?? "",
                step ?? "",
                note ?? "");

            return;
        }

        throw new InvalidOperationException(
            "Chỉ hỗ trợ file .xlsx, .csv hoặc .txt.");
    }

    static AutoColumnLayout ResolveAutoColumns(
        List<List<string>> rows,
        bool allocateManagedColumns)
    {
        var headerRow =
            rows.FindIndex(r =>
                r.Any(c =>
                    !string.IsNullOrWhiteSpace(c)));

        if (headerRow < 0)
        {
            if (!allocateManagedColumns)
                return new AutoColumnLayout(
                    -1,
                    -1,
                    -1,
                    -1);

            headerRow = 0;
        }

        var headers =
            headerRow < rows.Count
                ? rows[headerRow]
                : new List<string>();

        var status = FindHeader(
            headers,
            "+auto trạng thái",
            "auto trạng thái",
            "auto status",
            "+auto",
            "auto");

        var step = FindHeader(
            headers,
            "+auto bước",
            "auto bước",
            "auto step");

        var note = FindHeader(
            headers,
            "+auto ghi chú",
            "auto ghi chú",
            "auto note");

        if (allocateManagedColumns)
        {
            var lastUsed =
                Math.Max(
                    -1,
                    LastUsedColumn(rows));

            if (status < 0)
                status = ++lastUsed;
            else
                lastUsed = Math.Max(
                    lastUsed,
                    status);

            if (step < 0
                || step == status)
            {
                step = ++lastUsed;
            }
            else
            {
                lastUsed = Math.Max(
                    lastUsed,
                    step);
            }

            if (note < 0
                || note == status
                || note == step)
            {
                note = ++lastUsed;
            }
        }

        return new AutoColumnLayout(
            headerRow,
            status,
            step,
            note);
    }

    static void EnsureDelimitedAutoColumns(
        string path)
    {
        var lines =
            File.ReadAllLines(
                    path,
                    Encoding.UTF8)
                .ToList();

        var separator =
            DetectSeparator(lines);

        if (lines.Count == 0)
            lines.Add("");

        var rows =
            lines
                .Select(line =>
                    SplitDelimited(
                        line,
                        separator))
                .ToList();

        var columns =
            ResolveAutoColumns(
                rows,
                allocateManagedColumns: true);

        while (lines.Count <= columns.HeaderRow)
            lines.Add("");

        var header =
            SplitDelimited(
                lines[columns.HeaderRow],
                separator);

        EnsureCellCount(
            header,
            Math.Max(
                columns.Status,
                Math.Max(
                    columns.Step,
                    columns.Note)) + 1);

        header[columns.Status] =
            "+auto trạng thái";

        header[columns.Step] =
            "+auto bước";

        header[columns.Note] =
            "+auto ghi chú";

        lines[columns.HeaderRow] =
            string.Join(
                separator,
                header.Select(x =>
                    EscapeDelimited(
                        x,
                        separator)));

        AtomicWrite(
            path,
            string.Join(
                Environment.NewLine,
                lines));
    }

    static void EnsureXlsxAutoColumns(
        string path)
    {
        var rowsBeforeWrite =
            ReadXlsx(path);

        var columns =
            ResolveAutoColumns(
                rowsBeforeWrite,
                allocateManagedColumns: true);

        using var source =
            new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite
                | FileShare.Delete);

        using var memory =
            new MemoryStream();

        source.CopyTo(memory);
        memory.Position = 0;

        string sheetName;
        XDocument sheetDoc;

        using (var zip =
               new ZipArchive(
                   memory,
                   ZipArchiveMode.Update,
                   leaveOpen: true))
        {
            var sheetEntry =
                ResolveFirstSheet(zip)
                ?? throw new InvalidOperationException(
                    "File Excel không có worksheet.");

            sheetName =
                sheetEntry.FullName;

            using (var stream =
                   sheetEntry.Open())
            {
                sheetDoc =
                    XDocument.Load(stream);
            }

            sheetEntry.Delete();

            XNamespace ns =
                sheetDoc.Root?.Name.Namespace
                ?? "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

            var sheetData =
                sheetDoc
                    .Descendants(
                        ns + "sheetData")
                    .FirstOrDefault()
                ?? throw new InvalidOperationException(
                    "Worksheet không có sheetData.");

            var headerNumber =
                Math.Max(
                    1,
                    columns.HeaderRow + 1);

            var headerRow =
                GetOrCreateRow(
                    sheetData,
                    ns,
                    headerNumber);

            SetInlineCell(
                headerRow,
                ns,
                ColumnName(
                    columns.Status)
                + headerNumber,
                "+auto trạng thái");

            SetInlineCell(
                headerRow,
                ns,
                ColumnName(
                    columns.Step)
                + headerNumber,
                "+auto bước");

            SetInlineCell(
                headerRow,
                ns,
                ColumnName(
                    columns.Note)
                + headerNumber,
                "+auto ghi chú");

            ReorderCells(
                headerRow,
                ns);

            ReorderRows(
                sheetData,
                ns);

            var newEntry =
                zip.CreateEntry(
                    sheetName,
                    CompressionLevel.Optimal);

            using var outStream =
                newEntry.Open();

            sheetDoc.Save(outStream);
        }

        memory.Position = 0;

        var temp =
            path + ".tooltmp";

        using (var output =
               new FileStream(
                   temp,
                   FileMode.Create,
                   FileAccess.Write,
                   FileShare.None))
        {
            memory.CopyTo(output);
        }

        ReplaceFileFromTemp(
            temp,
            path);
    }

    static void SetDelimitedAutoState(
        string path,
        int sourceRow,
        string status,
        string step,
        string note)
    {
        var lines =
            File.ReadAllLines(
                    path,
                    Encoding.UTF8)
                .ToList();

        var separator =
            DetectSeparator(lines);

        if (lines.Count == 0)
            lines.Add("");

        var rows =
            lines
                .Select(line =>
                    SplitDelimited(
                        line,
                        separator))
                .ToList();

        var columns =
            ResolveAutoColumns(
                rows,
                allocateManagedColumns: true);

        while (lines.Count <= columns.HeaderRow)
            lines.Add("");

        var header =
            SplitDelimited(
                lines[columns.HeaderRow],
                separator);

        EnsureCellCount(
            header,
            Math.Max(
                columns.Status,
                Math.Max(
                    columns.Step,
                    columns.Note)) + 1);

        header[columns.Status] =
            "+auto trạng thái";

        header[columns.Step] =
            "+auto bước";

        header[columns.Note] =
            "+auto ghi chú";

        lines[columns.HeaderRow] =
            string.Join(
                separator,
                header.Select(x =>
                    EscapeDelimited(
                        x,
                        separator)));

        while (lines.Count < sourceRow)
            lines.Add("");

        var cells =
            SplitDelimited(
                lines[sourceRow - 1],
                separator);

        EnsureCellCount(
            cells,
            Math.Max(
                columns.Status,
                Math.Max(
                    columns.Step,
                    columns.Note)) + 1);

        cells[columns.Status] =
            status;

        cells[columns.Step] =
            step;

        cells[columns.Note] =
            note;

        lines[sourceRow - 1] =
            string.Join(
                separator,
                cells.Select(x =>
                    EscapeDelimited(
                        x,
                        separator)));

        AtomicWrite(
            path,
            string.Join(
                Environment.NewLine,
                lines));
    }

    static void SetXlsxAutoState(
        string path,
        int sourceRow,
        string status,
        string step,
        string note)
    {
        var rowsBeforeWrite =
            ReadXlsx(path);

        var columns =
            ResolveAutoColumns(
                rowsBeforeWrite,
                allocateManagedColumns: true);

        using var source =
            new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite
                | FileShare.Delete);

        using var memory =
            new MemoryStream();

        source.CopyTo(memory);
        memory.Position = 0;

        string sheetName;
        XDocument sheetDoc;

        using (var zip =
               new ZipArchive(
                   memory,
                   ZipArchiveMode.Update,
                   leaveOpen: true))
        {
            var sheetEntry =
                ResolveFirstSheet(zip)
                ?? throw new InvalidOperationException(
                    "File Excel không có worksheet.");

            sheetName =
                sheetEntry.FullName;

            using (var stream =
                   sheetEntry.Open())
            {
                sheetDoc =
                    XDocument.Load(stream);
            }

            sheetEntry.Delete();

            XNamespace ns =
                sheetDoc.Root?.Name.Namespace
                ?? "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

            var sheetData =
                sheetDoc
                    .Descendants(
                        ns + "sheetData")
                    .FirstOrDefault()
                ?? throw new InvalidOperationException(
                    "Worksheet không có sheetData.");

            var headerNumber =
                Math.Max(
                    1,
                    columns.HeaderRow + 1);

            var headerRow =
                GetOrCreateRow(
                    sheetData,
                    ns,
                    headerNumber);

            SetInlineCell(
                headerRow,
                ns,
                ColumnName(
                    columns.Status)
                + headerNumber,
                "+auto trạng thái");

            SetInlineCell(
                headerRow,
                ns,
                ColumnName(
                    columns.Step)
                + headerNumber,
                "+auto bước");

            SetInlineCell(
                headerRow,
                ns,
                ColumnName(
                    columns.Note)
                + headerNumber,
                "+auto ghi chú");

            ReorderCells(
                headerRow,
                ns);

            var row =
                GetOrCreateRow(
                    sheetData,
                    ns,
                    sourceRow);

            SetInlineCell(
                row,
                ns,
                ColumnName(
                    columns.Status)
                + sourceRow,
                status);

            SetInlineCell(
                row,
                ns,
                ColumnName(
                    columns.Step)
                + sourceRow,
                step);

            SetInlineCell(
                row,
                ns,
                ColumnName(
                    columns.Note)
                + sourceRow,
                note);

            ReorderCells(
                row,
                ns);

            ReorderRows(
                sheetData,
                ns);

            var newEntry =
                zip.CreateEntry(
                    sheetName,
                    CompressionLevel.Optimal);

            using var outStream =
                newEntry.Open();

            sheetDoc.Save(outStream);
        }

        memory.Position = 0;

        var temp =
            path + ".tooltmp";

        using (var output =
               new FileStream(
                   temp,
                   FileMode.Create,
                   FileAccess.Write,
                   FileShare.None))
        {
            memory.CopyTo(output);
        }

        ReplaceFileFromTemp(
            temp,
            path);
    }

    static string Protect(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "";

        var raw =
            Encoding.UTF8.GetBytes(value);

        return Convert.ToBase64String(
            ProtectedData.Protect(
                raw,
                Entropy,
                DataProtectionScope.CurrentUser));
    }

    static string Unprotect(
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        var raw =
            Convert.FromBase64String(value);

        return Encoding.UTF8.GetString(
            ProtectedData.Unprotect(
                raw,
                Entropy,
                DataProtectionScope.CurrentUser));
    }

    static void AtomicWrite(
        string path,
        string content)
    {
        var temp =
            path + ".tmp";

        File.WriteAllText(
            temp,
            content,
            new UTF8Encoding(false));

        ReplaceFileFromTemp(
            temp,
            path);
    }

    static void ReplaceFileFromTemp(
        string temp,
        string path)
    {
        try
        {
            File.Move(
                temp,
                path,
                true);

            return;
        }
        catch (Exception ex)
            when (ex is IOException
                  or UnauthorizedAccessException)
        {
            try
            {
                using var input =
                    new FileStream(
                        temp,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read);

                using var output =
                    new FileStream(
                        path,
                        FileMode.Create,
                        FileAccess.Write,
                        FileShare.ReadWrite
                        | FileShare.Delete);

                input.CopyTo(output);
                output.Flush(true);

                output.Close();
                input.Close();

                try
                {
                    File.Delete(temp);
                }
                catch
                {
                }

                return;
            }
            catch (Exception writeEx)
                when (writeEx is IOException
                      or UnauthorizedAccessException)
            {
                try
                {
                    if (File.Exists(temp))
                        File.Delete(temp);
                }
                catch
                {
                }

                throw new IOException(
                    "Không thể ghi vào file Excel. "
                    + "File đang bị Excel/chương trình khác khóa quyền ghi "
                    + "hoặc file/thư mục đang ở chế độ chỉ đọc. "
                    + "Hãy kiểm tra file không phải Read-only; "
                    + "nếu Excel đang mở file ở chế độ Protected View/Read-only "
                    + "thì bật Edit rồi thử lại.",
                    writeEx);
            }
        }
    }
}
