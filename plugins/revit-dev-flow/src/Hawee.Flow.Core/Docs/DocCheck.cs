using System.Text;
using System.Text.RegularExpressions;
using Hawee.Flow.Core.Infra;
using Hawee.Flow.Core.Knowledge;
using Hawee.Flow.Core.Tasks;

namespace Hawee.Flow.Core.Docs;

public sealed record Finding(string Level, string Where, string What);

/// <summary>
/// Kiểm spec/plan bằng máy: doc-lint (gom theo mã lỗi) + phần tĩnh của /analyze (tham chiếu file:dòng / rule / KI có thật,
/// AC phủ ma trận §7, bất biến có test, UNKNOWN còn mở). Báo cáo đầy đủ ghi analyze-&lt;ngày&gt;.md; trả về ≤ ~20 dòng.
/// Phần cần suy luận (giả định viết như sự thật, caller thiếu, mâu thuẫn) để agent independent-reviewer làm.
/// </summary>
public sealed class DocCheck(ProjectContext ctx, IProcRunner? runner = null)
{
    private readonly IProcRunner _run = runner ?? ProcRunner.Instance;
    private static readonly Regex FileRef = new(@"`?([\w./\\-]+\.(?:cs|xaml|csproj|ps1|json|ya?ml|md|sql|config|xml)):(\d+)(?:[-–](\d+))?`?", RegexOptions.Compiled);
    private static readonly Regex RuleRef = new(@"\b(?!DEC-|KI-|LL-|PT-|K-|ADR-)([A-Z]{2,}(?:-[A-Z0-9]+)*-\d{3})\b", RegexOptions.Compiled);
    private static readonly Regex KiRef = new(@"\b(KI-[A-Z0-9]+-\d{3,})\b", RegexOptions.Compiled);

    public const string VerdictPrefix = "Kết luận:", VerdictOk = "có thể trình cổng", VerdictNotOk = "chưa trình cổng được";

    public string Check(string? task, string which = "both")
    {
        which = which.Trim().ToLowerInvariant();
        if (which is not ("spec" or "plan" or "both")) throw new FlowException("which: spec | plan | both");
        var dir = ctx.ResolveTask(task);
        var docs = new List<string>();
        if (which is "spec" or "both") docs.Add(Path.Combine(dir, "spec.md"));
        if (which is "plan" or "both") docs.Add(Path.Combine(dir, "plan.md"));
        docs = docs.Where(File.Exists).ToList();
        if (docs.Count == 0) throw new FlowException("không có spec.md / plan.md trong task");

        var lint = DocLint(docs);
        var findings = new List<Finding>();
        foreach (var d in docs) findings.AddRange(Static(d, dir));

        var date = ctx.Clock.Now.ToString("yyyy-MM-dd");
        var report = Path.Combine(dir, $"analyze-{date}.md");
        var ok = lint.Errors.Count == 0 && findings.All(f => f.Level is "MEDIUM" or "LOW");
        var full = new StringBuilder();
        full.AppendLine($"# /analyze (máy) — {Path.GetFileName(dir)} — {date} — phạm vi: {which}");
        full.AppendLine();
        full.AppendLine($"{VerdictPrefix} {(ok ? VerdictOk : VerdictNotOk)}");   // gate_submit G1 đọc dòng này
        full.AppendLine();
        full.AppendLine($"doc-lint: {(lint.Errors.Count == 0 ? "OK" : lint.Errors.Count + " lỗi")} · {lint.Warnings.Count} cảnh báo");
        full.AppendLine();
        full.AppendLine("| # | Mức | Ở đâu | Phát hiện |").AppendLine("|---|---|---|---|");
        var n = 1;
        foreach (var f in findings.OrderBy(f => Rank(f.Level))) full.AppendLine($"| {n++} | {f.Level} | {Text.Cell(f.Where)} | {Text.Cell(f.What)} |");
        full.AppendLine().AppendLine("## doc-lint (nguyên văn)").AppendLine();
        foreach (var l in lint.Errors.Concat(lint.Warnings)) full.AppendLine("- " + l);
        full.AppendLine().AppendLine("Phần cần người/agent khác đọc (không máy làm): giả định viết như sự thật (§2/§3), caller thiếu, mâu thuẫn với quyết định §14/§1.2.");
        Text.Write(report, full.ToString());

        var sb = new StringBuilder();
        var counts = findings.GroupBy(f => f.Level).OrderBy(g => Rank(g.Key)).Select(g => $"{g.Count()} {g.Key}");
        sb.AppendLine($"doc-lint: {(lint.Errors.Count == 0 ? "OK" : lint.Errors.Count + " lỗi")}, {lint.Warnings.Count} cảnh báo · phân tích: {(findings.Count == 0 ? "không có phát hiện" : string.Join(" · ", counts))}");
        foreach (var g in Group(lint.Errors).Take(8)) sb.AppendLine("lint " + g);
        foreach (var f in findings.OrderBy(f => Rank(f.Level)).Take(10)) sb.AppendLine($"{f.Level} {f.Where}: {f.What}");
        if (findings.Count > 10) sb.AppendLine($"… còn {findings.Count - 10} phát hiện");
        sb.Append($"Báo cáo: {ctx.Rel(report)}");
        sb.Append(ok ? " · có thể trình cổng (sau rà độc lập theo tier)" : " · chưa trình cổng được");
        return sb.ToString();
    }

