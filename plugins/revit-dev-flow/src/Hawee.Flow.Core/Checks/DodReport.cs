using System.Text;
using System.Text.RegularExpressions;
using Hawee.Flow.Core.Docs;
using Hawee.Flow.Core.Infra;
using Hawee.Flow.Core.Tasks;

namespace Hawee.Flow.Core.Checks;

/// <summary>
/// Bước 8: chạy lại mọi kiểm (check_all full), thêm kiểm DoD riêng (test hồi quy cho bug, bản đồ code, MCP registry),
/// ghi dod-report.md theo mẫu skill /dod của Base, dựng commit message nháp, đặt state commit. Không commit (G4 là người).
/// </summary>
public sealed class DodReport(ProjectContext ctx, IProcRunner? runner = null)
{
    private readonly IProcRunner _run = runner ?? ProcRunner.Instance;

    public string Build(string? task, string? baseRef)
    {
        var dir = ctx.ResolveTask(task);
        var name = Path.GetFileName(dir);
        var check = new CheckAll(ctx, _run).Run(task, "full", baseRef, countLoop: true);
        var status = StatusFile.ForTask(dir);
        var st = status.Data;
        var extra = new List<CheckLine>();

        // bug: test hồi quy gắn số issue (GEN-REG-001)
        var issue = Regex.Match(st["issue"].S(), @"\d+").Value;
        if (st["type"].S() == "bug")
        {
            var tests = check.ChangedFiles.Where(f => Regex.IsMatch(f, @"(?i)test")).ToList();
            var tagged = tests.Where(f => File.Exists(ctx.Full(f)) && issue.Length > 0 && Text.Read(ctx.Full(f)).Contains(issue)).ToList();
            extra.Add(new CheckLine("test hồi quy (bug)", "grep số issue trong file test đổi", tagged.Count > 0,
                tagged.Count > 0 ? $"có: {string.Join(", ", tagged.Take(3))}" : $"không thấy test nào trong diff chứa #{issue} (GEN-REG-001) — hoặc KI ghi 'Test hồi quy: chưa – lý do' có người duyệt",
                new()));
        }
        // capability mới / MCP: file tool đổi thì registry/contract phải đổi cùng
        var toolFiles = check.ChangedFiles.Where(f => Regex.IsMatch(f, @"(?i)(mcp|tool).*\.cs$")).ToList();
        if (toolFiles.Count > 0)
        {
            var registry = check.ChangedFiles.Where(f => Regex.IsMatch(f, @"(?i)(registry|contract)")).ToList();
            extra.Add(new CheckLine("MCP contract/registry", "diff", registry.Count > 0,
                registry.Count > 0 ? $"có: {string.Join(", ", registry.Take(3))}" : $"đổi {string.Join(", ", toolFiles.Take(3))} mà không đổi registry/contract (MCP-FEAT-002/006) — hoặc ghi 'không có capability mới'",
                new()));
        }
        // bản đồ code: diff đụng thư mục theo dõi của bản đồ nào thì bản đồ phải trong diff
        foreach (var (map, dirs) in CodeMaps())
        {
            var touched = check.ChangedFiles.Where(f => dirs.Any(d => f.StartsWith(d.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase))).ToList();
            if (touched.Count == 0) continue;
            var inDiff = check.ChangedFiles.Contains(map);
            extra.Add(new CheckLine($"bản đồ code {Path.GetFileName(map)}", "header bản đồ ↔ diff", inDiff,
                inDiff ? "đã cập nhật trong diff" : $"diff đụng {touched.Count} file đang theo dõi ({touched[0]}…) mà bản đồ chưa cập nhật (GEN-DOC-001)", new()));
        }

        var all = check.Lines.Concat(extra).ToList();
        var ok = all.All(l => l.Ok);
        var git = new Git(ctx, _run);
        var uncommitted = git.Uncommitted().Count;
        var (added, deleted) = git.DiffSize(check.BaseRef);
        var date = ctx.Clock.Now.ToString("yyyy-MM-dd");

        var sb = new StringBuilder();
        var head = check.Commit.Length >= 8 ? check.Commit[..8] : check.Commit;
        sb.AppendLine($"# DoD — {name} — {date} — commit {head} (chưa commit: {uncommitted} file)");
        sb.AppendLine();
        sb.AppendLine($"Kết quả máy: {(ok ? "PASS" : "FAIL — " + string.Join("; ", all.Where(l => !l.Ok).Select(l => l.Name)))}");
        sb.AppendLine();
        sb.AppendLine("| Kiểm | Lệnh | Kết quả | Bằng chứng |").AppendLine("|---|---|---|---|");
        foreach (var l in all)
            sb.AppendLine($"| {Text.Cell(l.Name)} | `{Text.Cell(l.Command)}` | {(l.Ok ? "OK" : "FAIL")} — {Text.Cell(l.Summary)} | {Text.Cell(string.Join("; ", l.Details.Take(3)))} |");
        sb.AppendLine();
        sb.AppendLine($"Vòng sửa bước 6: {st["loops"]["build_test"].Int()} · bước 7 (Revit): {st["loops"]["host_test"].Int()} · lệch plan: {st["deviations"].Int()} · diff so {check.BaseRef}: +{added}/-{deleted} dòng{(added + deleted > 400 ? " (> 400 — cân nhắc tách MR theo bước plan)" : "")}");
        sb.AppendLine();
        sb.AppendLine("## Người kiểm (chưa tick)");
        foreach (var item in HumanItems()) sb.AppendLine("- [ ] " + item);
        sb.AppendLine();
        sb.AppendLine("## Nội dung đề xuất cho commit / MR");
        sb.AppendLine();
        var msg = CommitMessage(dir, st);
        sb.AppendLine("```text").AppendLine(msg).AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("Lệnh đã chạy: xem bảng trên (chi tiết checks-last.json).");
        if (st["test_summary"].S() is { Length: > 0 } ts) sb.AppendLine($"Test trong Revit: {ts}");
        else if (ctx.TestBlock.Exists) sb.AppendLine("Test trong Revit: chưa chạy — ghi rõ \"chưa kiểm trên Revit\" trong MR nếu task đụng Revit.");
        var report = Path.Combine(dir, "dod-report.md");
        Text.Write(report, sb.ToString());

        status.Set("state", "commit");
        status.Save();

        var res = new StringBuilder();
        res.AppendLine($"DoD {(ok ? "PASS" : "FAIL")} — {ctx.Rel(report)}");
        foreach (var l in all.Where(l => !l.Ok)) res.AppendLine($"FAIL {l.Name}: {Text.Clip(l.Summary, 160)}");
        res.AppendLine("Commit nháp:");
        res.AppendLine(msg);
        res.Append(ok ? "DỪNG chờ G4 — chủ task nói rõ \"commit\" / \"push\" cho lần này." : "Sửa các mục FAIL (≤ 5 vòng) rồi chạy lại dod_report.");
        return res.ToString();
    }

