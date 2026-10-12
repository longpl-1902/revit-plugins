using Hawee.Flow.Core.Checks;
using Hawee.Flow.Core.Docs;
using Hawee.Flow.Core.Infra;
using Hawee.Flow.Core.Tasks;
using Hawee.Flow.Core.Testing;

namespace Hawee.Flow.Tests;

public class QuestionPackTests
{
    private const string Pack1 = """
        doc: spec
        round: 1
        answerers: "BA: Q1–Q2"
        questions:
          - id: Q0
            q: Tier T2 — đồng ý?
            seen: ba project
            options: { A: "T2", B: "T3" }
            recommend: A
            default: A
            sets: tier
            values: { A: T2, B: T3 }
            applies: status
          - id: Q1
            q: Tờ trùng thì làm gì?
            seen: "Import.cs:88"
            options: { A: "cập nhật", B: "bỏ qua" }
            recommend: B
            default: B
            decider: BA
            applies: "§3"
          - id: Q2
            q: Xoá tờ thừa?
            seen: issue note 2
            options: { A: "xoá", B: "giữ" }
            recommend: B
            default: CHẶN
            decider: BA
        extras:
          - { id: D1, seen: "hai phiên", propose: "ngoài phạm vi" }
        """;

    private static string Setup(TestRepo repo, string tier = "T2")
    {
        new TaskOpener(repo.Ctx(), repo.Runner).Open(new OpenInput("7", "dup", Tier: tier));
        var dir = Path.Combine(repo.Root, "docs/tasks/7-dup");
        Text.Write(Path.Combine(dir, "questions/pack-1.yaml"), Pack1);
        return dir;
    }

    [Fact]
    public void Parse_tra_loi_ngan()
    {
        var a = QuestionPack.ParseAnswers("Q1: B — kèm cảnh báo, Q2: mặc định; Q3: xem lại — hỏi khách\nQ4: chỉ khi có tờ, D1: bỏ, D2: giữ");
        Assert.Equal(new[] { "Q1", "Q2", "Q3", "Q4", "D1", "D2" }, a.Select(x => x.Id));
        Assert.Equal(("choice", "B", "kèm cảnh báo"), (a[0].Kind, a[0].Choice, a[0].Note));
        Assert.Equal("default", a[1].Kind);
        Assert.Equal("reopen", a[2].Kind);
        Assert.Equal(("text", "chỉ khi có tờ"), (a[3].Kind, a[3].Note));
        Assert.Equal("drop", a[4].Kind);
        Assert.Equal("keep", a[5].Kind);
    }

    [Fact]
    public void Validate_bat_loi_goi()
    {
        var p = QuestionPack.Parse("""
            questions:
              - { id: X1, q: "", options: { A: a }, recommend: C, default: D }
              - { id: Q2, q: hỏi, seen: s, options: { A: a, B: b }, recommend: A, default: A, sets: owner }
            """);
        var errors = QuestionPack.Validate(p, 1).ToList();
        Assert.Contains(errors, e => e.Contains("giới hạn 1"));
        Assert.Contains(errors, e => e.Contains("X1") && e.Contains("Q<n>"));
        Assert.Contains(errors, e => e.Contains("thiếu câu hỏi"));
        Assert.Contains(errors, e => e.Contains("bằng chứng"));
        Assert.Contains(errors, e => e.Contains("đề xuất C"));
        Assert.Contains(errors, e => e.Contains("mặc định 'D'"));
        Assert.Contains(errors, e => e.Contains("sets chỉ nhận"));
    }

    [Fact]
    public void Render_ghi_md_va_doi_trang_thai()
    {
        using var repo = new TestRepo();
        var dir = Setup(repo);
        var chat = new QuestionPack(repo.Ctx()).Render("7", "questions/pack-1.yaml");
        Assert.Contains("Q1 Tờ trùng thì làm gì?", chat);
        Assert.Contains("CHẶN nếu không trả lời", chat);
        Assert.True(File.Exists(Path.Combine(dir, "questions/pack-1.md")));
        var st = StatusFile.ForTask(dir).Data;
        Assert.Equal("spec", st["state"].S());
        Assert.Equal("đang trao đổi (gói 1)", st["spec"].S());
    }

