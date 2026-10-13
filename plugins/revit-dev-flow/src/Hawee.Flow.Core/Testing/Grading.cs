using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Hawee.Flow.Core.Infra;

namespace Hawee.Flow.Core.Testing;

public static class Verdicts
{
    public const string Pass = "PASS", Fail = "FAIL", Suspect = "SUSPECT", NotMeasurable = "NOT_MEASURABLE", Error = "ERROR";
    /// <summary>Luật cần agent chấm (nhìn ảnh / Máy chấm không dịch được). Ghi vào result.json là SUSPECT + pending_agent.</summary>
    public const string Agent = "AGENT";
}

public sealed record GroupResult(string Group, string Verdict, string Expected, string Actual, string Evidence);
public sealed record GradeResult(string Verdict, List<GroupResult> Groups, string Reason, List<string> FilesRead);

/// <summary>Dữ liệu một lần chạy cho bộ chấm: RUN_DIR\data, baseline\ của suite, file cấu hình mà các step đã truyền vào.</summary>
public sealed class GradingData(string runDir, string suiteDir, IReadOnlyList<string> configFiles)
{
    public string RunDir => runDir;
    public string DataDir => Path.Combine(runDir, "data");
    public string BaselineDir => Path.Combine(suiteDir, "baseline");
    public List<string> FilesRead { get; } = new();
    private readonly Dictionary<string, JsonNode?> _json = new(StringComparer.Ordinal);

    /// <summary>Tìm như rule-eval.ps1 (data\ rồi RUN_DIR), thêm data\checkpoints\ và tìm sâu theo tên file trong data\.</summary>
    public string? FindData(string rel)
    {
        rel = rel.Trim().Trim('`', '\'', '"').Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        if (rel.Length == 0) return null;
        foreach (var root in new[] { DataDir, runDir, Path.Combine(DataDir, "checkpoints") })
        {
            var p = Path.Combine(root, rel);
            if (File.Exists(p)) return Track(p);
        }
        // chỉ tên file: tìm trong data\ (kể cả checkpoints\<step>\)
        if (!rel.Contains(Path.DirectorySeparatorChar) && Directory.Exists(DataDir))
        {
            var hit = Directory.EnumerateFiles(DataDir, rel, SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal).FirstOrDefault();
            if (hit is not null) return Track(hit);
        }
        return null;
    }

    public string? FindBaseline(string rel)
    {
        var p = Path.Combine(BaselineDir, rel.Trim().Replace('\\', Path.DirectorySeparatorChar));
        return File.Exists(p) ? Track(p) : null;
    }

    public string? FindConfig(string name)
    {
        var n = name.Trim().Trim('`', '\'', '"');
        var hit = configFiles.FirstOrDefault(f => f.Replace('\\', '/').EndsWith(n.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));
        return hit is not null && File.Exists(hit) ? Track(hit) : null;
    }

