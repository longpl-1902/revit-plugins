using System.ComponentModel;
using Hawee.Flow.Core.Checks;
using Hawee.Flow.Core.Tasks;
using ModelContextProtocol.Server;

namespace Hawee.Flow.Server.Tools;

[McpServerToolType]
public static class CheckTools
{
    [McpServerTool(Name = "check_all"), Description("Build + L0 so baseline + lint (quick | full); đếm vòng sửa (≤ 5).")]
    public static string CheckAllTool(string task = "", [Description("quick | full")] string scope = "quick",
        [Description("Nhánh tích hợp, vd. origin/DEV (trống = tự tìm)")] string base_ref = "",
        [Description("false = không tính vòng sửa (chạy thử)")] bool count_loop = true)
        => Safe.Run(() =>
        {
            var c = new CheckAll(Safe.Ctx());
            return c.Summarize(c.Run(task, scope == "full" ? "full" : "quick", base_ref, count_loop));
        });

    [McpServerTool(Name = "dod_report"), Description("Bước 8: check_all full + kiểm DoD → dod-report.md + commit nháp. DỪNG chờ G4.")]
    public static string DodReportTool(string task = "", string base_ref = "")
        => Safe.Run(() => new DodReport(Safe.Ctx()).Build(task, base_ref));

    [McpServerTool(Name = "deviation_add"), Description("Ghi lệch plan §13; lệch lớn quay lại G2/G1 và DỪNG.")]
    public static string DeviationAdd(string what, string why, [Description("nhỏ | lớn")] string level,
        [Description("Lệch lớn: G2 | G1")] string regate = "", string task = "")
        => Safe.Run(() => new Deviation(Safe.Ctx()).Add(task, what, why, level, regate));
}

[McpServerToolType]
public static class ClosingTools
{
    [McpServerTool(Name = "review_packet"), Description("Bước 9: gói review (checklist, AC, plan §5/§7, diff) cho agent adversarial-reviewer.")]
    public static string ReviewPacket(string task = "", string base_ref = "", int max_diff_lines = 1500)
        => Safe.Run(() => new Closing(Safe.Ctx()).ReviewPacket(task, base_ref, max_diff_lines));

    [McpServerTool(Name = "handoff_draft"), Description("Bước 10: dựng handoff từ git/status/dod/test; agent điền ô <…>.")]
    public static string HandoffDraft(string task = "")
        => Safe.Run(() => new Closing(Safe.Ctx()).HandoffDraft(task));

    [McpServerTool(Name = "ki_report"), Description("Thống kê known-issue gọn.")]
    public static string KiReport()
        => Safe.Run(() => new Closing(Safe.Ctx()).KiReport());
}