    [Fact]
    public void Render_tu_choi_qua_gioi_han_T1()
    {
        using var repo = new TestRepo();
        Setup(repo, "T1");
        var p = Path.Combine(repo.Root, "docs/tasks/7-dup/questions/pack-1.yaml");
        Text.Write(p, Pack1.Replace("questions:", "questions:\n  - { id: Q9, q: a, seen: s, options: { A: a, B: b }, recommend: A, default: A }"));
        var ex = Assert.Throws<FlowException>(() => new QuestionPack(repo.Ctx()).Render("7", "questions/pack-1.yaml"));
        Assert.Contains("giới hạn 3", ex.Message);
    }

    [Fact]
    public void Apply_ghi_14_12_13_dec_va_cau_treo()
    {
        using var repo = new TestRepo();
        var dir = Setup(repo);
        var qp = new QuestionPack(repo.Ctx());
        var r = qp.Apply("7", "questions/pack-1.yaml", "Q1: B, Q0: B, D1: bỏ", "An (BA)", "chat 12/10");
        Assert.Contains("CHẶN còn mở", r);
        var spec = new MdDoc(Path.Combine(dir, "spec.md"));
        var rows14 = spec.TableRows("14");
        Assert.Equal(new[] { "Q0", "Q1" }, rows14.Select(x => x[0]));     // dòng mẫu trống đã bỏ
        Assert.Contains("DEC-2026-001", rows14[0][4]);
        Assert.Empty(spec.TableRows("12"));                                 // không có mặc định nào được áp (Q2 là CHẶN)
        Assert.Equal("Q2", spec.TableRows("13").Single()[0]);
        var st = StatusFile.ForTask(dir).Data;
        Assert.Equal(new[] { "Q2" }, st["open_questions"].Strings());
        Assert.Equal("T3", st["tier"].S());                                 // sets: tier
        Assert.Contains("| Q2 |", repo.Read("docs/decisions/decision-log.md"));

        // trả lời câu CHẶN sau đó → gỡ khỏi câu treo, điền §13
        qp.Apply("7", "questions/pack-1.yaml", "Q2: B", "An (BA)", "chat", acceptDefaults: false);
        st = StatusFile.ForTask(dir).Data;
        Assert.Empty(st["open_questions"].Strings());
        Assert.Equal("giữ", new MdDoc(Path.Combine(dir, "spec.md")).TableRows("13").Single()[^1]);
        var log = repo.Read("docs/decisions/decision-log.md");
        Assert.Contains("DEC-2026-003", log);
        Assert.DoesNotContain("| Q2 | 2026", log);
    }

    [Fact]
    public void Apply_mac_dinh_thanh_gia_dinh()
    {
        using var repo = new TestRepo();
        var dir = Setup(repo);
        new QuestionPack(repo.Ctx()).Apply("7", "questions/pack-1.yaml", "Q2: A", "An", "chat");
        var spec = new MdDoc(Path.Combine(dir, "spec.md"));
        var assumptions = spec.TableRows("12");
        Assert.Equal(2, assumptions.Count);                                 // Q0, Q1 theo mặc định
        Assert.StartsWith("Q0: mặc định A", assumptions[0][1]);
    }

    [Fact]
    public void Apply_khong_ghi_nua_chung_khi_loi()
    {
        using var repo = new TestRepo();
        Setup(repo);
        Assert.Throws<FlowException>(() => new QuestionPack(repo.Ctx()).Apply("7", "questions/pack-1.yaml", "Q1: C", "An", "chat"));
        Assert.Throws<FlowException>(() => new QuestionPack(repo.Ctx()).Apply("7", "questions/pack-1.yaml", "Q8: A", "An", "chat"));
        Assert.False(File.Exists(Path.Combine(repo.Root, "docs/decisions/decision-log.md")));
    }

