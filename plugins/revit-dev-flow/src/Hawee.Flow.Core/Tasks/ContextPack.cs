using System.Text;
using Hawee.Flow.Core.Flow;
using Hawee.Flow.Core.Infra;
using Hawee.Flow.Core.Knowledge;

namespace Hawee.Flow.Core.Tasks;

/// <summary>
/// Gom ngữ cảnh bước 2 bằng máy (thay task-start bước 3b–7): chuỗi chủ đề (doc-lint -Index), bản đồ code + độ lệch
/// (codemap-lint), rule / known-issue / KB liên quan theo từ khoá, kịch bản bắt buộc của profile, bảng tier.
/// Ghi context.md (≤ ~80 dòng) — agent đọc file này thay vì đọc tài liệu quy trình.
/// </summary>
public sealed class ContextPack(ProjectContext ctx, IProcRunner? runner = null)
{
    private readonly IProcRunner _run = runner ?? ProcRunner.Instance;

    public string Build(string? task, string keywords, bool write = true)
    {
        var dir = ctx.ResolveTask(task);
        var st = StatusFile.ForTask(dir).Data;
        var kw = new List<string> { keywords, st["topic"].S(), Path.GetFileName(dir) };
        var words = kw.SelectMany(Text.Keywords).Where(w => !w.All(char.IsDigit)).Distinct().ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"# Ngữ cảnh — {Path.GetFileName(dir)}");
        sb.AppendLine();
        sb.AppendLine($"Sinh bởi hawee-flow context_pack ngày {ctx.Clock.Now:yyyy-MM-dd} · từ khoá: {string.Join(", ", words)}");
        sb.AppendLine($"Dự án: {ctx.Config["project"].S()} · profile: {string.Join(", ", ctx.Profiles)} · Base {ReadBaseVersion()}");
        if (ctx.Config["revit"].Exists)
            sb.AppendLine($"Revit: {string.Join(", ", ctx.Config.At("revit.versions").Strings())} · build API {ctx.Config.At("revit.build_api").S()}");
        sb.AppendLine();

        sb.AppendLine("## Chuỗi chủ đề (doc-lint -Index)");
        foreach (var l in Text.Cap(TopicIndex(words), 10)) sb.AppendLine("- " + l);
        sb.AppendLine();

        sb.AppendLine("## Bản đồ code");
        foreach (var l in Text.Cap(CodeMaps(words), 8)) sb.AppendLine("- " + l);
        sb.AppendLine();

        sb.AppendLine("## Rule liên quan (chi tiết: rule_get <ID>)");
        var rules = new RulesIndex(ctx).Search(words, 10).ToList();
        if (rules.Count == 0) sb.AppendLine("- (không khớp từ khoá — dùng rule_for khi biết file sẽ sửa)");
        foreach (var (r, _) in rules) sb.AppendLine("- " + RulesIndex.Line(r));
        sb.AppendLine();

        var kb = new KbSearch(ctx);
        sb.AppendLine("## Known-issue liên quan");
        var kis = kb.SearchKi(words).ToList();
        if (kis.Count == 0) sb.AppendLine("- (không có / chưa khai kb_lint.ki_files)");
        foreach (var k in kis) sb.AppendLine($"- {k.Id} — {k.Title} · {k.State} {k.Level}{(k.Body.Contains("Lặp:** lần") ? " · LẶP" : "")}");
        sb.AppendLine();

        sb.AppendLine("## KB");
        var hits = kb.Search(words).ToList();
        if (hits.Count == 0) sb.AppendLine("- (không khớp — câu cần biết mà KB không trả lời → ghi kb/inbox loại gap)");
        foreach (var h in hits) sb.AppendLine($"- {h.Id} — {h.Title} ({h.Path})");
        sb.AppendLine();

        if (ProfileScenarios() is { Length: > 0 } sc)
        {
            sb.AppendLine("## Kịch bản bắt buộc của profile (spec §4)");
            sb.AppendLine(sc);
            sb.AppendLine();
        }

