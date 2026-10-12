using System.Text.RegularExpressions;
using Hawee.Flow.Core.Infra;

namespace Hawee.Flow.Core.Knowledge;

public sealed record RuleEntry(string Id, string Title, string Level, string Status, string File, string When, string Must, string Body);

/// <summary>
/// Chỉ mục rule (BIM_KB_Base core/rules, profiles/&lt;p&gt;/rules, rules.extra, kb_lint.rule_dirs).
/// Thay cho việc agent đọc cả general.md (37 KB): tìm theo từ khoá trả 1 dòng/rule, chi tiết lấy bằng Get(id).
/// </summary>
public sealed class RulesIndex
{
    private static readonly Regex Heading = new(
        @"^##\s+([A-Z][A-Z0-9]*(?:-[A-Z0-9]+)+)\s+[—–-]\s+(.+?)\s*$", RegexOptions.Compiled);
    private static readonly Regex Tag = new(@"·\s*`([^`]+)`", RegexOptions.Compiled);

    public IReadOnlyList<RuleEntry> Rules { get; }

    public RulesIndex(ProjectContext ctx)
    {
        var files = new List<string>();
        void AddDir(string d)
        {
            if (Directory.Exists(d))
                files.AddRange(Directory.GetFiles(d, "*.md", SearchOption.AllDirectories)
                    .Where(f => !Path.GetFileName(f).Equals("README.md", StringComparison.OrdinalIgnoreCase)));
        }
        AddDir(Path.Combine(ctx.BaseDir, "core", "rules"));
        foreach (var p in ctx.Profiles) AddDir(Path.Combine(ctx.BaseDir, "profiles", p, "rules"));
        foreach (var f in ctx.Config.At("rules.extra").Strings()) { var full = ctx.Full(f); if (File.Exists(full)) files.Add(full); }
        foreach (var d in ctx.Config.At("kb_lint.rule_dirs").Strings()) AddDir(ctx.Full(d));
        Rules = files.Distinct().SelectMany(f => Parse(f, ctx.Rel(f))).ToList();
    }

    internal static IEnumerable<RuleEntry> Parse(string path, string rel)
    {
        var lines = Text.Lines(path);
        for (var i = 0; i < lines.Length; i++)
        {
            var m = Heading.Match(lines[i]);
            if (!m.Success) continue;
            var j = i + 1;
            while (j < lines.Length && !lines[j].StartsWith("## ")) j++;
            var body = string.Join('\n', lines[(i + 1)..j]).Trim();
            var titlePart = m.Groups[2].Value;
            var tags = Tag.Matches(titlePart).Select(t => t.Groups[1].Value).ToList();
            var title = Tag.Replace(titlePart, "").Trim().TrimEnd('·').Trim();
            var level = tags.FirstOrDefault(t => t is "blocker" or "high" or "medium" or "low") ?? "";
            var status = tags.FirstOrDefault(t => t is "draft" or "review" or "active" or "retired") ?? "";
            yield return new RuleEntry(m.Groups[1].Value, title, level, status, rel,
                Field(body, "Khi"), Field(body, "Phải"), body);
            i = j - 1;
        }
    }

    private static string Field(string body, string name)
    {
        var start = body.IndexOf($"**{name}:**", StringComparison.Ordinal);
        if (start < 0) return "";
        var rest = body[(start + name.Length + 5)..];
        var end = rest.IndexOf("\n- **", StringComparison.Ordinal);
        var blank = rest.IndexOf("\n\n", StringComparison.Ordinal);
        if (blank >= 0 && (end < 0 || blank < end)) end = blank;
        var text = Regex.Replace(end >= 0 ? rest[..end] : rest, @"\s+", " ").Trim();
        return Text.Clip(text, 320);
    }

    public RuleEntry? Get(string id) => Rules.FirstOrDefault(r => r.Id.Equals(id.Trim(), StringComparison.OrdinalIgnoreCase));

    public IEnumerable<(RuleEntry Rule, int Score)> Search(IEnumerable<string> keywords, int take = 10)
    {
        var kws = keywords.SelectMany(Text.Keywords).Distinct().ToList();
        if (kws.Count == 0) return Enumerable.Empty<(RuleEntry, int)>();
        var docs = Rules.Select(r => (Rule: r, T: new Terms.Doc(r.Id + " " + r.Title), B: new Terms.Doc(r.Body))).ToList();
        // từ hiếm nặng hơn từ có trong mọi rule (IDF): "transaction" > "revit"
        var idf = kws.ToDictionary(k => k, k =>
        {
            var df = docs.Count(d => d.T.Has(k) || d.B.Has(k));
            return Math.Log((docs.Count + 1.0) / (df + 1.0)) + 1;
        });
        return docs
            .Select(d =>
            {
                var hit = kws.Where(k => d.T.Has(k) || d.B.Has(k)).ToList();
                if (hit.Count == 0) return (Rule: d.Rule, Score: 0);
                var score = hit.Sum(k => idf[k] * ((d.T.Has(k) ? 5 : 0) + (d.B.Has(k) ? 1 : 0)));
                score += d.Rule.Level switch { "blocker" => 2, "high" => 1, _ => 0 };
                return (Rule: d.Rule, Score: (int)Math.Round(score));
            })
            .Where(x => x.Score > 2)
            .OrderByDescending(x => x.Score)
            .Take(take);
    }

    /// <summary>Từ khoá từ đường dẫn file sắp sửa; bỏ tên solution / project của repo (có trong mọi đường dẫn).</summary>
    public static List<string> PathKeywords(ProjectContext ctx, string paths)
    {
        if (string.IsNullOrWhiteSpace(paths)) return new();
        var names = Directory.Exists(ctx.Root)
            ? Directory.EnumerateFiles(ctx.Root, "*.sln").Concat(Directory.EnumerateFiles(ctx.Root, "*.csproj", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3 }))
                .Select(Path.GetFileNameWithoutExtension).OfType<string>()
            : Enumerable.Empty<string>();
        return Terms.FromPaths(paths, names);
    }

    public static string Line(RuleEntry r) =>
        $"{r.Id} — {r.Title}{(r.Level.Length > 0 ? " · " + r.Level : "")}{(r.Status.Length > 0 ? " · " + r.Status : "")}";
}
