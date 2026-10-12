using System.Text.RegularExpressions;
using Hawee.Flow.Core.Infra;

namespace Hawee.Flow.Core.Docs;

/// <summary>Sửa tài liệu markdown theo mục: tìm mục "## 14." / "### 1.2.", đọc / thêm dòng bảng, thay khối có đánh dấu.</summary>
public sealed class MdDoc
{
    public string Path { get; }
    public List<string> Lines { get; }

    public MdDoc(string path)
    {
        Path = path;
        Lines = File.Exists(path) ? Text.Read(path).TrimEnd('\n').Split('\n').ToList() : new List<string>();
    }

    public void Save() => Text.Write(Path, string.Join('\n', Lines) + "\n");

    /// <summary>(dòng heading, dòng kết thúc mục — không gồm). Mục "1.2" khớp "### 1.2." ; "14" khớp "## 14.".</summary>
    public (int Start, int End)? Section(string number)
    {
        var level = number.Contains('.') ? "###" : "##";
        var rx = new Regex($@"^{level}\s+{Regex.Escape(number)}\.\s");
        var start = Lines.FindIndex(l => rx.IsMatch(l));
        if (start < 0) return null;
        var end = start + 1;
        while (end < Lines.Count && !IsHeadingAtOrAbove(Lines[end], level)) end++;
        return (start, end);
    }

    private static bool IsHeadingAtOrAbove(string l, string level) =>
        level == "##" ? l.StartsWith("## ") || l.StartsWith("# ") : l.StartsWith("## ") || l.StartsWith("### ") || l.StartsWith("# ");

    public string SectionText(string number) =>
        Section(number) is { } s ? string.Join('\n', Lines.Skip(s.Start + 1).Take(s.End - s.Start - 1)) : "";

    public bool HasTable(string number) =>
        Section(number) is { } s && Lines.Skip(s.Start + 1).Take(s.End - s.Start - 1).Any(l => l.StartsWith('|'));

    /// <summary>Các dòng bảng (đã tách ô) đầu tiên trong mục, bỏ dòng tiêu đề và dòng ---.</summary>
    public List<string[]> TableRows(string number)
    {
        var rows = new List<string[]>();
        if (Section(number) is not { } s) return rows;
        var i = s.Start + 1;
        while (i < s.End && !Lines[i].StartsWith('|')) i++;
        if (i >= s.End) return rows;
        i++; // tiêu đề
        if (i < s.End && Regex.IsMatch(Lines[i], @"^\|[\s:|-]+\|$")) i++;
        for (; i < s.End && Lines[i].StartsWith('|'); i++) rows.Add(Cells(Lines[i]));
        return rows;
    }

    public static string[] Cells(string row) =>
        Regex.Split(row.Trim().Trim('|'), @"(?<!\\)\|").Select(c => c.Trim()).ToArray();

    /// <summary>
    /// Thêm dòng vào cuối bảng đầu tiên của mục. <paramref name="answerColumn"/> ≥ 0: bỏ dòng mẫu còn trống ở cột đó
    /// (vd. "| Q1 | 1 | | | | | | §3 |" của mẫu spec §14).
    /// </summary>
    public void AppendRows(string number, IEnumerable<string> rows, int answerColumn = -1)
    {
        var s = Section(number) ?? throw new FlowException($"{System.IO.Path.GetFileName(Path)} không có mục {number}");
        var i = s.Start + 1;
        while (i < s.End && !Lines[i].StartsWith('|')) i++;
        if (i >= s.End) throw new FlowException($"mục {number} của {System.IO.Path.GetFileName(Path)} không có bảng");
        i++;
        if (i < Lines.Count && Regex.IsMatch(Lines[i], @"^\|[\s:|-]+\|$")) i++;
        var first = i;
        while (i < Lines.Count && Lines[i].StartsWith('|')) i++;
        if (answerColumn >= 0)
            for (var j = i - 1; j >= first; j--)
            {
                var c = Cells(Lines[j]);
                if (c.Length > answerColumn && c[answerColumn].Length == 0) { Lines.RemoveAt(j); i--; }
            }
        Lines.InsertRange(i, rows);
    }

    /// <summary>Thay (hoặc thêm cuối file) khối giữa &lt;!-- hawee-flow:{key} --&gt; và &lt;!-- /hawee-flow:{key} --&gt;.</summary>
    public void ReplaceBlock(string key, IEnumerable<string> content)
    {
        var open = $"<!-- hawee-flow:{key} -->";
        var close = $"<!-- /hawee-flow:{key} -->";
        var a = Lines.IndexOf(open);
        var b = Lines.IndexOf(close);
        var block = new List<string> { open };
        block.AddRange(content);
        block.Add(close);
        if (a >= 0 && b > a) { Lines.RemoveRange(a, b - a + 1); Lines.InsertRange(a, block); }
        else { if (Lines.Count > 0 && Lines[^1].Length > 0) Lines.Add(""); Lines.AddRange(block); }
    }

    /// <summary>Giá trị nhãn ở đầu tài liệu, vd. "Tier" trong dòng "· Tier: T2 ·".</summary>
    public string HeaderValue(string label)
    {
        foreach (var l in Lines.Take(15))
        {
            var m = Regex.Match(l, $@"{Regex.Escape(label)}:\s*([^·]+?)\s*(·|$)");
            if (m.Success) return m.Groups[1].Value.Trim().Trim('`');
        }
        return "";
    }
}
