using System.Text.RegularExpressions;

namespace Hawee.Flow.Core.Infra;

/// <summary>
/// So khớp từ khoá cho tra rule / KB / known-issue: theo từ (không theo chuỗi con — "cat" không khớp "category",
/// "view" không khớp "review"), tách CamelCase, bỏ dấu, thêm vài cặp đồng nghĩa Việt ↔ Anh.
/// </summary>
public static class Terms
{
    private static readonly HashSet<string> Stop = new()
    {
        "theo", "cho", "cua", "khi", "voi", "mot", "cac", "nhung", "khong", "duoc", "trong", "tren", "duoi", "sau", "truoc",
        "the", "and", "for", "with", "lam", "them", "sua", "bang", "nay", "kia", "hay", "hoac", "neu", "thi", "phai", "task",
    };

    /// <summary>Từ chung của đường dẫn file — không nói gì về nội dung cần rule nào.</summary>
    private static readonly HashSet<string> PathStop = new()
    {
        "src", "source", "test", "tests", "unittests", "command", "commands", "common", "core", "shared", "lib", "libs", "app", "apps",
        "main", "cs", "xaml", "csproj", "sln", "props", "json", "bin", "obj", "revit", "addin", "hawee", "service", "services",
        "manager", "helper", "helpers", "util", "utils", "impl", "base", "extensions", "internal", "interface", "class",
    };

    /// <summary>Cụm tiếng Việt (đã bỏ dấu) → thuật ngữ Revit API thường dùng trong rule. Chỉ cặp ít nghĩa; thêm khi cần.</summary>
    private static readonly (string Vi, string En)[] Synonyms =
    {
        ("giao dich", "transaction"), ("khung nhin", "view"), ("ban ve", "sheet"), ("phien ban", "version"),
        ("tham so", "parameter"), ("chu thich", "annotation"), ("kich thuoc", "dimension"), ("cat", "crop"), ("huy", "cancel"),
    };

    /// <summary>"CropRegionService" → "Crop Region Service"; "AddInId" → "Add In Id"; "XMLReader" → "XML Reader".</summary>
    public static string SplitCamel(string s) => Regex.Replace(s, @"(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", " ");

    private static IEnumerable<string> Words(string s) => Regex.Split(Text.Fold(SplitCamel(s)), @"[^a-z0-9]+").Where(w => w.Length > 0);

    /// <summary>Từ khoá truy vấn (≥ 3 ký tự, bỏ từ dừng) + thuật ngữ đồng nghĩa (cả hai chiều; cụm hai từ giữ dấu cách).</summary>
    public static List<string> Query(string s)
    {
        var words = Words(s).ToList();
        var res = words.Where(w => w.Length >= 3 && !Stop.Contains(w)).ToList();
        var joined = " " + string.Join(' ', words) + " ";
        foreach (var (vi, en) in Synonyms)
        {
            if (joined.Contains(" " + vi + " ")) { res.Add(en); if (vi.Contains(' ')) res.Add(vi); }
            if (words.Contains(en)) res.Add(vi);
        }
        return res.Distinct().ToList();
    }

    /// <summary>Từ khoá từ danh sách đường dẫn "src/Draw/KeyplanService.cs, …": bỏ từ chung và tên solution / project.</summary>
    public static List<string> FromPaths(string paths, IEnumerable<string>? extraStop = null)
    {
        var stop = new HashSet<string>(PathStop);
        foreach (var e in extraStop ?? Enumerable.Empty<string>()) foreach (var w in Words(e)) stop.Add(w);
        return paths.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .SelectMany(p => p.Split('/', '\\', '.', '_', '-'))
            .SelectMany(Words)
            .Where(w => w.Length >= 3 && !stop.Contains(w) && !Stop.Contains(w))
            .Distinct().ToList();
    }

    /// <summary>Văn bản đã tách từ để so khớp nhiều lần.</summary>
    public sealed class Doc
    {
        private readonly HashSet<string> _words;
        private readonly string _joined;

        public Doc(string text)
        {
            var words = Words(text).ToList();
            _words = new HashSet<string>(words);
            // giữ cả từ ghép chưa tách ("viewtemplate") để khớp truy vấn viết liền
            foreach (Match m in Regex.Matches(Text.Fold(text), @"[a-z0-9]{3,}")) _words.Add(m.Value);
            _joined = " " + string.Join(' ', words) + " ";
        }

        /// <summary>Khớp nguyên từ, số nhiều (+s), tiền tố khi từ khoá ≥ 6 ký tự ("transaction" ~ "transactions"); cụm có dấu cách khớp nguyên cụm.</summary>
        public bool Has(string k)
        {
            if (k.Contains(' ')) return _joined.Contains(" " + k + " ");
            if (_words.Contains(k) || _words.Contains(k + "s") || (k.EndsWith('s') && _words.Contains(k[..^1]))) return true;
            return k.Length >= 6 && _words.Any(w => w.StartsWith(k, StringComparison.Ordinal));
        }
    }
}
