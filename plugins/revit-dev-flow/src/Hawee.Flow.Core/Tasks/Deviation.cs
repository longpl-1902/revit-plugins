using Hawee.Flow.Core.Docs;
using Hawee.Flow.Core.Infra;

namespace Hawee.Flow.Core.Tasks;

/// <summary>Lệch so với plan (spec-plan §4): ghi plan §13, tăng deviations; lệch lớn → quay lại G2 (hoặc G1 khi đổi hành vi).</summary>
public sealed class Deviation(ProjectContext ctx)
{
    public string Add(string? task, string what, string why, string level, string regate)
    {
        var dir = ctx.ResolveTask(task);
        level = level.Trim().ToLowerInvariant();
        if (level is not ("nhỏ" or "nho" or "small" or "lớn" or "lon" or "large")) throw new FlowException("level: nhỏ | lớn");
        var large = level is "lớn" or "lon" or "large";
        regate = regate.Trim().ToUpperInvariant();
        if (large && regate is not ("G1" or "G2")) throw new FlowException("lệch lớn phải quay lại G2 (hoặc G1 khi đổi hành vi) — regate: G1 | G2");

        var plan = new MdDoc(Path.Combine(dir, "plan.md"));
        plan.AppendRows("13", new[]
        {
            $"| {ctx.Clock.Now:yyyy-MM-dd} | {Text.Cell(what)} | {Text.Cell(why)} | {(large ? "lớn" : "nhỏ")} | {(large ? regate : "không")} |",
        });
        plan.Save();
        var status = StatusFile.ForTask(dir);
        status.Set("deviations", status.Data["deviations"].Int() + 1);
        if (large)
        {
            status.Set("state", regate == "G1" ? "spec" : "plan");
            if (regate == "G1") status.Set("spec", "đang trao đổi (CR sau lệch lớn)");
            else status.Set("plan", "đang chốt thiết kế");
        }
        status.Save();
        return large
            ? $"Lệch LỚN ghi plan §13 · state → {(regate == "G1" ? "spec" : "plan")}. DỪNG code, báo người; sửa {(regate == "G1" ? "spec" : "plan")} rồi trình lại {regate}."
            : "Lệch nhỏ ghi plan §13 — đi tiếp, báo ở G5.";
    }
}
