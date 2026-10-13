using Hawee.Flow.Core.Infra;
using Hawee.Flow.Core.Testing;

namespace Hawee.Flow.Tests;

/// <summary>
/// Bộ chấm C# phải cho cùng verdict với BIM_KB_Base core/tools/rule-eval.ps1: fixture và bảng kỳ vọng chép nguyên từ
/// core/tools/tests/rule-eval.tests.ps1 (Base 0.8). Base đổi test → chép lại ở đây.
/// </summary>
public class RuleEvalConformanceTests : IDisposable
{
    private readonly string _root;
    private readonly string _task;
    private readonly string _run;

    private static string Rule(string id, string oracle, string extraHead = "")
    {
        var s = $"### {id}{extraHead}\n- **AC:** AC1 (K1)\n- **Kiem:** test\n";
        if (oracle.Length > 0) s += $"- **Máy chấm:** {oracle}\n";
        return s + "\n";
    }

    internal static readonly string Rules = "# Luat - test\n\n## Ap dung cho: buoc 1\n\n"
        + Rule("R-01", "`có dòng(addin.log, \"Loaded config: .*\\.json\")`")
        + Rule("R-02", "`không có dòng(addin.log, \"step=SAVE\")`")
        + Rule("R-03", "`đếm(sheet_components.json → Sheets[]) theo SetNo >= 1`")
        + Rule("R-04", "`đếm(sheet_components.json → Sheets[].Components[ComponentType=View]) theo SheetNumber = 1`")
        + Rule("R-05", "`giao(sheet_components.json → Sheets[].Components[ComponentType=View], Sheets[].Components[ComponentType=Keyplan]) theo SheetNumber = 0`")
        + Rule("R-06", "`giao(sheet_components.json → Sheets[SheetNumber=A02].Components[ComponentType=View], Sheets[SheetNumber=A02].Components[ComponentType=Keyplan]) theo SheetNumber = 0`")
        + Rule("R-07", "`step xong(SAVE)`")
        + Rule("R-08", "`step xong(EXPORT)`")
        + Rule("R-09", "`khớp baseline(sheet_components.json, khoá SheetNumber, trường SheetName)`")
        + Rule("R-10", "`khớp baseline(sheet_components.json, khoá SetNo, trường đếm(Sheets[]))`")
        + Rule("R-11", "`bằng(sheet_components.json → Sheets[SheetNumber=A01].Components[ComponentType=Keyplan].MaxX, 120) ± chờ`")
        + Rule("R-12", "`ảnh(label khong de keyplan)`")
        + Rule("R-13", "`có dòng(addin.log, \"name='<sheet_name dòng 1>'\")`")
        + Rule("R-14", "`đếm(sheet_components.json → Sheets[]) = 99`", " - bo - retired 2026-10-12")
        + Rule("R-15", "")
        + Rule("R-16", "`đếm(Sheets[].Components[ViewName bắt đầu bằng \"TMP\"]) = 0`")
        + Rule("R-17", "`trong(sheet_components.json → Sheets[].Components[ComponentType=Keyplan], Sheets[].Zones[]) theo SheetNumber`")
        + Rule("R-18", "`thứ tự(sheet_components.json → Sheets[SetNo=S1].SheetNumber) = [A01, A02]`")
        + Rule("R-19", "`bằng(sheet_components.json → GeneratedBy, \"test\")`")
        + Rule("R-20", "`bằng(sheet_components.json → GeneratedBy, \"khac\")`")
        + Rule("R-21", "`thứ tự(sheet_components.json → Sheets[SetNo=S1].SheetNumber) = [A02, A01]`")
        + Rule("R-22", "`trong(sheet_components.json → Sheets[].Components[ComponentType=View], Sheets[].Zones[]) theo SheetNumber`")
        + Rule("R-23", "`có dòng(addin.log, \"x\")` va `đếm(sheet_components.json → Sheets[to MatBang]) = 1`")
        + Rule("R-24", "`giao(sheet_components.json → Sheets[SheetNumber=A01].Components[], Sheets[SheetNumber=A01].Components[]) theo SheetNumber <= 100`")
        + Rule("R-25", "`giao(sheet_components.json → Sheets[SheetNumber=A01].Components[], Sheets[SheetNumber=A01].Components[]) theo SheetNumber < 100`")
        + Rule("R-26", "`đếm(results[]) >= 0`");

