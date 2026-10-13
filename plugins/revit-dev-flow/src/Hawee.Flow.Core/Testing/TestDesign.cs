using System.Text;
using System.Text.RegularExpressions;
using Hawee.Flow.Core.Docs;
using Hawee.Flow.Core.Infra;
using Hawee.Flow.Core.Tasks;

namespace Hawee.Flow.Core.Testing;

/// <summary>
/// Phần máy của bước 3d (/test-design): chép + băm bộ dữ liệu và ghi dataset.yaml, dựng folder suite từ mẫu profile
/// (không ghi đè: *.proposed.*), suite-lint gọn, ma trận AC → suite → luật trong test-design.md.
/// Viết luật từ AC (nội dung suite.yaml / rules.md) vẫn là việc của agent.
/// </summary>
public sealed class TestDesign(ProjectContext ctx, IProcRunner? runner = null)
{
    private readonly IProcRunner _run = runner ?? ProcRunner.Instance;
    private TestPaths P => new(ctx);

    // ---- bộ dữ liệu ------------------------------------------------------------------------------------

    public string RegisterDataset(string from, string name, string by, string classification, string description, string hostVersion)
    {
        if (string.IsNullOrWhiteSpace(by)) throw new FlowException("thiếu người đưa / cho phép dùng dữ liệu (by) — GEN-SEC-002");
        if (string.IsNullOrWhiteSpace(classification)) throw new FlowException("thiếu classification (nội bộ | khách hàng — được phép dùng nội bộ trên máy test) — hỏi người");
        if (!Directory.Exists(from)) throw new FlowException($"không thấy thư mục nguồn {from}");
        var setName = Text.Slug(string.IsNullOrWhiteSpace(name) ? Path.GetFileName(from.TrimEnd('\\', '/')) : name);
        var dest = P.Dataset(setName);
        if (Directory.Exists(dest)) throw new FlowException($"bộ {setName} đã có trong kho — dùng lại (dataset_check) hoặc đặt tên khác");

        var files = Directory.GetFiles(from, "*", SearchOption.AllDirectories).OrderBy(f => f).ToList();
        var size = files.Sum(f => new FileInfo(f).Length);
        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(P.DatasetsDir))!);
        if (drive.IsReady && drive.AvailableFreeSpace < 2 * size) throw new FlowException($"ổ đích còn {drive.AvailableFreeSpace / 1048576} MB < 2 × {size / 1048576} MB");

        var before = files.ToDictionary(f => f, Text.Sha256File);
        foreach (var f in files)
        {
            var target = Path.Combine(dest, Path.GetRelativePath(from, f));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(f, target, overwrite: false);
        }
        var changed = files.Where(f => Text.Sha256File(f) != before[f]).ToList();
        var copyBad = files.Where(f => Text.Sha256File(Path.Combine(dest, Path.GetRelativePath(from, f))) != before[f]).ToList();
        if (changed.Count > 0 || copyBad.Count > 0)
            throw new FlowException($"hash lệch sau khi chép (nguồn đổi: {changed.Count}, bản chép sai: {copyBad.Count}) — dừng, kiểm lại");

        var sb = new StringBuilder();
        sb.AppendLine("# dataset.yaml — sinh bởi hawee-flow dataset_register (mẫu core/templates/test-dataset.yaml)");
        sb.AppendLine("# Dữ liệu khách hàng là thông tin mật (GEN-SEC-002): không đưa file của bộ vào repo/Base/dịch vụ ngoài.");
        sb.AppendLine();
        sb.AppendLine($"dataset: {setName}");
        sb.AppendLine($"description: {Q(description)}");
        sb.AppendLine($"source: {Q(from)}");
        sb.AppendLine($"by: {Q(by)}");
        sb.AppendLine($"added: {ctx.Clock.Now:yyyy-MM-dd}");
        sb.AppendLine($"classification: {Q(classification)}");
        if (hostVersion.Length > 0) sb.AppendLine($"host_version: {hostVersion}");
        sb.AppendLine();
        sb.AppendLine("files:");
        foreach (var f in files)
        {
            var rel = Path.GetRelativePath(from, f).Replace('/', '\\');
            sb.AppendLine($"  - {{ path: {Q(rel)}, role: {Role(f)}, sha256: {before[f].ToLowerInvariant()}, size_mb: {Math.Round(new FileInfo(f).Length / 1048576.0, 2).ToString(System.Globalization.CultureInfo.InvariantCulture)}, from: {Q(Path.GetFileName(f))} }}");
        }
        sb.AppendLine();
        sb.AppendLine("facts:");
        var csv = files.Where(f => f.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)).ToList();
        sb.AppendLine($"  csv: {{ {string.Join(", ", csv.Select(c => $"{Q(Path.GetFileName(c))}: {{ rows: {CsvRows(c)} }}"))} }}");
        sb.AppendLine("  config: {}                             # agent điền: chỉ các khoá mà AC dùng");
        sb.AppendLine("  model: {}");
        sb.AppendLine();
        sb.AppendLine("variants: []");
        Text.Write(Path.Combine(dest, "dataset.yaml"), sb.ToString());

        return $"Bộ {setName}: chép {files.Count} file ({size / 1048576.0:0.#} MB) vào {dest} · hash nguồn trước = sau = bản chép\n" +
               $"dataset.yaml: role đoán theo đuôi file ({string.Join(", ", files.Select(Role).Distinct())}); điền facts.config (khoá AC dùng) nếu cần kỳ vọng \"theo cài đặt\".";
    }

    public string CheckDataset(string name)
    {
        var dir = P.Dataset(name);
        if (!Directory.Exists(dir)) throw new FlowException($"không có bộ {name} trong {P.DatasetsDir}");
        return SuiteLintRun(new[] { "-Dataset", dir, "-Hash" });
    }

    private static string Q(string s) => StatusFile.Scalar(s);

    private static string Role(string f) => Path.GetExtension(f).ToLowerInvariant() switch
    {
        ".rvt" => "model",
        ".rfa" => "family",
        ".csv" => "csv",
        ".json" => "config",
        ".xlsx" or ".xls" => "sheet_list",
        ".dwg" => "cad",
        _ => "file",
    };

    private static int CsvRows(string f)
    {
        try { return Math.Max(0, File.ReadLines(f).Count(l => l.Trim().Length > 0) - 1); } catch { return -1; }
    }

    // ---- suite -----------------------------------------------------------------------------------------

    public string ScaffoldSuite(string? task, string suffix, string dataset, string description)
    {
        var dir = ctx.ResolveTask(task);
        var name = Path.GetFileName(dir) + (string.IsNullOrWhiteSpace(suffix) ? "" : "-" + Text.Slug(suffix));
        var suiteDir = P.Suite(name);
        var exists = Directory.Exists(suiteDir) && File.Exists(Path.Combine(suiteDir, "suite.yaml"));
        var profile = ctx.Profiles.FirstOrDefault() ?? "revit-addin";
        var tplDir = Path.Combine(ctx.BaseDir, "profiles", profile, "templates");
        var suiteTpl = Path.Combine(tplDir, "revit-suite.yaml");
        var rulesTpl = Path.Combine(tplDir, "revit-suite.rules.md");
        if (!File.Exists(suiteTpl) || !File.Exists(rulesTpl)) throw new FlowException($"thiếu mẫu suite trong {ctx.Rel(tplDir)}");

        var specVersion = StatusFile.ForTask(dir).Get("spec_version") is { Length: > 0 } v ? v : "v1";
        var specRel = ctx.Rel(Path.Combine(dir, "spec.md"));
        var model = "<model>.rvt";
        if (dataset.Length > 0)
        {
            var ds = Y.Load(Path.Combine(P.Dataset(dataset), "dataset.yaml"));
            if (!ds.Exists) throw new FlowException($"bộ {dataset} chưa có dataset.yaml — dataset_register trước");
            model = ds["files"].Items.FirstOrDefault(f => f["role"].S() == "model")?["path"].S() ?? model;
        }
        var revitYear = ctx.Config.At("revit.build_api").S("2024");
        var suite = Text.Read(suiteTpl)
            .Replace("<ten-folder-task>", name)
            .Replace("<bug / tính năng được test, kiểm gì>", description.Length > 0 ? description : "<kiểm gì>")
            .Replace("dataset: <tên bộ dữ liệu>", dataset.Length > 0 ? $"dataset: {dataset}" : "dataset: <tên bộ dữ liệu>")
            .Replace("<docs/tasks/<task>/spec.md> v1", $"{specRel} {specVersion}")
            .Replace("'{dataset}\\<model>.rvt'", $"'{{dataset}}\\{model}'")
            .Replace("revit_version: 2024", $"revit_version: {revitYear}")
            .Replace("Revit 2024", $"Revit {revitYear}")
            .Replace("D:\\<repo>\\hawee.project.yaml", Path.Combine(ctx.Root, "hawee.project.yaml"));
        var rules = Text.Read(rulesTpl)
            .Replace("<ten-folder-task>", name)
            .Replace("<docs/tasks/<task>/spec.md>", specRel)
            .Replace("v<N> ngày <yyyy-MM-dd>", $"{specVersion} ngày {ctx.Clock.Now:yyyy-MM-dd}");

        Directory.CreateDirectory(suiteDir);
        var suiteFile = Path.Combine(suiteDir, exists ? "suite.proposed.yaml" : "suite.yaml");
        var rulesFile = Path.Combine(suiteDir, exists ? "rules.proposed.md" : "rules.md");
        if (File.Exists(suiteFile) || File.Exists(rulesFile)) throw new FlowException($"{suiteFile} đã có — sửa trực tiếp file đó");
        Text.Write(suiteFile, suite);
        Text.Write(rulesFile, rules);

        var status = StatusFile.ForTask(dir);
        status.AddToList("suites", name);
        status.Save();
        return (exists ? $"Suite {name} đã có → ghi bản đề xuất {Path.GetFileName(suiteFile)} + {Path.GetFileName(rulesFile)} (giữ ID luật cũ, luật mới lấy số kế tiếp, luật bỏ ghi retired)\n"
                       : $"Đã dựng {suiteDir}\\suite.yaml + rules.md từ mẫu profile\n") +
               "Viết: steps (tool + args theo spec §7, đường dẫn '{dataset}\\…' trong nháy đơn), luật R-xx đủ trường (AC, Kiểm, Kỳ vọng có nguồn, Cách đo, Sai số — chưa có thì 'chờ', Mức, Máy chấm, Ảnh minh chứng), bảng Chỗ hở.\n" +
               "Xong → suite_lint rồi matrix_build. Cú pháp Máy chấm / định dạng: references/rules-cheatsheet.md của skill test-design.";
    }

    public string SuiteLint(string? task, string suites, bool hash = true)
    {
        var names = suites.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        string? spec = null;
        if (names.Count == 0 || task is { Length: > 0 })
        {
            var dir = ctx.ResolveTask(task);
            if (names.Count == 0) names = StatusFile.ForTask(dir).Data["suites"].Strings();
            spec = Path.Combine(dir, "spec.md");
        }
        if (names.Count == 0) throw new FlowException("task chưa có suite — scaffold_suite trước");
        var args = new List<string> { "-Suite", string.Join(",", names.Select(P.Suite)), "-Block", ctx.TestBlockName };
        if (spec is not null && File.Exists(spec)) { args.Add("-Spec"); args.Add(spec); }
        if (hash) args.Add("-Hash");
        var res = SuiteLintRun(args);
        var noTol = names.SelectMany(n => File.Exists(Path.Combine(P.Suite(n), "rules.md"))
                ? RulesFile.Load(Path.Combine(P.Suite(n), "rules.md")).Rules.Where(r => !r.Pending
                    && Regex.IsMatch(Text.Fold(r.Grader), @"^\W*(bang|trong|giao|khop baseline)\s*\(") && !r.Grader.Contains('±')).Select(r => $"{n}/{r.Id}")
                : Enumerable.Empty<string>()).ToList();
        if (noTol.Count == 0) return res;
        var tol = ctx.TestBlock.Exists ? RunnerConfig.DefaultTol(ctx.TestBlock) : 0;
        return res + $"\nWARN [rule-tol] {noTol.Count} luật so số thực không ghi '± x' → so với sai số {tol.ToString(System.Globalization.CultureInfo.InvariantCulture)} mm (như rule-eval.ps1; feet → mm thường lệch 1e-8 → ghi ± vào Máy chấm): {string.Join(", ", noTol.Take(8))}{(noTol.Count > 8 ? " …" : "")}";
    }

    private string SuiteLintRun(IEnumerable<string> args)
    {
        var script = ctx.BaseTool("suite-lint.ps1");
        if (!File.Exists(script)) throw new FlowException("không có suite-lint.ps1 trong Base");
        var r = Ps.RunFile(_run, script, args, ctx.Root, TimeSpan.FromMinutes(5));
        var lines = r.Lines.Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var problems = lines.Where(l => Regex.IsMatch(l, @"^(WARN\s+)?\[[a-z-]+\]")).ToList();
        var pending = problems.Count(l => l.Contains("[rule-pending]"));
        var sb = new StringBuilder();
        sb.AppendLine($"suite-lint exit {r.ExitCode} ({(r.ExitCode == 0 ? "sạch" : r.ExitCode == 1 ? "có lỗi" : "đầu vào sai")}) · {problems.Count} dòng" + (pending > 0 ? $" · {pending} luật còn 'chờ' (câu hỏi cho người duyệt ở G2)" : ""));
        foreach (var l in Text.Cap(problems.Where(p => !p.Contains("[rule-pending]")), 15)) sb.AppendLine(l);
        if (problems.Count == 0 && r.ExitCode != 0) foreach (var l in lines.TakeLast(5)) sb.AppendLine(l);
        return sb.ToString().TrimEnd();
    }

    // ---- ma trận ---------------------------------------------------------------------------------------

    /// <summary>
    /// Cột Kết quả của một AC từ result.json lần chạy task mới nhất của từng suite (gộp verdict các luật dẫn AC):
    /// FAIL/ERROR → FAIL; SUSPECT/NOT_MEASURABLE → SUSPECT; mọi luật PASS → PASS; Chỗ hở → chưa đo; không luật → không phủ.
    /// </summary>
    /// <summary>Kết quả AC: run mới nhất purpose=task (cùng phiên test_session của task nếu có) — không lấy run adhoc / baseline.</summary>
    private string AcResult(string ac, IEnumerable<string> suites, bool gap, bool needSuite, string session)
    {
        var verdicts = new List<string>();
        var runs = new List<string>();
        foreach (var s in suites)
        {
            var runsDir = Path.Combine(P.Suite(s), "runs");
            if (!Directory.Exists(runsDir)) continue;
            var last = Directory.GetDirectories(runsDir).OrderByDescending(d => d).FirstOrDefault(d =>
            {
                if (!File.Exists(Path.Combine(d, "result.json")) || !File.Exists(Path.Combine(d, "run-info.json"))) return false;
                var info = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(d, "run-info.json")));
                return info?["purpose"]?.ToString() == "task" && (session.Length == 0 || info["session"]?.ToString() == session)
                       && info["rules_approved"]?.ToString() != "false";
            });
            if (last is null) continue;
            runs.Add($"{s}/{Path.GetFileName(last)}");
            var res = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(last, "result.json"))) as System.Text.Json.Nodes.JsonArray;
            foreach (var sheet in res ?? new System.Text.Json.Nodes.JsonArray())
                foreach (var rule in sheet?["rules"] as System.Text.Json.Nodes.JsonArray ?? new System.Text.Json.Nodes.JsonArray())
                    if (Regex.IsMatch(rule?["ac"]?.ToString() ?? "", $@"\b{ac}\b")) verdicts.Add(rule!["verdict"]!.ToString());
        }
        if (verdicts.Count == 0) return gap ? "chưa đo (chỗ hở)" : needSuite ? (runs.Count == 0 ? "chưa chạy" : "không phủ") : "—";
        var v = verdicts.Any(x => x is "FAIL" or "ERROR") ? "FAIL" : verdicts.All(x => x == "PASS") ? "PASS" : "SUSPECT";
        return $"{v} ({string.Join(", ", runs)})";
    }

    /// <summary>
    /// AC ở spec §5 có cột "Đo bằng" là suite / chạy trong Revit (bỏ câu mẫu "test thuần / log / …" chưa điền).
    /// Dùng chung cho matrix_build, gate_submit G2 và flow_next.
    /// </summary>
    public static List<string> AcsNeedingSuite(string specPath)
    {
        if (!File.Exists(specPath)) return new();
        return new MdDoc(specPath).TableRows("5")
            .Where(r => r.Length >= 5 && Regex.IsMatch(r[0], @"^AC\d+$") && !r[4].StartsWith("test thuần / log")
                        && Regex.IsMatch(Text.Fold(r[4]), "suite|revit-test|ung dung (that|chu)|trong revit"))
            .Select(r => r[0]).ToList();
    }

    /// <summary>File *.proposed.* còn trong folder suite (bản đề xuất của suite_scaffold chưa được đưa thành bản chính).</summary>
    public static List<string> ProposedLeft(string? suitesDir, IEnumerable<string> suites) =>
        suitesDir is null ? new() : suites.SelectMany(s => new[] { "suite.proposed.yaml", "rules.proposed.md" }
            .Where(f => File.Exists(Path.Combine(suitesDir, s, f))).Select(f => $"{s}\\{f}")).ToList();

    public string BuildMatrix(string? task)
    {
        var dir = ctx.ResolveTask(task);
        var status = StatusFile.ForTask(dir);
        var st = status.Data;
        var spec = new MdDoc(Path.Combine(dir, "spec.md"));
        var acRows = spec.TableRows("5").Where(r => Regex.IsMatch(r[0], @"^AC\d+$")).ToList();
        var needSuite = AcsNeedingSuite(Path.Combine(dir, "spec.md"));

        var suiteRules = new List<(string Suite, TestRule Rule)>();
        var gaps = new List<(string Suite, GapRow Gap)>();
        foreach (var s in st["suites"].Strings())
        {
            var rf = Path.Combine(P.Suite(s), "rules.md");
            if (!File.Exists(rf)) continue;
            var parsed = RulesFile.Load(rf);
            suiteRules.AddRange(parsed.Active.Select(r => (s, r)));
            gaps.AddRange(parsed.Gaps.Select(g => (s, g)));
        }

        var lines = new List<string>
        {
            $"Spec: {ctx.Rel(Path.Combine(dir, "spec.md"))} {st["spec_version"].S("v1")} · sinh bởi hawee-flow matrix_build {ctx.Clock.Now:yyyy-MM-dd}",
            "",
            "| AC | Đo bằng | Suite → luật (nguồn kỳ vọng) | Kết quả |",
            "|---|---|---|---|",
        };
        var uncovered = new List<string>();
        foreach (var r in acRows)
        {
            var ac = r[0];
            var hits = suiteRules.Where(x => x.Rule.AcIds.Contains(ac)).ToList();
            var gap = gaps.FirstOrDefault(g => g.Gap.Ac.Contains(ac));
            string cell;
            if (hits.Count > 0) cell = string.Join("; ", hits.GroupBy(h => h.Suite).Select(g => $"{g.Key} → " + string.Join(", ", g.Select(h => $"{h.Rule.Id} ({(h.Rule.ExpectSource.Length > 0 ? h.Rule.ExpectSource : "?")})"))));
            else if (gap.Gap is not null) cell = $"Chỗ hở ({gap.Suite}): {gap.Gap.Missing}";
            else if (needSuite.Contains(ac)) { cell = "KHÔNG PHỦ"; uncovered.Add(ac); }
            else cell = "ngoài suite (unit test / tool READ / kiểm tay)";
            lines.Add($"| {ac} | {Text.Cell(r.Length >= 5 ? Text.Clip(r[4], 60) : "")} | {Text.Cell(cell)} | {Text.Cell(AcResult(ac, hits.Select(h => h.Suite).Distinct(), gap.Gap is not null, needSuite.Contains(ac), st["test_session"].S()))} |");
        }
        var pending = suiteRules.Where(x => x.Rule.Pending).ToList();
        var noSource = suiteRules.Where(x => x.Rule.ExpectSource.Length == 0).ToList();
        var baselineNeeded = suiteRules.Any(x => x.Rule.ExpectSource == "baseline");
        lines.Add("");
        lines.Add($"Luật: {suiteRules.Count} (đang dùng) · còn 'chờ': {(pending.Count == 0 ? "không" : string.Join(", ", pending.Select(p => p.Rule.Id)))} · thiếu nguồn kỳ vọng: {(noSource.Count == 0 ? "không" : string.Join(", ", noSource.Select(p => p.Rule.Id)))}");
        lines.Add($"Cần lần chạy baseline ở 4a: {(baselineNeeded ? "có (luật theo baseline)" : "không")}");

        var tdPath = Path.Combine(dir, "test-design.md");
        var td = new MdDoc(tdPath);
        if (td.Lines.Count == 0)
            td.Lines.AddRange(new[] { $"# Test design — {Path.GetFileName(dir)}", "", "Bộ dữ liệu, kịch bản K → suite, câu hỏi 'chờ': agent ghi bên dưới (ngoài khối máy sinh).", "" , "## Ma trận AC → suite → luật → kết quả", "" });
        td.ReplaceBlock("matrix", lines);
        td.Save();
        status.Set("test_design", ctx.Rel(tdPath));
        status.Save();

        var sb = new StringBuilder();
        sb.AppendLine($"Ma trận: {acRows.Count} AC · {needSuite.Count} đo bằng suite · {suiteRules.Count} luật · không phủ: {(uncovered.Count == 0 ? "không" : string.Join(", ", uncovered))}");
        if (pending.Count > 0) sb.AppendLine($"Luật còn 'chờ' (hỏi người trước G2): {string.Join(", ", pending.Select(p => p.Rule.Id))}");
        if (noSource.Count > 0) sb.AppendLine($"Luật thiếu nguồn kỳ vọng: {string.Join(", ", noSource.Select(p => p.Rule.Id))}");
        if (baselineNeeded) sb.AppendLine("Có luật 'theo baseline' → chạy suite một lần purpose=baseline trên build commit gốc (bước 4a).");
        sb.Append($"Ghi: {ctx.Rel(tdPath)}");
        return sb.ToString();
    }
}
