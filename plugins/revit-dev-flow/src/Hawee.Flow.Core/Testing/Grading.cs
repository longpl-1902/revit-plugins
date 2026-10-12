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
    public string DataDir => Path.Combine(runDir, "data");
    public string BaselineDir => Path.Combine(suiteDir, "baseline");
    public List<string> FilesRead { get; } = new();

    public string? FindData(string rel)
    {
        rel = rel.Trim().Trim('`', '\'', '"').Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        foreach (var root in new[] { DataDir, Path.Combine(DataDir, "checkpoints") })
        {
            var p = Path.Combine(root, rel);
            if (File.Exists(p)) return Track(p);
        }
        // chỉ tên file: tìm trong data\ (kể cả checkpoints\<step>\)
        if (!rel.Contains(Path.DirectorySeparatorChar) && Directory.Exists(DataDir))
        {
            var hit = Directory.EnumerateFiles(DataDir, rel, SearchOption.AllDirectories).FirstOrDefault();
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
/// Bộ chấm dòng "Máy chấm" của rules.md (suite-format.md §2.1). Tất định: cùng dữ liệu → cùng verdict.
/// Không chấm được (cú pháp lạ, dữ liệu không đúng hình) → NOT_MEASURABLE kèm lý do, hoặc AGENT khi cần mắt người/agent (ảnh).
/// </summary>
public sealed class Grader(GradingData data)
{
    private sealed record Item(JsonNode Node, Dictionary<string, string> Ctx);

    public GradeResult Grade(TestRule rule, IReadOnlyList<string> sheets)
    {
        var expr = rule.Grader.Trim().Trim('`');
        if (rule.Pending) return Single(Verdicts.NotMeasurable, "người chưa duyệt ngưỡng / kỳ vọng ('chờ')");
        if (expr.Length == 0) return Single(Verdicts.Agent, "luật không có Máy chấm — agent chấm theo Kiểm / Cách đo");
        try
        {
            var call = Parse(expr);
            return call.Name switch
            {
                "anh" => Single(Verdicts.Agent, "luật nhìn ảnh: " + string.Join(", ", call.Args)),
                "dem" => Count(call, sheets),
                "giao" => Intersect(call, sheets),
                "trong" => Inside(call, sheets),
                "bang" => Equal(call),
                "thu tu" => Order(call),
                "khop baseline" => MatchBaseline(call),
                "co dong" => LogHas(call, true),
                "khong co dong" => LogHas(call, false),
                "step xong" => StepDone(call),
                _ => Single(Verdicts.Agent, $"Máy chấm '{call.Name}' chưa hỗ trợ — agent chấm"),
            };
        }
        catch (GradeException ex) { return Single(Verdicts.NotMeasurable, ex.Message); }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        {
            return Single(Verdicts.NotMeasurable, $"dữ liệu không đọc được: {ex.Message}");
        }

        GradeResult Single(string v, string reason) => new(v, new List<GroupResult>(), reason, data.FilesRead.ToList());
    }

    private sealed class GradeException(string m) : Exception(m);

    // ---- cú pháp ---------------------------------------------------------------------------------------

    internal sealed record Call(string Name, List<string> Args, string GroupBy, string Op, string Rhs, double? Tol);

    /// <summary>"giao(View, Keyplan) theo SheetNumber = 0" → Call(giao, [View, Keyplan], SheetNumber, "=", "0", null).</summary>
    internal static Call Parse(string expr)
    {
        var open = expr.IndexOf('(');
        if (open < 0) throw new GradeException($"Máy chấm không có '(': {expr}");
        var name = Regex.Replace(Text.Fold(expr[..open]).Trim(), @"\s+", " ");
        var depth = 0; var close = -1; var inQ = false;
        for (var i = open; i < expr.Length; i++)
        {
            var c = expr[i];
            if (c == '"') inQ = !inQ;
            if (inQ) continue;
            if (c is '(' or '[') depth++;
            else if (c is ')' or ']') { depth--; if (depth == 0 && c == ')') { close = i; break; } }
        }
        if (close < 0) throw new GradeException($"thiếu ')' trong: {expr}");
        var args = SplitTop(expr[(open + 1)..close]);
        var tail = expr[(close + 1)..].Trim();
        string groupBy = "", op = "", rhs = ""; double? tol = null;
        var tm = Regex.Match(tail, @"±\s*([\d.,]+)");
        if (tm.Success) { tol = Num(tm.Groups[1].Value); tail = tail.Remove(tm.Index, tm.Length).Trim(); }
        var gm = Regex.Match(tail, @"^theo\s+([^\s=<>≠≤≥!]+)");
        if (gm.Success) { groupBy = gm.Groups[1].Value; tail = tail[gm.Length..].Trim(); }
        var om = Regex.Match(tail, @"^(=|==|≠|!=|≤|<=|≥|>=|<|>)\s*(.*)$");
        if (om.Success) { op = om.Groups[1].Value; rhs = om.Groups[2].Value.Trim().TrimEnd('.', ';').Trim(); }
        rhs = Regex.Replace(rhs, @"\s*(mm²|mm2|mm|m²)$", "");
        return new Call(name, args, groupBy, op, rhs, tol);
    }

    private static List<string> SplitTop(string s)
    {
        var parts = new List<string>();
        var depth = 0; var inQ = false; var start = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '"') inQ = !inQ;
            if (inQ) continue;
            if (c is '(' or '[') depth++;
            else if (c is ')' or ']') depth--;
            else if (c == ',' && depth == 0) { parts.Add(s[start..i].Trim()); start = i + 1; }
        }
        parts.Add(s[start..].Trim());
        return parts.Where(p => p.Length > 0).ToList();
    }

    private static double Num(string s) => double.Parse(s.Replace(',', '.'), CultureInfo.InvariantCulture);

    private static bool Compare(double actual, string op, double expected, double tol) => op switch
    {
        "" or "=" or "==" => Math.Abs(actual - expected) <= tol,
        "≠" or "!=" => Math.Abs(actual - expected) > tol,
        "<" => actual < expected + tol,
        "≤" or "<=" => actual <= expected + tol,
        ">" => actual > expected - tol,
        "≥" or ">=" => actual >= expected - tol,
        _ => throw new GradeException($"phép so '{op}' không hỗ trợ"),
    };

    // ---- nguồn dữ liệu --------------------------------------------------------------------------------

    /// <summary>Nguồn "file.json → path[filter]" hoặc tên loại phần tử (tìm trong nguồn mặc định).</summary>
    private List<Item> Source(string src, string? defaultFile = null)
    {
        var parts = src.Split(new[] { "→", "->" }, 2, StringSplitOptions.TrimEntries);
        string file, path;
        if (parts.Length == 2) { file = parts[0]; path = parts[1]; }
        else if (Regex.IsMatch(parts[0], @"\.(json)\b", RegexOptions.IgnoreCase)) { file = parts[0]; path = ""; }
        else
        {
            // tên trần, vd. "Keyplan": phần tử có type/category/role/kind/name = Keyplan trong file mặc định
            file = defaultFile ?? DefaultComponentsFile() ?? throw new GradeException($"'{src}' không ghi file và không tìm thấy file toạ độ component trong data\\");
            return AllObjects(Load(file)).Where(it => TypeOf(it.Node).Equals(parts[0].Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        }
        return Eval(Load(file), path);
    }

    private JsonNode Load(string file)
    {
        var p = data.FindData(file) ?? throw new GradeException($"thiếu dữ liệu {file} trong data\\ (điểm kiểm chưa thu?)");
        return GradingData.LoadJson(p) ?? throw new GradeException($"{file} rỗng");
    }

    private string? DefaultComponentsFile()
    {
        if (!Directory.Exists(data.DataDir)) return null;
        var f = Directory.EnumerateFiles(data.DataDir, "*component*.json", SearchOption.AllDirectories).OrderBy(x => x).FirstOrDefault();
        return f is null ? null : Path.GetRelativePath(data.DataDir, f);
    }

    private static readonly string[] TypeKeys = { "type", "Type", "kind", "Kind", "category", "Category", "role", "Role", "componentType", "ComponentType", "name", "Name" };

    private static string TypeOf(JsonNode n)
    {
        if (n is not JsonObject o) return "";
        foreach (var k in TypeKeys) if (o[k] is JsonValue v && v.TryGetValue<string>(out var s)) return s;
        return "";
    }

    /// <summary>Đường dẫn "a.b[*].c[type=X,lvl!=0]" — mảng tự trải, trường vô hướng của cha truyền xuống (để nhóm theo SheetNumber).</summary>
    private static List<Item> Eval(JsonNode root, string path)
    {
        var cur = new List<Item> { new(root, new Dictionary<string, string>()) };
        path = path.Trim().TrimStart('$').TrimStart('.');
        if (path.Length > 0)
            foreach (var seg in Regex.Matches(path, @"([^.\[]+)?(\[[^\]]*\])?").Where(m => m.Length > 0))
            {
                var name = seg.Groups[1].Value.Trim();
                var filter = seg.Groups[2].Success ? seg.Groups[2].Value[1..^1].Trim() : "";
                var next = new List<Item>();
                foreach (var it in Flatten(cur))
                {
                    var node = name.Length == 0 ? it.Node : it.Node is JsonObject o ? Get(o, name) : null;
                    if (node is null) continue;
                    var ctx = Inherit(it);
                    if (node is JsonArray arr) next.AddRange(arr.Where(x => x is not null).Select(x => new Item(x!, ctx)));
                    else next.Add(new Item(node, ctx));
                }
                cur = filter is "" or "*" ? next : next.Where(i => Matches(i, filter)).ToList();
            }
        return Flatten(cur).ToList();
    }

    private static JsonNode? Get(JsonObject o, string key) =>
        o[key] ?? o.FirstOrDefault(kv => kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;

    private static IEnumerable<Item> Flatten(IEnumerable<Item> items)
    {
        foreach (var it in items)
            if (it.Node is JsonArray a) { foreach (var x in a.Where(x => x is not null)) yield return new Item(x!, it.Ctx); }
            else yield return it;
    }

    private static Dictionary<string, string> Inherit(Item it)
    {
        var d = new Dictionary<string, string>(it.Ctx, StringComparer.OrdinalIgnoreCase);
        if (it.Node is JsonObject o)
            foreach (var (k, v) in o)
                if (v is JsonValue jv) d[k] = jv.ToString();
        return d;
    }

    private static IEnumerable<Item> AllObjects(JsonNode root)
    {
        var stack = new Stack<Item>();
        stack.Push(new Item(root, new Dictionary<string, string>()));
        while (stack.Count > 0)
        {
            var it = stack.Pop();
            if (it.Node is JsonObject o)
            {
                yield return it;
                var ctx = Inherit(it);
                foreach (var (_, v) in o) if (v is JsonObject or JsonArray) stack.Push(new Item(v!, ctx));
            }
            else if (it.Node is JsonArray a)
                foreach (var x in a.Reverse().Where(x => x is not null)) stack.Push(new Item(x!, it.Ctx));
        }
    }

    private static bool Matches(Item it, string filter)
    {
        foreach (var cond in filter.Split(new[] { ',', '&' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var m = Regex.Match(cond, @"^([^!=]+?)\s*(!=|=)\s*(.+)$");
            if (!m.Success) { if (Field(it, cond) is null) return false; continue; }
            var v = Field(it, m.Groups[1].Value.Trim()) ?? "";
            var eq = v.Equals(m.Groups[3].Value.Trim().Trim('"', '\''), StringComparison.OrdinalIgnoreCase);
            if (m.Groups[2].Value == "=" ? !eq : eq) return false;
        }
        return true;
    }

    private static string? Field(Item it, string key)
    {
        if (it.Node is JsonObject o && Get(o, key) is JsonValue v) return v.ToString();
        return it.Ctx.TryGetValue(key, out var s) ? s : null;
    }

    private static Dictionary<string, List<Item>> GroupBy(IEnumerable<Item> items, string key) =>
        key.Length == 0
            ? new Dictionary<string, List<Item>> { ["*"] = items.ToList() }
            : items.GroupBy(i => Field(i, key) ?? "(không có " + key + ")").ToDictionary(g => g.Key, g => g.ToList());

    /// <summary>Nhóm cần chấm: các sheet trong "Áp dụng cho" (nhóm vắng mặt vẫn chấm, đếm = 0) hoặc mọi nhóm có dữ liệu.</summary>
    private static IEnumerable<string> Groups(Dictionary<string, List<Item>> g, IReadOnlyList<string> sheets, string key,
        Dictionary<string, List<Item>>? other = null) =>
        key.Length > 0 && sheets.Count > 0 ? sheets
            : g.Keys.Union(other?.Keys.Where(k => k != "*") ?? Enumerable.Empty<string>()).OrderBy(k => k, StringComparer.Ordinal);

    internal record struct Box(double X1, double Y1, double X2, double Y2)
    {
        public double Area => Math.Max(0, X2 - X1) * Math.Max(0, Y2 - Y1);
        public static double Overlap(Box a, Box b) =>
            Math.Max(0, Math.Min(a.X2, b.X2) - Math.Max(a.X1, b.X1)) * Math.Max(0, Math.Min(a.Y2, b.Y2) - Math.Max(a.Y1, b.Y1));
        public bool Inside(Box o, double tol) => X1 >= o.X1 - tol && Y1 >= o.Y1 - tol && X2 <= o.X2 + tol && Y2 <= o.Y2 + tol;
        public override string ToString() => FormattableString.Invariant($"[{X1:0.#},{Y1:0.#} – {X2:0.#},{Y2:0.#}]");
    }

    /// <summary>bbox từ: {minX,minY,maxX,maxY} · {Min:{X,Y},Max:{X,Y}} · {x,y,width,height} · [x1,y1,x2,y2]; trong item hoặc trường bbox/bounds/box.</summary>
    internal static Box? BoxOf(JsonNode? n)
    {
        if (n is JsonObject o)
        {
            foreach (var k in new[] { "bbox", "BBox", "Bbox", "boundingBox", "BoundingBox", "bounds", "Bounds", "box", "Box", "outline", "Outline" })
                if (Get(o, k) is { } inner && BoxOf(inner) is { } b) return b;
            double? D(params string[] ks)
            {
                foreach (var k in ks) if (Get(o, k) is JsonValue v && double.TryParse(v.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
                return null;
            }
            if (D("minX", "MinX", "xMin", "left", "Left") is { } x1 && D("minY", "MinY", "yMin", "bottom", "Bottom") is { } y1
                && D("maxX", "MaxX", "xMax", "right", "Right") is { } x2 && D("maxY", "MaxY", "yMax", "top", "Top") is { } y2)
                return new Box(Math.Min(x1, x2), Math.Min(y1, y2), Math.Max(x1, x2), Math.Max(y1, y2));
            if (Get(o, "min") is JsonObject mn && Get(o, "max") is JsonObject mx && BoxOf(new JsonArray(Get(mn, "x")?.DeepClone(), Get(mn, "y")?.DeepClone(), Get(mx, "x")?.DeepClone(), Get(mx, "y")?.DeepClone())) is { } mm) return mm;
            if (D("x", "X") is { } x && D("y", "Y") is { } y && D("width", "Width", "w", "W") is { } w && D("height", "Height", "h", "H") is { } h)
                return new Box(x, y, x + w, y + h);
        }
        if (n is JsonArray a && a.Count == 4 && a.All(v => v is JsonValue))
        {
            var v = a.Select(e => double.Parse(e!.ToString(), CultureInfo.InvariantCulture)).ToArray();
            return new Box(Math.Min(v[0], v[2]), Math.Min(v[1], v[3]), Math.Max(v[0], v[2]), Math.Max(v[1], v[3]));
        }
        return null;
    }

    private static Box NeedBox(Item it, string what) =>
        BoxOf(it.Node) ?? throw new GradeException($"{what}: phần tử không có bbox đọc được ({Text.Clip(it.Node.ToJsonString(), 80)})");

    // ---- các phép chấm --------------------------------------------------------------------------------

    private GradeResult Count(Call c, IReadOnlyList<string> sheets)
    {
        if (c.Args.Count != 1) throw new GradeException("đếm(<nguồn>) cần đúng 1 tham số");
        if (c.Op.Length == 0) throw new GradeException("đếm(...) thiếu phép so, vd. '= 1'");
        var items = Source(c.Args[0]);
        var g = GroupBy(items, c.GroupBy);
        var expected = Num(c.Rhs);
        var res = Groups(g, sheets, c.GroupBy).Select(k =>
        {
            var n = g.TryGetValue(k, out var l) ? l.Count : 0;
            var ok = Compare(n, c.Op, expected, c.Tol ?? 0);
            return new GroupResult(k, ok ? Verdicts.Pass : Verdicts.Fail, $"{c.Op} {c.Rhs}", n.ToString(), $"đếm({c.Args[0]}) = {n}");
        }).ToList();
        return Done(res);
    }

    private GradeResult Intersect(Call c, IReadOnlyList<string> sheets)
    {
        if (c.Args.Count != 2) throw new GradeException("giao(A, B) cần 2 tham số");
        var a = GroupBy(Source(c.Args[0]), c.GroupBy);
        var b = GroupBy(Source(c.Args[1]), c.GroupBy);
        var expected = c.Rhs.Length > 0 ? Num(c.Rhs) : 0;
        var res = Groups(a, sheets, c.GroupBy, b).Select(k =>
        {
            var la = a.GetValueOrDefault(k) ?? new();
            var lb = b.GetValueOrDefault(k) ?? new();
            if (la.Count == 0 || lb.Count == 0)
                return new GroupResult(k, Verdicts.NotMeasurable, $"{c.Op} {expected}", "—", $"thiếu {(la.Count == 0 ? c.Args[0] : c.Args[1])} trong nhóm {k}");
            var area = la.Sum(x => lb.Sum(y => Box.Overlap(NeedBox(x, c.Args[0]), NeedBox(y, c.Args[1]))));
            var ok = Compare(area, c.Op.Length > 0 ? c.Op : "=", expected, c.Tol ?? 0);
            return new GroupResult(k, ok ? Verdicts.Pass : Verdicts.Fail, $"{(c.Op.Length > 0 ? c.Op : "=")} {expected} mm²",
                area.ToString("0.##", CultureInfo.InvariantCulture) + " mm²",
                $"giao({c.Args[0]}{Boxes(la)}, {c.Args[1]}{Boxes(lb)}) = {area.ToString("0.##", CultureInfo.InvariantCulture)}");
        }).ToList();
        return Done(res);
    }

    private GradeResult Inside(Call c, IReadOnlyList<string> sheets)
    {
        if (c.Args.Count != 2) throw new GradeException("trong(A, vùng) cần 2 tham số");
        var a = GroupBy(Source(c.Args[0]), c.GroupBy);
        var region = GroupBy(Source(c.Args[1]), c.GroupBy);
        var tol = c.Tol ?? 0;
        var res = Groups(a, sheets, c.GroupBy, region).Select(k =>
        {
            var la = a.GetValueOrDefault(k) ?? new();
            var lr = region.GetValueOrDefault(k) ?? (region.Count == 1 && region.ContainsKey("*") ? region["*"] : new());
            if (la.Count == 0 || lr.Count == 0)
                return new GroupResult(k, Verdicts.NotMeasurable, "nằm trong", "—", $"thiếu {(la.Count == 0 ? c.Args[0] : c.Args[1])} trong nhóm {k}");
            var zone = NeedBox(lr[0], c.Args[1]);
            var outside = la.Select(x => NeedBox(x, c.Args[0])).Where(bx => !bx.Inside(zone, tol)).ToList();
            return new GroupResult(k, outside.Count == 0 ? Verdicts.Pass : Verdicts.Fail, $"trong {zone} ± {tol}",
                outside.Count == 0 ? "trong" : $"{outside.Count} ngoài: {string.Join(" ", outside.Take(3))}",
                $"trong({c.Args[0]}{Boxes(la)}, {c.Args[1]} {zone}) ± {tol}");
        }).ToList();
        return Done(res);
    }

    private GradeResult Equal(Call c)
    {
        if (c.Args.Count != 2) throw new GradeException("bằng(giá trị, kỳ vọng) cần 2 tham số");
        var actual = Value(c.Args[0]);
        var expected = Value(c.Args[1]);
        bool ok;
        if (double.TryParse(actual, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) && double.TryParse(expected, NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
            ok = Math.Abs(x - y) <= (c.Tol ?? 0);
        else ok = string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
        return Done(new() { new GroupResult("*", ok ? Verdicts.Pass : Verdicts.Fail, expected + (c.Tol is { } t ? $" ± {t}" : ""), actual, $"bằng({c.Args[0]} = {actual}, {c.Args[1]} = {expected})") });
    }

    private GradeResult Order(Call c)
    {
        if (c.Args.Count != 1) throw new GradeException("thứ tự(<danh sách>) cần 1 tham số");
        var src = c.Args[0];
        var items = Source(src);
        var actual = items.Select(i => i.Node is JsonValue v ? v.ToString() : (c.GroupBy.Length > 0 ? Field(i, c.GroupBy) : null) ?? i.Node.ToJsonString()).ToList();
        var expected = List(c.Rhs);
        var ok = actual.SequenceEqual(expected, StringComparer.OrdinalIgnoreCase);
        return Done(new() { new GroupResult("*", ok ? Verdicts.Pass : Verdicts.Fail, string.Join(", ", expected), string.Join(", ", actual.Take(30)), $"thứ tự({src}) theo {c.GroupBy}") });
    }

    private GradeResult MatchBaseline(Call c)
    {
        if (c.Args.Count < 3) throw new GradeException("khớp baseline(<file>, khoá <k>, trường <f…>) cần 3 tham số");
        var file = c.Args[0];
        var key = Regex.Replace(c.Args[1], @"^khoá\s+|^khoa\s+", "").Trim();
        var fields = Regex.Replace(c.Args[2], @"^trường\s+|^truong\s+", "").Split(new[] { ' ', '+', ';' }, StringSplitOptions.RemoveEmptyEntries);
        var basePath = data.FindBaseline(file) ?? data.FindBaseline(Path.GetFileName(file));
        if (basePath is null) return new(Verdicts.NotMeasurable, new(), $"chưa có baseline\\{file} (lần chạy đầu là lần lấy baseline)", data.FilesRead.ToList());
        var now = AllObjects(Load(file)).Where(i => Field(i, key) is not null).GroupBy(i => Field(i, key)!).ToDictionary(g => g.Key, g => g.First());
        var old = AllObjects(GradingData.LoadJson(basePath)!).Where(i => Field(i, key) is not null).GroupBy(i => Field(i, key)!).ToDictionary(g => g.Key, g => g.First());
        var tol = c.Tol ?? 0;
        var res = new List<GroupResult>();
        foreach (var k in old.Keys.Union(now.Keys).OrderBy(x => x, StringComparer.Ordinal))
        {
            if (!now.TryGetValue(k, out var n)) { res.Add(new(k, Verdicts.Fail, "có (baseline)", "thiếu", $"{key}={k} có ở baseline, không có ở lần chạy")); continue; }
            if (!old.TryGetValue(k, out var o)) { res.Add(new(k, Verdicts.Fail, "không có (baseline)", "mới", $"{key}={k} mới so với baseline")); continue; }
            var diffs = new List<string>();
            foreach (var f in fields)
            {
                var a = FieldOrBox(n, f); var b = FieldOrBox(o, f);
                if (!Same(a, b, tol)) diffs.Add($"{f}: {b} → {a}");
            }
            res.Add(new(k, diffs.Count == 0 ? Verdicts.Pass : Verdicts.Fail, "như baseline", diffs.Count == 0 ? "khớp" : string.Join("; ", diffs), $"khớp baseline({file}) {key}={k}"));
        }
        return Done(res);

        static string FieldOrBox(Item i, string f) => f.Equals("bbox", StringComparison.OrdinalIgnoreCase) && BoxOf(i.Node) is { } b ? b.ToString() : Field(i, f) ?? "";
        static bool Same(string a, string b, double tol)
        {
            if (a == b) return true;
            var na = Regex.Matches(a, @"-?\d+(\.\d+)?").Select(m => double.Parse(m.Value, CultureInfo.InvariantCulture)).ToList();
            var nb = Regex.Matches(b, @"-?\d+(\.\d+)?").Select(m => double.Parse(m.Value, CultureInfo.InvariantCulture)).ToList();
            return na.Count > 0 && na.Count == nb.Count && na.Zip(nb).All(p => Math.Abs(p.First - p.Second) <= tol);
        }
    }

    private GradeResult LogHas(Call c, bool shouldHave)
    {
        if (c.Args.Count != 2) throw new GradeException("có dòng(<log>, \"<regex>\") cần 2 tham số");
        var p = data.FindData(c.Args[0]) ?? throw new GradeException($"thiếu log {c.Args[0]} trong data\\");
        var rx = new Regex(c.Args[1].Trim().Trim('"'));
        var lines = File.ReadAllLines(p);
        var hit = Array.FindIndex(lines, l => rx.IsMatch(l));
        var ok = shouldHave ? hit >= 0 : hit < 0;
        return Done(new() { new GroupResult("*", ok ? Verdicts.Pass : Verdicts.Fail, shouldHave ? "có dòng" : "không có dòng",
            hit >= 0 ? $"dòng {hit + 1}: {Text.Clip(lines[hit].Trim(), 120)}" : "không có", $"{c.Name}({c.Args[0]}, {c.Args[1]})") });
    }

    private GradeResult StepDone(Call c)
    {
        var code = c.Args.FirstOrDefault()?.Trim() ?? throw new GradeException("step xong(<mã step>) thiếu mã");
        var p = data.FindData("step_status.json") ?? throw new GradeException("thiếu data\\step_status.json");
        var st = GradingData.LoadJson(p);
        var step = st?["steps"]?.AsArray().FirstOrDefault(s => s?["stepCode"]?.ToString() == code);
        var status = step?["status"]?.ToString() ?? "không có";
        return Done(new() { new GroupResult("*", status == "SUCCEEDED" ? Verdicts.Pass : Verdicts.Fail, "SUCCEEDED", status, $"step_status.json {code}") });
    }

    // ---- tiện ích ------------------------------------------------------------------------------------

    private string Value(string expr)
    {
        expr = expr.Trim();
        var cfg = Regex.Match(expr, @"^cài đặt\s*\(\s*([^,]+),\s*([^)]+)\)$|^cai dat\s*\(\s*([^,]+),\s*([^)]+)\)$");
        if (cfg.Success)
        {
            var file = cfg.Groups[1].Success && cfg.Groups[1].Value.Length > 0 ? cfg.Groups[1].Value : cfg.Groups[3].Value;
            var key = cfg.Groups[2].Success && cfg.Groups[2].Value.Length > 0 ? cfg.Groups[2].Value : cfg.Groups[4].Value;
            var p = data.FindConfig(file) ?? throw new GradeException($"không thấy file cấu hình {file} trong args của các step");
            JsonNode? n = GradingData.LoadJson(p);
            foreach (var part in key.Trim().Split('.')) n = n is JsonObject o ? Get(o, part) : null;
            return n?.ToString() ?? throw new GradeException($"{file} không có khoá {key}");
        }
        if (expr.Contains('→') || expr.Contains("->"))
        {
            var items = Source(expr);
            if (items.Count == 1) return items[0].Node.ToString();
            return items.Count.ToString(CultureInfo.InvariantCulture);
        }
        return expr.Trim('"', '\'');
    }

    private List<string> List(string rhs)
    {
        rhs = rhs.Trim();
        if (rhs.StartsWith("cài đặt") || rhs.StartsWith("cai dat") || rhs.Contains('→'))
        {
            var v = Value(rhs);
            try { if (JsonNode.Parse(v) is JsonArray a) return a.Select(x => x!.ToString()).ToList(); } catch (JsonException) { }
            return v.Split(',', StringSplitOptions.TrimEntries).ToList();
        }
        return rhs.Trim('[', ']').Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim('"', '\'')).ToList();
    }

    private static string Boxes(List<Item> items) => items.Count == 1 && BoxOf(items[0].Node) is { } b ? b.ToString() : $"×{items.Count}";

    private GradeResult Done(List<GroupResult> groups)
    {
        var v = groups.Count == 0 ? Verdicts.NotMeasurable
            : groups.Any(g => g.Verdict == Verdicts.Fail) ? Verdicts.Fail
            : groups.Any(g => g.Verdict != Verdicts.Pass) ? Verdicts.NotMeasurable
            : Verdicts.Pass;
        return new GradeResult(v, groups, groups.Count == 0 ? "không có nhóm dữ liệu nào" : "", data.FilesRead.ToList());
    }
}