    private static string Box(double a, double b, double c, double d) => FormattableString.Invariant($"\"MinX\": {a}, \"MinY\": {b}, \"MaxX\": {c}, \"MaxY\": {d}");

    internal static string Data(string nameA02) => $$"""
        { "GeneratedBy": "test", "Sheets": [
          { "SetNo": "S1", "SheetNumber": "A01", "SheetName": "Mat bang 1",
            "Components": [ { "ComponentType": "View", "ViewName": "V1", {{Box(0, 0, 100, 100)}} }, { "ComponentType": "Keyplan", "ViewName": "K1", {{Box(90, 90, 120, 120)}} } ],
            "Zones": [ { {{Box(80, 80, 130, 130)}} } ] },
          { "SetNo": "S1", "SheetNumber": "A02", "SheetName": "{{nameA02}}",
            "Components": [ { "ComponentType": "View", "ViewName": "V2", {{Box(0, 0, 50, 50)}} }, { "ComponentType": "Keyplan", "ViewName": "K2", {{Box(60, 60, 80, 80)}} } ],
            "Zones": [ { {{Box(50, 50, 100, 100)}} } ] },
          { "SetNo": "S2", "SheetNumber": "B01", "SheetName": "Bia", "Components": [], "Zones": [] }
        ] }
        """;

    /// <summary>Bảng $expect của rule-eval.tests.ps1 (R-14 retired: không chấm).</summary>
    public static readonly Dictionary<string, string> Expect = new()
    {
        ["R-01"] = "PASS", ["R-02"] = "FAIL", ["R-03"] = "PASS", ["R-04"] = "FAIL", ["R-05"] = "FAIL", ["R-06"] = "PASS", ["R-07"] = "PASS", ["R-08"] = "FAIL",
        ["R-09"] = "FAIL", ["R-10"] = "PASS", ["R-11"] = "NOT_MEASURABLE", ["R-12"] = "AGENT", ["R-13"] = "AGENT", ["R-15"] = "AGENT",
        ["R-16"] = "PASS", ["R-17"] = "PASS", ["R-18"] = "PASS", ["R-19"] = "PASS", ["R-20"] = "FAIL", ["R-21"] = "FAIL", ["R-22"] = "FAIL", ["R-23"] = "FAIL",
        ["R-24"] = "PASS", ["R-25"] = "FAIL", ["R-26"] = "AGENT",
    };

    public RuleEvalConformanceTests()
    {
        _root = Directory.CreateTempSubdirectory("re").FullName;
        _task = Path.Combine(_root, "tasks", "t1");
        _run = Path.Combine(_task, "runs", "20260101-000000");
        Text.Write(Path.Combine(_task, "rules.md"), Rules);
        Text.Write(Path.Combine(_run, "data", "sheet_components.json"), Data("Mat bang 2"));
        Text.Write(Path.Combine(_task, "baseline", "sheet_components.json"), Data("Mat bang 2 cu"));
        File.WriteAllText(Path.Combine(_run, "data", "addin.log"), "2026-01-01 10:00:00 [1] INFO - Loaded config: abc.json\r\n2026-01-01 10:00:01 [1] INFO - step=SAVE done\r\n");
        Text.Write(Path.Combine(_run, "run.log"), "buoc 1 ok\n");
        Text.Write(Path.Combine(_run, "data", "step_status.json"), """{ "externalRunId": "run:1", "source": "x", "ok": true, "message": "", "steps": [ { "stepCode": "SAVE", "status": "SUCCEEDED" } ] }""");
    }

    public void Dispose() => Directory.Delete(_root, true);

    /// <summary>Chấm như runner (TestRunner.Grade): một GradingData cho cả lần chạy, mỗi luật một Grader.</summary>
    private Dictionary<string, GradeResult> GradeAll(string run)
    {
        var rf = RulesFile.Load(Path.Combine(_task, "rules.md"));
        var data = new GradingData(run, _task, Array.Empty<string>());
        return rf.Active.ToDictionary(r => r.Id, r => new Grader(data).Grade(r, TestRunner.SheetsOf(r.AppliesTo)));
    }