    private IEnumerable<(string Map, List<string> Dirs)> CodeMaps()
    {
        if (!Directory.Exists(ctx.CodeMapsDir)) yield break;
        foreach (var f in Directory.GetFiles(ctx.CodeMapsDir, "*.md", SearchOption.AllDirectories))
        {
            var head = Text.Lines(f).Take(12).FirstOrDefault(l => l.Contains("Đối chiếu code:") || l.Contains("Doi chieu code:"));
            if (head is null) continue;
            var m = Regex.Match(head, @"(?:Thư mục theo dõi|Thu muc theo doi):\s*(.+)$");
            if (!m.Success) continue;
            var dirs = m.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(d => d.Trim('`', ',').Replace('\\', '/')).ToList();
            yield return (ctx.Rel(f), dirs);
        }
    }

    private IEnumerable<string> HumanItems()
    {
        var dod = Path.Combine(ctx.BaseDir, "core", "process", "dod.md");
        if (File.Exists(dod))
            foreach (var l in Text.Lines(dod).Where(l => l.Contains("[người]")))
                yield return Text.Clip(Regex.Replace(l, @"^[★\s]*\[người\]\s*", ""), 160);
        foreach (var p in ctx.Profiles)
        {
            var readme = Path.Combine(ctx.BaseDir, "profiles", p, "README.md");
            if (!File.Exists(readme)) continue;
            var lines = Text.Lines(readme);
            var start = Array.FindIndex(lines, l => l.StartsWith("## DoD"));
            if (start < 0) continue;
            foreach (var l in lines.Skip(start + 1).TakeWhile(l => !l.StartsWith("## ")).Where(l => l.StartsWith("[ ]")))
                yield return Text.Clip(l[3..].Trim(), 160) + $" ({p})";
        }
    }

    private string CommitMessage(string dir, Y st)
    {
        var type = st["type"].S() switch { "bug" => "fix", "refactor" => "refactor", "docs" => "docs", _ => "feat" };
        var topic = st["topic"].S();
        var title = new MdDoc(Path.Combine(dir, "spec.md")).Lines.FirstOrDefault()?.Split('—').ElementAtOrDefault(1)?.Trim() ?? topic;
        var rules = new List<string>();
        var spec = Path.Combine(dir, "spec.md");
        if (File.Exists(spec))
            rules = new MdDoc(spec).TableRows("9").Select(r => r[0]).Where(id => Regex.IsMatch(id, @"^[A-Z]+(-[A-Z0-9]+)+-\d{3}$")).ToList();
        var issue = Regex.Match(st["issue"].S(), @"\d+").Value;
        return $"{type}({topic}): {Text.Clip(title, 60)}\n\nRules: {(rules.Count > 0 ? string.Join(", ", rules) : "<rule ID đã đối chiếu>")}\nMCP: <tool đã đụng | không>\nIssue: #{(issue.Length > 0 ? issue : "<số>")}";
    }
}
