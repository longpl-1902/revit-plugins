using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Hawee.Flow.Core.Infra;
using Hawee.Flow.Core.Tasks;

namespace Hawee.Flow.Core.Testing;

/// <summary>
/// Thao tác sau một lần chạy: xem chi tiết theo yêu cầu (thay cho đọc report/log/JSON đầy đủ), agent chấm luật cần mắt,
/// sửa chẩn đoán, chạy lại test-summary; nháp ghi chú issue khi mọi tiêu chí PASS.
/// </summary>
public sealed class RunStore(ProjectContext ctx, RunnerConfig cfg, IProcRunner? proc = null)
{
    private readonly IProcRunner _proc = proc ?? ProcRunner.Instance;
    private readonly TestPaths _paths = new(ctx);
    private static readonly JsonSerializerOptions JsonOut = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public string RunDir(string suite, string run)
    {
        var runs = Path.Combine(_paths.Suite(suite), "runs");
        if (!Directory.Exists(runs)) throw new FlowException($"suite {suite} chưa có lần chạy nào");
        if (run.Length > 0)
        {
            var d = Path.Combine(runs, run);
            return Directory.Exists(d) ? d : throw new FlowException($"không có run {run} trong {suite}");
        }
        return Directory.GetDirectories(runs).OrderByDescending(d => d).FirstOrDefault() ?? throw new FlowException($"suite {suite} chưa có lần chạy nào");
    }

    private static JsonNode? Read(string p) => File.Exists(p) ? JsonNode.Parse(File.ReadAllText(p)) : null;

    // ---- get_detail ------------------------------------------------------------------------------------

