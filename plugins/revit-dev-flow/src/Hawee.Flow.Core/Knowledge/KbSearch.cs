using System.Text.RegularExpressions;
using Hawee.Flow.Core.Infra;

namespace Hawee.Flow.Core.Knowledge;

public sealed record KbHit(string Id, string Title, string Path, int Score);
public sealed record KnownIssue(string Id, string Title, string State, string Level, string File, string Body);

/// <summary>Tìm trong KB (kb/INDEX.md + tiêu đề trang kb/) và known-issue của dự án, trả một dòng mỗi kết quả.</summary>
public sealed class KbSearch(ProjectContext ctx)
{
    private static readonly Regex KbId = new(@"\b((?:K-[A-Z]+|LL-[A-Z-]+|PT)-\d{3})\b", RegexOptions.Compiled);
    private static readonly Regex KiHeading = new(@"^###\s+(KI-[A-Z0-9]+-\d{3,})\s+[—–-]\s+(.+)$", RegexOptions.Compiled);

    public IEnumerable<KbHit> Search(IEnumerable<string> keywords, int take = 6)
    {
        var kws = keywords.SelectMany(Text.Keywords).Distinct().ToList();
        if (kws.Count == 0) return Enumerable.Empty<KbHit>();
        var kb = Path.Combine(ctx.BaseDir, "kb");
        if (!Directory.Exists(kb)) return Enumerable.Empty<KbHit>();
        var hits = new List<KbHit>();
        foreach (var f in Directory.GetFiles(kb, "*.md", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(f);
            if (name is "README.md" or "INDEX.md") continue;
            var lines = Text.Lines(f);
            var title = lines.Select(l => Regex.Match(l, "^title:\\s*(.+)$")).FirstOrDefault(m => m.Success)?.Groups[1].Value
                        ?? lines.FirstOrDefault(l => l.StartsWith("# "))?[2..] ?? name;
            var id = KbId.Match(name.ToUpperInvariant().Replace('_', '-')).Groups[1].Value;
            var t = Text.Fold(title + " " + name);
            var head = Text.Fold(string.Join(' ', lines.Take(40)));
            var score = kws.Sum(k => (t.Contains(k) ? 4 : 0) + (head.Contains(k) ? 1 : 0));
            if (score >= 4) hits.Add(new KbHit(id.Length > 0 ? id : Path.GetFileNameWithoutExtension(f), Text.Clip(title.Trim(), 120), ctx.Rel(f), score));
        }
        return hits.OrderByDescending(h => h.Score).Take(take);
    }

    public IEnumerable<KnownIssue> KnownIssues()
    {
        foreach (var rel in ctx.Config.At("kb_lint.ki_files").Strings())
        {
            var f = ctx.Full(rel);
            if (!File.Exists(f)) continue;
            var lines = Text.Lines(f);
            for (var i = 0; i < lines.Length; i++)
            {
                var m = KiHeading.Match(lines[i]);
                if (!m.Success) continue;
                var j = i + 1;
                while (j < lines.Length && !lines[j].StartsWith("### ") && !lines[j].StartsWith("## ")) j++;
                var body = string.Join('\n', lines[(i + 1)..j]);
                var state = Regex.Match(body, @"Tr.ng th.i:\*\*\s*([A-Z]+)").Groups[1].Value;
                var level = Regex.Match(body, @"M.c:\*\*\s*([a-z]+)").Groups[1].Value;
                yield return new KnownIssue(m.Groups[1].Value, m.Groups[2].Value.Trim(), state, level, ctx.Rel(f), body);
                i = j - 1;
            }
        }
    }

    public IEnumerable<KnownIssue> SearchKi(IEnumerable<string> keywords, int take = 8)
    {
        var kws = keywords.SelectMany(Text.Keywords).Distinct().ToList();
        return KnownIssues()
            .Select(k => (Ki: k, Score: kws.Sum(w => (Text.Fold(k.Title).Contains(w) ? 3 : 0) + (Text.Fold(k.Body).Contains(w) ? 1 : 0))))
            .Where(x => x.Score >= 3)
            .OrderByDescending(x => x.Ki.State == "OPEN").ThenByDescending(x => x.Score)
            .Take(take).Select(x => x.Ki);
    }
}
