using System.ComponentModel;
using Hawee.Flow.Core.Flow;
using Hawee.Flow.Core.Infra;
using Hawee.Flow.Core.Knowledge;
using Hawee.Flow.Core.Tasks;
using ModelContextProtocol.Server;

namespace Hawee.Flow.Server.Tools;

/// <summary>Chạy một tool: lỗi nghiệp vụ trả về một dòng "LỖI:" thay vì ném exception (agent đọc ngắn).</summary>
internal static class Safe
{
    public static string Run(Func<string> f)
    {
        try { return f(); }
        catch (FlowException ex) { return "LỖI: " + ex.Message; }
        catch (Exception ex) { return $"LỖI ({ex.GetType().Name}): {ex.Message}"; }
    }

    public static ProjectContext Ctx(string? repo) => ProjectContext.Open(string.IsNullOrWhiteSpace(repo) ? null : repo);
}

[McpServerToolType]
public static class FlowTools
{
    [McpServerTool(Name = "flow_status"), Description("Trạng thái ngắn của task: tier, state, cổng G1–G6, vòng sửa, suite, câu CHẶN mở. Bỏ trống task = task đang làm gần nhất.")]
    public static string FlowStatus(
        [Description("Tên/số task, vd. 42591 hoặc 42591-crop-gap; trống = task gần nhất")] string task = "",
        [Description("Gốc repo (trống = thư mục hiện tại)")] string repo = "")
        => Safe.Run(() => new FlowEngine(Safe.Ctx(repo)).Status(task));

    [McpServerTool(Name = "flow_next"), Description("Việc phải làm ở bước hiện tại của task (≤ 15 dòng): việc, tool dùng, điều kiện cổng, nhắc việc còn thiếu, điểm DỪNG. Gọi đầu mỗi phiên thay vì đọc tài liệu quy trình.")]
    public static string FlowNext(string task = "", string repo = "")
        => Safe.Run(() => new FlowEngine(Safe.Ctx(repo)).Next(task));

    [McpServerTool(Name = "task_open"), Description("Bước 2: tạo folder task (status.yaml, spec/plan dựng sẵn theo tier, questions/) và context.md (chuỗi chủ đề, bản đồ code, rule, KI, KB). Không ghi đè task có sẵn.")]
    public static string TaskOpen(
        [Description("Số issue Redmine (trống = 0000)")] string issue,
        [Description("slug kebab-case ASCII, vd. crop-gap")] string slug,
        [Description("Tiêu đề ngắn")] string title = "",
        [Description("T0|T1|T2|T3 — tier đề xuất (người xác nhận ở Q0/G1)")] string tier = "T2",
        [Description("true nếu là bug (dùng bugfix-spec)")] bool bug = false,
        string owner = "",
        [Description("Task gốc khi đây là CR")] string cr_of = "",
        [Description("Từ khoá thêm để tìm rule/KI/KB (tên phân hệ, lệnh, tool)")] string keywords = "",
        string repo = "")
        => Safe.Run(() => new TaskOpener(Safe.Ctx(repo)).Open(new OpenInput(issue, slug, title, bug, tier, owner, "claude-code", cr_of, keywords)));

    [McpServerTool(Name = "context_pack"), Description("Gom lại ngữ cảnh của task theo từ khoá mới và ghi đè context.md. Trả nội dung context.md (≤ ~80 dòng).")]
    public static string ContextPackTool(string task = "", [Description("Từ khoá thêm")] string keywords = "", string repo = "")
        => Safe.Run(() => new ContextPack(Safe.Ctx(repo)).Build(task, keywords));

    [McpServerTool(Name = "gate_record"), Description("Ghi quyết định cổng G1–G6 (status.yaml + DEC trong decision log) và chuyển state. CHỈ khi người duyệt đã nói rõ quyết định; hook sẽ hỏi lại người trước khi chạy.")]
    public static string GateRecord(
        [Description("G1..G6")] string gate,
        [Description("PASS | CONCERNS | FAIL | WAIVED")] string decision,
        [Description("Tên người duyệt (vai)")] string by,
        [Description("Bằng chứng: link issue/MR/chat, file")] string evidence,
        string task = "",
        [Description("Bắt buộc với WAIVED")] string reason = "",
        [Description("G2: người sẽ code xác nhận đủ để code")] bool coder_confirmed = false,
        [Description("G3: KI chặn khi chấp nhận có điều kiện")] string conditional_on = "",
        [Description("G4: hash commit, phân cách bằng dấu phẩy")] string commits = "",
        [Description("G5: đường dẫn review-<ngày>.md")] string review_record = "",
        [Description("G6: tên bản phát hành")] string release = "",
        string repo = "")
        => Safe.Run(() => new GateRecorder(Safe.Ctx(repo)).Record(task,
            new GateInput(gate, decision, by, evidence, reason, coder_confirmed, conditional_on, commits, review_record, release)));
}

[McpServerToolType]
public static class KnowledgeTools
{
    [McpServerTool(Name = "rule_for"), Description("Rule liên quan theo từ khoá hoặc file sắp sửa (1 dòng/rule: ID — tên · mức). Thay cho đọc cả general.md / mcp.md.")]
    public static string RuleFor(
        [Description("Từ khoá, vd. 'transaction view template'")] string keywords = "",
        [Description("Đường dẫn file sắp sửa, phân cách dấu phẩy")] string paths = "",
        int take = 8, string repo = "")
        => Safe.Run(() =>
        {
            var words = new List<string> { keywords };
            words.AddRange(paths.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .SelectMany(p => p.Trim().Split('/', '\\', '.')).Where(s => s.Length > 2));
            var hits = new RulesIndex(Safe.Ctx(repo)).Search(words, take).ToList();
            return hits.Count == 0 ? "không có rule khớp" : string.Join('\n', hits.Select(h => RulesIndex.Line(h.Rule)));
        });

    [McpServerTool(Name = "rule_get"), Description("Nội dung một rule theo ID (Khi / Phải / ngoại lệ), vd. REVIT-THREAD-001.")]
    public static string RuleGet(string id, [Description("true = cả nội dung; false = Khi + Phải")] bool full = false, string repo = "")
        => Safe.Run(() =>
        {
            var r = new RulesIndex(Safe.Ctx(repo)).Get(id) ?? throw new FlowException($"không có rule {id}");
            return full
                ? $"{RulesIndex.Line(r)} ({r.File})\n{r.Body}"
                : $"{RulesIndex.Line(r)} ({r.File})\nKhi: {(r.When.Length > 0 ? r.When : "—")}\nPhải: {(r.Must.Length > 0 ? r.Must : Text.Clip(r.Body, 400))}";
        });

    [McpServerTool(Name = "kb_search"), Description("Tìm KB (bài học LL-, mẫu PT-, trang vendor K-) và known-issue theo từ khoá; 1 dòng/kết quả.")]
    public static string KbSearchTool(string keywords, int take = 6, string repo = "")
        => Safe.Run(() =>
        {
            var ctx = Safe.Ctx(repo);
            var kb = new KbSearch(ctx);
            var lines = kb.Search(new[] { keywords }, take).Select(h => $"{h.Id} — {h.Title} ({h.Path})")
                .Concat(kb.SearchKi(new[] { keywords }, take).Select(k => $"{k.Id} — {k.Title} · {k.State} {k.Level} ({k.File})"))
                .ToList();
            return lines.Count == 0 ? "không có kết quả" : string.Join('\n', lines);
        });
}