    /// <summary>File .json để suy ra nguồn khi luật không ghi tên file: ở thẳng data\ (như rule-eval.ps1), hoặc mọi tầng (trừ grading\).</summary>
    public IEnumerable<string> JsonFiles(bool deep)
    {
        if (!Directory.Exists(DataDir)) return Enumerable.Empty<string>();
        return Directory.EnumerateFiles(DataDir, "*.json", deep ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
            .Where(f => !Path.GetFileName(f).Equals("rule-eval.json", StringComparison.OrdinalIgnoreCase)
                        && !Path.GetRelativePath(DataDir, f).Replace('\\', '/').StartsWith("grading/", StringComparison.Ordinal))
            .OrderBy(f => f, StringComparer.Ordinal);
    }

    /// <summary>JSON đã đọc dùng chung trong một lần chấm — cùng file thì cùng node (giao một tập với chính nó).</summary>
    public JsonNode? Json(string path)
    {
        if (!_json.TryGetValue(path, out var n)) _json[path] = n = LoadJson(path);
        return n;
    }

    private string Track(string p) { if (!FilesRead.Contains(p)) FilesRead.Add(p); return p; }

    public static JsonNode? LoadJson(string path)
    {
        var text = File.ReadAllText(path);
        try { return JsonNode.Parse(text); }
        catch (JsonException)
        {
            // đọc chịu lỗi: bỏ comment // và /* */, dấu phẩy thừa
            var cleaned = Regex.Replace(text, @"(?<!:)//[^\n]*|/\*.*?\*/", "", RegexOptions.Singleline);
            cleaned = Regex.Replace(cleaned, @",(\s*[}\]])", "$1");
            return JsonNode.Parse(cleaned, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
    }
}

/// <summary>
/// Bộ chấm dòng "Máy chấm" của rules.md — cùng cú pháp và ngữ nghĩa với BIM_KB_Base core/tools/rule-eval.ps1
/// (suite-format.md §2.1 "Máy thực thi"). Tất định: cùng dữ liệu → cùng verdict.
/// Mỗi phép trong `…` chấm riêng, luật lấy verdict xấu nhất FAIL > AGENT > NOT_MEASURABLE > PASS.
/// Máy không đọc được phép → AGENT (agent chấm); thiếu dữ liệu / ngưỡng "chờ" → NOT_MEASURABLE; không bao giờ đoán thành PASS.
/// Mở rộng so với rule-eval.ps1 (chỉ ở chỗ rule-eval ra AGENT, hoặc khi dự án bật default_tol_mm): tên loại trần
/// (vd. giao(Label, Keyplan)), cài đặt(file, khoá), nhiều dạng bbox, sai số "± x" ở giao, nhóm từ "Áp dụng cho".
/// </summary>
public sealed class Grader(GradingData data)
{
    /// <summary>Sai số khi luật so số thực mà không ghi "± x" (revit_test.default_tol_mm; mặc định 0 như rule-eval.ps1).</summary>
    public double DefaultTol { get; init; }

    private readonly List<string> _files = new();
    private string? _lastJson;

    private sealed class UnsupportedException(string m) : Exception(m);   // → AGENT
    private sealed class MissingException(string m) : Exception(m);       // → NOT_MEASURABLE

    private sealed record Clause(string Verdict, List<GroupResult> Groups, string Reason);

    internal sealed record Item(JsonNode? Node, JsonNode?[] Chain);

    private sealed record Seg(string Name, bool Br, List<Cond> Conds);
    private sealed record Cond(string Field, string Op, string Value);

    /// <summary>Nguồn đã giải: file + đường dẫn (Segs null = tên loại trần, tìm theo trường type/kind/…).</summary>
    private sealed record Src(string File, string Path, Seg[]? Segs, JsonNode? Root, List<Item> Items);

    public GradeResult Grade(TestRule rule, IReadOnlyList<string> sheets)
    {
        _files.Clear();
        _lastJson = null;
        if (rule.Pending) return Result(Verdicts.NotMeasurable, new(), "người chưa duyệt ngưỡng / kỳ vọng ('chờ')");
        var clauses = rule.Clauses;
        if (clauses.Count == 0) return Result(Verdicts.Agent, new(), "luật không có Máy chấm — agent chấm theo Kiểm / Cách đo");
        var done = clauses.Select(c => (Text: c, R: Eval(c, sheets))).ToList();
        if (done.Count == 1) return Result(done[0].R.Verdict, done[0].R.Groups, done[0].R.Reason);

        // nhiều phép nối bằng "và": gộp theo nhóm, verdict xấu nhất
        var verdict = Worst(done.Select(d => d.R.Verdict));
        var reason = string.Join(" · ", done.Select(d => $"{d.R.Verdict} `{Text.Clip(d.Text, 60)}`{(d.R.Reason.Length > 0 ? ": " + d.R.Reason : "")}"));
        var merged = new Dictionary<string, List<GroupResult>>();
        var order = new List<string>();
        foreach (var d in done)
            foreach (var g in d.R.Groups.Count > 0 ? d.R.Groups : new List<GroupResult> { new("*", d.R.Verdict, d.Text, "", d.R.Reason) })
            {
                if (!merged.TryGetValue(g.Group, out var l)) { merged[g.Group] = l = new(); order.Add(g.Group); }
                l.Add(g);
            }
        var groups = order.Select(k => merged[k]).Select(l => new GroupResult(l[0].Group, Worst(l.Select(x => x.Verdict)),
            string.Join(" và ", l.Select(x => x.Expected)), string.Join(" · ", l.Select(x => x.Actual)), string.Join(" · ", l.Select(x => x.Evidence)))).ToList();
        return Result(verdict, groups, reason);

        GradeResult Result(string v, List<GroupResult> g, string r) => new(v, g, r, _files.ToList());
    }

    /// <summary>FAIL > AGENT > NOT_MEASURABLE > PASS (Get-Worst của rule-eval.ps1).</summary>
    internal static string Worst(IEnumerable<string> verdicts)
    {
        var l = verdicts.ToList();
        foreach (var v in new[] { Verdicts.Fail, Verdicts.Agent, Verdicts.NotMeasurable })
            if (l.Contains(v)) return v;
        return l.All(v => v == Verdicts.Pass) ? Verdicts.Pass : Verdicts.NotMeasurable;
    }

    // chỗ trống kiểu <bộ>, <sheet_name dòng 1> (có dấu cách hoặc chữ có dấu) — kể cả trong regex của log; nhóm có tên (?<ten>…) không tính
    private static readonly Regex Placeholder = new(@"(?<!\(\?)<[^<>=!]*(\s|[^\x00-\x7F])[^<>]*>", RegexOptions.Compiled);

    private Clause Eval(string clause, IReadOnlyList<string> sheets)
    {
        var t = clause.Trim();
        try
        {
            if (Placeholder.IsMatch(t)) return Agent("còn chỗ trống <…> — luật chưa điền giá trị cụ thể");
            var c = Parse(t);
            return c.Name switch
            {
                "anh" => Agent("luật nhìn ảnh: " + string.Join(", ", c.Args)),
                "dem" => Count(c, sheets),
                "giao" => Intersect(c, sheets),
                "trong" => Inside(c, sheets),
                "bang" => Equal(c),
                "thu tu" => Order(c),
                "khop baseline" => MatchBaseline(c),
                "co dong" => LogHas(c, true),
                "khong co dong" => LogHas(c, false),
                "step xong" => StepDone(c),
                _ => Agent($"Máy chấm '{c.Name}' chưa hỗ trợ — agent chấm"),
            };
        }
        catch (UnsupportedException ex) { return Agent("máy không đọc được: " + ex.Message); }
        catch (MissingException ex) { return new Clause(Verdicts.NotMeasurable, new(), ex.Message); }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or ArgumentException or OverflowException)
        {
            return Agent($"lỗi khi tính: {ex.Message}");
        }

        static Clause Agent(string why) => new(Verdicts.Agent, new(), why);
    }

    // ---- cú pháp ---------------------------------------------------------------------------------------

    /// <summary>Tên phép (bỏ dấu) + đối số ở mức ngoài cùng + phần sau ')'.</summary>
    internal sealed record Call(string Name, List<string> Args, string Rest);

    /// <summary>"giao(A, B) theo SheetNumber = 0" → Call(giao, [A, B], "theo SheetNumber = 0").</summary>
    internal static Call Parse(string expr)
    {
        var open = expr.IndexOf('(');
        if (open < 0) throw new UnsupportedException($"không có '(': {expr}");
        var name = Regex.Replace(Text.Fold(expr[..open]).Trim(), @"\s+", " ");
        var depth = 0; var close = -1; var inQ = false;
        for (var i = open; i < expr.Length; i++)
        {
            var c = expr[i];
            if (c == '"') { inQ = !inQ; continue; }
            if (inQ) continue;
            if (c is '(' or '[') depth++;
            else if (c is ')' or ']') { depth--; if (depth == 0 && c == ')') { close = i; break; } }
        }
        if (close < 0) throw new UnsupportedException($"thiếu ')' trong: {expr}");
        return new Call(name, SplitTop(expr[(open + 1)..close], ',').Where(p => p.Length > 0).ToList(), expr[(close + 1)..].Trim());
    }

    /// <summary>Tách ở mức ngoài cùng (ngoài "…", (), []) theo ký tự sep.</summary>
    private static List<string> SplitTop(string s, char sep)
    {
        var parts = new List<string>();
        var depth = 0; var inQ = false; var start = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '"') { inQ = !inQ; continue; }
            if (inQ) continue;
            if (c is '(' or '[') depth++;
            else if (c is ')' or ']') depth--;
            else if (c == sep && depth == 0) { parts.Add(s[start..i].Trim()); start = i + 1; }
        }
        parts.Add(s[start..].Trim());
        return parts;
    }

