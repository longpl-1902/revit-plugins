using Hawee.Flow.Core.Flow;
using Hawee.Flow.Core.Infra;
using Hawee.Flow.Core.Testing;

namespace Hawee.Flow.Core.Tasks;

/// <summary>
/// Agent trình cổng: máy kiểm điều kiện cứng (câu CHẶN, baseline, luật 'chờ', mức tự chủ, dod-report…) rồi đặt state chờ duyệt.
/// Không ghi quyết định — quyết định do người ghi bằng gate_record.
/// </summary>
public sealed class GateSubmit(ProjectContext ctx)
{
    public string Submit(string? task, string gate)
    {
        var dir = ctx.ResolveTask(task);
        var g = gate.Trim().ToUpperInvariant();
        var status = StatusFile.ForTask(dir);
        var st = status.Data;
        var missing = new List<string>();
        switch (g)
        {
            case "G1":
                if (st["open_questions"].Strings() is { Count: > 0 } oq) missing.Add("còn câu CHẶN: " + string.Join(", ", oq));
                if (!File.Exists(Path.Combine(dir, "spec.md")) && st["tier"].S() != "T0") missing.Add("chưa có spec.md");
                if (!Directory.GetFiles(dir, "analyze-*.md").Any() && st["tier"].S() != "T0") missing.Add("chưa chạy doc_check");
                break;
            case "G2":
                if (st["baseline"].S().Length == 0 && st["tier"].S() != "T0") missing.Add("chưa có baseline (baseline_run)");
                if (st["autonomy"].S().Length == 0) missing.Add("chưa chốt mức tự chủ ask/night");
                if (st["open_questions"].Strings() is { Count: > 0 } oq2) missing.Add("còn câu CHẶN: " + string.Join(", ", oq2));
                // Base gates.md G2: có test trong ứng dụng chủ → bảng luật 3d phải có và đã duyệt
                if (st["suites"].Strings().Count == 0 && !SuiteWaived(st)
                    && TestDesign.AcsNeedingSuite(Path.Combine(dir, "spec.md")) is { Count: > 0 } needSuite)
                    missing.Add($"spec có AC đo bằng suite ({string.Join(", ", needSuite)}) nhưng chưa có suite — chạy /test-design "
                                + "(người quyết không cần suite: status.yaml test_design: \"không cần — <lý do> (<người>)\")");
                foreach (var s in st["suites"].Strings())
                {
                    var rf = Path.Combine(SafeSuitesDir() ?? "", s, "rules.md");
                    if (!File.Exists(rf)) { missing.Add($"không thấy rules.md của suite {s}"); continue; }
                    var pending = RulesFile.Load(rf).Active.Where(r => r.Pending).Select(r => r.Id).ToList();
                    if (pending.Count > 0) missing.Add($"suite {s} còn luật 'chờ': {string.Join(", ", pending)}");
                }
                break;
            case "G3":
                if (st["test_summary"].S().Length == 0) missing.Add("chưa có test-summary (test_run)");
                break;
            case "G4":
                if (!File.Exists(Path.Combine(dir, "dod-report.md"))) missing.Add("chưa có dod-report.md (dod_report)");
                break;
            case "G5":
            case "G6":
                break;
            default:
                throw new FlowException("gate phải là G1..G6");
        }
        if (missing.Count > 0) throw new FlowException($"chưa trình {g} được:\n- " + string.Join("\n- ", missing));

        var def = new FlowEngine(ctx).GateDef(g);
        status.Set("state", def["from"].S());
        if (g == "G1") status.Set("spec", "chờ G1");
        if (g == "G2") status.Set("plan", "chờ G2");
        status.Save();
        return $"{Path.GetFileName(dir)}: trình {g} — state {def["from"].S()}. Người duyệt: {new FlowEngine(ctx).Approvers(g)}. Điều kiện: {def["check"].S()}. DỪNG chờ /gate {g}.";
    }

    /// <summary>Người đã quyết task không cần suite: status.yaml test_design: "không cần — lý do (người)".</summary>
    internal static bool SuiteWaived(Y st) => Text.Fold(st["test_design"].S()).StartsWith("khong can");

    private string? SafeSuitesDir()
    {
        try { return new TestPaths(ctx).SuitesDir; } catch (FlowException) { return null; }
    }
}
