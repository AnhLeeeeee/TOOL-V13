using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace CommentVisibilityMonitor;

internal sealed record BanCheckAccountRow(
    int SourceRow,
    string Username,
    string Password,
    string TotpSecret,
    string Note);

/// <summary>
/// Kho Excel tối giản dành riêng cho CHECK BAN.
/// Giữ nguyên file nguồn; chỉ đọc Tài khoản/Mật khẩu/2FA và dùng/thêm đúng một cột "Ghi chú".
/// Không tạo các cột quản lý của Manager như Profile/Tên-ảnh/VIDEO/TIMELOGIN.
/// Cách đọc/ghi XLSX bám theo TikTokAccountPoolService hiện tại để giữ cùng semantics file nguồn.
/// </summary>
internal sealed class BanCheckExcelStore
{
    sealed record Layout(int HeaderRow, int User, int Password, int Totp, int Note);

    public IReadOnlyList<BanCheckAccountRow> Open(string path)
    {
        path = ValidatePath(path);
        EnsureNoteColumn(path);
        return ReadRows(path);
    }

    public IReadOnlyList<BanCheckAccountRow> Reload(string path)
    {
        path = ValidatePath(path);
        return ReadRows(path);
    }

    public void WriteBan(string path, int sourceRow)
        => WriteCheckpoint(path, sourceRow, "ban");

    public void WriteDone(string path, int sourceRow)
        => WriteCheckpoint(path, sourceRow, "done");

    void WriteCheckpoint(string path, int sourceRow, string note)
    {
        path = ValidatePath(path);
        if (sourceRow <= 0)
            throw new ArgumentOutOfRangeException(nameof(sourceRow));

        var rows = ReadXlsx(path);
        var layout = ResolveLayout(rows, allocateNote: true);
        WriteNoteCell(path, rows, layout, sourceRow, note);
    }

    static string ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new FileNotFoundException("Không tìm thấy file Excel.", path);

        path = Path.GetFullPath(path);
        if (!Path.GetExtension(path).Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("CHECK BAN hiện dùng file Excel .xlsx.");
        return path;
    }

    static IReadOnlyList<BanCheckAccountRow> ReadRows(string path)
    {
        var rows = ReadXlsx(path);
        var layout = ResolveLayout(rows, allocateNote: false);
        var result = new List<BanCheckAccountRow>();

        if (rows.Count == 0 || layout.HeaderRow < 0)
            return result;

        for (var rowIndex = layout.HeaderRow + 1; rowIndex < rows.Count; rowIndex++)
        {
            var row = rows[rowIndex];
            var username = GetCell(row, layout.User).Trim();
            if (username.Length == 0)
                continue;

            result.Add(new BanCheckAccountRow(
                rowIndex + 1,
                username,
                GetCell(row, layout.Password).Trim(),
                GetCell(row, layout.Totp).Trim(),
                layout.Note >= 0 ? GetCell(row, layout.Note).Trim() : ""));
        }

        return result;
    }

    static void EnsureNoteColumn(string path)
    {
        var rows = ReadXlsx(path);
        var layout = ResolveLayout(rows, allocateNote: true);
        if (layout.Note < 0)
            throw new InvalidOperationException("Không xác định được cột Ghi chú.");

        var currentHeader = layout.HeaderRow >= 0 && layout.HeaderRow < rows.Count
            ? GetCell(rows[layout.HeaderRow], layout.Note).Trim()
            : "";

        if (currentHeader.Equals("Ghi chú", StringComparison.OrdinalIgnoreCase))
            return;

        WriteHeaderCell(path, rows, layout.HeaderRow + 1, layout.Note, "Ghi chú");
    }

    static Layout ResolveLayout(List<List<string>> rows, bool allocateNote)
    {
        var headerRow = rows.FindIndex(r => r.Any(c => !string.IsNullOrWhiteSpace(c)));
        if (headerRow < 0)
            headerRow = 0;

        var headers = headerRow < rows.Count ? rows[headerRow] : new List<string>();

        var user = FindHeader(headers,
            "tài khoản", "tai khoan", "username", "user", "account", "tiktok");
        if (user < 0) user = 0;

        var password = FindHeader(headers,
            "mật khẩu", "mat khau", "password", "pass", "passwd");
        if (password < 0) password = 1;

        var totp = FindHeader(headers,
            "2fa", "totp", "secret 2fa", "2fa secret", "totp secret", "mã 2fa", "ma 2fa");
        if (totp < 0) totp = 2;

        var note = FindHeader(headers, "ghi chú", "ghi chu", "note", "notes");
        if (note < 0 && allocateNote)
        {
            var maxUsed = Math.Max(2, LastUsedColumn(rows));
            note = -1;

            // Đúng yêu cầu CHECK BAN: ưu tiên một cột hoàn toàn trống từ D trở đi.
            for (var col = 3; col <= maxUsed; col++)
            {
                var completelyBlank = true;
                for (var r = 0; r < rows.Count; r++)
                {
                    if (!string.IsNullOrWhiteSpace(GetCell(rows[r], col)))
                    {
                        completelyBlank = false;
                        break;
                    }
                }
                if (completelyBlank)
                {
                    note = col;
                    break;
                }
            }

            if (note < 0)
                note = maxUsed + 1;
        }

        return new Layout(headerRow, user, password, totp, note);
    }

