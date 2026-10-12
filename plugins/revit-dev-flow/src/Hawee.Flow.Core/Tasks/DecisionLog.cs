using System.Text.RegularExpressions;
using Hawee.Flow.Core.Infra;

namespace Hawee.Flow.Core.Tasks;

public sealed record DecisionRow(
    string Who, string Doc, string Question, string Decision, string AppliesTo, string Source, string Replaces = "—");

public sealed record PendingRow(string Id, string Doc, string Question, string Decider, string Default);

/// <summary>docs/decisions/decision-log.md — cấp số DEC-YYYY-NNN, thêm dòng, quản bảng câu hỏi treo.</summary>
public sealed class DecisionLog(ProjectContext ctx)
{
    private static readonly Regex DecId = new(@"DEC-(\d{4})-(\d{3,})", RegexOptions.Compiled);
    private const string PendingHeading = "## Câu hỏi đang treo";

    public string Path => ctx.DecisionLog;

    private List<string> LoadOrCreate()
    {
        if (File.Exists(Path)) return Text.Read(Path).TrimEnd('\n').Split('\n').ToList();
        var tpl = ctx.BaseTemplate("decision-log.md");
        var lines = File.Exists(tpl)
            ? Text.Read(tpl).TrimEnd('\n').Split('\n').ToList()
            : new List<string>
            {
                "# Nhật ký quyết định", "",
                "| ID | Ngày | Ai (vai) | Việc / tài liệu | Câu hỏi | Quyết định | Hệ quả / áp vào | Nguồn (link) | Thay cho |",
                "|---|---|---|---|---|---|---|---|---|", "", PendingHeading, "",
                "| ID câu | Từ ngày | Tài liệu | Câu hỏi (tóm tắt) | Người quyết định | Mặc định nếu không trả lời |",
                "|---|---|---|---|---|---|",
            };
        // bỏ dòng ví dụ của mẫu
        lines.RemoveAll(l => l.StartsWith("| DEC-") && l.Contains("<tên>"));
        lines = lines.Select(l => l.Replace("<dự án>", ctx.Config["project"].S("dự án"))).ToList();
        return lines;
    }

    public string NextId(int year)
    {
        var max = 0;
        if (File.Exists(Path))
            foreach (Match m in DecId.Matches(Text.Read(Path)))
                if (int.Parse(m.Groups[1].Value) == year) max = Math.Max(max, int.Parse(m.Groups[2].Value));
        return $"DEC-{year}-{max + 1:000}";
    }

    /// <summary>Thêm các dòng quyết định, trả về ID đã cấp (theo thứ tự).</summary>
    public List<string> Append(IEnumerable<DecisionRow> rows)
    {
        var lines = LoadOrCreate();
        var date = ctx.Clock.Now.ToString("yyyy-MM-dd");
        var year = ctx.Clock.Now.Year;
        var next = int.Parse(NextId(year)[9..]);
        var ids = new List<string>();
        var insertAt = EndOfTable(lines, l => l.StartsWith("| ID |"));
        foreach (var r in rows)
        {
            var id = $"DEC-{year}-{next++:000}";
            ids.Add(id);
            var row = $"| {id} | {date} | {Text.Cell(r.Who)} | {Text.Cell(r.Doc)} | {Text.Cell(r.Question)} | {Text.Cell(r.Decision)} | {Text.Cell(r.AppliesTo)} | {Text.Cell(r.Source)} | {Text.Cell(r.Replaces)} |";
            lines.Insert(insertAt++, row);
        }
        Text.Write(Path, string.Join('\n', lines) + "\n");
        return ids;
    }

    public void AddPending(IEnumerable<PendingRow> rows)
    {
        var lines = LoadOrCreate();
        if (!lines.Any(l => l.StartsWith(PendingHeading)))
            lines.AddRange(new[] { "", PendingHeading, "", "| ID câu | Từ ngày | Tài liệu | Câu hỏi (tóm tắt) | Người quyết định | Mặc định nếu không trả lời |", "|---|---|---|---|---|---|" });
        var date = ctx.Clock.Now.ToString("yyyy-MM-dd");
        var insertAt = EndOfTable(lines, l => l.StartsWith("| ID câu |"));
        foreach (var r in rows)
            lines.Insert(insertAt++, $"| {Text.Cell(r.Id)} | {date} | {Text.Cell(r.Doc)} | {Text.Cell(r.Question)} | {Text.Cell(r.Decider)} | {Text.Cell(r.Default)} |");
        Text.Write(Path, string.Join('\n', lines) + "\n");
    }

    /// <summary>Bỏ câu treo đã có trả lời (khớp cột ID câu + tài liệu).</summary>
    public void ResolvePending(string id, string doc)
    {
        if (!File.Exists(Path)) return;
        var lines = Text.Read(Path).TrimEnd('\n').Split('\n').ToList();
        var inPending = false;
        lines.RemoveAll(l =>
        {
            if (l.StartsWith("## ")) inPending = l.StartsWith(PendingHeading);
            return inPending && l.StartsWith($"| {id} |") && l.Contains(doc);
        });
        Text.Write(Path, string.Join('\n', lines) + "\n");
    }

    /// <summary>Vị trí ngay sau dòng cuối của bảng có dòng tiêu đề khớp <paramref name="header"/>.</summary>
    internal static int EndOfTable(List<string> lines, Func<string, bool> header)
    {
        var h = lines.FindIndex(l => header(l));
        if (h < 0) { lines.Add(""); return lines.Count; }
        var i = h + 1;
        while (i < lines.Count && lines[i].StartsWith('|')) i++;
        return i;
    }
}
