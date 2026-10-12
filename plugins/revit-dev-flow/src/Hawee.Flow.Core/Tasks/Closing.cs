using System.Text;
using System.Text.RegularExpressions;
using Hawee.Flow.Core.Checks;
using Hawee.Flow.Core.Docs;
using Hawee.Flow.Core.Infra;

namespace Hawee.Flow.Core.Tasks;

/// <summary>
/// Bước 9–10 phần máy: gói review đối kháng (diff + thiết kế + bất biến + ma trận + checklist, cho agent phiên riêng),
/// handoff dựng sẵn từ git / status / dod / test-summary / decision log, báo cáo known-issue gọn.
/// </summary>
public sealed class Closing(ProjectContext ctx, IProcRunner? runner = null)
{
    private readonly IProcRunner _run = runner ?? ProcRunner.Instance;

    /// <summary>File sinh tự động: không review, chỉ tốn token.</summary>
    internal static readonly string[] GeneratedSpecs =
        { ":(exclude,glob)**/*.Designer.cs", ":(exclude,glob)**/*.g.cs", ":(exclude,glob)**/*.g.i.cs", ":(exclude,glob)**/*.resx", ":(exclude,glob)**/*.AssemblyInfo.cs" };

    public string ReviewPacket(string? task, string? baseRef, int maxDiffLines = 1500)
    {
        var dir = ctx.ResolveTask(task);
        var git = new Git(ctx, _run);
        var bref = git.IntegrationRef(baseRef);
        var stat = _run.Run("git", new[] { "diff", "--stat", bref }, ctx.Root, TimeSpan.FromMinutes(1)).Lines;
        var diff = _run.Run("git", new[] { "diff", "-U3", bref, "--", ".", ":(exclude)docs/tasks" }.Concat(GeneratedSpecs), ctx.Root, TimeSpan.FromMinutes(2)).Output.Replace("\r", "").Split('\n');
        var (added, deleted) = git.DiffSize(bref);
        var plan = new MdDoc(Path.Combine(dir, "plan.md"));
        var spec = new MdDoc(Path.Combine(dir, "spec.md"));

        var sb = new StringBuilder();
        sb.AppendLine($"# Gói review đối kháng — {Path.GetFileName(dir)} — {ctx.Clock.Now:yyyy-MM-dd}");
        sb.AppendLine();
        sb.AppendLine("> Cho agent review phiên riêng (adversarial-reviewer): chỉ đọc gói này + code được dẫn. Báo khoảng trống ảnh hưởng tính đúng đắn, mỗi phát hiện kèm file:dòng và cách tái hiện/kiểm; không góp ý phong cách; không sửa code.");
        sb.AppendLine();
        sb.AppendLine("## Checklist G5");
        foreach (var item in Checklist()) sb.AppendLine("- " + item);
        sb.AppendLine();
        sb.AppendLine("## AC (spec §5)");
        sb.AppendLine(Section(spec, "5"));
        foreach (var (num, title) in new[] { ("5.1", "Luồng sau khi sửa"), ("5.2", "Kiểu & hàm mới / đổi"), ("5.3", "Ngữ cảnh chạy, transaction, huỷ"), ("5.6", "Bất biến"), ("7", "Ma trận AC → bước → test"), ("13", "Lệch so với plan") })
        {
            sb.AppendLine($"## Plan §{num} — {title}");
            sb.AppendLine(Section(plan, num));
        }
        sb.AppendLine($"## Diff so với {bref} (+{added}/-{deleted}){(added + deleted > 400 ? " — > 400 dòng: xem có nên tách MR" : "")}");
        sb.AppendLine("```text");
        foreach (var l in stat.TakeLast(40)) sb.AppendLine(l);
        sb.AppendLine("```");
        sb.AppendLine("```diff");
        foreach (var l in diff.Take(maxDiffLines)) sb.AppendLine(l);
        if (diff.Length > maxDiffLines)
        {
            var rest = diff.Skip(maxDiffLines).Select(l => Regex.Match(l, @"^diff --git a/(\S+)")).Where(m => m.Success).Select(m => m.Groups[1].Value).ToList();
            sb.AppendLine($"… cắt {diff.Length - maxDiffLines} dòng — đọc khi cần: git diff {bref} -- <file>{(rest.Count > 0 ? $"; file chưa có trong gói: {string.Join(", ", rest.Take(20))}{(rest.Count > 20 ? " …" : "")}" : "")}");
        }
        sb.AppendLine("```");
        var file = Path.Combine(dir, $"review-packet-{ctx.Clock.Now:yyyy-MM-dd}.md");
        Text.Write(file, sb.ToString());
        return $"Gói review: {ctx.Rel(file)} · diff +{added}/-{deleted} so {bref} · {stat.Length - 1} file\n" +
               $"Giao cho agent adversarial-reviewer (chỉ đọc gói này), ghi kết quả vào review-{ctx.Clock.Now:yyyy-MM-dd}.md (CRITICAL/HIGH/MEDIUM/LOW).";
    }

