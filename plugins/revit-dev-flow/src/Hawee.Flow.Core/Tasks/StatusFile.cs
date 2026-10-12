using System.Text.RegularExpressions;
using Hawee.Flow.Core.Infra;

namespace Hawee.Flow.Core.Tasks;

/// <summary>
/// status.yaml của task. Đọc bằng YamlDotNet; ghi bằng cách sửa đúng dòng (giữ comment, thứ tự, dạng một dòng
/// "{ .. }" / "[ .. ]") để script PS 5.1 (HaweeYaml.ps1) của BIM_KB_Base vẫn đọc được.
/// </summary>
public sealed class StatusFile
{
    public string Path { get; }
    private List<string> _lines;

    public StatusFile(string path)
    {
        Path = path;
        _lines = File.Exists(path) ? Text.Read(path).Split('\n').ToList() : new List<string>();
        if (_lines.Count > 0 && _lines[^1] == "") _lines.RemoveAt(_lines.Count - 1);
    }

    public static StatusFile ForTask(string taskDir) => new(System.IO.Path.Combine(taskDir, "status.yaml"));

    public Y Data => Y.Parse(string.Join('\n', _lines));
    public string Get(string key) => Data[key].S();

    public void Save() => Text.Write(Path, string.Join('\n', _lines) + "\n");

    // ---- ghi -------------------------------------------------------------

    public void Set(string key, string value) => SetRaw(key, Scalar(value));
    public void Set(string key, int value) => SetRaw(key, value.ToString());
    public void Set(string key, bool value) => SetRaw(key, value ? "true" : "false");
    public void SetList(string key, IEnumerable<string> items) => SetRaw(key, "[" + string.Join(", ", items.Select(Scalar)) + "]");
    public void SetMap(string key, IEnumerable<KeyValuePair<string, string>> map) => SetRaw(key, FlowMap(map));

    /// <summary>Thêm phần tử vào list một dòng (không trùng).</summary>
    public void AddToList(string key, string item)
    {
        var cur = Data[key].Strings();
        if (!cur.Contains(item)) cur.Add(item);
        SetList(key, cur);
    }

    /// <summary>Đặt dòng "  G1: { ... }" trong khối gates.</summary>
    public void SetGate(string gate, IEnumerable<KeyValuePair<string, string>> fields)
    {
        var start = FindTop("gates");
        if (start < 0) { _lines.Add("gates:"); start = _lines.Count - 1; }
        var line = $"  {gate}: {FlowMap(fields)}";
        for (var i = start + 1; i < _lines.Count && (_lines[i].StartsWith(' ') || _lines[i].Length == 0); i++)
        {
            if (Regex.IsMatch(_lines[i], $@"^\s+{Regex.Escape(gate)}:"))
            {
                _lines[i] = line + Comment(_lines[i]);
                return;
            }
        }
        var insertAt = start + 1;
        while (insertAt < _lines.Count && _lines[insertAt].StartsWith(' ')) insertAt++;
        _lines.Insert(insertAt, line);
    }

    /// <summary>Đặt một khoá trong map con một dòng, vd. loops.host_test.</summary>
    public void SetInMap(string key, string sub, string rawValue)
    {
        var map = Data[key].Entries.ToDictionary(e => e.Key, e => RawOf(e.Value));
        map[sub] = rawValue;
        SetRaw(key, "{ " + string.Join(", ", map.Select(kv => $"{kv.Key}: {kv.Value}")) + " }");
    }

    public void SetRaw(string key, string raw)
    {
        var i = FindTop(key);
        var newLine = $"{key}: {raw}";
        if (i < 0) { _lines.Add(newLine); return; }
        var comment = Comment(_lines[i]);
        if (comment.Length > 0)
        {
            var col = _lines[i].IndexOf(comment.TrimStart(), StringComparison.Ordinal);
            newLine = newLine.Length + 1 < col ? newLine.PadRight(col) + comment.TrimStart() : newLine + "  " + comment.TrimStart();
        }
        _lines[i] = newLine;
        // khối con nhiều dòng bên dưới (nếu có) bị thay bằng giá trị một dòng
        var j = i + 1;
        while (j < _lines.Count && _lines[j].StartsWith(' ') && !_lines[j].TrimStart().StartsWith('#')) _lines.RemoveAt(j);
    }

    // ---- tiện ích --------------------------------------------------------

    private int FindTop(string key)
    {
        for (var i = 0; i < _lines.Count; i++)
            if (_lines[i].StartsWith(key + ":", StringComparison.Ordinal)) return i;
        return -1;
    }

    /// <summary>Phần comment cuối dòng (" # ...") nằm ngoài chuỗi nháy.</summary>
    private static string Comment(string line)
    {
        bool dq = false, sq = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"' && !sq && (i == 0 || line[i - 1] != '\\')) dq = !dq;
            else if (c == '\'' && !dq) sq = !sq;
            else if (c == '#' && !dq && !sq && i > 0 && line[i - 1] == ' ')
            {
                var s = i;
                while (s > 0 && line[s - 1] == ' ') s--;
                return line[s..];
            }
        }
        return "";
    }

    public static string FlowMap(IEnumerable<KeyValuePair<string, string>> map) =>
        "{ " + string.Join(", ", map.Select(kv => $"{kv.Key}: {kv.Value}")) + " }";

    /// <summary>Chuỗi YAML an toàn cho cả YamlDotNet lẫn HaweeYaml.ps1.</summary>
    public static string Scalar(string? v)
    {
        v ??= "";
        if (v.Length == 0) return "\"\"";
        var plain = Regex.IsMatch(v, @"^[\p{L}\p{N}_./()§][\p{L}\p{N} _./()§+-]*$") && !v.EndsWith(' ')
                    && v is not ("true" or "false" or "null" or "~" or "yes" or "no");
        return plain ? v : "\"" + v.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    private static string RawOf(Y y) => y.IsMap || y.IsList ? "{}" : (y.Str is { } s && Regex.IsMatch(s, @"^-?\d+$") ? s : Scalar(y.S()));
}
