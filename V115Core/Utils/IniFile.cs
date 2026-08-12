using System.Text;

namespace ToolTikTokV11.Utils;

public sealed class IniFile
{
    readonly Dictionary<string, Dictionary<string, string>> _data = new(StringComparer.OrdinalIgnoreCase);
    public string PathName { get; }

    public IniFile(string path)
    {
        PathName = path;
        if (File.Exists(path)) Load(path);
    }

    void Load(string path)
    {
        var text = File.ReadAllText(path, Encoding.UTF8); // BOM auto-detected (UTF-16 V10 is supported)
        string section = "";
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#')) continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim();
                Ensure(section);
                continue;
            }
            var pos = line.IndexOf('=');
            if (pos < 0) continue;
            Ensure(section)[line[..pos].Trim()] = line[(pos + 1)..].Trim();
        }
    }

    Dictionary<string, string> Ensure(string section)
    {
        if (!_data.TryGetValue(section, out var d)) _data[section] = d = new(StringComparer.OrdinalIgnoreCase);
        return d;
    }

    public string Get(string section, string key, string fallback = "") =>
        _data.TryGetValue(section, out var d) && d.TryGetValue(key, out var v) ? v : fallback;
    public int GetInt(string s, string k, int f = 0) => int.TryParse(Get(s, k), out var v) ? v : f;
    public double GetDouble(string s, string k, double f = 0) => double.TryParse(Get(s, k), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : f;
    public bool GetBool(string s, string k, bool f = false) => GetInt(s, k, f ? 1 : 0) != 0;
    public void Set(string s, string k, object? value) => Ensure(s)[k] = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "";

    public void Remove(string section, string key)
    {
        if (_data.TryGetValue(section, out var d)) d.Remove(key);
    }

    public void RemoveSection(string section) => _data.Remove(section);

    public void RemoveSectionsStartingWith(string prefix)
    {
        foreach (var key in _data.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
            _data.Remove(key);
    }

    public void Save()
    {
        var sb = new StringBuilder();
        foreach (var sec in _data)
        {
            sb.Append('[').Append(sec.Key).AppendLine("]");
            foreach (var kv in sec.Value) sb.Append(kv.Key).Append('=').AppendLine(kv.Value);
            sb.AppendLine();
        }
        File.WriteAllText(PathName, sb.ToString(), Encoding.Unicode); // giữ tương thích V10
    }
}
