using Hawee.Flow.Core.Infra;
using Hawee.Flow.Core.Testing;

namespace Hawee.Flow.Tests;

public class GradingTests : IDisposable
{
    private readonly string _run;
    private readonly string _suite;
    private readonly string _cfg;

    public GradingTests()
    {
        var root = Directory.CreateTempSubdirectory("grade").FullName;
        _suite = Path.Combine(root, "suite");
        _run = Path.Combine(_suite, "runs", "20261012-090000");
        Text.Write(Path.Combine(_run, "data", "checkpoints", "SAVE", "sheet_components.json"), """
            { "sheets": [
              { "SheetNumber": "A-101", "components": [
                  { "type": "Viewport", "bbox": { "minX": 10, "minY": 10, "maxX": 300, "maxY": 200 } },
                  { "type": "Keyplan",  "bbox": { "minX": 320, "minY": 10, "maxX": 380, "maxY": 50 } },
                  { "type": "Label",    "bbox": [ 330, 40, 360, 60 ] },
                  { "type": "Titleblock", "bbox": { "x": 0, "y": 0, "width": 420, "height": 297 } } ] },
              { "SheetNumber": "A-102", "components": [
                  { "type": "Viewport", "bbox": { "minX": 10, "minY": 10, "maxX": 300, "maxY": 200 } },
                  { "type": "Viewport", "bbox": { "minX": 10, "minY": 10, "maxX": 100, "maxY": 100 } },
                  { "type": "Keyplan",  "bbox": { "minX": 400, "minY": 10, "maxX": 460, "maxY": 50 } },
                  { "type": "Titleblock", "bbox": { "x": 0, "y": 0, "width": 420, "height": 297 } } ] } ] }
            """);
        Text.Write(Path.Combine(_run, "data", "addin.log"), "2026-10-12 09:01:00 [1] INFO start\n2026-10-12 09:01:05 [1] ERROR boom\n");
        Text.Write(Path.Combine(_run, "data", "step_status.json"), """{ "steps": [ { "stepCode": "CREATE_SHEETS", "status": "SUCCEEDED" }, { "stepCode": "SAVE", "status": "FAILED" } ] }""");
        _cfg = Path.Combine(root, "dataset", "config.json");
        Text.Write(_cfg, "{\n  // cấu hình\n  \"Keyplan\": { \"Canvas\": [60, 40], \"Anchor\": \"TopRight\", },\n  \"Order\": [\"A-101\", \"A-102\"]\n}");
        Text.Write(Path.Combine(_suite, "baseline", "sheet_components.json"), File.ReadAllText(Path.Combine(_run, "data", "checkpoints", "SAVE", "sheet_components.json")).Replace("\"maxX\": 380", "\"maxX\": 381"));
    }

    public void Dispose() => Directory.Delete(Path.GetDirectoryName(_suite)!, true);

    private GradeResult G(string grader, string extra = "", params string[] sheets)
    {
        var rule = RulesFile.Parse($"### R-01 — x\n- Kỳ vọng: theo spec (AC1): x\n- Sai số: không\n{extra}- Máy chấm: {grader}\n").Rules[0];
        return new Grader(new GradingData(_run, _suite, new[] { _cfg })).Grade(rule, sheets);
    }

    [Fact]
    public void Parse_cu_phap()
    {
        var c = Grader.Parse("giao(View, Keyplan) theo SheetNumber = 0");
        Assert.Equal(("giao", "theo SheetNumber = 0"), (c.Name, c.Rest));
        Assert.Equal(new[] { "View", "Keyplan" }, c.Args);
        Assert.Equal("theo SheetNumber ± 1,5 mm", Grader.Parse("trong(Keyplan, Titleblock) theo SheetNumber ± 1,5 mm").Rest);
        Assert.Equal(new[] { "a.json → x[ViewName bắt đầu bằng \"V, 1\"]" }, Grader.Parse("đếm(a.json → x[ViewName bắt đầu bằng \"V, 1\"]) = 0").Args);
        Assert.Equal("dem", Grader.Parse("đếm(a.json → x[type=Viewport]) theo SheetNumber = 1").Name);
        Assert.Equal("khong co dong", Grader.Parse("không có dòng(addin.log, \"ERROR, x\")").Name);
        Assert.Equal(new[] { "addin.log", "\"ERROR, x\"" }, Grader.Parse("có dòng(addin.log, \"ERROR, x\")").Args);
    }

    [Fact]
    public void Dem_theo_sheet()
    {
        var r = G("đếm(sheet_components.json → sheets.components[type=Viewport]) theo SheetNumber = 1");
        Assert.Equal(Verdicts.Fail, r.Verdict);
        Assert.Equal(Verdicts.Pass, r.Groups.Single(g => g.Group == "A-101").Verdict);
        Assert.Equal("2", r.Groups.Single(g => g.Group == "A-102").Actual);
        // sheet trong "Áp dụng cho" nhưng không có dữ liệu → đếm 0
        var r2 = G("đếm(sheet_components.json → sheets.components[type=Viewport]) theo SheetNumber ≥ 1", "", "A-101", "A-999");
        Assert.Equal("0", r2.Groups.Single(g => g.Group == "A-999").Actual);
        Assert.Equal(Verdicts.Fail, r2.Verdict);
    }

