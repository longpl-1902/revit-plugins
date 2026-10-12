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
    [McpServerTool(Name = "doc_scaffold"), Description("Dựng lại spec.md / plan.md / bugfix-spec từ mẫu Base theo tier (mục ngoài tier ghi Không áp dụng). Dùng khi đổi tier hoặc CR lớn; mặc định không ghi đè.")]
    public static string DocScaffoldTool(
        [Description("spec | plan | bug")] string kind, string task = "",
        [Description("Trống = tier trong status.yaml")] string tier = "", string title = "",
        [Description("true = ghi đè file đang có")] bool overwrite = false, string repo = "")
        => Safe.Run(() =>
        {
            var ctx = Safe.Ctx(repo);
            var dir = ctx.ResolveTask(task);
            var st = StatusFile.ForTask(dir).Data;
            var t = tier.Length > 0 ? tier.ToUpperInvariant() : st["tier"].S("T2");
            var issue = st["issue"].S().TrimStart('#');
            var file = new DocScaffold(ctx).Write(dir, new ScaffoldInput(kind, t, title.Length > 0 ? title : st["topic"].S(), st["topic"].S(), issue, st["spec_version"].S("v1")), overwrite);
            return $"Đã dựng {ctx.Rel(file)} ({t})";
        });

    [McpServerTool(Name = "pack_render"), Description("Gói câu hỏi: đọc questions/pack-N.yaml (agent viết: doc, round, answerers, read_before, questions[id,q,kind,seen,why,options{A,B},recommend,because,default|CHẶN,decider,applies,sets?,values?], extras[]), kiểm (≤ giới hạn tier, có bằng chứng, có mặc định), ghi pack-N.md, trả bản ngắn để gửi người. Xong thì DỪNG chờ trả lời.")]
    public static string PackRender([Description("vd. questions/pack-1.yaml")] string file, string task = "", string repo = "")
        => Safe.Run(() => new QuestionPack(Safe.Ctx(repo)).Render(task, file));

    [McpServerTool(Name = "pack_apply"), Description("Áp câu trả lời của người (\"Q1: B, Q2: mặc định, Q3: xem lại — …, D1: bỏ\") vào spec §14 / plan §1.2 / bug §7 + DEC; câu không trả lời có mặc định → giả định §12; CHẶN → §13 + câu treo. Trả bản 'tôi hiểu là …' để đọc lại cho người.")]
    public static string PackApply(
        [Description("vd. questions/pack-1.yaml")] string file,
        [Description("Câu trả lời nguyên văn của người")] string answers,
        [Description("Người trả lời (vai)")] string by,
        [Description("Nguồn: link chat / issue note")] string source = "chat",
        string task = "",
        [Description("false = câu không trả lời để mở (không áp mặc định)")] bool accept_defaults = true,
        string repo = "")
        => Safe.Run(() => new QuestionPack(Safe.Ctx(repo)).Apply(task, file, answers, by, source, accept_defaults));

    [McpServerTool(Name = "doc_check"), Description("Kiểm spec/plan bằng máy: doc-lint (gom theo mã) + tham chiếu file:dòng / rule / KI có thật, AC phủ ma trận §7, bất biến có test, baseline có số, UNKNOWN còn mở. Ghi analyze-<ngày>.md, trả ≤ 20 dòng.")]
    public static string DocCheckTool([Description("spec | plan | both")] string which = "both", string task = "", string repo = "")
        => Safe.Run(() => new DocCheck(Safe.Ctx(repo)).Check(task, which));

    [McpServerTool(Name = "baseline_run"), Description("Bước 4a: chạy test-baseline (build 2 sln + test) trên commit gốc, lưu baseline.json vào folder task, điền bảng plan §1.3, liệt kê fail sẵn có cần KI.")]
    public static string BaselineRun(string task = "", bool build = true,
        [Description("L0 | L1 … (trống = mọi bộ không cần host)")] string level = "",
        [Description("Tên bộ test, phân cách dấu phẩy")] string only = "", string repo = "")
        => Safe.Run(() => new Baseline(Safe.Ctx(repo)).RunForPlan(task, build, level, only));
}

[McpServerToolType]
public static class TestDesignTools
{
    [McpServerTool(Name = "dataset_register"), Description("Bước 3d: chép nguyên folder dữ liệu người đưa vào kho datasets_dir\\<bộ> (hash nguồn trước = sau = bản chép), ghi dataset.yaml (files + sha256, role, số dòng CSV). Không ghi đè bộ có sẵn.")]
    public static string DatasetRegister(
        [Description("Đường dẫn folder người đưa")] string from,
        [Description("Người đưa — kiêm người cho phép dùng")] string by,
        [Description("nội bộ | khách hàng — được phép dùng nội bộ trên máy test")] string classification,
        [Description("Tên bộ (trống = slug tên folder)")] string name = "",
        string description = "", string host_version = "", string repo = "")
        => Safe.Run(() => new TestDesign(Safe.Ctx(repo)).RegisterDataset(from, name, by, classification, description, host_version));

    [McpServerTool(Name = "dataset_check"), Description("Kiểm một bộ dữ liệu có sẵn trong kho (suite-lint -Dataset -Hash): file bị sửa thì báo.")]
    public static string DatasetCheck(string name, string repo = "")
        => Safe.Run(() => new TestDesign(Safe.Ctx(repo)).CheckDataset(name));

    [McpServerTool(Name = "suite_scaffold"), Description("Bước 3d: dựng folder suite <task>[-hậu tố] ngoài repo từ mẫu profile (suite.yaml + rules.md, điền spec/dataset/model); suite đã có → *.proposed.*. Thêm vào status.yaml.suites.")]
    public static string SuiteScaffold(string task = "", [Description("Hậu tố suite, vd. bien")] string suffix = "",
        [Description("Tên bộ dữ liệu trong kho")] string dataset = "", string description = "", string repo = "")
        => Safe.Run(() => new TestDesign(Safe.Ctx(repo)).ScaffoldSuite(task, suffix, dataset, description));

    [McpServerTool(Name = "suite_lint"), Description("Kiểm suite (cú pháp, đường dẫn, dataset hash, tool cấm, trường luật, 'chờ', phủ AC theo spec). Trả gọn; luật 'chờ' chỉ đếm.")]
    public static string SuiteLint(string task = "", [Description("Tên suite, phân cách dấu phẩy; trống = suites của task")] string suites = "", bool hash = true, string repo = "")
        => Safe.Run(() => new TestDesign(Safe.Ctx(repo)).SuiteLint(task, suites, hash));

    [McpServerTool(Name = "matrix_build"), Description("Bước 3d: dựng ma trận AC → suite → luật (nguồn kỳ vọng) → kết quả trong test-design.md từ spec §5 + rules.md; báo AC không phủ, luật 'chờ', cần baseline.")]
    public static string MatrixBuild(string task = "", string repo = "")
        => Safe.Run(() => new TestDesign(Safe.Ctx(repo)).BuildMatrix(task));
}
