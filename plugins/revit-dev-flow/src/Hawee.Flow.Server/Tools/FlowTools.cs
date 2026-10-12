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

    /// <summary>Repo dự án: HAWEE_FLOW_REPO → CLAUDE_PROJECT_DIR → thư mục hiện tại (đi lên tới hawee.project.yaml).</summary>
    public static ProjectContext Ctx() =>
        ProjectContext.Open(Environment.GetEnvironmentVariable("HAWEE_FLOW_REPO") is { Length: > 0 } r ? r : null);
}

[McpServerToolType]
public static class FlowTools
{
    [McpServerTool(Name = "flow_status"), Description("Trạng thái task: tier, state, cổng, vòng sửa, câu CHẶN. task trống = task gần nhất.")]
    public static string FlowStatus(
        string task = "")
        => Safe.Run(() => new FlowEngine(Safe.Ctx()).Status(task));

    [McpServerTool(Name = "flow_next"), Description("Việc của bước hiện tại + tool + điểm DỪNG. Gọi đầu mỗi phiên thay vì đọc tài liệu quy trình.")]
    public static string FlowNext(string task = "")
        => Safe.Run(() => new FlowEngine(Safe.Ctx()).Next(task));

    [McpServerTool(Name = "task_open"), Description("Bước 2: tạo folder task (status, spec/plan theo tier) + context.md. Không ghi đè.")]
    public static string TaskOpen(
        [Description("Số issue Redmine (trống = 0000)")] string issue,
        [Description("slug kebab-case ASCII, vd. crop-gap")] string slug,
        [Description("Tiêu đề ngắn")] string title = "",
        [Description("T0|T1|T2|T3 — tier đề xuất (người xác nhận ở Q0/G1)")] string tier = "T2",
        [Description("true nếu là bug (dùng bugfix-spec)")] bool bug = false,
        string owner = "",
        [Description("Task gốc khi đây là CR")] string cr_of = "",
        [Description("Từ khoá thêm để tìm rule/KI/KB (tên phân hệ, lệnh, tool)")] string keywords = "")
        => Safe.Run(() => new TaskOpener(Safe.Ctx()).Open(new OpenInput(issue, slug, title, bug, tier, owner, "claude-code", cr_of, keywords)));

    [McpServerTool(Name = "context_pack"), Description("Gom lại context.md (chủ đề, bản đồ code, rule, KI, KB) theo từ khoá mới.")]
    public static string ContextPackTool(string task = "", [Description("Từ khoá thêm")] string keywords = "")
        => Safe.Run(() => new ContextPack(Safe.Ctx()).Build(task, keywords));

    [McpServerTool(Name = "gate_submit"), Description("Trình cổng: máy kiểm điều kiện cứng rồi đặt state chờ duyệt. Không ghi quyết định.")]
    public static string GateSubmitTool([Description("G1..G6")] string gate, string task = "")
        => Safe.Run(() => new GateSubmit(Safe.Ctx()).Submit(task, gate));

    [McpServerTool(Name = "gate_record"), Description("Ghi quyết định cổng (status + DEC). Chỉ khi người duyệt đã nói rõ; hook hỏi lại người.")]
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
        [Description("G6: tên bản phát hành")] string release = "")
        => Safe.Run(() => new GateRecorder(Safe.Ctx()).Record(task,
            new GateInput(gate, decision, by, evidence, reason, coder_confirmed, conditional_on, commits, review_record, release)));
}

[McpServerToolType]
public static class KnowledgeTools
{
    [McpServerTool(Name = "rule_for"), Description("Rule liên quan theo từ khoá / file sắp sửa, 1 dòng mỗi rule.")]
    public static string RuleFor(
        [Description("Từ khoá, vd. 'transaction view template'")] string keywords = "",
        [Description("Đường dẫn file sắp sửa, phân cách dấu phẩy")] string paths = "",
        int take = 8)
        => Safe.Run(() =>
        {
            var words = new List<string> { keywords };
            words.AddRange(paths.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .SelectMany(p => p.Trim().Split('/', '\\', '.')).Where(s => s.Length > 2));
            var hits = new RulesIndex(Safe.Ctx()).Search(words, take).ToList();
            return hits.Count == 0 ? "không có rule khớp" : string.Join('\n', hits.Select(h => RulesIndex.Line(h.Rule)));
        });

    [McpServerTool(Name = "rule_get"), Description("Nội dung rule theo ID (Khi / Phải).")]
    public static string RuleGet(string id, [Description("true = cả nội dung; false = Khi + Phải")] bool full = false)
        => Safe.Run(() =>
        {
            var r = new RulesIndex(Safe.Ctx()).Get(id) ?? throw new FlowException($"không có rule {id}");
            return full
                ? $"{RulesIndex.Line(r)} ({r.File})\n{r.Body}"
                : $"{RulesIndex.Line(r)} ({r.File})\nKhi: {(r.When.Length > 0 ? r.When : "—")}\nPhải: {(r.Must.Length > 0 ? r.Must : Text.Clip(r.Body, 400))}";
        });

    [McpServerTool(Name = "kb_search"), Description("Tìm KB (LL-/PT-/K-) và known-issue theo từ khoá.")]
    public static string KbSearchTool(string keywords, int take = 6)
        => Safe.Run(() =>
        {
            var ctx = Safe.Ctx();
            var kb = new KbSearch(ctx);
            var lines = kb.Search(new[] { keywords }, take).Select(h => $"{h.Id} — {h.Title} ({h.Path})")
                .Concat(kb.SearchKi(new[] { keywords }, take).Select(k => $"{k.Id} — {k.Title} · {k.State} {k.Level} ({k.File})"))
                .ToList();
            return lines.Count == 0 ? "không có kết quả" : string.Join('\n', lines);
        });
}