    private static int Rank(string level) => level switch { "CRITICAL" => 0, "HIGH" => 1, "MEDIUM" => 2, _ => 3 };

    private (List<string> Errors, List<string> Warnings) DocLint(List<string> docs)
    {
        var script = ctx.BaseTool("doc-lint.ps1");
        if (!File.Exists(script)) return (new List<string>(), new List<string> { "không có doc-lint.ps1" });
        var lines = docs.SelectMany(d => Ps.RunFile(_run, script, new[] { "-Path", d }, ctx.Root, TimeSpan.FromMinutes(3)).Lines).ToList();
        var errors = lines.Where(l => Regex.IsMatch(l, @"^\[[a-z0-9-]+\]") && !l.StartsWith("[doc-lint]")).Select(Shorten).Distinct().ToList();
        var warnings = lines.Where(l => l.StartsWith("WARN ")).Select(Shorten).Distinct().ToList();
        return (errors, warnings);
    }

    private string Shorten(string l) => l.Replace(ctx.Root.Replace('\\', '/') + "/", "").Replace("docs/tasks/", "").Trim();

    /// <summary>"[sec-empty] x/plan.md [plan T2]: muc 1.1 rong …" ×n → "[sec-empty] ×n: plan 1.1, 1.2 …"</summary>
    internal static IEnumerable<string> Group(IEnumerable<string> lines)
    {
        var rx = new Regex(@"^\[(?<code>[a-z0-9-]+)\]\s+(?<file>\S+)\s+\[(?<kind>\w+)[^\]]*\]:\s*(?<msg>.*)$");
        var parsed = lines.Select(l => (Line: l, M: rx.Match(l))).ToList();
        foreach (var g in parsed.Where(p => p.M.Success).GroupBy(p => p.M.Groups["code"].Value))
        {
            if (g.Count() == 1) { yield return g.First().Line; continue; }
            var items = g.Select(p =>
            {
                var msg = p.M.Groups["msg"].Value;
                var id = Regex.Match(msg, @"muc ([\d.]+)|\b(K\d+\+?|AC\d+|I\d+)\b|'([^']+)'");
                var key = !id.Success ? Text.Clip(msg, 30)
                    : id.Groups[1].Success ? id.Groups[1].Value : id.Groups[2].Success ? id.Groups[2].Value : Text.Clip(id.Groups[3].Value, 24);
                return p.M.Groups["kind"].Value + " " + key;
            }).Distinct().ToList();
            yield return $"[{g.Key}] ×{g.Count()}: {string.Join(", ", items.Take(14))}{(items.Count > 14 ? ", …" : "")}";
        }
        foreach (var p in parsed.Where(p => !p.M.Success)) yield return p.Line;
    }

    // ---- phần tĩnh của /analyze --------------------------------------------------------------------------