    public string Detail(string suite, string run, string kind, string reference, int window)
    {
        var dir = RunDir(suite, run);
        var data = Path.Combine(dir, "data");
        switch (kind)
        {
            case "rule":
            {
                var rule = RulesFile.Load(Path.Combine(_paths.Suite(suite), "rules.md")).Rules.FirstOrDefault(r => r.Id == reference)
                           ?? throw new FlowException($"không có luật {reference}");
                var sb = new StringBuilder(rule.Block).AppendLine().AppendLine();
                foreach (var (sheet, r) in RuleEntries(dir, reference))
                {
                    sb.AppendLine($"[{sheet}] {r["verdict"]}{(r["pending_agent"]?.GetValue<bool>() == true ? " (cần agent)" : "")} · kỳ vọng {r["expected"]} · thực tế {r["actual"]} · {Text.Clip(r["evidence"]?.ToString() ?? "", 160)}");
                    foreach (var snap in r["snapshots"] as JsonArray ?? new JsonArray()) sb.AppendLine("  ảnh: " + Path.Combine(dir, snap!.ToString().Replace('/', Path.DirectorySeparatorChar)));
                }
                var images = Directory.Exists(data) ? Directory.EnumerateFiles(data, "*.*", SearchOption.AllDirectories).Where(f => Regex.IsMatch(f, @"\.(png|jpe?g)$", RegexOptions.IgnoreCase)).Count() : 0;
                sb.Append($"(ảnh có trong data\\: {images}; dùng Read để xem ảnh; chấm xong → record_verdict)");
                return sb.ToString();
            }
            case "log":
            {
                var log = Path.Combine(data, "addin.log");
                if (!File.Exists(log)) return "không có data\\addin.log";
                var lines = File.ReadAllLines(log);
                var at = int.TryParse(reference, out var n) ? n : Read(Path.Combine(data, "reconcile.json"))?["log"]?["firstError"]?["line"]?.GetValue<int>() ?? 1;
                var from = Math.Max(1, at - window);
                var to = Math.Min(lines.Length, at + window * 2);
                var sb = new StringBuilder($"addin.log dòng {from}–{to} / {lines.Length} (lỗi đầu tiên: {at})\n");
                for (var i = from; i <= to; i++) sb.AppendLine($"{i,6}{(i == at ? ">" : " ")} {Text.Clip(lines[i - 1], 240)}");
                return sb.ToString().TrimEnd();
            }
            case "finding":
            {
                var rec = Read(Path.Combine(data, "reconcile.json"));
                var f = (rec?["findings"] as JsonArray ?? new JsonArray()).Where(x => reference.Length == 0 || x?["code"]?.ToString() == reference || x?["step"]?.ToString() == reference);
                var list = f.Select(x => $"{x?["code"]} · step {x?["step"]} · {x?["detail"]}").ToList();
                return list.Count == 0 ? $"reconcile: {rec?["result"]?.ToString() ?? "không có reconcile.json"} — không có phát hiện khớp" : string.Join('\n', Text.Cap(list, 20));
            }
            case "diagnosis":
                return Read(Path.Combine(data, "diagnosis.json"))?.ToJsonString(JsonOut) ?? "chưa có diagnosis.json";
            case "steps":
            {
                var st = Read(Path.Combine(data, "step_status.json"));
                return st is null ? "không có step_status.json" : string.Join('\n', (st["steps"] as JsonArray ?? new JsonArray()).Select(x => $"{x?["stepCode"]}: {x?["status"]}")) + (st["message"]?.ToString() is { Length: > 0 } m ? "\n" + m : "");
            }
            case "summary":
            {
                var p = Path.Combine(dir, "test-summary.md");
                return File.Exists(p) ? string.Join('\n', Text.Cap(Text.Lines(p).Where(l => l.Trim().Length > 0), 30)) : "chưa có test-summary.md";
            }
            case "files":
                return string.Join('\n', Text.Cap(Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(dir, f)).OrderBy(f => f), 60));
            default:
                throw new FlowException("kind: rule | log | finding | diagnosis | steps | summary | files");
        }
    }

    private static IEnumerable<(string Sheet, JsonObject Rule)> RuleEntries(string dir, string ruleId)
    {
        var res = Read(Path.Combine(dir, "result.json")) as JsonArray ?? new JsonArray();
        foreach (var s in res.OfType<JsonObject>())
            foreach (var r in (s["rules"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Where(r => r["id"]?.ToString() == ruleId))
                yield return (s["sheet"]?.ToString() ?? "", r);
    }

    // ---- record_verdict --------------------------------------------------------------------------------

    public string RecordVerdict(string suite, string run, string ruleId, string sheet, string verdict, string note, string autonomy = "")
    {
        verdict = verdict.Trim().ToUpperInvariant();
        if (verdict is not (Verdicts.Pass or Verdicts.Fail or Verdicts.Suspect or Verdicts.NotMeasurable))
            throw new FlowException("verdict: PASS | FAIL | SUSPECT | NOT_MEASURABLE");
        if (verdict == Verdicts.Pass && note.Trim().Length < 15)
            throw new FlowException("PASS bằng mắt phải mô tả cụ thể đã kiểm gì trên ảnh (note ≥ 15 ký tự) — còn nghi ngờ thì SUSPECT");
        var dir = RunDir(suite, run);
        var resPath = Path.Combine(dir, "result.json");
        var res = Read(resPath) as JsonArray ?? throw new FlowException("run chưa có result.json");
        var changed = 0;
        foreach (var s in res.OfType<JsonObject>())
        {
            var sh = s["sheet"]?.ToString() ?? "";
            if (sheet.Length > 0 && sheet != "*" && sh != sheet) continue;
            var rules = (s["rules"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
            foreach (var r in rules.Where(r => r["id"]?.ToString() == ruleId))
            {
                r["verdict"] = verdict;
                r["pending_agent"] = false;
                r["evidence"] = "agent: " + note;
                changed++;
            }
            var vs = rules.Select(r => r["verdict"]?.ToString()).ToList();
            if (s["verdict"]?.ToString() != Verdicts.Error)
                s["verdict"] = vs.Contains(Verdicts.Error) ? Verdicts.Error : vs.Contains(Verdicts.Fail) ? Verdicts.Fail : vs.All(v => v == Verdicts.Pass) ? Verdicts.Pass : Verdicts.Suspect;
        }
        if (changed == 0) throw new FlowException($"không có luật {ruleId}{(sheet.Length > 0 ? " ở sheet " + sheet : "")} trong result.json");
        File.WriteAllText(resPath, res.ToJsonString(JsonOut));

        // lưu để lần chạy sau dùng lại khi khoá chấm (hash luật + dữ liệu) không đổi
        var gradingDir = Path.Combine(dir, "data", "grading");
        Directory.CreateDirectory(gradingDir);
        var agentPath = Path.Combine(gradingDir, "agent.json");
        var agent = Read(agentPath) as JsonObject ?? new JsonObject();
        var key = (Read(Path.Combine(gradingDir, "keys.json")) as JsonObject)?[ruleId]?.ToString() ?? "";
        agent[$"{ruleId}|{(sheet.Length > 0 ? sheet : "*")}"] = new JsonObject { ["verdict"] = verdict, ["note"] = note, ["key"] = key };
        File.WriteAllText(agentPath, agent.ToJsonString(JsonOut));
        File.AppendAllText(Path.Combine(dir, "report.md"), $"\n- Agent chấm {ruleId} {(sheet.Length > 0 ? sheet : "(mọi sheet)")}: {verdict} — {note}\n");
        return $"{ruleId}: {verdict} ({changed} dòng) · " + SummaryLine(dir, autonomy);
    }

    // ---- diagnosis_set ---------------------------------------------------------------------------------

    public string SetDiagnosis(string suite, string run, string cls, string evidence, string inTaskDiff, string ki, string blocksTask, string fixTarget, string autonomy = "")
    {
        cls = cls.Trim().ToUpperInvariant();
        if (cls is not ("CODE" or "INPUT" or "TRACKING" or "ENV" or "DECISION" or "UNKNOWN")) throw new FlowException("class: CODE | INPUT | TRACKING | ENV | DECISION | UNKNOWN");
        if (string.IsNullOrWhiteSpace(evidence)) throw new FlowException("evidence bắt buộc: file:dòng hoặc dòng log nguyên văn");
        var dir = RunDir(suite, run);
        var path = Path.Combine(dir, "data", "diagnosis.json");
        var d = Read(path) as JsonObject ?? new JsonObject();

        // file:dòng trong evidence phải có thật; lỗi CODE bắt buộc có (sửa code mà không biết chỗ là đoán)
        var refs = SourceRefs(evidence);
        var missing = refs.Where(r => r.Path is null).Select(r => r.Text).ToList();
        if (cls == "CODE")
        {
            if (missing.Count > 0) throw new FlowException($"evidence trỏ tới chỗ không có trong repo: {string.Join(", ", missing)}");
            if (refs.Count == 0) throw new FlowException("lỗi CODE cần evidence dạng file:dòng có thật (vd. Draw/KeyplanService.cs:142)");
        }
        refs = refs.Where(r => r.Path is not null).ToList(); // lớp khác: frame của thư viện ngoài repo thì bỏ qua

        // in_task_diff: máy tính từ file trong evidence (không có thì stack trace) × file thay đổi của task
        var machine = MachineInDiff(dir, refs.Select(r => r.Path!).ToList(), d);
        var claimed = Tri(inTaskDiff);
        if (claimed is not null && machine is not null && claimed != machine)
            throw new FlowException($"in_task_diff khai {Low(claimed)} nhưng máy tính ra {Low(machine)} (file lỗi: {string.Join(", ", refs.Count > 0 ? refs.Select(r => r.Path!) : d["stack_files"]?.AsArray().Select(x => x!.ToString()) ?? Enumerable.Empty<string>())}) — kiểm lại evidence, hoặc bỏ trống để dùng giá trị máy");

        d["class"] = cls;
        d["evidence"] = evidence;
        if (ki.Trim().Length > 0) d["ki"] = ki.Trim() == "-" ? "" : ki.Trim();
        d["in_task_diff"] = machine ?? claimed ?? (d["in_task_diff"]?.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? d["in_task_diff"]!.GetValue<bool>() : null);
        if (blocksTask.Length > 0) d["blocks_task"] = Tri(blocksTask);
        if (fixTarget.Length > 0) d["fix_target"] = fixTarget;
        d["suggested_by"] = "agent (diagnosis_set)";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, d.ToJsonString(JsonOut));
        return $"chẩn đoán {cls} · in_task_diff {d["in_task_diff"]?.ToString() ?? "null"}{(machine is not null ? " (máy)" : "")} · " + SummaryLine(dir, autonomy);

        static bool? Tri(string v) => v.Trim().ToLowerInvariant() switch { "true" => true, "false" => false, _ => null };
        static string Low(bool? b) => b == true ? "true" : "false";
    }

    private bool? MachineInDiff(string runDir, List<string> evidenceFiles, JsonObject d)
    {
        var task = Read(Path.Combine(runDir, "run-info.json"))?["task"]?.ToString() ?? "";
        if (task.Length == 0) return null; // chạy lẻ: không có diff của task để so
        var files = evidenceFiles.Count > 0 ? evidenceFiles : (d["stack_files"] as JsonArray)?.Select(x => x!.ToString()).ToList() ?? new();
        if (files.Count == 0) return null;
        var git = new Checks.Git(ctx, _proc);
        var changed = git.ChangedFiles(TestRunner.SafeBase(git));
        return TestRunner.InDiff(files, changed);
    }

    /// <summary>Các tham chiếu "file.cs:142" trong evidence, kèm đường dẫn tương đối trong repo (null = không thấy file hoặc dòng vượt quá file).</summary>
    internal List<(string Text, string? Path)> SourceRefs(string evidence)
    {
        var res = new List<(string, string?)>();
        foreach (Match m in Regex.Matches(evidence, @"([\w.\-\\/]+\.(?:cs|xaml|vb|cpp|h|fs)):(?:line\s*)?(\d+)", RegexOptions.IgnoreCase))
        {
            var rel = m.Groups[1].Value.Replace('\\', '/').TrimStart('/');
            var line = int.Parse(m.Groups[2].Value);
            var found = FindSource(rel);
            var ok = found is not null && File.ReadLines(Path.Combine(ctx.Root, found)).Count() >= line;
            res.Add((m.Value, ok ? found : null));
        }
        return res;
    }

    private string? FindSource(string rel)
    {
        if (File.Exists(Path.Combine(ctx.Root, rel))) return rel;
        // đường dẫn tuyệt đối máy build / chỉ tên file: tìm file trong repo có đuôi khớp dài nhất
        var parts = rel.Split('/');
        var tracked = _proc.Run("git", new[] { "ls-files" }, ctx.Root, TimeSpan.FromMinutes(1));
        var files = tracked.ExitCode == 0 && tracked.Lines.Length > 0
            ? tracked.Lines.Select(l => l.Trim().Replace('\\', '/')).ToList()
            : Directory.EnumerateFiles(ctx.Root, parts[^1], SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(ctx.Root, f).Replace('\\', '/'))
                .Where(f => !Regex.IsMatch(f, @"(^|/)(bin|obj|\.git)/")).ToList();
        for (var n = parts.Length; n >= 1; n--)
        {
            var tail = string.Join('/', parts[^n..]);
            var hit = files.FirstOrDefault(f => f.Equals(tail, StringComparison.OrdinalIgnoreCase) || f.EndsWith("/" + tail, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return hit;
        }
        return null;
    }

    private string SummaryLine(string dir, string autonomy)
    {
        var info = Read(Path.Combine(dir, "run-info.json"));
        var auto = autonomy.Length > 0 ? autonomy : info?["autonomy"]?.ToString() ?? "";
        var runner = new TestRunner(ctx, cfg, _ => throw new FlowException("không dùng"), new WindowsHostProcess(), _proc);
        var s = runner.Summary(dir, auto);
        return $"test-summary: {s.Outcome}{(s.Scope.Length > 0 ? $" ({s.Scope})" : "")} · {Text.Clip(s.Reason, 160)}{(s.Next.Length > 0 ? " · tiếp: " + Text.Clip(s.Next, 160) : "")}";
    }

    // ---- issue_note_draft (bước 3b của /verify) ---------------------------------------------------------

    public string IssueNote(string? task)
    {
        var dir = ctx.ResolveTask(task);
        var st = StatusFile.ForTask(dir).Data;
        var problems = new List<string>();
        var issue = Regex.Match(st["issue"].S(), @"\d+").Value;
        if (issue.Length == 0 || issue == "0000") problems.Add("status.yaml.issue chưa phải số issue thật");
        if (!File.Exists(Path.Combine(dir, "dod-report.md")) && !File.Exists(Path.Combine(dir, CheckAllLast))) problems.Add("chưa có check_all/dod xanh");
        var rows = new List<string>();
        var images = new List<string>();
        foreach (var suite in st["suites"].Strings())
        {
            string runDir;
            try { runDir = RunDir(suite, ""); } catch (FlowException ex) { problems.Add(ex.Message); continue; }
            var sum = Read(Path.Combine(runDir, "data", "test-summary.json"));
            if (sum?["outcome"]?.ToString() != "PASS") problems.Add($"{suite}: test-summary {sum?["outcome"]?.ToString() ?? "chưa có"}");
            if ((Read(Path.Combine(runDir, "data", "reconcile.json"))?["findings"] as JsonArray)?.Count > 0) problems.Add($"{suite}: reconcile có phát hiện");
            var info = Read(Path.Combine(runDir, "run-info.json"));
            if ((info?["manual_steps"] as JsonArray)?.Count > 0) problems.Add($"{suite}: có manual_steps");
            foreach (var s in (Read(Path.Combine(runDir, "result.json")) as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                foreach (var r in (s["rules"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                {
                    if (r["verdict"]?.ToString() != Verdicts.Pass) problems.Add($"{suite} {r["id"]} {s["sheet"]}: {r["verdict"]}");
                    var snaps = (r["snapshots"] as JsonArray ?? new JsonArray()).Select(x => Path.Combine(runDir, x!.ToString().Replace('/', Path.DirectorySeparatorChar))).ToList();
                    images.AddRange(snaps.Take(3));
                    rows.Add($"| {suite} | {r["id"]} | {Text.Cell(r["expected"]?.ToString())} | {Text.Cell(r["actual"]?.ToString())} | {r["verdict"]} | {string.Join(" ", snaps.Select(p => "!" + Path.GetFileName(p) + "!"))} |");
                }
        }
        foreach (var nr in st["night_reports"].Strings()) problems.Add($"night-report {nr}: kiểm DEC-AUTO đã xác nhận (người)");
        if (problems.Count > 0)
            return "Không gửi issue: " + string.Join("; ", problems.Take(8)) + (problems.Count > 8 ? $"; … (+{problems.Count - 8})" : "");
        var git = new Checks.Git(ctx, _proc);
        var note = new StringBuilder();
        note.AppendLine($"[/verify {st["test_session"].S()}] Test tự động PASS — chuyển Ready For QA");
        note.AppendLine($"Commit {git.Head()} · nhánh {git.Branch()} · lượt sửa {st["loops"]["host_test"].Int()} · suite: {string.Join(", ", st["suites"].Strings())}");
        note.AppendLine("| Suite | Tiêu chí | Kỳ vọng | Thực tế | Verdict | Ảnh |").AppendLine("|---|---|---|---|---|---|");
        foreach (var r in rows) note.AppendLine(r);
        note.AppendLine($"Báo cáo: {st["test_summary"].S()}");
        var draft = Path.Combine(dir, "issue-note-draft.md");
        Text.Write(draft, note.ToString());
        return $"Đủ điều kiện gửi issue #{issue}. Ghi chú nháp: {ctx.Rel(draft)} · {images.Distinct().Count()} ảnh cần upload:\n" +
               string.Join('\n', Text.Cap(images.Distinct(), 20)) +
               $"\nDùng MCP issue tracker: upload từng ảnh (mô tả '<suite> · R-xx · <sheet>'), rồi một lần cập nhật note + uploads + đổi trạng thái theo issue_tracker.on_pass (chỉ khi trạng thái hiện tại thuộc from_status).";
    }

    private const string CheckAllLast = "checks-last.json";
}