    static int FindHeader(IReadOnlyList<string> headers, params string[] names)
    {
        for (var i = 0; i < headers.Count; i++)
        {
            var actual = NormalizeHeader(headers[i]);
            if (actual.Length == 0) continue;
            foreach (var name in names)
            {
                if (actual.Equals(NormalizeHeader(name), StringComparison.OrdinalIgnoreCase))
                    return i;
            }
        }
        return -1;
    }

    static string NormalizeHeader(string? value)
        => (value ?? "").Trim().ToLowerInvariant().Replace('_', ' ').Replace('-', ' ');

    static int LastUsedColumn(List<List<string>> rows)
    {
        var last = -1;
        foreach (var row in rows)
        {
            for (var i = row.Count - 1; i >= 0; i--)
            {
                if (string.IsNullOrWhiteSpace(row[i])) continue;
                last = Math.Max(last, i);
                break;
            }
        }
        return last;
    }

    static string GetCell(IReadOnlyList<string> row, int index)
        => index >= 0 && index < row.Count ? row[index] ?? "" : "";

    static void WriteHeaderCell(
        string path,
        List<List<string>> rowsBeforeWrite,
        int excelRow,
        int column,
        string value)
    {
        RewriteXlsx(path, (sheetData, ns) =>
        {
            var row = GetOrCreateRow(sheetData, ns, Math.Max(1, excelRow));
            SetInlineCell(row, ns, ColumnName(column) + Math.Max(1, excelRow), value);
            ReorderCells(row, ns);
        });
    }

    static void WriteNoteCell(
        string path,
        List<List<string>> rowsBeforeWrite,
        Layout layout,
        int sourceRow,
        string value)
    {
        if (layout.Note < 0)
            throw new InvalidOperationException("File chưa có cột Ghi chú.");

        RewriteXlsx(path, (sheetData, ns) =>
        {
            var headerNumber = Math.Max(1, layout.HeaderRow + 1);
            var headerRow = GetOrCreateRow(sheetData, ns, headerNumber);
            SetInlineCell(headerRow, ns, ColumnName(layout.Note) + headerNumber, "Ghi chú");
            ReorderCells(headerRow, ns);

            var row = GetOrCreateRow(sheetData, ns, sourceRow);
            SetInlineCell(row, ns, ColumnName(layout.Note) + sourceRow, value);
            ReorderCells(row, ns);
        });
    }

    static void RewriteXlsx(
        string path,
        Action<XElement, XNamespace> mutate)
    {
        try
        {
            using var source = new FileStream(
                path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var memory = new MemoryStream();
            source.CopyTo(memory);
            memory.Position = 0;

            string sheetName;
            XDocument sheetDoc;

            using (var zip = new ZipArchive(memory, ZipArchiveMode.Update, leaveOpen: true))
            {
                var sheetEntry = ResolveFirstSheet(zip)
                    ?? throw new InvalidOperationException("File Excel không có worksheet.");
                sheetName = sheetEntry.FullName;
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

            memory.Position = 0;
            var temp = path + ".bancheck.tmp";
            using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                memory.CopyTo(output);
                output.Flush(true);
            }
            ReplaceFileFromTemp(temp, path);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidOperationException("File Excel không hợp lệ hoặc đang được lưu dở.", ex);
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

    static void ReplaceFileFromTemp(string temp, string path)
    {
        try
        {
            File.Move(temp, path, true);
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                using var input = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var output = new FileStream(
                    path, FileMode.Create, FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete);
                input.CopyTo(output);
                output.Flush(true);
            }
            catch (Exception writeEx) when (writeEx is IOException or UnauthorizedAccessException)
            {
                throw new IOException(
                    "Không thể ghi vào file Excel. Hãy đóng Protected View/Read-only hoặc kiểm tra quyền ghi rồi thử lại.",
                    writeEx);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
        }
    }
}
