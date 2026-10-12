using System.Text.RegularExpressions;
using Hawee.Flow.Core.Flow;
using Hawee.Flow.Core.Infra;

namespace Hawee.Flow.Core.Docs;

public sealed record ScaffoldInput(string Kind, string Tier, string Title, string Topic, string Issue, string SpecVersion = "v1");

/// <summary>
/// Dựng spec / plan / bugfix-spec từ mẫu của BIM_KB_Base: điền dòng đầu, mục ngoài tier ghi "Không áp dụng".
/// Agent không phải đọc mẫu rồi chép lại (mẫu đi qua context 2 lần) — chỉ đọc file đã dựng khi điền nội dung.
/// </summary>
public sealed class DocScaffold(ProjectContext ctx)
{
    private static readonly Regex Sec2 = new(@"^##\s+(\d+)\.\s", RegexOptions.Compiled);
    private static readonly Regex Sec3 = new(@"^###\s+(\d+\.\d+)\.\s", RegexOptions.Compiled);

    public string Template(string kind) => ctx.BaseTemplate(kind switch
    {
        "spec" => "spec.md",
        "plan" => "plan.md",
        "bug" => "bugfix-spec.md",
        "handoff" => "handoff.md",
        _ => throw new FlowException($"kind '{kind}' không hỗ trợ (spec | plan | bug)"),
    });

    public string Build(ScaffoldInput i)
    {
        if (i.Tier == "T0" && i.Kind is "spec" or "plan")
            throw new FlowException("T0 không cần spec/plan — mô tả 3–5 dòng trong issue/commit (tiers.md §2)");
        var tpl = Template(i.Kind);
        if (!File.Exists(tpl)) throw new FlowException($"thiếu mẫu {ctx.Rel(tpl)}");
        var lines = Text.Read(tpl).Split('\n').ToList();
        FillHeader(lines, i);
        var keep = new FlowEngine(ctx).Sections(i.Kind, i.Tier).Strings();
        if (keep.Count > 0) MarkOutOfTier(lines, keep.ToHashSet(), i.Tier);
        return string.Join('\n', lines);
    }

    /// <summary>Ghi file; đã có thì không ghi đè (trả lỗi).</summary>
    public string Write(string taskDir, ScaffoldInput i, bool overwrite = false)
    {
        var file = Path.Combine(taskDir, i.Kind == "bug" ? "spec.md" : i.Kind + ".md");
        if (File.Exists(file) && !overwrite) throw new FlowException($"{ctx.Rel(file)} đã có — không ghi đè");
        Text.Write(file, Build(i));
        return file;
    }

    private void FillHeader(List<string> lines, ScaffoldInput i)
    {
        var date = ctx.Clock.Now.ToString("yyyy-MM-dd");
        var issue = i.Issue.TrimStart('#');
        for (var n = 0; n < Math.Min(lines.Count, 12); n++)
        {
            var l = lines[n];
            if (n == 0)
            {
                l = Regex.Replace(l, @"<tên[^>]*>|<tiêu đề ngắn>", i.Title);
                l = l.Replace("#<issue>", "#" + issue);
            }
            l = l.Replace("`<slug>`", $"`{i.Topic}`")
                 .Replace("Issue: #<số>", "Issue: #" + issue)
                 .Replace("Ngày: <YYYY-MM-DD>", "Ngày: " + date);
            l = Regex.Replace(l, @"Tier: T\d", "Tier: " + i.Tier);
            l = Regex.Replace(l, @"Trạng thái: [^·]*$", "Trạng thái: " + (i.Kind == "bug" ? "thu bằng chứng" : "nháp"));
            l = Regex.Replace(l, @"Spec: <link> v<N> \(qua G1 ngày <YYYY-MM-DD>\)", $"Spec: spec.md {i.SpecVersion} (chưa qua G1)");
            if (l.StartsWith("- Thay cho:")) l = "- Thay cho: không có";
            if (l.StartsWith("- Bị thay bởi:")) l = "- Bị thay bởi: —";
            lines[n] = l;
        }
    }

    internal static void MarkOutOfTier(List<string> lines, HashSet<string> keep, string tier)
    {
        var na = $"Không áp dụng – ngoài tier {tier} (tiers.md §2).";
        var parentsWithSubs = keep.Where(k => k.Contains('.')).Select(k => k.Split('.')[0]).ToHashSet();
        var result = new List<string>();
        bool skip2 = false, skip3 = false;
        foreach (var line in lines)
        {
            var m2 = Sec2.Match(line);
            if (m2.Success || line.StartsWith("## "))
            {
                skip2 = m2.Success && !keep.Contains(m2.Groups[1].Value);
                skip3 = false;
                result.Add(line);
                if (skip2) result.AddRange(new[] { "", na, "" });
                continue;
            }
            var m3 = Sec3.Match(line);
            if (m3.Success || line.StartsWith("### "))
            {
                if (skip2) continue;
                var id = m3.Success ? m3.Groups[1].Value : "";
                skip3 = m3.Success && parentsWithSubs.Contains(id.Split('.')[0]) && !keep.Contains(id);
                result.Add(line);
                if (skip3) result.AddRange(new[] { "", na, "" });
                continue;
            }
            if (skip2 || skip3) continue;
            result.Add(line);
        }
        lines.Clear();
        lines.AddRange(result);
    }
}
