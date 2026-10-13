using Hawee.Flow.Core.Flow;
using Hawee.Flow.Core.Infra;
using Hawee.Flow.Core.Testing;

namespace Hawee.Flow.Core.Tasks;

public sealed record GateInput(
    string Gate, string Decision, string By, string Evidence,
    string Reason = "", bool CoderConfirmed = false, string ConditionalOn = "",
    string Commits = "", string ReviewRecord = "", string Release = "");

/// <summary>
/// Ghi quyết định cổng: dòng gates.Gn trong status.yaml + dòng DEC trong decision log, rồi chuyển state.
/// Chỉ người được ghi — hook PreToolUse của plugin hỏi lại người trước mỗi lần gọi tool này.
/// </summary>
public sealed class GateRecorder(ProjectContext ctx)
{
    private static readonly string[] Decisions = { "PASS", "CONCERNS", "FAIL", "WAIVED" };

    public string Record(string? task, GateInput g)
    {
        var dir = ctx.ResolveTask(task);
        var name = Path.GetFileName(dir);
        var gate = g.Gate.Trim().ToUpperInvariant();
        var decision = g.Decision.Trim().ToUpperInvariant();
        if (!FlowEngine.Gates.Contains(gate)) throw new FlowException($"cổng '{g.Gate}' không hợp lệ (G1–G6)");
        if (!Decisions.Contains(decision)) throw new FlowException("decision phải là PASS | CONCERNS | FAIL | WAIVED");
        if (string.IsNullOrWhiteSpace(g.By)) throw new FlowException("thiếu người duyệt (by)");
        if (decision == "WAIVED" && string.IsNullOrWhiteSpace(g.Reason)) throw new FlowException("WAIVED bắt buộc có lý do (reason)");

        var status = StatusFile.ForTask(dir);
        var st = status.Data;
        var passed = decision is "PASS" or "CONCERNS" or "WAIVED";
        var notes = new List<string>();

        if (gate == "G1" && passed && st["open_questions"].Strings().Count > 0 && decision != "WAIVED")
            throw new FlowException($"còn câu CHẶN mở ({string.Join(", ", st["open_questions"].Strings())}) — chưa qua G1 được");
        if (gate == "G2" && decision is "PASS" or "CONCERNS" && !g.CoderConfirmed)
            throw new FlowException("G2 cần người sẽ code xác nhận \"đủ để code\" (coder_confirmed=true)");
        if (gate == "G2" && passed && TestDesign.ProposedLeft(SuitesDir(), st["suites"].Strings()) is { Count: > 0 } proposed)
            throw new FlowException($"suite còn bản đề xuất ({string.Join(", ", proposed)}) — đổi tên thành suite.yaml / rules.md trước, G2 khoá hash bản chính");

        var date = ctx.Clock.Now.ToString("yyyy-MM-dd");
        var log = new DecisionLog(ctx);
        var decText = decision + (g.Reason.Length > 0 ? " — " + g.Reason : "");
        var decId = log.Append(new[]
        {
            new DecisionRow(g.By, $"{gate} {name}", $"Cổng {gate}", decText, $"status.yaml gates.{gate}", g.Evidence),
        })[0];

        var fields = new List<KeyValuePair<string, string>>
        {
            new("decision", StatusFile.Scalar(decision)),
            new("by", StatusFile.Scalar(g.By)),
            new("date", StatusFile.Scalar(date)),
            new("evidence", StatusFile.Scalar(g.Evidence)),
            new("decision_log", StatusFile.Scalar(decId)),
        };
        if (decision == "WAIVED") fields.Add(new("reason", StatusFile.Scalar(g.Reason)));
        switch (gate)
        {
            case "G2": fields.Add(new("coder_confirmed", g.CoderConfirmed ? "true" : "false")); break;
            case "G3": fields.Add(new("conditional_on", StatusFile.Scalar(g.ConditionalOn))); break;
            case "G4": fields.Add(new("commits", "[" + string.Join(", ", Split(g.Commits).Select(StatusFile.Scalar)) + "]")); break;
            case "G5": fields.Add(new("review_record", StatusFile.Scalar(g.ReviewRecord))); break;
            case "G6": fields.Add(new("release", StatusFile.Scalar(g.Release))); break;
        }
        status.SetGate(gate, fields);

        // G2 qua: khoá hash suite (luật đổi sau đó thì /verify dừng tới khi duyệt lại)
        if (gate == "G2" && passed)
        {
            var hashes = SuiteHashes(st["suites"].Strings(), g.By, date, notes);
            if (hashes.Count > 0) status.SetMap("suite_hashes", hashes);
        }

        var def = new FlowEngine(ctx).GateDef(gate);
        var newState = passed ? def["pass"].S() : def["fail"].S();
        if (newState.Length > 0) status.Set("state", newState);
        if (gate == "G1" && passed) status.Set("spec", "chốt");
        if (gate == "G2" && passed) status.Set("plan", "đang làm");
        status.Save();

        var msg = $"{gate} {decision} — {name} · {decId} · state → {newState}";
        return notes.Count == 0 ? msg : msg + "\n" + string.Join("\n", notes.Select(n => "! " + n));
    }

    private List<KeyValuePair<string, string>> SuiteHashes(List<string> suites, string by, string date, List<string> notes)
    {
        var result = new List<KeyValuePair<string, string>>();
        var root = SuitesDir();
        foreach (var s in suites)
        {
            var dir = Path.Combine(root ?? "", s);
            var sy = Path.Combine(dir, "suite.yaml");
            var rm = Path.Combine(dir, "rules.md");
            if (root is null || !File.Exists(sy) || !File.Exists(rm))
            {
                notes.Add($"không tính được hash suite {s} (thiếu {dir}\\suite.yaml hoặc rules.md) — ghi tay sau");
                continue;
            }
            var inner = StatusFile.FlowMap(new KeyValuePair<string, string>[]
            {
                new("suite_yaml", Text.Sha256File(sy)), new("rules_md", Text.Sha256File(rm)),
                new("approved_by", StatusFile.Scalar(by)), new("date", StatusFile.Scalar(date)),
            });
            result.Add(new(s, inner));
            // bản sao trong folder suite: runner kiểm cả khi chạy lẻ (/revit-test, không có task)
            File.WriteAllText(Path.Combine(dir, "approved.json"), new System.Text.Json.Nodes.JsonObject
            {
                ["suite_yaml"] = Text.Sha256File(sy), ["rules_md"] = Text.Sha256File(rm), ["approved_by"] = by, ["date"] = date,
                ["note"] = "ghi bởi gate_record G2 — suite.yaml / rules.md đổi sau đó thì lần chạy bị đánh dấu 'luật chưa duyệt'",
            }.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        return result;
    }

    public string? SuitesDir()
    {
        var env = Environment.GetEnvironmentVariable("HAWEE_REVIT_TASKS_DIR");
        var dir = env is { Length: > 0 } ? env : ctx.TestBlock["suites_dir"].S();
        return dir.Length == 0 ? null : ctx.Full(dir);
    }

    private static IEnumerable<string> Split(string s) =>
        s.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