    private static string Unquote(string s)
    {
        s = s.Trim();
        return s.Length >= 2 && s[0] == '"' && s[^1] == '"' ? s[1..^1] : s;
    }

    private const string Key = @"[A-Za-z_][A-Za-z0-9_]*";
    private const string Op = @"==|!=|>=|<=|=|>|<|≥|≤|≠";
    private const string Num = @"-?\d+(?:[.,]\d+)?";
    private const string Unit = @"(?:\s*(?:mm²|mm2|mm|m²))?";
    private const string Tol = @"(?:±|\+-|\+/-)\s*(\d+(?:[.,]\d+)?)" + Unit;

    private static readonly Regex TailCount = new($@"^(?:theo\s+({Key})\s*)?({Op})\s*({Num}){Unit}$");
    private static readonly Regex TailIntersect = new($@"^(?:theo\s+({Key})\s*)?(?:({Op})\s*({Num}){Unit})?\s*(?:{Tol})?$");
    private static readonly Regex TailInside = new($@"^(?:theo\s+({Key}))?\s*(?:{Tol})?$");
    private static readonly Regex TailTol = new($@"^(?:{Tol})?$");
    private static readonly Regex TailOrder = new($@"^(?:theo\s+({Key})\s*)?=\s*(.+)$");

    private static Match Tail(Regex rx, Call c)
    {
        var m = rx.Match(c.Rest);
        return m.Success ? m : throw new UnsupportedException($"phần sau {c.Name}(...): '{c.Rest}'");
    }

    private static double ParseNum(string s) => double.Parse(s.Replace(',', '.'), CultureInfo.InvariantCulture);

    private static bool TestOp(double a, string op, double b) => op switch
    {
        "=" or "==" => Math.Abs(a - b) < 1e-9,
        "!=" or "≠" => Math.Abs(a - b) >= 1e-9,
        ">=" or "≥" => a >= b,
        "<=" or "≤" => a <= b,
        ">" => a > b,
        "<" => a < b,
        _ => throw new UnsupportedException($"phép so '{op}'"),
    };

    /// <summary>So có sai số (chỉ khi luật ghi "± x" — phần mở rộng).</summary>
    private static bool TestOpTol(double a, string op, double b, double tol) => op switch
    {
        "=" or "==" => Math.Abs(a - b) <= tol,
        "!=" or "≠" => Math.Abs(a - b) > tol,
        "<" => a < b + tol,
        "<=" or "≤" => a <= b + tol,
        ">" => a > b - tol,
        ">=" or "≥" => a >= b - tol,
        _ => throw new UnsupportedException($"phép so '{op}'"),
    };

    // ---- đường dẫn JSON ----------------------------------------------------------------------------------

    private static readonly Regex SegRx = new($@"^({Key})(\[(.*)\])?$", RegexOptions.Singleline);
    private static readonly Regex CondEq = new($@"^({Key})\s*(!=|==|=)\s*(.*)$", RegexOptions.Singleline);
    private static readonly Regex CondStarts = new($@"^({Key})\s+(?:bắt đầu bằng|bat dau bang)\s+(.+)$", RegexOptions.Singleline | RegexOptions.IgnoreCase);
    private static readonly Regex CondNotContains = new($@"^({Key})\s+(?:không chứa|khong chua)\s+(.+)$", RegexOptions.Singleline | RegexOptions.IgnoreCase);
    private static readonly Regex CondContains = new($@"^({Key})\s+(?:chứa|chua)\s+(.+)$", RegexOptions.Singleline | RegexOptions.IgnoreCase);

    /// <summary>"Ten[loc].Ten[loc].Ten" — lọc rỗng = mọi phần tử; Trường=giá trị | != | bắt đầu bằng | chứa | không chứa "…".</summary>
    private static Seg[] ParsePath(string p)
    {
        p = p.Trim();
        if (p.StartsWith("$.")) p = p[2..];
        var segs = new List<Seg>();
        foreach (var raw in SplitTop(p, '.'))
        {
            var m = SegRx.Match(raw);
            if (!m.Success) throw new UnsupportedException($"đoạn đường dẫn '{raw}'");
            var conds = new List<Cond>();
            var inner = m.Groups[3].Value.Trim();
            if (m.Groups[2].Success && inner.Length > 0 && inner != "*")
                foreach (var c in SplitTop(inner, ','))
                {
                    Match cm;
                    if ((cm = CondEq.Match(c)).Success) conds.Add(new Cond(cm.Groups[1].Value, cm.Groups[2].Value == "!=" ? "ne" : "eq", Unquote(cm.Groups[3].Value)));
                    else if ((cm = CondStarts.Match(c)).Success) conds.Add(new Cond(cm.Groups[1].Value, "starts", Unquote(cm.Groups[2].Value)));
                    else if ((cm = CondNotContains.Match(c)).Success) conds.Add(new Cond(cm.Groups[1].Value, "notcontains", Unquote(cm.Groups[2].Value)));
                    else if ((cm = CondContains.Match(c)).Success) conds.Add(new Cond(cm.Groups[1].Value, "contains", Unquote(cm.Groups[2].Value)));
                    else throw new UnsupportedException($"điều kiện lọc '{c}'");
                }
            segs.Add(new Seg(m.Groups[1].Value, m.Groups[2].Success, conds));
        }
        return segs.ToArray();
    }