    private static string Section(MdDoc d, string num)
    {
        var t = d.SectionText(num).Trim();
        return t.Length == 0 ? "(trống)\n" : t.Length <= 3000 ? t + "\n" : Text.Clip(t, 3000) + $"\n(cắt — đọc đủ ở {Path.GetFileName(d.Path)} §{num})\n";
    }

    private IEnumerable<string> Checklist()
    {
        var review = Path.Combine(ctx.BaseDir, "core", "process", "review.md");
        var fromBase = File.Exists(review)
            ? Text.Lines(review).SkipWhile(l => !l.StartsWith("## 2")).Skip(1).TakeWhile(l => !l.StartsWith("## ")).Where(l => Regex.IsMatch(l, @"^\s*(-|\d+\.|\[ \])\s")).Select(l => Text.Clip(l.Trim().TrimStart('-', ' '), 200)).ToList()
            : new List<string>();
        if (fromBase.Count > 0) return fromBase;
        return new[]
        {
            "Đúng spec: mỗi AC có test thật sự fail khi code sai",
            "Bất biến plan §5.6 còn test canh",
            "Rule blocker/high: thread Revit, transaction, version API thấp nhất, idempotency, bảo mật",
            "csproj bản đôi, registry MCP, bản đồ code, log hai nơi (logger + tracking)",
            "Độ phức tạp có lý do; lệch plan đã ghi §13",
        };
    }