    [Fact]
    public void Giao_va_trong_dung_ten_tran()
    {
        var g = G("giao(Label, Keyplan) theo SheetNumber = 0");
        Assert.Equal(Verdicts.Fail, g.Groups.Single(x => x.Group == "A-101").Verdict);       // label đè keyplan 300 mm²
        Assert.Equal("300 mm²", g.Groups.Single(x => x.Group == "A-101").Actual);
        Assert.DoesNotContain(g.Groups, x => x.Group == "A-102");                              // A-102 không có Label → không có nhóm để giao
        var t = G("trong(Keyplan, Titleblock) theo SheetNumber ± 1");
        Assert.Equal(Verdicts.Pass, t.Groups.Single(x => x.Group == "A-101").Verdict);
        Assert.Equal(Verdicts.Fail, t.Groups.Single(x => x.Group == "A-102").Verdict);        // 460 > 420
    }

    [Fact]
    public void Bang_cai_dat_thu_tu_log_step()
    {
        Assert.Equal(Verdicts.Pass, G("bằng(cài đặt(config.json, Keyplan.Anchor), TopRight)").Verdict);
        // nguồn ra nhiều phần tử không phải một giá trị → agent chấm (rule-eval.ps1 cũng vậy), không lấy số phần tử
        Assert.Equal(Verdicts.Agent, G("bằng(sheet_components.json → sheets, 2)").Verdict);
        Assert.Equal(Verdicts.Fail, G("bằng(cài đặt(config.json, Keyplan.Anchor), topright)").Verdict);   // so chuỗi phân biệt hoa thường
        Assert.Equal(Verdicts.Pass, G("thứ tự(sheet_components.json → sheets.SheetNumber) = cài đặt(config.json, Order)").Verdict);
        Assert.Equal(Verdicts.Fail, G("thứ tự(sheet_components.json → sheets.SheetNumber) = [A-102, A-101]").Verdict);
        Assert.Equal(Verdicts.Pass, G("có dòng(addin.log, \"ERROR boom\")").Verdict);
        var nl = G("không có dòng(addin.log, \"ERROR\")");
        Assert.Equal(Verdicts.Fail, nl.Verdict);
        Assert.Contains("dòng 2", nl.Groups[0].Actual);
        Assert.Equal(Verdicts.Pass, G("step xong(CREATE_SHEETS)").Verdict);
        Assert.Equal(Verdicts.Fail, G("step xong(SAVE)").Verdict);
    }

    [Fact]
    public void Khop_baseline_co_sai_so()
    {
        var strict = G("khớp baseline(sheet_components.json, khoá type, trường bbox)");
        Assert.Equal(Verdicts.Fail, strict.Verdict);
        var tol = G("khớp baseline(sheet_components.json, khoá type, trường bbox) ± 1");
        Assert.Equal(Verdicts.Pass, tol.Verdict);
        Assert.Equal(Verdicts.NotMeasurable, G("khớp baseline(khong-co.json, khoá type, trường bbox)").Verdict);
    }

    [Fact]
    public void So_so_thuc_dung_sai_so_mac_dinh()
    {
        GradeResult T(string grader, double tol)
        {
            var rule = RulesFile.Parse($"### R-01 — x\n- Kỳ vọng: x\n- Máy chấm: {grader}\n").Rules[0];
            return new Grader(new GradingData(_run, _suite, new[] { _cfg })) { DefaultTol = tol }.Grade(rule, Array.Empty<string>());
        }
        // feet → mm: 99.99999997 thay vì 100
        Assert.Equal(Verdicts.Fail, T("bằng(99.99999997, 100)", 0).Verdict);
        Assert.Equal(Verdicts.Pass, T("bằng(99.99999997, 100)", 0.01).Verdict);
        Assert.Equal(Verdicts.Fail, T("bằng(99.9, 100)", 0.01).Verdict);
        // "± x" trong luật thắng sai số mặc định
        Assert.Equal(Verdicts.Fail, T("bằng(99.99999997, 100) ± 0", 0.01).Verdict);
        // khớp baseline không ghi ± → so chuỗi y nguyên như rule-eval.ps1; có ± → so số (đọc được dạng mũ)
        Text.Write(Path.Combine(_suite, "baseline", "e.json"), """[ { "type": "A", "v": "0.000012" } ]""");
        Text.Write(Path.Combine(_run, "data", "e.json"), """[ { "type": "A", "v": "1.2E-05" } ]""");
        Assert.Equal(Verdicts.Fail, T("khớp baseline(e.json, khoá type, trường v)", 0).Verdict);
        Assert.Equal(Verdicts.Pass, T("khớp baseline(e.json, khoá type, trường v) ± 0.0001", 0).Verdict);
    }

    [Fact]
    public void Khong_cham_duoc_thi_noi_ro()
    {
        Assert.Equal(Verdicts.Agent, G("ảnh(label không đè keyplan)").Verdict);
        Assert.Equal(Verdicts.Agent, G("so sánh mờ(x)").Verdict);
        var miss = G("đếm(thieu.json → a) = 1");
        Assert.Equal(Verdicts.NotMeasurable, miss.Verdict);
        Assert.Contains("thieu.json", miss.Reason);
        var pending = RulesFile.Parse("### R-02 — y\n- Sai số: chờ — ±1?\n- Máy chấm: đếm(a.json → x) = 1\n").Rules[0];
        Assert.Equal(Verdicts.NotMeasurable, new Grader(new GradingData(_run, _suite, Array.Empty<string>())).Grade(pending, Array.Empty<string>()).Verdict);
    }
}
