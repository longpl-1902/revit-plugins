using System.Text;
using Hawee.Flow.Core.Infra;
using Hawee.Flow.Core.Tasks;

namespace Hawee.Flow.Core.Flow;

/// <summary>flow.yaml (nhúng trong DLL; dự án ghi đè bằng .hawee-flow/flow.yaml ở gốc repo).</summary>
public static class FlowDef
{
    private static Y? _embedded;

    public static Y Load(ProjectContext? ctx = null)
    {
        if (ctx is not null)
        {
            var over = Path.Combine(ctx.Root, ".hawee-flow", "flow.yaml");
            if (File.Exists(over)) return Y.Load(over);
        }
        if (_embedded is null)
        {
            using var s = typeof(FlowDef).Assembly.GetManifestResourceStream("flow.yaml")
                          ?? throw new FlowException("thiếu flow.yaml nhúng");
            using var r = new StreamReader(s);
            _embedded = Y.Parse(r.ReadToEnd());
        }
        return _embedded;
    }
}

/// <summary>Trả "thẻ bước" ngắn cho task: đang ở đâu, cổng nào đã qua, việc kế tiếp, điểm dừng.</summary>
public sealed class FlowEngine(ProjectContext ctx)
{
    private readonly Y _flow = FlowDef.Load(ctx);

    public static readonly string[] Gates = { "G1", "G2", "G3", "G4", "G5", "G6" };

    public string Status(string? task)
    {
        var dir = ctx.ResolveTask(task);
        var st = StatusFile.ForTask(dir).Data;
        var sb = new StringBuilder();
        sb.AppendLine($"Task {Path.GetFileName(dir)} · {st["tier"].S("T2")} · state {st["state"].S("context")} · spec: {st["spec"].S("-")} · plan: {st["plan"].S("-")}");
        sb.AppendLine("Cổng: " + string.Join(" · ", Gates.Select(g => GateShort(st, g))));
        var loops = st["loops"];
        sb.AppendLine($"Vòng sửa: build/test {loops["build_test"].Int()} · Revit {loops["host_test"].Int()} · lệch plan {st["deviations"].Int()} · autonomy {st["autonomy"].S("ask")}");
        var suites = st["suites"].Strings();
        if (suites.Count > 0) sb.AppendLine($"Suite: {string.Join(", ", suites)}{(st["suite_hashes"].Entries.Any() ? " (đã khoá hash ở G2)" : "")}");
        var oq = st["open_questions"].Strings();
        if (oq.Count > 0) sb.AppendLine($"Câu CHẶN mở: {string.Join(", ", oq)}");
        if (st["test_summary"].S() is { Length: > 0 } ts) sb.AppendLine($"test-summary: {ts}");
        return sb.ToString().TrimEnd();
    }

    public string Next(string? task)
    {
        var dir = ctx.ResolveTask(task);
        var st = StatusFile.ForTask(dir).Data;
        var state = st["state"].S("context");
        var node = _flow["states"][state];
        if (!node.Exists) throw new FlowException($"state '{state}' không có trong flow.yaml");
        var sb = new StringBuilder();
        sb.AppendLine($"Bước {node["step"].S()} — {node["name"].S()}{(node["skill"].S() is { Length: > 0 } sk ? $" ({sk})" : "")} · task {Path.GetFileName(dir)} · {st["tier"].S("T2")}");
        foreach (var d in node["do"].Strings()) sb.AppendLine("- " + d);
        if (node["tools"].Strings() is { Count: > 0 } tools) sb.AppendLine("Tool: " + string.Join(", ", tools));
        if (node["limits"].S() is { Length: > 0 } lim) sb.AppendLine("Giới hạn: " + lim);
        if (node["gate"].S() is { Length: > 0 } gate)
        {
            var g = _flow["gates"][gate];
            sb.AppendLine($"Điều kiện {gate}: {g["check"].S()}");
            sb.AppendLine($"Người duyệt: {Approvers(gate)}");
        }
        foreach (var h in Hints(dir, st, state)) sb.AppendLine("! " + h);
        if (node["stop"].S() is { Length: > 0 } stop) sb.AppendLine("Dừng: " + stop);
        return sb.ToString().TrimEnd();
    }

    public string TierTable()
    {
        var sb = new StringBuilder("Tier | khi nào | spec | plan | rà độc lập\n");
        foreach (var (k, v) in _flow["tiers"].Entries)
            sb.AppendLine($"{k} | {v["when"].S()} | {v["spec"].S()} | {v["plan"].S()} | {v["review"].S()}");
        return sb.ToString().TrimEnd();
    }

    public Y GateDef(string gate) => _flow["gates"][gate];
    public Y Sections(string kind, string tier) => _flow["sections"][kind][tier];
    public int Limit(string key, int fallback) => _flow["limits"][key].Int(fallback);

    public string Approvers(string gate)
    {
        var g = ctx.Config["gates"][gate];
        if (g.IsMap) return g["approver"].S(string.Join(", ", g["approvers"].Strings()));
        var list = g.Strings();
        return list.Count > 0 ? string.Join(", ", list) : "(chưa khai trong hawee.project.yaml → gates)";
    }

    private static string GateShort(Y st, string g)
    {
        var d = st["gates"][g]["decision"].S();
        return d.Length == 0 ? $"{g} —" : $"{g} {d}";
    }

    /// <summary>Nhắc việc còn thiếu dựa trên dữ liệu thật của task (không phải văn bản quy trình).</summary>
    private IEnumerable<string> Hints(string dir, Y st, string state)
    {
        var tier = st["tier"].S("T2");
        var hasHostTest = ctx.TestBlock.Exists;
        if (state is "context" && !File.Exists(Path.Combine(dir, "context.md")))
            yield return "chưa có context.md — gọi context_pack";
        if (state is "spec" or "g1" && st["open_questions"].Strings().Count > 0)
            yield return "còn câu CHẶN mở — không trình G1 được";
        if (state is "plan" or "g2")
        {
            if (st["baseline"].S().Length == 0 && tier != "T0") yield return "chưa có baseline — gọi baseline_run trên commit gốc";
            if (hasHostTest && st["suites"].Strings().Count == 0) yield return "chưa có suite — nếu spec có AC đo bằng suite: /test-design trước plan";
            if (state == "g2" && st["autonomy"].S().Length == 0) yield return "chưa chốt mức tự chủ ask/night";
        }
        if (state is "code" && st["loops"]["build_test"].Int() >= Limit("build_test_loops", 5))
            yield return "đã hết 5 vòng sửa build/test — dừng, báo người";
        if (state is "test" or "g3")
        {
            var max = ctx.TestBlock.At("fix_loop.max_runs").Int(0);
            var fixes = max > 0 ? max - 1 : Limit("host_test_fixes_default", 3);
            yield return $"lượt sửa trong Revit: {st["loops"]["host_test"].Int()}/{fixes}";
        }
        if (state is "commit" or "g4" && !File.Exists(Path.Combine(dir, "dod-report.md")))
            yield return "chưa có dod-report.md — gọi dod_report";
    }
}
