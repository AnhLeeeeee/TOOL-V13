using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace CommentVisibilityMonitor;

internal sealed record IdChangeAccountRow(int SourceRow, string Username, string Password, string TotpSecret, string Note,
    string NewUsername, string State, string Candidate);

// Independent XLSX store: never edits TK/MK/2FA, Check BAN notes, or Manager columns.
internal sealed class IdChangeExcelStore
{
    sealed record Layout(int HeaderRow, int User, int Password, int Totp, int Note, int NewId, int State, int Candidate);
    public IReadOnlyList<IdChangeAccountRow> Open(string path)
    {
        path = ValidatePath(path);
        var rows = ReadXlsx(path);
        var layout = ResolveLayout(rows);
        if (layout.User < 0 || layout.Password < 0 || layout.Totp < 0)
            throw new InvalidOperationException("Excel phải có 3 cột Tài khoản, Mật khẩu, 2FA.");
        // Opening an Excel file must be read-only, exactly like the existing account store.
        // Add ID columns only when the first actual checkpoint is written.
        return ReadRows(path);
    }
    public IReadOnlyList<IdChangeAccountRow> Reload(string path) => ReadRows(ValidatePath(path));

    static string ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new FileNotFoundException("Không thấy file Excel.", path);
        path = Path.GetFullPath(path);
        if (!Path.GetExtension(path).Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Chỉ hỗ trợ .xlsx.");
        return path;
    }
    static string Cell(IReadOnlyList<string> row, int col) => col >= 0 && col < row.Count ? row[col] : "";
    static string Norm(string? v) => (v ?? "").Trim().ToLowerInvariant().Replace('_',' ').Replace('-',' ');
    static int FindHeader(IReadOnlyList<string> row, params string[] matches)
    {
        for (int i = 0; i < row.Count; i++)
            if (matches.Any(x => Norm(x) == Norm(row[i]))) return i;
        return -1;
    }
    static Layout ResolveLayout(List<List<string>> rows)
    {
        var headerRow = rows.FindIndex(r => r.Any(c => !string.IsNullOrWhiteSpace(c)));
        if (headerRow < 0) throw new InvalidOperationException("File Excel trống.");
        var h = rows[headerRow];
        var user = FindHeader(h, "tài khoản", "tai khoan", "username", "user", "account", "tiktok");
        var pass = FindHeader(h, "mật khẩu", "mat khau", "password", "pass", "passwd");
        var totp = FindHeader(h, "2fa", "totp", "secret 2fa", "2fa secret", "totp secret", "mã 2fa");
        var note = FindHeader(h, "ghi chú", "ghi chu", "note", "notes");
        // Matches the Check BAN import convention (first 3 columns when headings absent).
        if (user < 0) user = 0;
        if (pass < 0) pass = 1;
        if (totp < 0) totp = 2;
        int last = 2;
        foreach (var r in rows) for (int i=0; i<r.Count; i++) if (!string.IsNullOrWhiteSpace(r[i])) last=Math.Max(last,i);
        int next = last + 1;
        int newId = FindHeader(h, "tài khoản mới", "tai khoan moi");
        int state = FindHeader(h, "đổi id", "doi id", "trạng thái đổi id", "trang thai doi id");
        int candidate = FindHeader(h, "id dự kiến", "id du kien");
        if (newId < 0) newId = next++;
        if (state < 0) state = next++;
        if (candidate < 0) candidate = next++;
        if (new[] {user,pass,totp,newId,state,candidate}.Distinct().Count()!=6)
            throw new InvalidOperationException("Tên các cột Excel bị trùng. Hãy kiểm tra 3 cột tài khoản/mật khẩu/2FA.");
        return new Layout(headerRow,user,pass,totp,note,newId,state,candidate);
    }
    static IReadOnlyList<IdChangeAccountRow> ReadRows(string path)
    {
        var rows = ReadXlsx(path);
        var layout = ResolveLayout(rows);
        var result = new List<IdChangeAccountRow>();
        for (int r = layout.HeaderRow+1; r < rows.Count; r++)
        {
            var u = Cell(rows[r],layout.User).Trim();
            if (u.Length == 0) continue;
            result.Add(new IdChangeAccountRow(r+1,u,Cell(rows[r],layout.Password).Trim(),Cell(rows[r],layout.Totp).Trim(),
                Cell(rows[r],layout.Note).Trim(),Cell(rows[r],layout.NewId).Trim(),
                Cell(rows[r],layout.State).Trim(),Cell(rows[r],layout.Candidate).Trim()));
        }
        return result;
    }
    // Used ONLY during the first result write, in the same atomic XLSX replacement
    // as the per-account status. Opening and reloading the source remain read-only.
    static void SetIdHeaders(XElement sheetData, XNamespace ns, Layout layout)
    {
        var number = layout.HeaderRow + 1;
        var row = GetOrCreateRow(sheetData, ns, number);
        SetInlineCell(row, ns, ColumnName(layout.NewId) + number, "Tài khoản mới");
        SetInlineCell(row, ns, ColumnName(layout.State) + number, "Đổi ID");
        SetInlineCell(row, ns, ColumnName(layout.Candidate) + number, "ID dự kiến");
        ReorderCells(row, ns);
    }
    public void WritePending(string path, int sourceRow, string original, string candidate)
        => Write(path,sourceRow,original,"","need_verify",candidate, false);
    public void WriteDone(string path, int sourceRow, string original, string newUsername)
        => Write(path,sourceRow,original,newUsername,"done","", true);
    public void WriteOutcome(string path, int sourceRow, string original, string state)
        => Write(path,sourceRow,original,"",state,"", false);

