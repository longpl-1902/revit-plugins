using System.ComponentModel;
using Hawee.Flow.Core.Checks;
using Hawee.Flow.Core.Tasks;
using ModelContextProtocol.Server;

namespace Hawee.Flow.Server.Tools;

[McpServerToolType]
public static class CheckTools
{
    [McpServerTool(Name = "check_all"), Description("Bước 5–6: build 2 sln + test L0 so baseline (fail mới theo tên test) + lint (quick: code-lint -Diff, csproj-twin-lint; full: + kb-lint, project-lint, doc-lint, codemap-lint). Có lỗi → tăng vòng sửa (tối đa 5). Trả gọn; chi tiết ở checks-last.json.")]
    public static string CheckAllTool(string task = "", [Description("quick | full")] string scope = "quick",
        [Description("Nhánh tích hợp, vd. origin/DEV (trống = tự tìm)")] string base_ref = "",
        [Description("false = không tính vòng sửa (chạy thử)")] bool count_loop = true, string repo = "")
        => Safe.Run(() =>
        {
            var c = new CheckAll(Safe.Ctx(repo));
            return c.Summarize(c.Run(task, scope == "full" ? "full" : "quick", base_ref, count_loop));
        });

    [McpServerTool(Name = "dod_report"), Description("Bước 8: check_all full + kiểm DoD riêng (test hồi quy cho bug, MCP registry, bản đồ code) → dod-report.md, commit message nháp, state commit. Không commit; DỪNG chờ G4.")]
    public static string DodReportTool(string task = "", string base_ref = "", string repo = "")
        => Safe.Run(() => new DodReport(Safe.Ctx(repo)).Build(task, base_ref));

    [McpServerTool(Name = "deviation_add"), Description("Ghi lệch so với plan vào plan §13 và tăng deviations. Lớn (đổi chữ ký có caller ngoài file, đổi hành vi, contract/cấu hình dùng chung, kịch bản mới) → quay lại G2 hoặc G1 và DỪNG.")]
    public static string DeviationAdd(string what, string why, [Description("nhỏ | lớn")] string level,
        [Description("Lệch lớn: G2 | G1")] string regate = "", string task = "", string repo = "")
        => Safe.Run(() => new Deviation(Safe.Ctx(repo)).Add(task, what, why, level, regate));
}
