using YamlDotNet.RepresentationModel;

namespace Hawee.Flow.Core.Infra;

/// <summary>Đọc YAML thành cây đơn giản (map / list / chuỗi) — đủ cho hawee.project.yaml, status.yaml, suite.yaml, flow.yaml.</summary>
public sealed class Y
{
    private readonly YamlNode? _node;
    private Y(YamlNode? node) => _node = node;

    public static readonly Y Missing = new(null);

    public static Y Parse(string text)
    {
        var stream = new YamlStream();
        using var reader = new StringReader(text);
        stream.Load(reader);
        return stream.Documents.Count == 0 ? Missing : new Y(stream.Documents[0].RootNode);
    }

    public static Y Load(string path) => File.Exists(path) ? Parse(File.ReadAllText(path)) : Missing;

    public bool Exists => _node is not null && !(_node is YamlScalarNode s && s.Value is null or "~" or "null");
    public bool IsMap => _node is YamlMappingNode;
    public bool IsList => _node is YamlSequenceNode;

    public Y this[string key]
    {
        get
        {
            if (_node is not YamlMappingNode map) return Missing;
            foreach (var (k, v) in map.Children)
                if (k is YamlScalarNode ks && ks.Value == key) return new Y(v);
            return Missing;
        }
    }

    /// <summary>Đường dẫn dạng "revit_test.capture.run_id.log".</summary>
    public Y At(string dotted)
    {
        var cur = this;
        foreach (var part in dotted.Split('.')) cur = cur[part];
        return cur;
    }

    public string? Str => _node is YamlScalarNode s ? s.Value : null;
    public string S(string fallback = "") => Str is { } v && v != "~" ? v : fallback;
    public int Int(int fallback = 0) => int.TryParse(Str, out var v) ? v : fallback;
    public bool Bool(bool fallback = false) => Str?.ToLowerInvariant() switch { "true" or "yes" => true, "false" or "no" => false, _ => fallback };

    public IEnumerable<Y> Items => _node is YamlSequenceNode seq ? seq.Children.Select(c => new Y(c)) : Enumerable.Empty<Y>();

    /// <summary>Danh sách chuỗi: chấp nhận cả list lẫn một chuỗi đơn.</summary>
    public List<string> Strings()
    {
        if (_node is YamlSequenceNode) return Items.Select(i => i.S()).Where(s => s.Length > 0).ToList();
        return Str is { Length: > 0 } one ? new List<string> { one } : new List<string>();
    }

    public IEnumerable<(string Key, Y Value)> Entries =>
        _node is YamlMappingNode map
            ? map.Children.Select(kv => (((YamlScalarNode)kv.Key).Value ?? "", new Y(kv.Value)))
            : Enumerable.Empty<(string, Y)>();

    /// <summary>Chuyển về object thuần (Dictionary/List/string) — dùng để ghi JSON.</summary>
    public object? ToPlain() => ToPlain(_node);

    private static object? ToPlain(YamlNode? n) => n switch
    {
        YamlMappingNode m => m.Children.ToDictionary(kv => ((YamlScalarNode)kv.Key).Value ?? "", kv => ToPlain(kv.Value)),
        YamlSequenceNode s => s.Children.Select(ToPlain).ToList(),
        YamlScalarNode sc => sc.Value,
        _ => null,
    };
}