    private IEnumerable<Finding> Static(string docPath, string taskDir)
    {
        var name = Path.GetFileName(docPath);
        var doc = new MdDoc(docPath);
        var text = string.Join('\n', doc.Lines);

        // 1. tham chiếu file:dòng có thật
        foreach (Match m in FileRef.Matches(text))
        {
            var rel = m.Groups[1].Value.Replace('\\', '/');
            if (rel.Contains('<') || rel.StartsWith("core/") || rel.StartsWith("BIM_KB_Base")) continue;
            var full = ResolveRepoFile(rel);
            if (full is null) { yield return new Finding("HIGH", name, $"`{rel}:{m.Groups[2].Value}` — file không có trong repo"); continue; }
            var line = int.Parse(m.Groups[3].Success ? m.Groups[3].Value : m.Groups[2].Value);
            var count = File.ReadLines(full).Count();
            if (line > count) yield return new Finding("HIGH", name, $"`{rel}:{line}` — file chỉ có {count} dòng");
        }

        // 2. rule ID / KI ID có thật
        var rules = new RulesIndex(ctx);
        if (rules.Rules.Count > 0)
            foreach (var id in RuleRef.Matches(text).Select(m => m.Groups[1].Value).Distinct())
                if (rules.Get(id) is null && !id.StartsWith(ctx.Config["ki_prefix"].S("§§")))
                    yield return new Finding("HIGH", name, $"rule {id} không có trong rule của Base/dự án");
        var kis = new KbSearch(ctx).KnownIssues().Select(k => k.Id).ToHashSet();
        if (kis.Count > 0)
            foreach (var id in KiRef.Matches(text).Select(m => m.Groups[1].Value).Distinct())
                if (!kis.Contains(id)) yield return new Finding("HIGH", name, $"{id} không có trong file known-issue");

        if (name == "spec.md")
        {
            // 3. UNKNOWN còn mở
            foreach (var r in doc.TableRows("13").Where(r => r.Length >= 5 && r[1].Length > 0 && r[^1].Length == 0))
                yield return new Finding("HIGH", "spec §13", $"{r[0]} chưa có trả lời — không qua G1 được");
            // 4. AC có "Đo bằng"
            foreach (var r in doc.TableRows("5").Where(r => r.Length >= 5 && Regex.IsMatch(r[0], @"^AC\d+")))
                if (r[4].Length == 0 || r[4].StartsWith("test thuần / log")) yield return new Finding("CRITICAL", "spec §5", $"{r[0]} chưa có cột Đo bằng");
        }
        else
        {
            var specPath = Path.Combine(taskDir, "spec.md");
            var acs = File.Exists(specPath)
                ? new MdDoc(specPath).TableRows("5").Select(r => r[0]).Where(a => Regex.IsMatch(a, @"^AC\d+$")).ToList()
                : new List<string>();
            var matrix = doc.TableRows("7");
            // 5. AC phủ ma trận §7, mỗi dòng có ≥ 1 cột kiểm
            foreach (var ac in acs)
            {
                var row = matrix.FirstOrDefault(r => r[0] == ac);
                if (row is null) yield return new Finding("CRITICAL", "plan §7", $"{ac} không có dòng trong ma trận");
                else if (row.Skip(2).All(c => c.Length == 0)) yield return new Finding("CRITICAL", "plan §7", $"{ac} không có cột kiểm nào");
            }
            // 6. bất biến có test canh
            foreach (var r in doc.TableRows("5.6").Where(r => Regex.IsMatch(r[0], @"^I\d+") && r.Length >= 4 && r[1].Length > 0 && !r[1].StartsWith("vd.")))
                if (r[3].Length == 0) yield return new Finding("HIGH", "plan §5.6", $"bất biến {r[0]} chưa có test canh");
            // 7. baseline có số thật
            var baseline = doc.SectionText("1.3");
            if (doc.Section("1.3") is not null && !Regex.IsMatch(baseline, @"pass \d+|exit \d+|\d+ lỗi"))
                yield return new Finding("HIGH", "plan §1.3", "baseline chưa có số đo thật — baseline_run");
        }
    }

    private string? ResolveRepoFile(string rel)
    {
        var direct = ctx.Full(rel);
        if (File.Exists(direct)) return direct;
        // chỉ ghi tên file: tìm trong repo (bỏ bin/obj/.git)
        if (!rel.Contains('/'))
        {
            try
            {
                return Directory.EnumerateFiles(ctx.Root, rel, new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                    .FirstOrDefault(f => !Regex.IsMatch(f.Replace('\\', '/'), "/(bin|obj|\\.git)/"));
            }
            catch { return null; }
        }
        return null;
    }
}