    /// <summary>Lọc chỉ trên chính phần tử, so đúng từng ký tự (như rule-eval.ps1).</summary>
    private static bool TestConds(JsonNode? node, List<Cond> conds)
    {
        foreach (var c in conds)
        {
            if (node is not JsonObject o) return false;
            var v = Str(Get(o, c.Field));
            var ok = c.Op switch
            {
                "eq" => string.Equals(v, c.Value, StringComparison.Ordinal),
                "ne" => !string.Equals(v, c.Value, StringComparison.Ordinal),
                "starts" => v.StartsWith(c.Value, StringComparison.Ordinal),
                "contains" => v.Contains(c.Value, StringComparison.Ordinal),
                "notcontains" => !v.Contains(c.Value, StringComparison.Ordinal),
                _ => false,
            };
            if (!ok) return false;
        }
        return true;
    }

    private static List<Item> InvokePath(JsonNode? root, Seg[] segs, int take = -1)
    {
        if (take < 0) take = segs.Length;
        var items = new List<Item> { new(root, Array.Empty<JsonNode?>()) };
        for (var s = 0; s < take; s++)
        {
            var seg = segs[s];
            var next = new List<Item>();
            foreach (var it in items)
            {
                if (!Has(it.Node, seg.Name)) continue;
                var v = Get((JsonObject)it.Node!, seg.Name);
                var chain = it.Chain.Append(it.Node).ToArray();
                if (seg.Br || v is JsonArray)
                {
                    foreach (var e in ToList(v)) if (TestConds(e, seg.Conds)) next.Add(new Item(e, chain));
                }
                else if (TestConds(v, seg.Conds)) next.Add(new Item(v, chain));
            }
            items = next;
        }
        return items;
    }

    private static IEnumerable<JsonNode?> ToList(JsonNode? v) => v switch { null => Enumerable.Empty<JsonNode?>(), JsonArray a => a, _ => new[] { v } };

