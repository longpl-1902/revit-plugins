using System.Text;
using System.Text.RegularExpressions;
using Hawee.Flow.Core.Flow;
using Hawee.Flow.Core.Infra;
using Hawee.Flow.Core.Tasks;

namespace Hawee.Flow.Core.Docs;

public sealed record PackQuestion(
    string Id, string Q, string Kind, string Seen, string Why, List<(string Key, string Text)> Options,
    string Recommend, string Because, string Default, string Decider, string Applies, string Sets, Dictionary<string, string> Values)
{
    public bool Blocking => Default.Length == 0 || Default.StartsWith("CHẶN", StringComparison.OrdinalIgnoreCase) || Default.Equals("BLOCK", StringComparison.OrdinalIgnoreCase);
    public string OptionText(string key) => Options.FirstOrDefault(o => o.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Text ?? "";
}

public sealed record Pack(string Doc, int Round, string Answerers, string ReadBefore, List<PackQuestion> Questions, List<(string Id, string Seen, string Propose)> Extras);

/// <summary>
/// Gói câu hỏi có cấu trúc (questions/pack-N.yaml) → markdown theo mẫu question-pack.md + bản ngắn để gửi người;
/// câu trả lời "Q1: B, Q2: mặc định" → spec §14 / plan §1.2 / bug §7 + DEC + §12 (mặc định) + §13/câu treo (CHẶN).
/// </summary>
public sealed class QuestionPack(ProjectContext ctx)
{
    private static readonly string[] SettableKeys = { "tier", "autonomy", "topic" };

    public static Pack Parse(string yamlText)
    {
        var y = Y.Parse(yamlText);
        var qs = y["questions"].Items.Select(q => new PackQuestion(
            q["id"].S(), q["q"].S(), q["kind"].S(), q["seen"].S(), q["why"].S(),
            q["options"].Entries.Select(e => (e.Key, e.Value.S())).ToList(),
            q["recommend"].S(), q["because"].S(), q["default"].S(), q["decider"].S(), q["applies"].S(),
            q["sets"].S(), q["values"].Entries.ToDictionary(e => e.Key, e => e.Value.S()))).ToList();
        var extras = y["extras"].Items.Select(e => (e["id"].S(), e["seen"].S(), e["propose"].S())).ToList();
        return new Pack(y["doc"].S("spec"), y["round"].Int(1), y["answerers"].S(), y["read_before"].S(), qs, extras);
    }

    private string PackPath(string dir, int round, string ext) => Path.Combine(dir, "questions", $"pack-{round}{ext}");

    private Pack Load(string dir, string file)
    {
        var p = Path.IsPathRooted(file) ? file : Path.Combine(dir, file);
        if (!File.Exists(p)) throw new FlowException($"không thấy {ctx.Rel(p)} — viết gói câu hỏi dạng YAML (questions/pack-N.yaml) trước");
        return Parse(Text.Read(p));
    }

    // ---- render -------------------------------------------------------------------------------------------

    public string Render(string? task, string file)
    {
        var dir = ctx.ResolveTask(task);
        var pack = Load(dir, file);
        var st = StatusFile.ForTask(dir);
        var tier = st.Get("tier");
        var max = new FlowEngine(ctx).Limit("questions_per_pack", 10);
        var limitNode = FlowDef.Load(ctx)["limits"]["questions_per_pack"][tier];
        if (limitNode.Exists) max = limitNode.Int(max);
        var errors = Validate(pack, max).ToList();
        if (errors.Count > 0) throw new FlowException("gói câu hỏi chưa đúng:\n- " + string.Join("\n- ", errors));

        Text.Write(PackPath(dir, pack.Round, ".md"), ToMarkdown(pack, Path.GetFileName(dir)));
        if (pack.Doc is "spec" or "bug")
        {
            st.Set("spec", $"đang trao đổi (gói {pack.Round})");
            if (st.Get("state") == "context") st.Set("state", "spec");
        }
        else st.Set("plan", "đang chốt thiết kế");
        st.Save();

        var sb = new StringBuilder();
        sb.AppendLine($"Gói câu hỏi {pack.Round} — {pack.Doc} {Path.GetFileName(dir)} · trả lời ngắn: Q1: B, Q2: mặc định, Q3: xem lại — …");
        if (pack.Answerers.Length > 0) sb.AppendLine("Người trả lời: " + pack.Answerers);
        foreach (var q in pack.Questions)
        {
            var opts = string.Join(" · ", q.Options.Select(o => $"{o.Key}) {o.Text}"));
            sb.AppendLine($"{q.Id}{(q.Kind.Length > 0 ? $" [{q.Kind}]" : "")} {q.Q}");
            sb.AppendLine($"   {opts}");
            sb.AppendLine($"   Đề xuất {q.Recommend}{(q.Because.Length > 0 ? " — " + q.Because : "")} · {(q.Blocking ? "CHẶN nếu không trả lời" : "mặc định " + q.Default)}");
        }
        foreach (var e in pack.Extras) sb.AppendLine($"{e.Id} (đề xuất thêm) {e.Seen} → {e.Propose} — giữ/bỏ?");
        sb.Append($"(bản đầy đủ có bằng chứng: {ctx.Rel(PackPath(dir, pack.Round, ".md"))})");
        return sb.ToString();
    }

    internal static IEnumerable<string> Validate(Pack p, int max)
    {
        if (p.Questions.Count == 0) yield return "không có câu hỏi nào";
        if (p.Questions.Count > max) yield return $"{p.Questions.Count} câu > giới hạn {max} của tier — tách gói hoặc bỏ câu tự tìm được";
        foreach (var dup in p.Questions.GroupBy(q => q.Id).Where(g => g.Count() > 1)) yield return $"trùng ID {dup.Key}";
        foreach (var q in p.Questions)
        {
            if (!Regex.IsMatch(q.Id, @"^Q\d+$")) yield return $"ID '{q.Id}' phải dạng Q<n>";
            if (q.Q.Length == 0) yield return $"{q.Id}: thiếu câu hỏi (q)";
            if (q.Seen.Length == 0) yield return $"{q.Id}: thiếu bằng chứng đã thấy (seen) — file:dòng / issue / log";
            if (q.Options.Count < 2 && !q.Blocking) yield return $"{q.Id}: cần ≥ 2 phương án (options) hoặc ghi default: CHẶN";
            if (q.Recommend.Length > 0 && q.Options.Count > 0 && q.OptionText(q.Recommend).Length == 0) yield return $"{q.Id}: đề xuất {q.Recommend} không có trong options";
            if (!q.Blocking && q.OptionText(q.Default).Length == 0) yield return $"{q.Id}: mặc định '{q.Default}' không có trong options (hoặc ghi CHẶN)";
            if (q.Sets.Length > 0 && !SettableKeys.Contains(q.Sets)) yield return $"{q.Id}: sets chỉ nhận {string.Join(", ", SettableKeys)}";
        }
    }

    private static string ToMarkdown(Pack p, string task)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Gói câu hỏi — {p.Doc} {task} · lần {p.Round}");
        sb.AppendLine();
        sb.AppendLine("> Sinh bởi hawee-flow pack_render từ pack-" + p.Round + ".yaml (mẫu core/templates/question-pack.md). Trả lời ngắn: `Q3: B`, `Q5: mặc định`, `Q7: xem lại — …`.");
        sb.AppendLine();
        if (p.ReadBefore.Length > 0) sb.AppendLine($"**Trước khi hỏi, tôi đã:** đọc {p.ReadBefore}; những điều dưới đây code không trả lời được.").AppendLine();
        if (p.Answerers.Length > 0) sb.AppendLine($"**Người trả lời:** {p.Answerers}").AppendLine();
        foreach (var q in p.Questions)
        {
            sb.AppendLine("---").AppendLine();
            sb.AppendLine($"### {q.Id} — {q.Q}{(q.Kind.Length > 0 ? $"  `{q.Kind}`" : "")}").AppendLine();
            sb.AppendLine($"- **Đã thấy:** {q.Seen}");
            if (q.Why.Length > 0) sb.AppendLine($"- **Vì sao phải hỏi:** {q.Why}");
            sb.AppendLine("- **Phương án:**");
            foreach (var (k, t) in q.Options) sb.AppendLine($"  - {k} — {t}");
            sb.AppendLine($"- **Đề xuất:** {q.Recommend}{(q.Because.Length > 0 ? " — vì " + q.Because : "")}");
            sb.AppendLine($"- **Nếu không trả lời:** {(q.Blocking ? "`CHẶN – không có mặc định, dừng ở cổng`" : "mặc định " + q.Default)}");
            sb.AppendLine();
        }
        if (p.Extras.Count > 0)
        {
            sb.AppendLine("---").AppendLine().AppendLine("### Đề xuất thêm — điều có thể bạn chưa nghĩ tới (gạch bỏ nếu không cần)").AppendLine();
            sb.AppendLine("| # | Điều agent thấy | Đề xuất | Giữ / bỏ |").AppendLine("|---|---|---|---|");
            foreach (var e in p.Extras) sb.AppendLine($"| {e.Id} | {Text.Cell(e.Seen)} | {Text.Cell(e.Propose)} | |");
        }
        return sb.ToString();
    }

    // ---- apply --------------------------------------------------------------------------------------------

    public sealed record Answer(string Id, string Kind, string Choice, string Note);

    /// <summary>"Q1: B, Q2: mặc định; Q3: xem lại — hỏi khách, D1: bỏ"</summary>
    public static List<Answer> ParseAnswers(string text)
    {
        var list = new List<Answer>();
        var rx = new Regex(@"(?<id>[QD]\d+)\s*[:=]\s*(?<val>.*?)(?=\s*[,;\n]\s*[QD]\d+\s*[:=]|$)", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        foreach (Match m in rx.Matches(text))
        {
            var id = m.Groups["id"].Value.ToUpperInvariant();
            var val = m.Groups["val"].Value.Trim().TrimEnd(',', ';').Trim();
            var fold = Text.Fold(val);
            if (id.StartsWith('D')) { list.Add(new Answer(id, fold.StartsWith("bo") ? "drop" : "keep", "", val)); continue; }
            var lm = Regex.Match(val, @"^([A-Z])(?:\b|$)\s*(?:[—–:-]\s*)?(.*)$");
            if (fold.StartsWith("mac dinh") || fold.StartsWith("default")) list.Add(new Answer(id, "default", "", Regex.Replace(val, @"^\S+\s+\S+\s*[—–:-]?\s*", "")));
            else if (fold.StartsWith("xem lai") || fold.StartsWith("hoi lai")) list.Add(new Answer(id, "reopen", "", val));
            else if (lm.Success) list.Add(new Answer(id, "choice", lm.Groups[1].Value, lm.Groups[2].Value.Trim()));
            else list.Add(new Answer(id, "text", "", val));
        }
        return list;
    }

    public string Apply(string? task, string file, string answers, string by, string source, bool acceptDefaults = true)
    {
        var dir = ctx.ResolveTask(task);
        var name = Path.GetFileName(dir);
        var pack = Load(dir, file);
        var given = ParseAnswers(answers);
        var unknown = given.Where(a => a.Id.StartsWith('Q') && pack.Questions.All(q => q.Id != a.Id)).Select(a => a.Id).ToList();
        if (unknown.Count > 0) throw new FlowException($"câu {string.Join(", ", unknown)} không có trong gói {pack.Round}");
        if (string.IsNullOrWhiteSpace(by)) throw new FlowException("thiếu người trả lời (by)");

        var date = ctx.Clock.Now.ToString("yyyy-MM-dd");
        var status = StatusFile.ForTask(dir);
        var docFile = Path.Combine(dir, pack.Doc == "plan" ? "plan.md" : "spec.md");
        var doc = new MdDoc(docFile);
        var log = new DecisionLog(ctx);
        var open = status.Data["open_questions"].Strings();

        var decided = new List<(PackQuestion Q, string Key, string Text, bool ByDefault)>();
        var reopened = new List<PackQuestion>();
        var blocked = new List<PackQuestion>();
        foreach (var q in pack.Questions)
        {
            var a = given.FirstOrDefault(x => x.Id == q.Id);
            switch (a?.Kind)
            {
                case "choice":
                    var opt = q.OptionText(a.Choice);
                    if (opt.Length == 0) throw new FlowException($"{q.Id}: không có phương án {a.Choice}");
                    decided.Add((q, a.Choice, opt + (a.Note.Length > 0 ? " — " + a.Note : ""), false));
                    break;
                case "text":
                    decided.Add((q, "", a.Note, false));
                    break;
                case "reopen":
                    reopened.Add(q);
                    break;
                case "default":
                case null when acceptDefaults:
                    if (q.Blocking) { if (!open.Contains(q.Id)) blocked.Add(q); }
                    else decided.Add((q, q.Default, q.OptionText(q.Default), true));
                    break;
            }
        }

        // Cấp số DEC trước, sửa tài liệu trong bộ nhớ, rồi mới ghi decision log + lưu file (không ghi nửa chừng).
        var docLabel = $"{pack.Doc} {name} gói {pack.Round}";
        var year = ctx.Clock.Now.Year;
        var firstNo = int.Parse(log.NextId(year)[9..]);
        var ids = decided.Select((_, k) => $"DEC-{year}-{firstNo + k:000}").ToList();

        var rows = new List<string>();
        for (var i = 0; i < decided.Count; i++)
        {
            var (q, key, txt, byDefault) = decided[i];
            var answer = (byDefault ? $"mặc định {key} được chấp nhận — " : (key.Length > 0 ? key + " — " : "")) + txt;
            var proposal = q.Recommend.Length > 0 ? $"{q.Recommend} — {q.OptionText(q.Recommend)}" : "";
            rows.Add(pack.Doc switch
            {
                "plan" => $"| {q.Id} | {Text.Cell(q.Q)} | {Text.Cell(proposal)} | {Text.Cell(answer)} ({ids[i]}) | {Text.Cell(by)} | {date} | {Text.Cell(q.Applies)} |",
                "bug" => $"| {q.Id} | {Text.Cell(q.Q)} | {Text.Cell(answer)} | {Text.Cell(by)} | {date} | {ids[i]} |",
                _ => $"| {q.Id} | {pack.Round} | {Text.Cell(q.Q)} | {Text.Cell(proposal)} | {Text.Cell(answer)} ({ids[i]}) | {Text.Cell(by)} | {date} | {Text.Cell(q.Applies)} |",
            });
        }
        var (section, answerCol) = pack.Doc switch { "plan" => ("1.2", 3), "bug" => ("7", 2), _ => ("14", 4) };
        if (rows.Count > 0) doc.AppendRows(section, rows, answerCol);

        // mặc định được chấp nhận → giả định §12 (spec)
        var defaults = decided.Where(d => d.ByDefault).ToList();
        if (pack.Doc == "spec" && defaults.Count > 0 && doc.HasTable("12"))
        {
            var n = doc.TableRows("12").Count;
            doc.AppendRows("12", defaults.Select((d, k) =>
                $"| G{n + k + 1} | {d.Q.Id}: mặc định {d.Key} — {Text.Cell(d.Text)} | chưa — người không trả lời ({d.Q.Id} §14) | quay lại {d.Q.Id} |"));
        }

        // CHẶN chưa có trả lời → §13 + câu treo; câu treo cũ nay đã trả lời → gỡ
        if (blocked.Count > 0)
        {
            if (pack.Doc == "spec" && doc.HasTable("13"))
                doc.AppendRows("13", blocked.Select(q => $"| {q.Id} | {Text.Cell(q.Q)} | {Text.Cell(q.Recommend)} | {Text.Cell(q.Decider)} | |"));
        }
        var resolved = decided.Where(d => open.Contains(d.Q.Id)).ToList();
        if (pack.Doc == "spec") foreach (var d in resolved) FillUnknownAnswer(doc, d.Q.Id, d.Text);

        var appended = log.Append(decided.Select(d => new DecisionRow(by, docLabel, $"{d.Q.Id} — {d.Q.Q}",
            (d.Key.Length > 0 ? d.Key + " — " : "") + d.Text + (d.ByDefault ? " (mặc định, người không trả lời)" : ""),
            $"{pack.Doc} {d.Q.Applies}".Trim(), source)));
        if (!appended.SequenceEqual(ids)) throw new FlowException("decision log vừa đổi trong lúc ghi — chạy lại pack_apply");
        foreach (var d in resolved) log.ResolvePending(d.Q.Id, name);
        if (blocked.Count > 0) log.AddPending(blocked.Select(q => new PendingRow(q.Id, docLabel, q.Q, q.Decider, "CHẶN")));
        doc.Save();

        var stillOpen = open.Except(decided.Select(d => d.Q.Id)).Union(blocked.Select(q => q.Id)).ToList();
        status.SetList("open_questions", stillOpen);
        foreach (var d in decided.Where(d => d.Q.Sets.Length > 0))
        {
            var v = d.Q.Values.TryGetValue(d.Key, out var mapped) ? mapped : d.Key;
            status.Set(d.Q.Sets, v);
        }
        status.Save();

        var sb = new StringBuilder();
        sb.AppendLine($"Đã ghi {decided.Count} câu vào {Path.GetFileName(docFile)} §{section} + decision log ({(ids.Count > 0 ? ids[0] + (ids.Count > 1 ? "…" + ids[^1] : "") : "—")}).");
        sb.AppendLine("Đọc lại cho người xác nhận — tôi hiểu là:");
        foreach (var d in decided) sb.AppendLine($"- {d.Q.Id}: {(d.ByDefault ? "(mặc định) " : "")}{Text.Clip(d.Text, 140)}");
        if (reopened.Count > 0) sb.AppendLine("Hỏi lại ở gói sau: " + string.Join(", ", reopened.Select(q => q.Id)));
        if (stillOpen.Count > 0) sb.AppendLine("CHẶN còn mở (chưa qua cổng được): " + string.Join(", ", stillOpen));
        var drops = given.Where(a => a.Id.StartsWith('D')).ToList();
        if (drops.Count > 0) sb.AppendLine("Đề xuất thêm: " + string.Join(", ", drops.Select(d => $"{d.Id} {(d.Kind == "drop" ? "bỏ" : "giữ")}")) + " — bỏ thì ghi 1 dòng ở §14 để không đề xuất lại.");
        return sb.ToString().TrimEnd();
    }

    private static void FillUnknownAnswer(MdDoc doc, string id, string answer)
    {
        if (doc.Section("13") is not { } s) return;
        for (var i = s.Start; i < s.End; i++)
            if (doc.Lines[i].StartsWith($"| {id} |"))
            {
                var c = MdDoc.Cells(doc.Lines[i]);
                c[^1] = Text.Cell(answer);
                doc.Lines[i] = "| " + string.Join(" | ", c) + " |";
            }
    }
}
