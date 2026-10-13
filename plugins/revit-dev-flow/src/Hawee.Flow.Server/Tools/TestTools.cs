using System.ComponentModel;
using Hawee.Flow.Core.Infra;
using Hawee.Flow.Core.Testing;
using ModelContextProtocol.Server;

namespace Hawee.Flow.Server.Tools;

[McpServerToolType]
public static class TestTools
{
    private static async Task<string> SafeAsync(Func<Task<string>> f)
    {
        try { return await f(); }
        catch (FlowException ex) { return "LỖI: " + ex.Message; }
        catch (Exception ex) { return $"LỖI ({ex.GetType().Name}): {ex.Message}"; }
    }

    [McpServerTool(Name = "test_run"), Description("Bước 7: chạy suite trong Revit trọn vòng bằng máy, trả ≤ 25 dòng. Chạy lâu — không gọi lại khi đang chạy.")]
    public static Task<string> TestRun(
        [Description("Tên folder suite trong revit_test.suites_dir")] string suite,
        [Description("Task (để khoá hash G2, phiên, vòng sửa); trống = chạy lẻ")] string task = "",
        [Description("task | baseline | adhoc")] string purpose = "task",
        [Description("Phiên kiểm chứng; trống = phiên đang có của task hoặc phiên mới")] string session = "",
        [Description("ask | night; trống = status.yaml.autonomy")] string autonomy = "",
        [Description("true = chạy fix_loop.build và kiểm DLL trước khi mở Revit (Revit phải đang đóng)")] bool build = false,
        [Description("true = bắt đầu phiên mới")] bool new_session = false)
        => SafeAsync(async () =>
        {
            var ctx = Safe.Ctx();
            var cfg = RunnerConfig.From(ctx);
            var runner = new TestRunner(ctx, cfg, async name => await McpToolClient.Connect(ctx, name), new WindowsHostProcess(), ProcRunner.Instance);
            return await runner.Run(new RunRequest(suite, purpose, session, autonomy, build, task, new_session));
        });

    [McpServerTool(Name = "get_detail"), Description("Chi tiết một lần chạy: kind = rule | log | finding | diagnosis | steps | summary | files.")]
    public static string GetDetail([Description("Tên suite; trống = suite của lần test_run gần nhất")] string suite = "",
        [Description("rule | log | finding | diagnosis | steps | summary | files")] string kind = "summary",
        [Description("R-xx (rule), số dòng (log), mã phát hiện / step (finding)")] string @ref = "",
        [Description("Trống = lần chạy mới nhất")] string run = "", int window = 15)
        => Safe.Run(() =>
        {
            var ctx = Safe.Ctx();
            return new RunStore(ctx, RunnerConfig.From(ctx)).Detail(suite, run, kind, @ref, window);
        });

    [McpServerTool(Name = "record_verdict"), Description("Agent chấm luật cần mắt sau khi xem ảnh/dữ liệu; chạy lại test-summary.")]
    public static string RecordVerdict([Description("R-xx")] string rule = "", [Description("PASS | FAIL | SUSPECT | NOT_MEASURABLE")] string verdict = "",
        [Description("Đã kiểm gì, thấy gì (bằng chứng)")] string note = "", [Description("Tên suite; trống = suite của lần test_run gần nhất")] string suite = "",
        [Description("Sheet; trống = mọi sheet của luật")] string sheet = "",
        string run = "")
        => Safe.Run(() =>
        {
            var ctx = Safe.Ctx();
            return new RunStore(ctx, RunnerConfig.From(ctx)).RecordVerdict(suite, run, rule, sheet, verdict, note);
        });

    [McpServerTool(Name = "diagnosis_set"), Description("Xác nhận / sửa chẩn đoán lỗi gốc (máy chỉ gợi ý); chạy lại test-summary.")]
    public static string DiagnosisSet([Description("CODE | INPUT | TRACKING | ENV | DECISION | UNKNOWN")] string @class = "",
        [Description("file:dòng hoặc dòng log nguyên văn")] string evidence = "", [Description("Tên suite; trống = suite của lần test_run gần nhất")] string suite = "",
        [Description("trống = máy tính từ evidence × diff của task; khai khác máy → lỗi")] string in_task_diff = "", [Description("KI-… nếu thuộc known-issue; trống = giữ, - = xoá")] string ki = "",
        [Description("night: true | false")] string blocks_task = "", [Description("night: code | test_data")] string fix_target = "",
        string run = "")
        => Safe.Run(() =>
        {
            var ctx = Safe.Ctx();
            return new RunStore(ctx, RunnerConfig.From(ctx)).SetDiagnosis(suite, run, @class, evidence, in_task_diff, ki, blocks_task, fix_target);
        });

    [McpServerTool(Name = "issue_note_draft"), Description("Kiểm đủ điều kiện gửi issue và dựng ghi chú + danh sách ảnh minh chứng.")]
    public static string IssueNoteDraft(string task = "")
        => Safe.Run(() =>
        {
            var ctx = Safe.Ctx();
            return new RunStore(ctx, RunnerConfig.From(ctx)).IssueNote(task);
        });
}