    private static bool Has(JsonNode? n, string key) =>
        n is JsonObject o && (o.ContainsKey(key) || o.Any(kv => kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase)));

    private static JsonNode? Get(JsonObject o, string key) =>
        o.TryGetPropertyValue(key, out var v) ? v : o.FirstOrDefault(kv => kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;

    /// <summary>Chuỗi của một giá trị như To-Str của rule-eval.ps1: số nguyên giữ nguyên, số thực dạng "R", bool true/false.</summary>
    internal static string Str(JsonNode? n)
    {
        switch (n)
        {
            case null: return "";
            case JsonValue v:
                if (v.TryGetValue<string>(out var s)) return s;
                if (v.TryGetValue<bool>(out var b)) return b ? "true" : "false";
                if (v.TryGetValue<long>(out var l)) return l.ToString(CultureInfo.InvariantCulture);
                if (v.TryGetValue<double>(out var d)) return d.ToString("R", CultureInfo.InvariantCulture);
                return v.ToString();
            default: return n.ToJsonString();
        }
    }

    /// <summary>Khoá lấy ở chính phần tử hoặc cha gần nhất.</summary>
    private static string? ItemKey(Item it, string k)
    {
        if (Has(it.Node, k)) return Str(Get((JsonObject)it.Node!, k));
        for (var i = it.Chain.Length - 1; i >= 0; i--)
            if (Has(it.Chain[i], k)) return Str(Get((JsonObject)it.Chain[i]!, k));
        return null;
    }

    // ---- nguồn dữ liệu --------------------------------------------------------------------------------

    private static bool HasArrow(string s) => s.Contains('→') || s.Contains("->");
    private static bool IsIdent(string s) => Regex.IsMatch(s.Trim(), $"^{Key}$");

    private string Use(string path) { if (!_files.Contains(path)) _files.Add(path); return path; }

    private JsonNode? Json(string path) => data.Json(Use(path));

    /// <summary>File .json có thuộc tính gốc trùng tên (ở thẳng data\; không có thì mọi tầng).</summary>
    private List<string> Candidates(string rootName)
    {
        foreach (var deep in new[] { false, true })
        {
            var c = new List<string>();
            foreach (var f in data.JsonFiles(deep))
            {
                try { if (Has(data.Json(f), rootName)) c.Add(f); }
                catch (JsonException) { /* file hỏng: bỏ qua như rule-eval */ }
            }
            if (c.Count > 0) return c;
        }
        return new();
    }

    /// <summary>
    /// "file → đường dẫn" (→ hoặc ->). Bỏ file → file của phép trước trong cùng luật; không có thì file .json duy nhất có thuộc tính
    /// gốc trùng đoạn đầu. Không thấy file nào và tham số là một định danh → tên loại trần (phần mở rộng của plugin).
    /// </summary>
    private Src Source(string arg)
    {
        var a = arg.Trim();
        string? file = null; var path = a;
        foreach (var sep in new[] { "→", "->" })
        {
            var i = a.IndexOf(sep, StringComparison.Ordinal);
            if (i >= 0) { file = a[..i].Trim(); path = a[(i + sep.Length)..].Trim(); break; }
        }
        if (file is null && Regex.IsMatch(a, @"^[^\s\[\]]+\.json$", RegexOptions.IgnoreCase)) { file = a; path = ""; }

        string full;
        if (file is not null)
        {
            full = data.FindData(file) ?? throw new MissingException($"thiếu file dữ liệu {file} trong data\\ (điểm kiểm chưa thu?)");
        }
        else
        {
            var segs0 = ParsePath(path);
            if (_lastJson is not null) full = _lastJson;
            else
            {
                var cands = Candidates(segs0[0].Name);
                if (cands.Count == 0 && IsIdent(path)) return TypeSource(path);
                if (cands.Count != 1) throw new UnsupportedException($"không rõ file dữ liệu cho '{path}' ({cands.Count} file .json có '{segs0[0].Name}')");
                full = cands[0];
            }
        }
        _lastJson = full;
        var root = Json(full) ?? throw new MissingException($"{Path.GetFileName(full)} rỗng");
        if (path.Length == 0) return new Src(full, "", null, root, ToList(root).Select(n => new Item(n, new JsonNode?[] { root })).ToList());
        var segs = ParsePath(path);
        return new Src(full, path, segs, root, InvokePath(root, segs));
    }

    /// <summary>Tên trần, vd. "Keyplan": phần tử có type/kind/category/role/componentType/name = Keyplan trong file *component*.json.</summary>
    private Src TypeSource(string name)
    {
        var f = Directory.Exists(data.DataDir)
            ? Directory.EnumerateFiles(data.DataDir, "*component*.json", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal).FirstOrDefault()
            : null;
        if (f is null) throw new UnsupportedException($"'{name}' không ghi file và không tìm thấy file toạ độ component trong data\\");
        var root = Json(f);
        var items = AllObjects(root).Where(it => TypeOf(it.Node).Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        return new Src(f, "loại:" + name, null, root, items);
    }

    private static readonly string[] TypeKeys = { "type", "kind", "category", "role", "componentType", "name" };

    private static string TypeOf(JsonNode? n)
    {
        if (n is not JsonObject o) return "";
        foreach (var k in TypeKeys) if (Has(o, k) && Get(o, k) is JsonValue v && v.TryGetValue<string>(out var s)) return s;
        return "";
    }

    private static IEnumerable<Item> AllObjects(JsonNode? root)
    {
        var stack = new Stack<Item>();
        stack.Push(new Item(root, Array.Empty<JsonNode?>()));
        while (stack.Count > 0)
        {
            var it = stack.Pop();
            if (it.Node is JsonObject o)
            {
                yield return it;
                var chain = it.Chain.Append(o).ToArray();
                foreach (var (_, v) in o.Reverse()) if (v is JsonObject or JsonArray) stack.Push(new Item(v, chain));
            }
            else if (it.Node is JsonArray a)
                foreach (var x in a.Reverse().Where(x => x is not null)) stack.Push(new Item(x, it.Chain));
        }
    }

    /// <summary>
    /// Nhóm theo khoá như Get-Groups của rule-eval.ps1: mở mọi giá trị khoá ở tầng nông nhất có khoá (nhóm không có phần tử
    /// nào ở tầng cuối vẫn tính = 0). Mở rộng: khoá có chữ "Sheet" → thêm các sheet trong "Áp dụng cho" chưa có trong dữ liệu.
    /// </summary>
    private static Dictionary<string, List<Item>> Groups(Src src, string key, IReadOnlyList<string> sheets)
    {
        var g = new Dictionary<string, List<Item>>(StringComparer.Ordinal);
        var order = new List<string>();
        void Add(string k) { if (!g.ContainsKey(k)) { g[k] = new(); order.Add(k); } }
        if (key.Length == 0) { g["*"] = src.Items.ToList(); return g; }
        if (src.Segs is { } segs)
        {
            var level = 0;
            for (var p = 1; p <= segs.Length && level == 0; p++)
                if (InvokePath(src.Root, segs, p).Any(i => Has(i.Node, key))) level = p;
            if (level == 0) throw new UnsupportedException($"khoá '{key}' không có trong dữ liệu của đường dẫn {src.Path}");
            foreach (var it in InvokePath(src.Root, segs, level)) if (ItemKey(it, key) is { } k) Add(k);
        }
        foreach (var it in src.Items)
            if (ItemKey(it, key) is { } k) { Add(k); g[k].Add(it); }
        if (key.Contains("sheet", StringComparison.OrdinalIgnoreCase)) foreach (var s in sheets) Add(s);
        return order.ToDictionary(k => k, k => g[k]);
    }

    internal record struct Box(double X1, double Y1, double X2, double Y2)
    {
        public bool Inside(Box o, double tol) => X1 >= o.X1 - tol && Y1 >= o.Y1 - tol && X2 <= o.X2 + tol && Y2 <= o.Y2 + tol;
        public override string ToString() => FormattableString.Invariant($"[{X1:0.#},{Y1:0.#} – {X2:0.#},{Y2:0.#}]");
    }

    /// <summary>bbox từ: {MinX,MinY,MaxX,MaxY} (như rule-eval) · {Min:{X,Y},Max:{X,Y}} · {x,y,width,height} · [x1,y1,x2,y2]; trong item hoặc trường bbox/bounds/box.</summary>
    internal static Box? BoxOf(JsonNode? n)
    {
        if (n is JsonObject o)
        {
            double? D(params string[] ks)
            {
                foreach (var k in ks) if (Has(o, k) && Get(o, k) is JsonValue v && double.TryParse(v.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
                return null;
            }
            if (D("minX", "xMin", "left") is { } x1 && D("minY", "yMin", "bottom") is { } y1
                && D("maxX", "xMax", "right") is { } x2 && D("maxY", "yMax", "top") is { } y2)
                return new Box(Math.Min(x1, x2), Math.Min(y1, y2), Math.Max(x1, x2), Math.Max(y1, y2));
            foreach (var k in new[] { "bbox", "boundingBox", "bounds", "box", "outline" })
                if (Has(o, k) && BoxOf(Get(o, k)) is { } b) return b;
            if (Has(o, "min") && Has(o, "max") && Get(o, "min") is JsonObject mn && Get(o, "max") is JsonObject mx
                && BoxOf(new JsonArray(Get(mn, "x")?.DeepClone(), Get(mn, "y")?.DeepClone(), Get(mx, "x")?.DeepClone(), Get(mx, "y")?.DeepClone())) is { } mm) return mm;
            if (D("x") is { } x && D("y") is { } y && D("width", "w") is { } w && D("height", "h") is { } h)
                return new Box(x, y, x + w, y + h);
        }
        if (n is JsonArray a && a.Count == 4 && a.All(v => v is JsonValue jv && double.TryParse(jv.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
        {
            var v = a.Select(e => double.Parse(e!.ToString(), CultureInfo.InvariantCulture)).ToArray();
            return new Box(Math.Min(v[0], v[2]), Math.Min(v[1], v[3]), Math.Max(v[0], v[2]), Math.Max(v[1], v[3]));
        }
        return null;
    }

    private static string Label(JsonNode? n)
    {
        if (n is JsonObject o)
            foreach (var f in new[] { "ViewName", "SheetNumber", "ComponentType", "Name", "type" })
                if (Has(o, f)) return Str(Get(o, f));
        return "?";
    }

    private static string Short(IEnumerable<string> list, int max = 10)
    {
        var a = list.ToList();
        return string.Join("; ", a.Take(max)) + (a.Count > max ? $"; ... (+{a.Count - max})" : "");
    }

    private static string F(double d) => d.ToString("0.##", CultureInfo.InvariantCulture);

    private static Clause Done(List<GroupResult> groups, string reason = "") =>
        new(groups.Count == 0 ? Verdicts.NotMeasurable
            : groups.Any(g => g.Verdict == Verdicts.Fail) ? Verdicts.Fail
            : groups.Any(g => g.Verdict != Verdicts.Pass) ? Verdicts.NotMeasurable
            : Verdicts.Pass, groups, groups.Count == 0 && reason.Length == 0 ? "không có nhóm dữ liệu nào" : reason);

    // ---- các phép chấm --------------------------------------------------------------------------------

    private Clause Count(Call c, IReadOnlyList<string> sheets)
    {
        if (c.Args.Count != 1) throw new UnsupportedException("đếm(<nguồn>) cần đúng 1 đối số");
        var m = Tail(TailCount, c);
        var key = m.Groups[1].Value; var op = m.Groups[2].Value; var num = ParseNum(m.Groups[3].Value);
        var src = Source(c.Args[0]);
        var g = Groups(src, key, sheets);
        if (g.Count == 0) throw new MissingException($"không có nhóm '{key}' nào");
        return Done(g.Select(kv =>
        {
            var n = kv.Value.Count;
            return new GroupResult(kv.Key, TestOp(n, op, num) ? Verdicts.Pass : Verdicts.Fail, $"{op} {m.Groups[3].Value}", n.ToString(CultureInfo.InvariantCulture), $"đếm({c.Args[0]}) = {n}");
        }).ToList());
    }

    private Clause Intersect(Call c, IReadOnlyList<string> sheets)
    {
        if (c.Args.Count != 2) throw new UnsupportedException("giao(A, B) cần 2 đối số");
        var m = Tail(TailIntersect, c);
        var key = m.Groups[1].Value;
        var op = m.Groups[2].Success ? m.Groups[2].Value : "=";
        var num = m.Groups[3].Success ? ParseNum(m.Groups[3].Value) : 0;
        double? tol = m.Groups[4].Success ? ParseNum(m.Groups[4].Value) : null;
        var a = Source(c.Args[0]);
        var b = Source(c.Args[1]);
        var ga = Groups(a, key, sheets);
        var gb = Groups(b, key, Array.Empty<string>());
        // giao một tập với chính nó: mỗi cặp chỉ tính một lần, bỏ cặp phần tử với chính nó
        var self = a.File == b.File && string.Equals(a.Path, b.Path, StringComparison.Ordinal);
        int noBox = 0, pairs = 0;
        var res = new List<GroupResult>();
        foreach (var (g, xs) in ga)
        {
            var bs = gb.GetValueOrDefault(g) ?? new();
            var area = 0.0; var hits = new List<string>();
            for (var i = 0; i < xs.Count; i++)
            {
                if (BoxOf(xs[i].Node) is not { } bx) { noBox++; continue; }
                for (var j = self ? i + 1 : 0; j < bs.Count; j++)
                {
                    if (ReferenceEquals(xs[i].Node, bs[j].Node)) continue;
                    if (BoxOf(bs[j].Node) is not { } by) { noBox++; continue; }
                    pairs++;
                    var w = Math.Min(bx.X2, by.X2) - Math.Max(bx.X1, by.X1);
                    var h = Math.Min(bx.Y2, by.Y2) - Math.Max(bx.Y1, by.Y1);
                    if (w > 1e-6 && h > 1e-6) { area += w * h; hits.Add($"{Label(xs[i].Node)} x {Label(bs[j].Node)}"); }
                }
            }
            var ok = tol is { } t ? TestOpTol(area, op, num, t) : TestOp(Math.Round(area, 6), op, num);
            res.Add(new GroupResult(g, ok ? Verdicts.Pass : Verdicts.Fail, $"{op} {F(num)} mm²", F(area) + " mm²",
                $"giao({c.Args[0]} ×{xs.Count}, {c.Args[1]} ×{bs.Count}) = {F(area)} mm²{(hits.Count > 0 ? " (" + Short(hits, 3) + ")" : "")}"));
        }
        var note = noBox > 0 ? $"{noBox} phần tử thiếu bbox (bỏ qua)" : "";
        if (pairs == 0 && noBox > 0) throw new MissingException($"không có bbox để tính; {note}");
        return Done(res, note);
    }

    private Clause Inside(Call c, IReadOnlyList<string> sheets)
    {
        if (c.Args.Count != 2) throw new UnsupportedException("trong(A, vùng) cần 2 đối số");
        var m = Tail(TailInside, c);
        var key = m.Groups[1].Value;
        var tol = m.Groups[2].Success ? ParseNum(m.Groups[2].Value) : DefaultTol;
        var a = Source(c.Args[0]);
        var b = Source(c.Args[1]);
        var ga = Groups(a, key, sheets);
        var gb = Groups(b, key, Array.Empty<string>());
        var n = 0;
        var res = new List<GroupResult>();
        foreach (var (g, xs) in ga)
        {
            var zones = (gb.GetValueOrDefault(g) ?? new()).Select(z => BoxOf(z.Node)).OfType<Box>().ToList();
            var boxed = xs.Select(x => (x, b: BoxOf(x.Node))).Where(p => p.b is not null).ToList();
            if (boxed.Count == 0) continue;
            n += boxed.Count;
            var outside = boxed.Where(p => !zones.Any(z => p.b!.Value.Inside(z, tol))).Select(p => $"{Label(p.x.Node)} {p.b}").ToList();
            res.Add(new GroupResult(g, outside.Count == 0 ? Verdicts.Pass : Verdicts.Fail,
                $"trong {(zones.Count == 1 ? zones[0].ToString() : zones.Count + " vùng")} ± {F(tol)}",
                outside.Count == 0 ? "trong" : $"{outside.Count} ngoài: {Short(outside, 3)}",
                zones.Count == 0 ? $"nhóm {g} không có vùng {c.Args[1]}" : $"trong({c.Args[0]} ×{boxed.Count}, {c.Args[1]} ×{zones.Count}) ± {F(tol)}"));
        }
        if (n == 0) throw new MissingException("không có phần tử có bbox");
        return Done(res);
    }

    private Clause Equal(Call c)
    {
        if (c.Args.Count != 2) throw new UnsupportedException("bằng(giá trị, kỳ vọng) cần 2 đối số");
        if (Regex.IsMatch(Text.Fold(c.Rest), @"\bcho\b")) throw new MissingException("sai số/ngưỡng còn \"chờ\" người duyệt");
        var m = Tail(TailTol, c);
        var tol = m.Groups[1].Success ? ParseNum(m.Groups[1].Value) : DefaultTol;
        var actual = Scalar(c.Args[0]);
        var expected = Scalar(c.Args[1]);
        bool ok;
        if (double.TryParse(actual, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) && double.TryParse(expected, NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
            ok = Math.Abs(x - y) <= tol + 1e-9;
        else ok = string.Equals(actual, expected, StringComparison.Ordinal);
        return Done(new() { new GroupResult("*", ok ? Verdicts.Pass : Verdicts.Fail, expected + (tol > 0 ? $" ± {F(tol)}" : ""), actual,
            $"bằng({c.Args[0]} = '{actual}', {c.Args[1]} = '{expected}') sai số {F(tol)}") });
    }

    private static readonly Regex NumLit = new(@"^-?\d+(?:\.\d+)?$");
    private static readonly Regex ConfigCall = new(@"^(?:cài đặt|cai dat)\s*\(\s*([^,]+),\s*([^)]+)\)$", RegexOptions.IgnoreCase);

    /// <summary>Số, "chuỗi", cài đặt(file, khoá), hoặc đường dẫn ra đúng một giá trị vô hướng (khác → AGENT).</summary>
    private string Scalar(string arg)
    {
        var s = arg.Trim();
        if (NumLit.IsMatch(s)) return s;
        if (s.StartsWith('"')) return Unquote(s);
        if (ConfigCall.Match(s) is { Success: true } cfg) return Str(Config(cfg.Groups[1].Value, cfg.Groups[2].Value));
        // định danh trần không trỏ tới dữ liệu nào → chuỗi (phần mở rộng; rule-eval ra AGENT)
        if (!HasArrow(s) && _lastJson is null && IsIdent(s) && Candidates(s).Count == 0) return s;
        var src = Source(s);
        if (src.Items.Count != 1 || src.Items[0].Node is JsonObject or JsonArray)
            throw new UnsupportedException($"'{s}' không ra một giá trị ({src.Items.Count})");
        return Str(src.Items[0].Node);
    }

    private JsonNode? Config(string file, string key)
    {
        var p = data.FindConfig(file) ?? throw new MissingException($"không thấy file cấu hình {file.Trim()} trong args của các step");
        var n = data.Json(Use(p));
        foreach (var part in key.Trim().Split('.')) n = n is JsonObject o && Has(o, part) ? Get(o, part) : null;
        return n ?? throw new MissingException($"{file.Trim()} không có khoá {key.Trim()}");
    }

    private Clause Order(Call c)
    {
        if (c.Args.Count != 1) throw new UnsupportedException("thứ tự(<danh sách>) cần 1 đối số");
        var m = Tail(TailOrder, c);
        var key = m.Groups[1].Value;
        var lst = m.Groups[2].Value.Trim();
        List<string> want;
        if (ConfigCall.Match(lst) is { Success: true } cfg)
            want = Config(cfg.Groups[1].Value, cfg.Groups[2].Value) is JsonArray ca ? ca.Select(Str).ToList() : throw new MissingException("cài đặt không phải danh sách");
        else
        {
            if (lst.StartsWith('[') && lst.EndsWith(']')) lst = lst[1..^1];
            want = SplitTop(lst, ',').Select(Unquote).ToList();
        }
        var src = Source(c.Args[0]);
        var g = Groups(src, key, Array.Empty<string>());
        return Done(g.Select(kv =>
        {
            var got = kv.Value.Select(i => Str(i.Node)).ToList();
            var ok = got.SequenceEqual(want, StringComparer.Ordinal);
            return new GroupResult(kv.Key, ok ? Verdicts.Pass : Verdicts.Fail, string.Join(", ", want), string.Join(", ", got.Take(30)), $"thứ tự({c.Args[0]}){(key.Length > 0 ? " theo " + key : "")}");
        }).ToList());
    }

    private static readonly Regex KhoaArg = new(@"^(?:khoá|khóa|khoa)\s+(.+)$", RegexOptions.IgnoreCase);
    private static readonly Regex TruongArg = new(@"^(?:trường|truong)\s+(.+)$", RegexOptions.IgnoreCase);

    private Clause MatchBaseline(Call c)
    {
        if (c.Args.Count < 1) throw new UnsupportedException("khớp baseline(<file>, khoá <k>, trường <f…>) thiếu đối số");
        string? key = null; var fields = new List<string>(); var inFields = false;
        foreach (var a in c.Args.Skip(1))
        {
            Match am;
            if ((am = KhoaArg.Match(a)).Success) { key = am.Groups[1].Value.Trim(); inFields = false; }
            else if ((am = TruongArg.Match(a)).Success) { fields.Add(am.Groups[1].Value.Trim()); inFields = true; }
            else if (inFields) fields.Add(a);
            else throw new UnsupportedException($"đối số '{a}'");
        }
        if (key is null || fields.Count == 0) throw new UnsupportedException("khớp baseline cần 'khoá <k>' và 'trường <f…>'");
        var tm = Tail(TailTol, c);
        double? tol = tm.Groups[1].Success ? ParseNum(tm.Groups[1].Value) : DefaultTol > 0 ? DefaultTol : null;

        var arg = c.Args[0].Trim(); var fname = arg; string? path = null;
        foreach (var sep in new[] { "→", "->" })
        {
            var i = arg.IndexOf(sep, StringComparison.Ordinal);
            if (i >= 0) { fname = arg[..i].Trim(); path = arg[(i + sep.Length)..].Trim(); break; }
        }
        var cur = data.FindData(fname) ?? throw new MissingException($"thiếu file dữ liệu {fname}");
        var bas = data.FindBaseline(fname) ?? data.FindBaseline(Path.GetFileName(fname))
            ?? throw new MissingException($"chưa có baseline\\{fname} (lần chạy đầu là lần lấy baseline)");
        Dictionary<string, List<JsonNode>> Map(string f)
        {
            var root = Json(f);
            var objs = path is not null ? InvokePath(root, ParsePath(path)).Select(i => i.Node).OfType<JsonNode>().ToList() : FindKeyed(root, key);
            var map = new Dictionary<string, List<JsonNode>>(StringComparer.Ordinal);
            foreach (var o in objs)
            {
                var kv = o is JsonObject jo && Has(jo, key) ? Str(Get(jo, key)) : "";
                if (!map.TryGetValue(kv, out var l)) map[kv] = l = new();
                l.Add(o);
            }
            return map;
        }
        var bm = Map(bas);
        var cm = Map(cur);
        var res = new List<GroupResult>();
        foreach (var k in bm.Keys.Concat(cm.Keys.Where(k => !bm.ContainsKey(k))))
        {
            if (!cm.TryGetValue(k, out var n)) { res.Add(new(k, Verdicts.Fail, "có (baseline)", "thiếu", $"{key}={k} có ở baseline, không có ở lần chạy")); continue; }
            if (!bm.TryGetValue(k, out var o)) { res.Add(new(k, Verdicts.Fail, "không có (baseline)", "thừa", $"{key}={k} mới so với baseline")); continue; }
            var diffs = new List<string>();
            foreach (var f in fields)
            {
                var bv = FieldVal(o, f); var cv = FieldVal(n, f);
                if (!Same(bv, cv, tol)) diffs.Add($"{f}: '{bv}' → '{cv}'");
            }
            res.Add(new(k, diffs.Count == 0 ? Verdicts.Pass : Verdicts.Fail, "như baseline", diffs.Count == 0 ? "khớp" : string.Join("; ", diffs),
                $"khớp baseline({fname}) {key}={k} ({string.Join(", ", fields)})"));
        }
        return Done(res);

        static bool Same(string a, string b, double? tol)
        {
            if (string.Equals(a, b, StringComparison.Ordinal)) return true;
            if (tol is not { } t) return false;
            if (double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) && double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
                return Math.Abs(x - y) <= t;
            // phần mở rộng: chuỗi nhiều số (bbox, "a | b") — so từng số với sai số
            var na = Regex.Matches(a, @"-?\d+(?:\.\d+)?(?:[eE][-+]?\d+)?").Select(m => double.Parse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture)).ToList();
            var nb = Regex.Matches(b, @"-?\d+(?:\.\d+)?(?:[eE][-+]?\d+)?").Select(m => double.Parse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture)).ToList();
            return na.Count > 0 && na.Count == nb.Count && na.Zip(nb).All(p => Math.Abs(p.First - p.Second) <= t)
                && Regex.Replace(a, @"-?\d+(?:\.\d+)?(?:[eE][-+]?\d+)?", "#") == Regex.Replace(b, @"-?\d+(?:\.\d+)?(?:[eE][-+]?\d+)?", "#");
        }
    }

    /// <summary>Các object có thuộc tính k ở độ sâu nhỏ nhất (≤ 12).</summary>
    private static List<JsonNode> FindKeyed(JsonNode? root, string k)
    {
        var level = ToList(root).OfType<JsonNode>().ToList();
        for (var d = 0; d < 12 && level.Count > 0; d++)
        {
            var hit = level.Where(n => Has(n, k)).ToList();
            if (hit.Count > 0) return hit;
            level = level.OfType<JsonObject>().SelectMany(o => o.SelectMany(p => ToList(p.Value))).Where(n => n is JsonObject).OfType<JsonNode>().ToList();
        }
        return new();
    }

    private static string FieldVal(List<JsonNode> items, string f)
    {
        if (Regex.IsMatch(Text.Fold(f), @"^dem\s*\(")) return items.Count.ToString(CultureInfo.InvariantCulture);
        var segs = ParsePath(f);
        return string.Join(" | ", items.SelectMany(it => InvokePath(it, segs)).Select(i => Str(i.Node)));
    }

    private Clause LogHas(Call c, bool want)
    {
        if (c.Args.Count != 2) throw new UnsupportedException("có dòng(<log>, \"<regex>\") cần 2 đối số");
        var p = data.FindData(c.Args[0]) ?? throw new MissingException($"thiếu log {c.Args[0]}");
        Use(p);
        var rx = new Regex(Unquote(c.Args[1]));
        var lines = File.ReadAllText(p).Replace("\r\n", "\n").Split('\n');
        var n = lines.Count(l => rx.IsMatch(l));
        var first = Array.FindIndex(lines, l => rx.IsMatch(l));
        var ok = want ? n > 0 : n == 0;
        return Done(new() { new GroupResult("*", ok ? Verdicts.Pass : Verdicts.Fail, want ? "có dòng" : "không có dòng",
            first >= 0 ? $"khớp {n} dòng, đầu tiên dòng {first + 1}: {Text.Clip(lines[first].Trim(), 120)}" : "không có", $"{c.Name}({c.Args[0]}, {c.Args[1]})") });
    }

    private Clause StepDone(Call c)
    {
        var code = c.Args.FirstOrDefault()?.Trim() ?? throw new UnsupportedException("step xong(<mã step>) thiếu mã");
        var p = data.FindData("step_status.json") ?? throw new MissingException("thiếu data\\step_status.json");
        var st = Json(p);
        var steps = (st?["steps"] as JsonArray)?.ToList() ?? new();
        if (st?["ok"] is JsonValue okv && okv.TryGetValue<bool>(out var okb) && !okb || steps.Count == 0)
            throw new MissingException($"tracking không đọc được: {Str(st?["message"])}");
        var hit = steps.LastOrDefault(s => Str(s?["stepCode"]) == code);
        if (hit is null) return Done(new() { new GroupResult("*", Verdicts.Fail, "SUCCEEDED", "không có", $"không có step {code} trên tracking") });
        var status = Str(hit["status"]);
        return Done(new() { new GroupResult("*", status == "SUCCEEDED" ? Verdicts.Pass : Verdicts.Fail, "SUCCEEDED", status, $"step_status.json {code}") });
    }
}