    [Fact]
    public void Apply_goi_plan_ghi_1_2_va_autonomy()
    {
        using var repo = new TestRepo();
        var dir = Setup(repo);
        Text.Write(Path.Combine(dir, "questions/pack-4a.yaml"), """
            doc: plan
            round: 4
            questions:
              - { id: Q1, q: "Phương án?", seen: "plan §2.1", options: { A: "A — tách lớp Tính", B: "B — sửa tại chỗ" }, recommend: A, default: CHẶN, applies: "§2" }
              - { id: Q2, q: "Mức tự chủ khi chạy test không người?", seen: "GEN-HOSTTEST-001 mục 13", options: { A: ask, B: night }, recommend: A, default: A, sets: autonomy, values: { A: ask, B: night } }
            """);
        new QuestionPack(repo.Ctx()).Apply("7", "questions/pack-4a.yaml", "Q1: A, Q2: B", "Minh (DEV)", "chat");
        var plan = new MdDoc(Path.Combine(dir, "plan.md"));
        var rows = plan.TableRows("1.2");
        Assert.Equal(new[] { "Q1", "Q2" }, rows.Select(r => r[0]));       // dòng mẫu T1 (Chốt trống) đã bỏ
        Assert.Equal("night", StatusFile.ForTask(dir).Get("autonomy"));
    }
}

public class DocCheckTests
{
    [Fact]
    public void Gom_loi_doc_lint_theo_ma()
    {
        var g = DocCheck.Group(new[]
        {
            "[sec-empty] 1-a/plan.md [plan T2]: muc 1.1 rong (chi co heading)",
            "[sec-empty] 1-a/plan.md [plan T2]: muc 5.6 rong (chi co heading)",
            "[plan-inv] 1-a/plan.md [plan T2]: muc 5.6 khong co bat bien nao",
        }).ToList();
        Assert.Contains("[sec-empty] ×2: plan 1.1, plan 5.6", g);
        Assert.Contains(g, l => l.StartsWith("[plan-inv]"));
    }

    [Fact]
    public void Phat_hien_tham_chieu_sai_va_AC_khong_phu()
    {
        using var repo = new TestRepo();
        new TaskOpener(repo.Ctx(), repo.Runner).Open(new OpenInput("8", "crop"));
        repo.Write("src/A.cs", "line1\nline2\n");
        var dir = Path.Combine(repo.Root, "docs/tasks/8-crop");
        var spec = Text.Read(Path.Combine(dir, "spec.md"))
            .Replace("## 3. Phạm vi", "## 3. Phạm vi\n\nĐiểm vào `src/A.cs:2`, `src/A.cs:9`, `src/B.cs:1`. Theo GEN-IDEMP-001, GEN-NOPE-001, KI-DM-007, KI-DM-099.\n");
        spec += "\n## 5. Hành vi đúng\n\n| # | Kịch bản | Khi | Thì | Đo bằng |\n|---|---|---|---|---|\n| AC1 | K1 | a | b | test |\n| AC2 | K1 | a | b | |\n";
        Text.Write(Path.Combine(dir, "spec.md"), spec);
        repo.Runner.Default = new ProcResult(0, "", false);
        var r = new DocCheck(repo.Ctx(), repo.Runner).Check("8", "both");
        var report = Text.Read(Path.Combine(dir, "analyze-2026-10-12.md"));
        Assert.Contains("src/A.cs:9", report);
        Assert.DoesNotContain("`src/A.cs:2`", report);
        Assert.Contains("src/B.cs:1", report);
        Assert.Contains("GEN-NOPE-001", report);
        Assert.DoesNotContain("rule GEN-IDEMP-001", report);
        Assert.Contains("KI-DM-099", report);
        Assert.Contains("AC2 chưa có cột Đo bằng", report);
        Assert.Contains("AC1 không có cột kiểm nào", report);
        Assert.Contains("AC2 không có dòng trong ma trận", report);
        Assert.Contains("chưa trình cổng", r);
        Assert.True(r.Split('\n').Length <= 22);
    }
}