    public string HandoffDraft(string? task)
    {
        var dir = ctx.ResolveTask(task);
        var name = Path.GetFileName(dir);
        var st = StatusFile.ForTask(dir).Data;
        var git = new Git(ctx, _run);
        var date = ctx.Clock.Now.ToString("yyyy-MM-dd");
        var file = Path.Combine(dir, $"handoff-{date}.md");
        for (var i = 2; File.Exists(file); i++) file = Path.Combine(dir, $"handoff-{date}-{i}.md");
        var topic = st["topic"].S(name);

        // handoff trước cùng chủ đề
        var previous = Directory.GetFiles(dir, "handoff-*.md")
            .Concat(Directory.Exists(ctx.HandoffsDir) ? Directory.GetFiles(ctx.HandoffsDir, "*.md") : Array.Empty<string>())
            .Where(f => f != file && Text.Read(f).Contains($"`{topic}`"))
            .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        var decs = File.Exists(ctx.DecisionLog)
            ? Regex.Matches(Text.Read(ctx.DecisionLog), $@"\|\s*(DEC-\d{{4}}-\d+)\s*\|[^\n]*{Regex.Escape(name)}").Select(m => m.Groups[1].Value).Distinct().ToList()
            : new List<string>();
        var log = _run.Run("git", new[] { "log", "--oneline", "-5" }, ctx.Root, TimeSpan.FromMinutes(1)).Lines;
        var uncommitted = git.Uncommitted();
        var checks = File.Exists(Path.Combine(dir, CheckAll.LastFile)) ? Text.Read(Path.Combine(dir, CheckAll.LastFile)) : "";
        var checksOk = Regex.Match(checks, "\"Ok\":\\s*(true|false)").Groups[1].Value;

        var sb = new StringBuilder();
        sb.AppendLine($"# Handoff — {topic} ({date})");
        sb.AppendLine();
        sb.AppendLine("> Tên file / quy ước: core/templates/handoff.md. Handoff là ảnh chụp: không sửa sau khi đóng. Dựng bởi hawee-flow handoff_draft — agent điền các ô <…>.");
        sb.AppendLine();
        sb.AppendLine($"- Chủ đề: `{topic}` · Task: {ctx.Rel(dir)} · Issue {st["issue"].S()}");
        sb.AppendLine($"- Tiếp nối (cùng chủ đề): {(previous is null ? "đầu chuỗi" : ctx.Rel(previous))}");
        sb.AppendLine($"- Dựa trên: spec {ctx.Rel(Path.Combine(dir, "spec.md"))} {st["spec_version"].S("v1")} · plan {ctx.Rel(Path.Combine(dir, "plan.md"))} · {(decs.Count > 0 ? string.Join(", ", decs.Take(12)) : "không có DEC")}");
        sb.AppendLine($"- Thay đổi so với handoff trước: {(previous is null ? "—" : "<quyết định/phạm vi nào đã khác, dẫn DEC>")}");
        sb.AppendLine("- Liên quan: <handoff/spec khác chủ đề, nếu cần>");
        sb.AppendLine();
        sb.AppendLine("## 1. Trạng thái").AppendLine();
        sb.AppendLine("| Mục | Giá trị |").AppendLine("|---|---|");
        sb.AppendLine($"| Nhánh | {git.Branch()} |");
        sb.AppendLine($"| Commit cuối | {Text.Cell(log.FirstOrDefault() ?? git.Head())} |");
        sb.AppendLine("| Push / MR | <đã push? link MR> |");
        sb.AppendLine($"| Còn lại chưa commit | {(uncommitted.Count == 0 ? "không" : Text.Cell(string.Join(", ", uncommitted.Take(10))) + (uncommitted.Count > 10 ? $" … (+{uncommitted.Count - 10})" : ""))} |");
        sb.AppendLine();
        sb.AppendLine("## 2. Đã xong").AppendLine();
        sb.AppendLine("| Việc | File | Bằng chứng (commit, lệnh + kết quả) |").AppendLine("|---|---|---|");
        foreach (var l in log.Take(5)) sb.AppendLine($"| <việc> | <file> | {Text.Cell(l)} |");
        sb.AppendLine();
        sb.AppendLine("## 3. Đã kiểm chứng / chưa").AppendLine();
        sb.AppendLine($"- **Đã:** check_all: {(checksOk.Length > 0 ? (checksOk == "true" ? "PASS" : "FAIL") : "chưa chạy")} (checks-last.json){(File.Exists(Path.Combine(dir, "dod-report.md")) ? " · dod-report.md" : "")}{(st["test_summary"].S().Length > 0 ? " · test Revit: " + st["test_summary"].S() : "")}");
        sb.AppendLine($"- **Chưa:** {(st["test_summary"].S().Length == 0 && ctx.TestBlock.Exists ? "chưa chạy trên Revit" : "<chưa kiểm gì — không làm tròn>")}");
        sb.AppendLine($"- **Số vòng sửa** bước 6 / bước 7: {st["loops"]["build_test"].Int()} / {st["loops"]["host_test"].Int()} · lệch plan: {st["deviations"].Int()}");
        sb.AppendLine();
        sb.AppendLine("## 4. Còn dở & câu hỏi mở").AppendLine();
        sb.AppendLine("| Việc / câu hỏi | Người quyết định | DEC / hạn |").AppendLine("|---|---|---|");
        foreach (var q in st["open_questions"].Strings()) sb.AppendLine($"| {q} (câu CHẶN) | <vai> | <hạn> |");
        sb.AppendLine();
        sb.AppendLine("**Phát hiện tốn công điều tra** (để người sau không tìm lại): <…>");
        sb.AppendLine();
        sb.AppendLine("**Cố ý chưa làm** (và vì sao, ai quyết): <…>");
        sb.AppendLine();
        sb.AppendLine("## 5. Bắt đầu từ đâu").AppendLine();
        sb.AppendLine("1. `flow_next` cho task này");
        sb.AppendLine("2. <bước tiếp theo cụ thể>");
        sb.AppendLine();
        sb.AppendLine("## 6. Liên kết").AppendLine();
        sb.AppendLine($"- Spec / plan / report / issue: spec.md, plan.md{(File.Exists(Path.Combine(dir, "test-design.md")) ? ", test-design.md" : "")}{(st["test_summary"].S().Length > 0 ? ", " + st["test_summary"].S() : "")}");
        var maps = st["code_maps"].Items.Select(i => i.IsMap ? $"{i["map"].S()} {i["from"].S()} → {i["to"].S()}" : i.S()).Where(x => x.Trim().Length > 0).ToList();
        sb.AppendLine($"- **Bản đồ code:** {(maps.Count > 0 ? string.Join("; ", maps) : "<file bản đồ> <hash cũ> → <hash mới> | không đổi – <lý do>")}");
        sb.AppendLine("- Known-issue / rule đã thêm: <KI-… (Loại, Phòng ngừa, Test hồi quy)> | không");
        sb.AppendLine("- **Bài học đưa lên KB:** `LL-…` | ghi `kb/inbox/<file>` | chưa có");
        Text.Write(file, sb.ToString());

        var status = StatusFile.ForTask(dir);
        status.AddToList("handoffs", Path.GetFileName(file));
        status.Save();
        var placeholders = Regex.Matches(sb.ToString(), "<[^>]+>").Count;
        return $"Handoff nháp: {ctx.Rel(file)} · còn {placeholders} ô <…> cho agent điền (§2 việc/file, §4 phát hiện + cố ý chưa làm, §5, §6 bản đồ + bài học)\n" +
               "Điền xong → check_all scope=full (doc-lint kiểm handoff-chain, handoff-map). state done chỉ sau G6 / khi không phát hành.";
    }

    public string KiReport()
    {
        var script = ctx.BaseTool("ki-report.ps1");
        if (!File.Exists(script)) throw new FlowException("không có ki-report.ps1");
        var r = Ps.RunFile(_run, script, Array.Empty<string>(), ctx.Root, TimeSpan.FromMinutes(3));
        var lines = r.Lines.Where(l => l.Trim().Length > 0 && !l.StartsWith("|---")).ToList();
        return string.Join('\n', Text.Cap(lines, 25));
    }
}
