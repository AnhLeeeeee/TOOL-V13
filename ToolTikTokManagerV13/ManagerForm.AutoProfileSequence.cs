using System.Text;
using System.Text.Json;
using ToolTikTokV12.Services;

namespace ToolTikTokManagerV13;

public sealed partial class ManagerForm
{
    sealed class AutoProfileSequenceDocument
    {
        public int Version { get; set; } = 1;

        public Dictionary<string, AutoProfileSequenceState> Sources { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    sealed class AutoProfileSequenceState
    {
        public string SourcePath { get; set; } = "";
        public string NextProfileName { get; set; } = "";
        public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    }

    readonly object _autoProfileSequenceLock = new();

    string AutoProfileSequencePath
        => Path.Combine(
            _baseDir,
            "manager_auto_profile_sequence.json");

    string DetectNextAutoProfileNameFromCurrentExcel()
    {
        try
        {
            var accounts =
                _accountPoolService
                    .Load()
                    .OrderBy(x => x.SourceRow)
                    .ToList();

            var used =
                BuildAutoProfileUsedNameSet(accounts);

            var sourceKey =
                GetCurrentAutoProfileSequenceKey();

            // 1) Nếu file Excel này đã có cursor được ghi nhớ thì cursor có quyền ưu tiên.
            // Đây là phần giữ lại lựa chọn 01/02/... của người dùng qua lần đóng/mở Tool.
            if (sourceKey.Length > 0)
            {
                lock (_autoProfileSequenceLock)
                {
                    var document =
                        LoadAutoProfileSequenceDocumentUnsafe();

                    if (document.Sources.TryGetValue(
                            sourceKey,
                            out var state)
                        && TryParseAutoProfileName(
                            state.NextProfileName,
                            out _,
                            out _,
                            out _))
                    {
                        return FindNextAvailableAutoProfileName(
                            state.NextProfileName,
                            used);
                    }
                }
            }

            // 2) Chưa có cursor: suy dãy số từ cột "Profile đã gán" trong CHÍNH Excel.
            //
            // Không lấy MAX vì file có thể còn các số cũ do logic trước đây, ví dụ:
            // 01, 71, 72.
            // Ta lấy "gốc dãy" nhỏ nhất của kiểu tên phù hợp rồi tìm chỗ trống kế tiếp:
            // 01 -> thử 02 -> 03...
            var numericAssigned =
                accounts
                    .Where(x => x.IsAssigned)
                    .Select(x =>
                    {
                        var name =
                            (x.AssignedProfile ?? "").Trim();

                        if (!TryParseAutoProfileName(
                                name,
                                out var prefix,
                                out var number,
                                out var width))
                        {
                            return null;
                        }

                        return new AutoProfileParsedName(
                            name,
                            prefix,
                            number,
                            width,
                            x.SourceRow);
                    })
                    .Where(x => x is not null)
                    .Cast<AutoProfileParsedName>()
                    .ToList();

            if (numericAssigned.Count > 0)
            {
                // Ưu tiên dãy chỉ có số (01,02...) vì đây là kiểu người dùng đang dùng.
                // Nếu file chỉ dùng a01/a02... thì lấy nhóm prefix xuất hiện sớm nhất.
                var selectedGroup =
                    numericAssigned
                        .GroupBy(
                            x => new AutoProfileSequenceStyle(
                                x.Prefix,
                                x.Width))
                        .OrderBy(g =>
                            string.IsNullOrEmpty(g.Key.Prefix)
                                ? 0
                                : 1)
                        .ThenBy(g =>
                            g.Min(x => x.SourceRow))
                        .First();

                var seed =
                    selectedGroup
                        .OrderBy(x => x.Number)
                        .ThenBy(x => x.SourceRow)
                        .First();

                var preferred =
                    FormatAutoProfileName(
                        seed.Prefix,
                        seed.Number + 1,
                        seed.Width);

                var detected =
                    FindNextAvailableAutoProfileName(
                        preferred,
                        used);

                SaveAutoProfileSequenceNext(
                    detected,
                    "excel_detect");

                return detected;
            }

            // 3) Excel chưa có profile nào: bắt đầu dãy thân thiện 01.
            var first =
                FindNextAvailableAutoProfileName(
                    "01",
                    used);

            SaveAutoProfileSequenceNext(
                first,
                "excel_empty");

            return first;
        }
        catch (Exception ex)
        {
            _log.Warn(
                $"[AUTO_PROFILE_SEQUENCE_DETECT_WARN] error={ex.Message}");

            // Không quay lại logic "max toàn hệ thống".
            // Nếu Excel tạm thời không đọc được thì dùng 01 rồi chống trùng bằng catalog.
            try
            {
                return FindNextAvailableAutoProfileName(
                    "01",
                    BuildAutoProfileUsedNameSet(
                        Array.Empty<TikTokAccountPoolItem>()));
            }
            catch
            {
                return "01";
            }
        }
    }

    void RememberAutoProfileSequenceStart(
        string requestedStartName)
    {
        requestedStartName =
            (requestedStartName ?? "").Trim();

        if (!TryParseAutoProfileName(
                requestedStartName,
                out _,
                out _,
                out _))
        {
            return;
        }

        SaveAutoProfileSequenceNext(
            requestedStartName,
            "user_start");

        _log.Info(
            $"[AUTO_PROFILE_SEQUENCE_USER_START] excel={Path.GetFileName(_accountPoolService.CurrentSourcePath)} start={requestedStartName}");
    }

    void AdvanceAutoProfileSequenceAfterCreated(
        string createdProfileName)
    {
        createdProfileName =
            (createdProfileName ?? "").Trim();

        if (!TryParseAutoProfileName(
                createdProfileName,
                out var prefix,
                out var number,
                out var width))
        {
            return;
        }

        try
        {
            var accounts =
                _accountPoolService.Load();

            var used =
                BuildAutoProfileUsedNameSet(accounts);

            var preferred =
                FormatAutoProfileName(
                    prefix,
                    number + 1,
                    width);

            var next =
                FindNextAvailableAutoProfileName(
                    preferred,
                    used);

            SaveAutoProfileSequenceNext(
                next,
                "profile_created");

            _log.Info(
                $"[AUTO_PROFILE_SEQUENCE_ADVANCE] excel={Path.GetFileName(_accountPoolService.CurrentSourcePath)} created={createdProfileName} next={next}");
        }
        catch (Exception ex)
        {
            _log.Warn(
                $"[AUTO_PROFILE_SEQUENCE_ADVANCE_WARN] created={createdProfileName} error={ex.Message}");
        }
    }

    HashSet<string> BuildAutoProfileUsedNameSet(
        IEnumerable<TikTokAccountPoolItem> accounts)
    {
        var used =
            new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

        // Catalog toàn hệ thống chỉ dùng để chống tạo trùng profile thật.
        try
        {
            foreach (var profile in _profileService.Load().Profiles)
            {
                var name =
                    (profile.Name ?? "").Trim();

                if (name.Length > 0)
                    used.Add(name);
            }
        }
        catch { }

        // Profile đã ghi trong Excel hiện tại cũng không được cấp lại.
        foreach (var account in accounts)
        {
            var name =
                (account.AssignedProfile ?? "").Trim();

            if (name.Length > 0)
                used.Add(name);
        }

        // Những tên đang được Tự bù giữ chỗ cũng không được lấy trùng.
        foreach (var name in _autoReplacementClaimedProfiles)
        {
            if (!string.IsNullOrWhiteSpace(name))
                used.Add(name.Trim());
        }

        return used;
    }

    string FindNextAvailableAutoProfileName(
        string preferredStart,
        HashSet<string> used)
    {
        preferredStart =
            (preferredStart ?? "").Trim();

        if (!TryParseAutoProfileName(
                preferredStart,
                out var prefix,
                out var number,
                out var width))
        {
            preferredStart = "01";

            TryParseAutoProfileName(
                preferredStart,
                out prefix,
                out number,
                out width);
        }

        var current =
            Math.Max(
                0,
                number);

        for (var guard = 0; guard < 100000; guard++)
        {
            var candidate =
                FormatAutoProfileName(
                    prefix,
                    current,
                    width);

            if (!used.Contains(candidate))
                return candidate;

            current++;
        }

        throw new InvalidOperationException(
            "Không tìm được tên profile trống trong dãy của file Excel hiện tại.");
    }

    string GetCurrentAutoProfileSequenceKey()
    {
        var path =
            (_accountPoolService.CurrentSourcePath ?? "").Trim();

        if (path.Length == 0)
            return "";

        try
        {
            return Path
                .GetFullPath(path)
                .TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path;
        }
    }

    void SaveAutoProfileSequenceNext(
        string nextProfileName,
        string reason)
    {
        nextProfileName =
            (nextProfileName ?? "").Trim();

        if (!TryParseAutoProfileName(
                nextProfileName,
                out _,
                out _,
                out _))
        {
            return;
        }

        var sourceKey =
            GetCurrentAutoProfileSequenceKey();

        if (sourceKey.Length == 0)
            return;

        lock (_autoProfileSequenceLock)
        {
            var document =
                LoadAutoProfileSequenceDocumentUnsafe();

            document.Sources[sourceKey] =
                new AutoProfileSequenceState
                {
                    SourcePath = sourceKey,
                    NextProfileName = nextProfileName,
                    UpdatedUtc = DateTime.UtcNow
                };

            SaveAutoProfileSequenceDocumentUnsafe(
                document);
        }

        _log.Info(
            $"[AUTO_PROFILE_SEQUENCE_SAVE] excel={Path.GetFileName(sourceKey)} next={nextProfileName} reason={reason}");
    }

    AutoProfileSequenceDocument
        LoadAutoProfileSequenceDocumentUnsafe()
    {
        try
        {
            if (!File.Exists(AutoProfileSequencePath))
                return NewAutoProfileSequenceDocument();

            var loaded =
                JsonSerializer.Deserialize<AutoProfileSequenceDocument>(
                    File.ReadAllText(
                        AutoProfileSequencePath),
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

            loaded ??=
                NewAutoProfileSequenceDocument();

            loaded.Sources ??=
                new Dictionary<string, AutoProfileSequenceState>(
                    StringComparer.OrdinalIgnoreCase);

            loaded.Sources =
                new Dictionary<string, AutoProfileSequenceState>(
                    loaded.Sources,
                    StringComparer.OrdinalIgnoreCase);

            return loaded;
        }
        catch (Exception ex)
        {
            _log.Warn(
                $"[AUTO_PROFILE_SEQUENCE_READ_WARN] error={ex.Message}");

            return NewAutoProfileSequenceDocument();
        }
    }

    void SaveAutoProfileSequenceDocumentUnsafe(
        AutoProfileSequenceDocument document)
    {
        var json =
            JsonSerializer.Serialize(
                document,
                new JsonSerializerOptions
                {
                    WriteIndented = true
                });

        var temp =
            AutoProfileSequencePath
            + ".tmp";

        File.WriteAllText(
            temp,
            json,
            new UTF8Encoding(false));

        File.Move(
            temp,
            AutoProfileSequencePath,
            overwrite: true);
    }

    static AutoProfileSequenceDocument
        NewAutoProfileSequenceDocument()
        => new()
        {
            Version = 1,
            Sources =
                new Dictionary<string, AutoProfileSequenceState>(
                    StringComparer.OrdinalIgnoreCase)
        };

    sealed record AutoProfileParsedName(
        string Name,
        string Prefix,
        int Number,
        int Width,
        int SourceRow);

    sealed record AutoProfileSequenceStyle(
        string Prefix,
        int Width);
}