public class BaselineTests
{
    private const string Json = """
        { "commit": "abcdef1234567", "branch": "DEV", "date": "2026-10-12 09:00", "uncommitted_files": 0, "out_dir": "x",
          "build": [ { "name": "build", "command": "msbuild A.sln", "exit": 0, "seconds": 41.2, "errors": 0, "warnings": 3, "timed_out": false, "log": "b.log" } ],
          "tests": [ { "name": "unit", "level": "L0", "command": "dotnet test", "exit": 1, "seconds": 9, "timed_out": false, "known_failures": 1,
                       "total": 10, "passed": 9, "failed": 1, "skipped": 0, "failed_tests": ["T.Old"], "log": "t.log", "new_failures": 0 },
                     { "name": "manual", "level": "L0", "command": "run.cmd", "exit": 0, "known_failures": 0, "log": "m.log", "output_tail": "" } ] }
        """;

    [Fact]
    public void Doc_json_va_so_fail_moi()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var a = Path.Combine(dir, "a.json");
        File.WriteAllText(a, Json);
        var b = Path.Combine(dir, "b.json");
        File.WriteAllText(b, Json.Replace("\"failed\": 1, \"skipped\": 0, \"failed_tests\": [\"T.Old\"]", "\"failed\": 2, \"skipped\": 0, \"failed_tests\": [\"T.Old\", \"T.New\"]")
            .Replace("\"exit\": 0, \"seconds\": 41.2, \"errors\": 0", "\"exit\": 1, \"seconds\": 41.2, \"errors\": 2")
            .Replace("\"run.cmd\", \"exit\": 0", "\"run.cmd\", \"exit\": 3"));
        var baseline = Baseline.Load(a);
        Assert.Equal(9, baseline.Tests[0].Passed);
        Assert.Null(baseline.Tests[1].Passed);
        var rows = Baseline.Rows(baseline).ToList();
        Assert.Contains("`DEV` @ `abcdef12`", rows[0]);
        Assert.Contains("pass 9 · fail 1 · skip 0 (đã biết 1)", rows[2]);
        var problems = Baseline.NewProblems(baseline, Baseline.Load(b));
        Assert.Equal(3, problems.Count);
        Assert.Contains(problems, p => p.Contains("T.New") && !p.Contains("T.Old"));
        Assert.Contains(problems, p => p.StartsWith("build build: exit 1, 2 lỗi"));
        Assert.Contains(problems, p => p.StartsWith("test manual: exit 3"));
        Assert.Empty(Baseline.NewProblems(baseline, baseline));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void RunForPlan_dien_1_3()
    {
        using var repo = new TestRepo();
        new TaskOpener(repo.Ctx(), repo.Runner).Open(new OpenInput("9", "b"));
        Text.Write(Path.Combine(repo.Root, "BIM_KB_Base/core/tools/test-baseline.ps1"), "# giả");
        var json = Path.Combine(repo.Root, "tmp-baseline.json");
        File.WriteAllText(json, Json);
        repo.Runner.ByScript["test-baseline.ps1"] = new ProcResult(0, $"| bảng |\n[test-baseline] ghi {json}\n", false);
        var r = new Baseline(repo.Ctx(), repo.Runner).RunForPlan("9", true, "", "");
        Assert.Contains("T.Old", r);                 // fail sẵn có cần KI
        Assert.Contains("run.cmd", r);               // không có TRX
        var dir = Path.Combine(repo.Root, "docs/tasks/9-b");
        Assert.True(File.Exists(Path.Combine(dir, "baseline.json")));
        Assert.Equal("docs/tasks/9-b/baseline.json", StatusFile.ForTask(dir).Get("baseline"));
        var rows = new MdDoc(Path.Combine(dir, "plan.md")).TableRows("1.3");
        Assert.Equal(4, rows.Count);
        Assert.Contains("-Build", repo.Runner.Calls.Last().Args);
    }
}