        sb.AppendLine("## Tier (đề xuất ở Q0, người xác nhận ở G1)");
        sb.AppendLine(new FlowEngine(ctx).TierTable());
        sb.AppendLine();
        sb.AppendLine("## Pre-flight còn thiếu (agent điền: version Revit đích, tính năng/màn hình, model thử, có đụng MCP, nhiều Revit đang chạy)");
        sb.AppendLine("- ");

        var text = sb.ToString();
        if (write)
        {
            Text.Write(Path.Combine(dir, "context.md"), text);
            if (st["topic"].S().Length == 0 || st["topic"].S().StartsWith('<'))
            {
                var status = StatusFile.ForTask(dir);
                status.Set("topic", Path.GetFileName(dir).Split('-', 2).ElementAtOrDefault(1) ?? Path.GetFileName(dir));
                status.Save();
            }
        }
        return text;
    }

    private string ReadBaseVersion()
    {
        var v = Path.Combine(ctx.BaseDir, "VERSION");
        return File.Exists(v) ? Text.Read(v).Trim() : "?";
    }

    /// <summary>Dòng của doc-lint -Index khớp từ khoá (chủ đề, spec/plan hiện hành, handoff mới nhất).</summary>
    private IEnumerable<string> TopicIndex(List<string> words)
    {
        var script = ctx.BaseTool("doc-lint.ps1");
        if (!File.Exists(script)) return new[] { "không có doc-lint.ps1" };
        var r = Ps.RunFile(_run, script, new[] { "-Index" }, ctx.Root, TimeSpan.FromMinutes(3));
        if (r.ExitCode != 0) return new[] { $"doc-lint -Index lỗi (exit {r.ExitCode}): {Text.Clip(r.Lines.LastOrDefault() ?? "", 160)}" };
        var lines = r.Lines.Where(l => l.Trim().Length > 0).ToList();
        var match = lines.Where(l => words.Any(w => Text.Fold(l).Contains(w))).ToList();
        return match.Count > 0 ? match.Select(l => l.Trim()) : new[] { "chưa có chủ đề khớp — đề xuất chủ đề mới = slug task (Q0)" };
    }

    private IEnumerable<string> CodeMaps(List<string> words)
    {
        if (!Directory.Exists(ctx.CodeMapsDir)) return new[] { $"chưa có thư mục bản đồ {ctx.Rel(ctx.CodeMapsDir)} — dựng ở bước 3 (spec-plan §1.1)" };
        var maps = Directory.GetFiles(ctx.CodeMapsDir, "*.md", SearchOption.AllDirectories)
            .Where(f => words.Any(w => Text.Fold(Path.GetFileName(f) + " " + string.Join(' ', Text.Lines(f).Take(15))).Contains(w)))
            .ToList();
        if (maps.Count == 0) return new[] { "không có bản đồ khớp — dựng ở bước 3 (spec-plan §1.1)" };
        var script = ctx.BaseTool("codemap-lint.ps1");
        return maps.Select(m =>
        {
            if (!File.Exists(script)) return ctx.Rel(m);
            var r = Ps.RunFile(_run, script, new[] { "-Path", m, "-MaxList", "5" }, ctx.Root, TimeSpan.FromMinutes(2));
            var drift = r.Lines.Where(l => l.Contains("map-")).Select(l => Text.Clip(l.Trim(), 140)).Take(3).ToList();
            return ctx.Rel(m) + (drift.Count == 0 ? " · khớp source" : " · " + string.Join(" | ", drift));
        });
    }

    private string ProfileScenarios()
    {
        foreach (var p in ctx.Profiles)
        {
            var readme = Path.Combine(ctx.BaseDir, "profiles", p, "README.md");
            if (!File.Exists(readme)) continue;
            var line = Text.Lines(readme).FirstOrDefault(l => l.StartsWith("Kịch bản bắt buộc"));
            if (line is not null) return Text.Clip(line, 400);
        }
        return "";
    }
}
