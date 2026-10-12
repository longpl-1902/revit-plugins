using Hawee.Flow.Core.Docs;
using Hawee.Flow.Core.Infra;

namespace Hawee.Flow.Core.Tasks;

public sealed record OpenInput(
    string IssueId, string Slug, string Title = "", bool Bug = false, string Tier = "T2",
    string Owner = "", string Agent = "claude-code", string CrOf = "", string Keywords = "");

/// <summary>Bước 2: tạo folder task (status.yaml, spec/plan dựng theo tier, questions/), rồi gom ngữ cảnh.</summary>
public sealed class TaskOpener(ProjectContext ctx, IProcRunner? runner = null)
{
    public string Open(OpenInput i)
    {
        var issue = string.IsNullOrWhiteSpace(i.IssueId) ? "0000" : i.IssueId.Trim().TrimStart('#');
        var slug = Text.Slug(i.Slug);
        var name = $"{issue}-{slug}";
        var dir = Path.Combine(ctx.TasksDir, name);
        if (Directory.Exists(dir)) throw new FlowException($"task {name} đã có — dùng flow_status / flow_next");
        var tier = string.IsNullOrWhiteSpace(i.Tier) ? "T2" : i.Tier.ToUpperInvariant();

        Directory.CreateDirectory(Path.Combine(dir, "questions"));
        var readme = Path.Combine(ctx.TasksDir, "README.md");
        if (!File.Exists(readme))
            Text.Write(readme, "# Task\n\nMỗi task một thư mục `<issueId>-<slug>/` (status.yaml, spec.md, plan.md, questions/, handoff-*.md) — core/process/conventions.md §2.\n");

        // status.yaml từ mẫu của Base (giữ comment), điền trường đầu
        var tpl = ctx.BaseTemplate("task-status.yaml");
        var statusPath = Path.Combine(dir, "status.yaml");
        Text.Write(statusPath, File.Exists(tpl) ? Text.Read(tpl) : "task: \nstate: context\n");
        var st = new StatusFile(statusPath);
        st.Set("task", name);
        st.Set("issue", issue == "0000" ? "chưa có — tạo trước G1" : "#" + issue);
        st.Set("topic", slug);
        st.Set("cr_of", i.CrOf);
        st.Set("type", i.Bug ? "bug" : "feature");
        st.Set("tier", tier);
        st.Set("owner", string.IsNullOrWhiteSpace(i.Owner) ? "chưa rõ — hỏi người" : i.Owner);
        st.Set("agent", i.Agent);
        st.SetRaw("created", ctx.Clock.Now.ToString("yyyy-MM-dd"));
        st.Set("state", "context");
        st.Save();

        // spec/plan theo tier (T0 không có)
        var made = new List<string>();
        var title = string.IsNullOrWhiteSpace(i.Title) ? slug : i.Title;
        var scaffold = new DocScaffold(ctx);
        if (tier != "T0")
        {
            made.Add(ctx.Rel(scaffold.Write(dir, new ScaffoldInput(i.Bug ? "bug" : "spec", tier, title, slug, issue))));
            if (!i.Bug) made.Add(ctx.Rel(scaffold.Write(dir, new ScaffoldInput("plan", tier, title, slug, issue))));
        }
        if (i.CrOf.Length > 0)
        {
            try
            {
                var origin = StatusFile.ForTask(ctx.ResolveTask(i.CrOf));
                origin.AddToList("change_requests", name);
                origin.Save();
            }
            catch (FlowException) { /* task gốc chưa có folder: bỏ qua, ghi trong kết quả */ }
        }

        var pack = new ContextPack(ctx, runner).Build(name, $"{i.Slug} {i.Title} {i.Keywords}");
        var sections = pack.Split('\n').Count(l => l.StartsWith("- ")) ;
        return $"Đã mở {ctx.Rel(dir)} · {tier} · state context\n" +
               $"File: status.yaml, {string.Join(", ", made.Select(Path.GetFileName))}, questions/, context.md ({sections} mục)\n" +
               "Tiếp: đọc context.md → đề xuất tier + chủ đề (Q0) → /spec (gói câu hỏi 1).";
    }
}
