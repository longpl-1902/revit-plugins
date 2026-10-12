using System.ComponentModel;
using Hawee.Flow.Core.Checks;
using Hawee.Flow.Core.Docs;
using Hawee.Flow.Core.Tasks;
using Hawee.Flow.Core.Testing;
using ModelContextProtocol.Server;

namespace Hawee.Flow.Server.Tools;

[McpServerToolType]
public static class DocTools
{
    [McpServerTool(Name = "doc_scaffold"), Description("Dựng lại spec/plan/bug từ mẫu Base theo tier (khi đổi tier / CR lớn).")]
    public static string DocScaffoldTool(
        [Description("spec | plan | bug")] string kind, string task = "",
        [Description("Trống = tier trong status.yaml")] string tier = "", string title = "",
        [Description("true = ghi đè file đang có")] bool overwrite = false)
        => Safe.Run(() =>
        {
            var ctx = Safe.Ctx();
            var dir = ctx.ResolveTask(task);
            var st = StatusFile.ForTask(dir).Data;
            var t = tier.Length > 0 ? tier.ToUpperInvariant() : st["tier"].S("T2");
            var issue = st["issue"].S().TrimStart('#');
            var file = new DocScaffold(ctx).Write(dir, new ScaffoldInput(kind, t, title.Length > 0 ? title : st["topic"].S(), st["topic"].S(), issue, st["spec_version"].S("v1")), overwrite);
            return $"Đã dựng {ctx.Rel(file)} ({t})";
        });

    [McpServerTool(Name = "pack_render"), Description("Kiểm + render questions/pack-N.yaml, trả bản ngắn để gửi người. Mẫu YAML: skill spec references/pack-format.md.")]
    public static string PackRender([Description("vd. questions/pack-1.yaml")] string file, string task = "")
        => Safe.Run(() => new QuestionPack(Safe.Ctx()).Render(task, file));

    [McpServerTool(Name = "pack_apply"), Description("Áp câu trả lời 'Q1: B, Q2: mặc định' vào spec §14 / plan §1.2 + DEC, §12, §13. Trả bản đọc lại.")]
    public static string PackApply(
        [Description("vd. questions/pack-1.yaml")] string file,
        [Description("Câu trả lời nguyên văn của người")] string answers,
        [Description("Người trả lời (vai)")] string by,
        [Description("Nguồn: link chat / issue note")] string source = "chat",
        string task = "",
        [Description("false = câu không trả lời để mở (không áp mặc định)")] bool accept_defaults = true)
        => Safe.Run(() => new QuestionPack(Safe.Ctx()).Apply(task, file, answers, by, source, accept_defaults));

    [McpServerTool(Name = "doc_check"), Description("doc-lint gom theo mã + kiểm tham chiếu, phủ AC, bất biến, baseline. Ghi analyze-<ngày>.md.")]
    public static string DocCheckTool([Description("spec | plan | both")] string which = "both", string task = "")
        => Safe.Run(() => new DocCheck(Safe.Ctx()).Check(task, which));

    [McpServerTool(Name = "baseline_run"), Description("Bước 4a: test-baseline trên commit gốc → baseline.json + điền plan §1.3.")]
    public static string BaselineRun(string task = "", bool build = true,
        [Description("L0 | L1 … (trống = mọi bộ không cần host)")] string level = "",
        [Description("Tên bộ test, phân cách dấu phẩy")] string only = "")
        => Safe.Run(() => new Baseline(Safe.Ctx()).RunForPlan(task, build, level, only));
}

[McpServerToolType]
public static class TestDesignTools
{
    [McpServerTool(Name = "dataset_register"), Description("Bước 3d: chép folder dữ liệu vào kho (hash trước = sau), ghi dataset.yaml.")]
    public static string DatasetRegister(
        [Description("Đường dẫn folder người đưa")] string from,
        [Description("Người đưa — kiêm người cho phép dùng")] string by,
        [Description("nội bộ | khách hàng — được phép dùng nội bộ trên máy test")] string classification,
        [Description("Tên bộ (trống = slug tên folder)")] string name = "",
        string description = "", string host_version = "")
        => Safe.Run(() => new TestDesign(Safe.Ctx()).RegisterDataset(from, name, by, classification, description, host_version));

    [McpServerTool(Name = "dataset_check"), Description("Kiểm hash một bộ dữ liệu trong kho.")]
    public static string DatasetCheck(string name)
        => Safe.Run(() => new TestDesign(Safe.Ctx()).CheckDataset(name));

    [McpServerTool(Name = "suite_scaffold"), Description("Bước 3d: dựng folder suite từ mẫu profile (đã có → *.proposed.*), thêm vào status.suites.")]
    public static string SuiteScaffold(string task = "", [Description("Hậu tố suite, vd. bien")] string suffix = "",
        [Description("Tên bộ dữ liệu trong kho")] string dataset = "", string description = "")
        => Safe.Run(() => new TestDesign(Safe.Ctx()).ScaffoldSuite(task, suffix, dataset, description));

    [McpServerTool(Name = "suite_lint"), Description("Kiểm suite + luật + phủ AC; trả gọn.")]
    public static string SuiteLint(string task = "", [Description("Tên suite, phân cách dấu phẩy; trống = suites của task")] string suites = "", bool hash = true)
        => Safe.Run(() => new TestDesign(Safe.Ctx()).SuiteLint(task, suites, hash));

    [McpServerTool(Name = "matrix_build"), Description("Ma trận AC → suite → luật → kết quả trong test-design.md.")]
    public static string MatrixBuild(string task = "")
        => Safe.Run(() => new TestDesign(Safe.Ctx()).BuildMatrix(task));
}