public class TestDesignTests
{
    private const string Rules = """
        # Luật — 9-crop

        ## Áp dụng cho: mọi sheet

        ### R-01 — Đủ viewport
        - AC: AC1 (K1)
        - Kiểm: mỗi sheet 1 viewport
        - Kỳ vọng: theo spec (AC1): viewport=1
        - Cách đo: checkpoints\SAVE\sheet_components.json
        - Sai số: không
        - Mức: FAIL nếu sai
        - Máy chấm: đếm(sheet_components.json → components[type=Viewport]) theo SheetNumber = 1

        ### R-02 — Keyplan trong khung
        - AC: AC2
        - Kỳ vọng: theo cài đặt (config.json → Keyplan.Canvas): 60 × 40 mm
        - Sai số: chờ — ±1 mm?
        - Máy chấm: trong(keyplan, khung) theo SheetNumber

        ### R-03 — Cũ · retired 2026-10-01
        - AC: AC3
        - Kỳ vọng: theo baseline (a.json)

        ## Chỗ hở

        | AC | Thiếu gì để đo | Khi nào có | Tạm thời |
        |---|---|---|---|
        | AC4 | bbox label | task sau | ảnh |
        """;

    [Fact]
    public void Doc_rules_md()
    {
        var rf = RulesFile.Parse(Rules);
        Assert.Equal(3, rf.Rules.Count);
        Assert.Equal(2, rf.Active.Count());
        var r1 = rf.Rules[0];
        Assert.Equal("spec", r1.ExpectSource);
        Assert.False(r1.Pending);
        Assert.StartsWith("đếm(", r1.Grader);
        Assert.True(rf.Rules[1].Pending);
        Assert.Equal("cài đặt", rf.Rules[1].ExpectSource);
        Assert.True(rf.Rules[2].Retired);
        Assert.Equal("AC4", rf.Gaps.Single().Ac);
    }

