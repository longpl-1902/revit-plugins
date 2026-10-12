using System.Text.RegularExpressions;
using Hawee.Flow.Core.Infra;

namespace Hawee.Flow.Core.Testing;

public sealed record TestRule(string Id, string Name, bool Retired, string AppliesTo, Dictionary<string, string> Fields, string Block)
{
    public string F(string name) => Fields.TryGetValue(name, out var v) ? v : "";
    public string Ac => F("AC");
    public string Expect => F("Kỳ vọng");
    public string Tolerance => F("Sai số");
    public string Grader => F("Máy chấm");
    public string Evidence => F("Ảnh minh chứng");
    public bool Pending => Regex.IsMatch(Text.Fold(Tolerance + " " + Expect), @"\bcho\b");

    /// <summary>Nguồn kỳ vọng: spec | cài đặt | baseline | tương đối | ảnh | "".</summary>
    public string ExpectSource
    {
        get
        {
            var f = Text.Fold(Expect);
            if (f.StartsWith("theo spec")) return "spec";
            if (f.StartsWith("theo cai dat")) return "cài đặt";
            if (f.StartsWith("theo baseline")) return "baseline";
            if (f.StartsWith("tuong doi")) return "tương đối";
            if (f.StartsWith("theo anh")) return "ảnh";
            return "";
        }
    }

    public IEnumerable<string> AcIds => Regex.Matches(Ac, @"AC\d+").Select(m => m.Value).Distinct();
}

public sealed record GapRow(string Ac, string Missing, string When, string Meanwhile);

/// <summary>rules.md của suite (suite-format.md §2): luật R-xx (5–6 trường + AC, Máy chấm), "Áp dụng cho", bảng "Chỗ hở".</summary>
public sealed class RulesFile
{
    private static readonly Regex Heading = new(@"^###\s+([A-Z][A-Z0-9]*(?:-[A-Z0-9]+)*-\d+)\s+[—–-]\s+(.+?)\s*$", RegexOptions.Compiled);
    private static readonly Regex FieldRx = new(@"^-\s+(?:\*\*)?([^:*]{1,40}?)(?:\*\*)?:\s*(?:\*\*)?\s*(.*)$", RegexOptions.Compiled);

    public List<TestRule> Rules { get; } = new();
    public List<GapRow> Gaps { get; } = new();
    public string Header { get; private set; } = "";

    public static RulesFile Load(string path) => Parse(Text.Read(path));

    public static RulesFile Parse(string text)
    {
        var rf = new RulesFile();
        var lines = text.Split('\n');
        var applies = "mọi sheet";
        rf.Header = string.Join('\n', lines.TakeWhile(l => !l.StartsWith("## ")));
        for (var i = 0; i < lines.Length; i++)
        {
            var l = lines[i];
            if (l.StartsWith("## Áp dụng cho:")) { applies = l["## Áp dụng cho:".Length..].Trim(); continue; }
            if (l.StartsWith("## Chỗ hở"))
            {
                var j = i + 1;
                while (j < lines.Length && !lines[j].StartsWith('|')) j++;
                j += 2;
                for (; j < lines.Length && lines[j].StartsWith('|'); j++)
                {
                    var c = Regex.Split(lines[j].Trim().Trim('|'), @"(?<!\\)\|").Select(x => x.Trim()).ToArray();
                    if (c.Length >= 4 && Regex.IsMatch(c[0], @"AC\d+")) rf.Gaps.Add(new GapRow(c[0], c[1], c[2], c[3]));
                }
                continue;
            }
            var m = Heading.Match(l);
            if (!m.Success) continue;
            var start = i;
            var fields = new Dictionary<string, string>();
            var k = i + 1;
            string? last = null;
            for (; k < lines.Length && !lines[k].StartsWith("## ") && !lines[k].StartsWith("### "); k++)
            {
                var fm = FieldRx.Match(lines[k]);
                if (fm.Success) { last = fm.Groups[1].Value.Trim(); fields[last] = fm.Groups[2].Value.Trim(); }
                else if (last is not null && lines[k].StartsWith("  ") && lines[k].Trim().Length > 0) fields[last] += " " + lines[k].Trim();
            }
            var title = m.Groups[2].Value;
            var retired = Regex.IsMatch(title, @"·\s*retired");
            var name = Regex.Replace(title, @"\s*·\s*retired.*$", "").Trim();
            rf.Rules.Add(new TestRule(m.Groups[1].Value, name, retired, applies, fields, string.Join('\n', lines[start..k]).TrimEnd()));
            i = k - 1;
        }
        return rf;
    }

    public IEnumerable<TestRule> Active => Rules.Where(r => !r.Retired);
}