    [Fact]
    public void Doc_rules_md_nhu_rule_eval()
    {
        var rf = RulesFile.Load(Path.Combine(_task, "rules.md"));
        Assert.Equal(26, rf.Rules.Count);
        Assert.True(rf.Rules.Single(r => r.Id == "R-14").Retired);
        Assert.DoesNotContain(rf.Active, r => r.Id == "R-14");
        Assert.Equal(new[] { "có dòng(addin.log, \"x\")", "đếm(sheet_components.json → Sheets[to MatBang]) = 1" }, rf.Rules.Single(r => r.Id == "R-23").Clauses);
        Assert.Empty(rf.Rules.Single(r => r.Id == "R-15").Clauses);
    }

    [Fact]
    public void Cung_verdict_voi_rule_eval()
    {
        var v = GradeAll(_run);
        var wrong = Expect.Where(e => !v.TryGetValue(e.Key, out var g) || g.Verdict != e.Value)
            .Select(e => $"{e.Key}: rule-eval {e.Value}, C# {(v.TryGetValue(e.Key, out var g) ? g.Verdict + " — " + g.Reason + " " + string.Join(" | ", g.Groups.Select(x => $"{x.Group}={x.Verdict}/{x.Actual}")) : "không chấm")}")
            .ToList();
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
        Assert.Equal(Expect.Count, v.Count);
    }

    [Fact]
    public void Chi_tiet_nhom_rong_va_giao_khong_nhan_doi()
    {
        var v = GradeAll(_run);
        Assert.Equal("0", v["R-04"].Groups.Single(g => g.Group == "B01").Actual);          // tờ không có view vẫn là một nhóm (= 0)
        Assert.Equal("100 mm²", v["R-05"].Groups.Single(g => g.Group == "A01").Actual);
        Assert.Equal("100 mm²", v["R-25"].Groups.Single(g => g.Group == "A01").Actual);    // giao tập với chính nó: mỗi cặp một lần
    }

    [Fact]
    public void Chay_lai_ra_cung_ket_qua_va_khoa_doi_khi_du_lieu_doi()
    {
        string Sig(Dictionary<string, GradeResult> v) => string.Join("\n", v.OrderBy(x => x.Key).Select(x => $"{x.Key} {x.Value.Verdict} {string.Join(",", x.Value.Groups.Select(g => g.Group + "=" + g.Actual))}"));
        var rules = RulesFile.Load(Path.Combine(_task, "rules.md"));
        var r03 = rules.Rules.Single(r => r.Id == "R-03");
        var first = GradeAll(_run);
        Assert.Equal(Sig(first), Sig(GradeAll(_run)));
        var key1 = TestRunner.GradingKey(r03, first["R-03"].FilesRead, Path.Combine(_run, "data"), first["R-03"].Verdict);
        Text.Write(Path.Combine(_run, "data", "sheet_components.json"), Data("Mat bang 2 moi"));
        var second = GradeAll(_run);
        Assert.NotEqual(key1, TestRunner.GradingKey(r03, second["R-03"].FilesRead, Path.Combine(_run, "data"), second["R-03"].Verdict));
        // luật chỉ đọc log không đổi file → không ghi file JSON vào khoá
        Assert.DoesNotContain(second["R-01"].FilesRead, f => f.EndsWith("sheet_components.json"));
    }

    [Fact]
    public void Thieu_du_lieu_khong_bao_gio_PASS()
    {
        var run2 = Path.Combine(_task, "runs", "20260101-000100");
        Text.Write(Path.Combine(run2, "run.log"), "x\n");
        var v = GradeAll(run2);
        Assert.Equal(Verdicts.NotMeasurable, v["R-03"].Verdict);
        Assert.Equal(Verdicts.NotMeasurable, v["R-01"].Verdict);
        Assert.Empty(v.Where(x => x.Value.Verdict == Verdicts.Pass).Select(x => x.Key));
    }
}