    [Fact]
    public void Dang_ky_bo_du_lieu_va_dung_suite_va_ma_tran()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        var suites = Path.Combine(root, "tasks");
        var datasets = Path.Combine(root, "datasets");
        using var repo = new TestRepo($"revit_test:\n  mcp: hawee\n  suites_dir: '{suites}'\n  datasets_dir: '{datasets}'\n");
        try
        {
            repo.Write("BIM_KB_Base/profiles/revit-addin/templates/revit-suite.yaml",
                "suite: <ten-folder-task>\ndescription: <bug / tính năng được test, kiểm gì>\ndataset: <tên bộ dữ liệu>\nspec: <docs/tasks/<task>/spec.md> v1\nmodel: '{dataset}\\<model>.rvt'\nsteps: []\n");
            repo.Write("BIM_KB_Base/profiles/revit-addin/templates/revit-suite.rules.md", "# Luật — <ten-folder-task>\n\n> Sinh bởi `/test-design` từ spec `<docs/tasks/<task>/spec.md>` v<N> ngày <yyyy-MM-dd>\n");
            var src = Path.Combine(root, "nguon");
            Text.Write(Path.Combine(src, "Model A.rvt"), "rvt");
            Text.Write(Path.Combine(src, "sheets.csv"), "a,b\n1,2\n3,4\n");
            var td = new TestDesign(repo.Ctx(), repo.Runner);
            Assert.Throws<FlowException>(() => td.RegisterDataset(src, "", "An", "", "", ""));
            var r = td.RegisterDataset(src, "", "An (QA)", "nội bộ", "bộ thử", "2024");
            Assert.Contains("hash nguồn trước = sau = bản chép", r);
            var ds = Y.Load(Path.Combine(datasets, "nguon", "dataset.yaml"));
            Assert.Equal("nguon", ds["dataset"].S());
            Assert.Equal(2, ds["files"].Items.Count());
            Assert.Equal("model", ds["files"].Items.First(f => f["path"].S() == "Model A.rvt")["role"].S());
            Assert.Equal(2, ds["facts"]["csv"]["sheets.csv"]["rows"].Int());
            Assert.Throws<FlowException>(() => td.RegisterDataset(src, "", "An", "nội bộ", "", ""));

            new TaskOpener(repo.Ctx(), repo.Runner).Open(new OpenInput("9", "crop"));
            var dir = Path.Combine(repo.Root, "docs/tasks/9-crop");
            td.ScaffoldSuite("9", "", "nguon", "keyplan");
            var suiteYaml = Y.Load(Path.Combine(suites, "9-crop", "suite.yaml"));
            Assert.Equal("9-crop", suiteYaml["suite"].S());
            Assert.Equal("{dataset}\\Model A.rvt", suiteYaml["model"].S());
            Assert.Equal("docs/tasks/9-crop/spec.md v1", suiteYaml["spec"].S());
            Assert.Equal(new[] { "9-crop" }, StatusFile.ForTask(dir).Data["suites"].Strings());
            // gọi lại → bản đề xuất, không ghi đè
            td.ScaffoldSuite("9", "", "nguon", "keyplan");
            Assert.True(File.Exists(Path.Combine(suites, "9-crop", "rules.proposed.md")));

            Text.Write(Path.Combine(suites, "9-crop", "rules.md"), Rules);
            var spec = Text.Read(Path.Combine(dir, "spec.md")) +
                "\n## 5. Hành vi đúng\n\n| # | Kịch bản | Khi | Thì | Đo bằng |\n|---|---|---|---|---|\n" +
                "| AC1 | K1 | a | b | suite /revit-test |\n| AC2 | K1 | a | b | suite |\n| AC3 | K2 | a | b | suite trong Revit |\n| AC4 | K2 | a | b | suite |\n| AC5 | K3 | a | b | unit test |\n";
            Text.Write(Path.Combine(dir, "spec.md"), spec);
            var m = td.BuildMatrix("9");
            Assert.Contains("không phủ: AC3", m);            // R-03 retired
            Assert.Contains("R-02", m);                       // còn chờ
            var tdm = Text.Read(Path.Combine(dir, "test-design.md"));
            Assert.Contains("| AC1 |", tdm);
            Assert.Contains("9-crop → R-01 (spec)", tdm);
            Assert.Contains("Chỗ hở (9-crop): bbox label", tdm);
            Assert.Contains("ngoài suite", tdm);
            // chạy lại giữ phần agent viết ngoài khối
            Text.Write(Path.Combine(dir, "test-design.md"), tdm + "\nGhi chú của agent\n");
            td.BuildMatrix("9");
            Assert.Contains("Ghi chú của agent", Text.Read(Path.Combine(dir, "test-design.md")));
            Assert.Single(Text.Read(Path.Combine(dir, "test-design.md")).Split("<!-- hawee-flow:matrix -->")[1..]);
        }
        finally { Directory.Delete(root, true); }
    }
}

public class GateSubmitTests
{
    [Fact]
    public void Trinh_G1_G2_kiem_dieu_kien()
    {
        using var repo = new TestRepo();
        new TaskOpener(repo.Ctx(), repo.Runner).Open(new OpenInput("4", "x"));
        var dir = Path.Combine(repo.Root, "docs/tasks/4-x");
        var gs = new GateSubmit(repo.Ctx());
        var ex = Assert.Throws<FlowException>(() => gs.Submit("4", "G1"));
        Assert.Contains("doc_check", ex.Message);
        Text.Write(Path.Combine(dir, "analyze-2026-10-12.md"), "x");
        Assert.Contains("state g1", gs.Submit("4", "G1"));
        Assert.Equal("chờ G1", StatusFile.ForTask(dir).Get("spec"));
        ex = Assert.Throws<FlowException>(() => gs.Submit("4", "G2"));
        Assert.Contains("baseline", ex.Message);
    }
}