    void Write(string path, int sourceRow, string original, string newId, string state, string candidate, bool confirm)
    {
        path = ValidatePath(path);
        var rows = ReadXlsx(path);
        var layout = ResolveLayout(rows);
        if (sourceRow <= layout.HeaderRow+1 || sourceRow > rows.Count)
            throw new InvalidOperationException("Dòng Excel không hợp lệ.");
        var source = rows[sourceRow-1];
        if (!string.Equals(Cell(source,layout.User).Trim(),original.Trim(),StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Excel đã thay đổi tài khoản tại dòng này. Dừng để tránh ghi nhầm.");
        var previous = Cell(source,layout.State).Trim();
        var previousId = Cell(source,layout.NewId).Trim();
        var pendingId = Cell(source,layout.Candidate).Trim();
        if (previous.Equals("done",StringComparison.OrdinalIgnoreCase) || previousId.Length > 0)
            throw new InvalidOperationException("Dòng Excel đã có ID mới/DONE, không ghi đè.");
        if (confirm && (!previous.Equals("need_verify",StringComparison.OrdinalIgnoreCase)
                         || !string.Equals(pendingId,newId,StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("ID xác minh không khớp ID dự kiến đang lưu trong Excel.");
        if (!confirm && previous.Equals("need_verify",StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Dòng đang NEED_VERIFY, không thể tự đổi thêm ID.");
        RewriteXlsx(path,(sheetData,ns)=>
        {
            SetIdHeaders(sheetData, ns, layout);
            var row=GetOrCreateRow(sheetData,ns,sourceRow);
            SetInlineCell(row,ns,ColumnName(layout.NewId)+sourceRow,newId);
            SetInlineCell(row,ns,ColumnName(layout.State)+sourceRow,state);
            SetInlineCell(row,ns,ColumnName(layout.Candidate)+sourceRow,candidate);
            ReorderCells(row,ns);
        });
    }

    static void RewriteXlsx(string path, Action<XElement, XNamespace> mutate)
    {
        // The original worksheet is never rewritten in-place. Build the new XLSX
        // in memory, write a sibling temporary file, then atomically replace it.
        // IMPORTANT: close the original FileStream BEFORE trying to replace it.
        string? temp = null;
        try
        {
            using var memory = new MemoryStream();
            using (var source = new FileStream(
                path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            {
                source.CopyTo(memory);
            }
            memory.Position = 0;

            using (var zip = new ZipArchive(memory, ZipArchiveMode.Update, leaveOpen: true))
            {
                var sheetEntry = ResolveFirstSheet(zip)
                    ?? throw new InvalidOperationException("File Excel không có worksheet.");
                var sheetName = sheetEntry.FullName;
                XDocument sheetDoc;
                using (var stream = sheetEntry.Open())
                    sheetDoc = XDocument.Load(stream);
                sheetEntry.Delete();

                XNamespace ns = sheetDoc.Root?.Name.Namespace
                    ?? "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
                var sheetData = sheetDoc.Descendants(ns + "sheetData").FirstOrDefault()
                    ?? throw new InvalidOperationException("Worksheet không có sheetData.");

                mutate(sheetData, ns);
                ReorderRows(sheetData, ns);

                var newEntry = zip.CreateEntry(sheetName, CompressionLevel.Optimal);
                using var outStream = newEntry.Open();
                sheetDoc.Save(outStream);
            }

            // Back up once, immediately before the first write (not on Open).
            // Never overwrite a pre-existing backup, including one from an earlier patch.
            var backup = path + ".before_id_change.xlsx";
            if (!File.Exists(backup))
            {
                try { File.Copy(path, backup, overwrite: false); }
                catch (IOException) when (File.Exists(backup)) { /* another run created it */ }
            }

            temp = path + ".idchange." + Guid.NewGuid().ToString("N") + ".tmp";
            memory.Position = 0;
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                memory.CopyTo(output);
                output.Flush(true);
            }

            // Validate the temporary archive before touching the original.
            using (var test = ZipFile.OpenRead(temp))
            {
                var sheet = ResolveFirstSheet(test)
                    ?? throw new InvalidDataException("File tạm thiếu worksheet.");
                using var stream = sheet.Open();
                _ = XDocument.Load(stream);
            }

            try
            {
                File.Move(temp, path, overwrite: true);
                temp = null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // DO NOT fall back to overwriting the original stream, which could
                // truncate an Excel file on failure. The original remains untouched.
                throw new IOException(
                    "Không thể cập nhật file Excel. File gốc vẫn được giữ nguyên. " +
                    "Hãy đóng Excel/công cụ đang giữ file, kiểm tra quyền ghi rồi thử lại. " +
                    "Chi tiết Windows: " + ex.Message, ex);
            }
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidOperationException("File Excel không hợp lệ hoặc đang được lưu dở.", ex);
        }
        finally
        {
            // Remove only a temporary file created by THIS write. Legacy .tmp
            // files from a failed old patch are left alone for manual inspection.
            if (!string.IsNullOrWhiteSpace(temp))
            {
                try { File.Delete(temp); } catch { }
            }
        }
    }

    static List<List<string>> ReadXlsx(string path)
    {
        using var source = new FileStream(
            path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var memory = new MemoryStream();
        source.CopyTo(memory);
        memory.Position = 0;

        using var zip = new ZipArchive(memory, ZipArchiveMode.Read, leaveOpen: false);
        var sharedStrings = ReadSharedStrings(zip);
        var sheetEntry = ResolveFirstSheet(zip)
            ?? throw new InvalidOperationException("File Excel không có worksheet.");
        using var stream = sheetEntry.Open();
        var doc = XDocument.Load(stream);
        XNamespace ns = doc.Root?.Name.Namespace
            ?? "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

        var rows = new List<List<string>>();
        foreach (var row in doc.Descendants(ns + "row"))
        {
            var excelRow = int.TryParse(row.Attribute("r")?.Value, out var parsed) && parsed > 0
                ? parsed
                : rows.Count + 1;
            while (rows.Count < excelRow - 1)
                rows.Add(new List<string>());

            var values = new List<string>();
            foreach (var cell in row.Elements(ns + "c"))
            {
                var reference = cell.Attribute("r")?.Value ?? "A1";
                var col = ColumnIndex(reference);
                if (col < 0) continue;
                EnsureCellCount(values, col + 1);

                var type = cell.Attribute("t")?.Value ?? "";
                var raw = cell.Element(ns + "v")?.Value ?? "";
                string value;
                if (type == "s" && int.TryParse(raw, out var sharedIndex)
                    && sharedIndex >= 0 && sharedIndex < sharedStrings.Count)
                {
                    value = sharedStrings[sharedIndex];
                }
                else if (type == "inlineStr")
                {
                    value = string.Concat(cell.Descendants(ns + "t").Select(x => x.Value));
                }
                else
                {
                    value = raw;
                }
                values[col] = value;
            }

            if (rows.Count == excelRow - 1) rows.Add(values);
            else rows[excelRow - 1] = values;
        }
        return rows;
    }

    static List<string> ReadSharedStrings(ZipArchive zip)
    {
        var entry = zip.GetEntry("xl/sharedStrings.xml");
        if (entry is null) return new List<string>();
        using var stream = entry.Open();
        var doc = XDocument.Load(stream);
        XNamespace ns = doc.Root?.Name.Namespace
            ?? "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        return doc.Descendants(ns + "si")
            .Select(si => string.Concat(si.Descendants(ns + "t").Select(t => t.Value)))
            .ToList();
    }

    static ZipArchiveEntry? ResolveFirstSheet(ZipArchive zip)
    {
        var direct = zip.GetEntry("xl/worksheets/sheet1.xml");
        if (direct is not null) return direct;
        return zip.Entries
            .Where(e => e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase)
                        && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    static XElement GetOrCreateRow(XElement sheetData, XNamespace ns, int rowNumber)
    {
        var row = sheetData.Elements(ns + "row")
            .FirstOrDefault(r => string.Equals(r.Attribute("r")?.Value, rowNumber.ToString(), StringComparison.Ordinal));
        if (row is not null) return row;
        row = new XElement(ns + "row", new XAttribute("r", rowNumber));
        sheetData.Add(row);
        return row;
    }

    static void SetInlineCell(XElement row, XNamespace ns, string reference, string value)
    {
        var cell = row.Elements(ns + "c")
            .FirstOrDefault(c => string.Equals(c.Attribute("r")?.Value, reference, StringComparison.OrdinalIgnoreCase));
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
        cell.Add(new XElement(ns + "is",
            new XElement(ns + "t", new XAttribute(xml + "space", "preserve"), value)));
    }

    static void ReorderCells(XElement row, XNamespace ns)
    {
        var ordered = row.Elements(ns + "c")
            .OrderBy(c => ColumnIndex(c.Attribute("r")?.Value ?? "A1"))
            .ToArray();
        foreach (var cell in ordered) cell.Remove();
        row.Add(ordered);
    }

    static void ReorderRows(XElement sheetData, XNamespace ns)
    {
        var ordered = sheetData.Elements(ns + "row")
            .OrderBy(r => int.TryParse(r.Attribute("r")?.Value, out var n) ? n : int.MaxValue)
            .ToArray();
        foreach (var row in ordered) row.Remove();
        sheetData.Add(ordered);
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

    static string ColumnName(int zeroBasedIndex)
    {
        if (zeroBasedIndex < 0) throw new ArgumentOutOfRangeException(nameof(zeroBasedIndex));
        var value = zeroBasedIndex + 1;
        var sb = new StringBuilder();
        while (value > 0)
        {
            value--;
            sb.Insert(0, (char)('A' + value % 26));
            value /= 26;
        }
        return sb.ToString();
    }

    static void EnsureCellCount(List<string> cells, int count)
    {
        while (cells.Count < count) cells.Add("");
    }

}
