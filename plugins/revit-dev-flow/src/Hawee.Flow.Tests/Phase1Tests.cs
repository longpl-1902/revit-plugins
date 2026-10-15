using Hawee.Flow.Core.Docs;
using Hawee.Flow.Core.Flow;
using Hawee.Flow.Core.Infra;
using Hawee.Flow.Core.Knowledge;
using Hawee.Flow.Core.Tasks;

namespace Hawee.Flow.Tests;

public class StatusFileTests
{
    [Fact]
    public void Set_giu_comment_va_cot()
    {
        using var repo = new TestRepo();
        var p = repo.Write("s.yaml", "tier: T2                                 # tier\nstate: context   # trạng thái\n");
        var s = new StatusFile(p);
        s.Set("tier", "T1");
        s.Set("state", "plan");
        s.Save();
        var lines = Text.Lines(p);
        Assert.Equal("tier: T1                                 # tier", lines[0]);
        Assert.StartsWith("state: plan", lines[1]);
        Assert.EndsWith("# trạng thái", lines[1]);
        Assert.Equal("T1", new StatusFile(p).Get("tier"));
    }

    [Theory]
    [InlineData("", "\"\"")]
    [InlineData("chốt", "chốt")]
    [InlineData("#123", "\"#123\"")]
    [InlineData("a: b", "\"a: b\"")]
    [InlineData(@"D:\RevitTest\x", "\"D:\\\\RevitTest\\\\x\"")]
    [InlineData("true", "\"true\"")]
    [InlineData("- x", "\"- x\"")]
    public void Scalar_an_toan(string input, string expected) => Assert.Equal(expected, StatusFile.Scalar(input));

    [Fact]
    public void Scalar_doc_lai_dung()
    {
        foreach (var v in new[] { @"D:\a\b.md", "x \"y\" z", "a, b: c", "#1 · {x}" })
        {
            var y = Y.Parse("k: " + StatusFile.Scalar(v));
            Assert.Equal(v, y["k"].S());
        }
    }

    [Fact]
    public void SetGate_thay_dung_dong_va_list()
    {
        using var repo = new TestRepo();
        var p = repo.Write("s.yaml", repo.Read("BIM_KB_Base/core/templates/task-status.yaml"));
        var s = new StatusFile(p);
        s.SetGate("G1", new KeyValuePair<string, string>[] { new("decision", "PASS"), new("by", StatusFile.Scalar("An (BA)")) });
        s.AddToList("suites", "0001-a");
        s.AddToList("suites", "0001-a");
        s.SetInMap("loops", "host_test", "2");
        s.Save();
        var d = new StatusFile(p).Data;
        Assert.Equal("PASS", d["gates"]["G1"]["decision"].S());
        Assert.Equal("An (BA)", d["gates"]["G1"]["by"].S());
        Assert.Equal("", d["gates"]["G2"]["decision"].S());
        Assert.Equal(new[] { "0001-a" }, d["suites"].Strings());
        Assert.Equal(2, d["loops"]["host_test"].Int());
        Assert.Equal(0, d["loops"]["build_test"].Int());
        Assert.Contains("# evidence", Text.Read(p));
    }
}

public class DecisionLogTests
{
    [Fact]
    public void Cap_so_tang_dan_bo_dong_vi_du()
    {
        using var repo = new TestRepo();
        var log = new DecisionLog(repo.Ctx());
        var ids = log.Append(new[] { new DecisionRow("An (BA)", "spec x", "Q1", "B", "§3", "chat") });
        Assert.Equal("DEC-2026-001", ids[0]);
        var ids2 = log.Append(new[] { new DecisionRow("An", "spec x", "Q2", "A", "§4", "chat"), new DecisionRow("An", "spec x", "Q3", "A|B", "§4", "chat") });
        Assert.Equal(new[] { "DEC-2026-002", "DEC-2026-003" }, ids2);
        var text = repo.Read("docs/decisions/decision-log.md");
        Assert.DoesNotContain("<tên>", text);
        Assert.Contains("A\\|B", text);
        Assert.Contains("# Nhật ký quyết định — demo-addin", text);
    }

    [Fact]
    public void Cau_treo_them_va_bo()
    {
        using var repo = new TestRepo();
        var log = new DecisionLog(repo.Ctx());
        log.AddPending(new[] { new PendingRow("Q4", "spec 1-a", "K5 dừng cả lô?", "BA", "CHẶN") });
        Assert.Contains("| Q4 |", repo.Read("docs/decisions/decision-log.md"));
        log.ResolvePending("Q4", "spec 1-a");
        Assert.DoesNotContain("| Q4 |", repo.Read("docs/decisions/decision-log.md"));
    }
}

public class TaskAndGateTests
{
    private static string Open(TestRepo repo, string tier = "T2") =>
        new TaskOpener(repo.Ctx(), repo.Runner).Open(new OpenInput("123", "Keyplan Crop", "Keyplan crop", Tier: tier, Keywords: "keyplan"));

    [Fact]
    public void Mo_task_tao_du_file()
    {
        using var repo = new TestRepo();
        var msg = Open(repo);
        Assert.Contains("123-keyplan-crop", msg);
        var dir = Path.Combine(repo.Root, "docs/tasks/123-keyplan-crop");
        foreach (var f in new[] { "status.yaml", "spec.md", "plan.md", "context.md" }) Assert.True(File.Exists(Path.Combine(dir, f)), f);
        Assert.True(Directory.Exists(Path.Combine(dir, "questions")));
        var st = StatusFile.ForTask(dir).Data;
        Assert.Equal("123-keyplan-crop", st["task"].S());
        Assert.Equal("#123", st["issue"].S());
        Assert.Equal("context", st["state"].S());
        var ctx = File.ReadAllText(Path.Combine(dir, "context.md"));
        Assert.Contains("KI-DM-007", ctx);
        Assert.Throws<FlowException>(() => Open(repo));
    }

    [Fact]
    public void T0_khong_co_spec_plan()
    {
        using var repo = new TestRepo();
        Open(repo, "T0");
        Assert.False(File.Exists(Path.Combine(repo.Root, "docs/tasks/123-keyplan-crop/spec.md")));
    }

    [Fact]
    public void G1_pass_chuyen_state_va_ghi_dec()
    {
        using var repo = new TestRepo();
        Open(repo);
        var r = new GateRecorder(repo.Ctx()).Record("123", new GateInput("g1", "pass", "An (BA)", "issue #123 note 4"));
        Assert.Contains("DEC-2026-001", r);
        var st = StatusFile.ForTask(Path.Combine(repo.Root, "docs/tasks/123-keyplan-crop")).Data;
        Assert.Equal("plan", st["state"].S());
        Assert.Equal("chốt", st["spec"].S());
        Assert.Equal("DEC-2026-001", st["gates"]["G1"]["decision_log"].S());
        Assert.Equal("issue #123 note 4", st["gates"]["G1"]["evidence"].S());
    }

    [Fact]
    public void Cong_tu_choi_dau_vao_sai()
    {
        using var repo = new TestRepo();
        Open(repo);
        var g = new GateRecorder(repo.Ctx());
        Assert.Throws<FlowException>(() => g.Record("123", new GateInput("G7", "PASS", "a", "b")));
        Assert.Throws<FlowException>(() => g.Record("123", new GateInput("G1", "OK", "a", "b")));
        Assert.Throws<FlowException>(() => g.Record("123", new GateInput("G1", "WAIVED", "a", "b")));
        Assert.Throws<FlowException>(() => g.Record("123", new GateInput("G2", "PASS", "a", "b")));
        Assert.Throws<FlowException>(() => g.Record("123", new GateInput("G1", "PASS", "", "b")));
    }

    [Fact]
    public void G1_bi_chan_khi_con_cau_chan()
    {
        using var repo = new TestRepo();
        Open(repo);
        var dir = Path.Combine(repo.Root, "docs/tasks/123-keyplan-crop");
        var s = StatusFile.ForTask(dir);
        s.SetList("open_questions", new[] { "Q3" });
        s.Save();
        var ex = Assert.Throws<FlowException>(() => new GateRecorder(repo.Ctx()).Record("123", new GateInput("G1", "PASS", "a", "b")));
        Assert.Contains("Q3", ex.Message);
    }

    [Fact]
    public void G2_pass_khoa_hash_suite()
    {
        var suites = Path.Combine(Path.GetTempPath(), "hawee-suites-" + Guid.NewGuid().ToString("N")[..6]);
        using var repo = new TestRepo($"revit_test:\n  mcp: hawee\n  suites_dir: '{suites}'\n");
        try
        {
            Text.Write(Path.Combine(suites, "123-main", "suite.yaml"), "suite: 123-main\n");
            Text.Write(Path.Combine(suites, "123-main", "rules.md"), "# Luật\n");
            Open(repo);
            var dir = Path.Combine(repo.Root, "docs/tasks/123-keyplan-crop");
            var s = StatusFile.ForTask(dir);
            s.SetList("suites", new[] { "123-main" });
            s.Set("state", "g2");
            s.Save();
            var r = new GateRecorder(repo.Ctx()).Record("123", new GateInput("G2", "PASS", "Minh", "chat", CoderConfirmed: true));
            Assert.DoesNotContain("!", r);
            var st = StatusFile.ForTask(dir).Data;
            Assert.Equal("code", st["state"].S());
            Assert.Equal(Text.Sha256File(Path.Combine(suites, "123-main", "rules.md")), st["suite_hashes"]["123-main"]["rules_md"].S());
            Assert.Equal("true", st["gates"]["G2"]["coder_confirmed"].S());
        }
        finally { Directory.Delete(suites, true); }
    }
}

public class ScaffoldTests
{
    [Fact]
    public void T1_danh_dau_muc_ngoai_tier()
    {
        using var repo = new TestRepo();
        var text = new DocScaffold(repo.Ctx()).Build(new ScaffoldInput("plan", "T1", "Crop", "crop", "#9"));
        Assert.Contains("Tier: T1", text);
        Assert.Contains("Trạng thái: nháp", text);
        Assert.Contains("Spec: spec.md v1 (chưa qua G1)", text);
        Assert.Contains("Issue: #9", text);
        // §2 ngoài tier T1: có heading + "Không áp dụng", không còn mục con 2.1
        Assert.Contains("## 2. Đánh giá tổng thể\n\nKhông áp dụng – ngoài tier T1", text);
        Assert.DoesNotContain("### 2.1.", text);
        // §3.2, §5.1 ngoài danh sách, §3.1, §5.2, §5.6 giữ
        Assert.Contains("### 3.2. Caller bị ảnh hưởng\n\nKhông áp dụng", text);
        Assert.Contains("### 3.1. Thay đổi trực tiếp\n\nFile.", text);
        Assert.Contains("### 5.1. Luồng sau khi sửa\n\nKhông áp dụng", text);
        Assert.Contains("### 5.6. Bất biến", text);
        Assert.Contains("### Tóm tắt", text);
        Assert.Contains("## 13. Lệch so với plan (ghi trong lúc làm)\n\nKhông áp dụng", text);
    }

    [Fact]
    public void T2_giu_nguyen_mau()
    {
        using var repo = new TestRepo();
        var text = new DocScaffold(repo.Ctx()).Build(new ScaffoldInput("spec", "T2", "Crop", "crop", "9"));
        Assert.DoesNotContain("Không áp dụng", text);
        Assert.Contains("# Spec — Crop", text);
        Assert.Contains("`crop`", text);
        Assert.Contains("- Thay cho: không có", text);
    }
}

public class KnowledgeTests
{
    [Fact]
    public void Doc_rule_va_tim()
    {
        using var repo = new TestRepo();
        var idx = new RulesIndex(repo.Ctx());
        Assert.Equal(4, idx.Rules.Count);
        var r = idx.Get("revit-thread-001")!;
        Assert.Equal("blocker", r.Level);
        Assert.Equal("Gọi Revit API chỉ từ ngữ cảnh hợp lệ", r.Title);
        var d = idx.Get("GEN-DOC-001")!;
        Assert.Equal("draft", d.Status);
        Assert.Contains("đã có thì cập nhật", idx.Get("GEN-IDEMP-001")!.Must);
        var hits = idx.Search(new[] { "transaction" }).ToList();
        Assert.Equal("REVIT-TXN-001", hits[0].Rule.Id);
    }

    [Fact]
    public void Tim_rule_theo_tu_khong_theo_chuoi_con()
    {
        using var repo = new TestRepo();
        repo.Write("BIM_KB_Base/core/rules/extra.md", """
            # Rule thêm

            ## GEN-FILTER-001 — Lọc phần tử theo category, không theo tên · `medium`

            - **Phải:** dùng BuiltInCategory, không so chuỗi tên; location của phần tử lấy từ LocationPoint.

            ## REVIT-CROP-001 — Crop region của view phải đặt trong transaction · `high`

            - **Phải:** đặt CropBox sau khi tắt crop annotation.
            """);
        var idx = new RulesIndex(repo.Ctx());
        // "cắt" → "cat" không được khớp "category" / "location"; qua đồng nghĩa ra "crop"
        var cut = idx.Search(new[] { "cắt khung" }).Select(h => h.Rule.Id).ToList();
        Assert.DoesNotContain("GEN-FILTER-001", cut);
        Assert.Equal("REVIT-CROP-001", cut.First());
        // "view" không khớp "review"; tiếng Việt "giao dịch" ra rule transaction
        Assert.Equal("REVIT-TXN-001", idx.Search(new[] { "giao dịch" }).First().Rule.Id);
        // đường dẫn: tách CamelCase, bỏ src / tên project
        var words = RulesIndex.PathKeywords(repo.Ctx(), "src/Hawee.Shop/Draw/CropRegionService.cs");
        Assert.Contains("crop", words);
        Assert.Contains("region", words);
        Assert.DoesNotContain("src", words);
        Assert.DoesNotContain("service", words);
        Assert.Equal("REVIT-CROP-001", idx.Search(words).First().Rule.Id);
    }

    [Fact]
    public void Tu_ghep_viet_lien_van_khop()
    {
        var d = new Terms.Doc("Dùng ViewTemplate khi tạo views; Transactions có tên");
        Assert.True(d.Has("view"));
        Assert.True(d.Has("template"));
        Assert.True(d.Has("viewtemplate"));
        Assert.True(d.Has("transaction"));
        Assert.False(d.Has("temp"));
        Assert.False(new Terms.Doc("code review checklist").Has("view"));
    }

    [Fact]
    public void Tim_known_issue()
    {
        using var repo = new TestRepo();
        var ki = new KbSearch(repo.Ctx()).SearchKi(new[] { "keyplan crop" }).Single();
        Assert.Equal("KI-DM-007", ki.Id);
        Assert.Equal("OPEN", ki.State);
        Assert.Equal("high", ki.Level);
    }
}

public class FlowDefTests
{
    [Fact]
    public void Moi_state_va_cong_deu_hop_le()
    {
        var flow = FlowDef.Load();
        var states = flow["states"].Entries.Select(e => e.Key).ToHashSet();
        foreach (var s in new[] { "context", "spec", "g1", "plan", "g2", "code", "test", "g3", "commit", "g4", "ci", "g5", "close", "done", "parked" })
            Assert.Contains(s, states);
        foreach (var (_, v) in flow["states"].Entries) Assert.Contains(v["next"].S(), states);
        foreach (var g in FlowEngine.Gates)
        {
            var gd = flow["gates"][g];
            Assert.Contains(gd["from"].S(), states);
            Assert.Contains(gd["pass"].S(), states);
            Assert.Contains(gd["fail"].S(), states);
        }
        var raw = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "../../../../Hawee.Flow.Core/Flow/flow.yaml"));
        Assert.DoesNotMatch("[\u2460-\u2473]", raw); // không dùng ký hiệu ①–⑩ (lỗi font trên terminal)
    }

    [Fact]
    public void Next_tra_the_buoc_ngan()
    {
        using var repo = new TestRepo();
        new TaskOpener(repo.Ctx(), repo.Runner).Open(new OpenInput("5", "abc"));
        var next = new FlowEngine(repo.Ctx()).Next("5");
        Assert.StartsWith("Bước 2", next);
        Assert.True(next.Split('\n').Length <= 15);
        var brief = new FlowEngine(repo.Ctx()).Next("5", brief: true);
        Assert.StartsWith("Bước 2", brief);
        Assert.Contains("Dừng:", brief);
        Assert.DoesNotContain("Tool:", brief);
        Assert.True(brief.Length < next.Length / 2, brief);
    }
}

public class TextTests
{
    [Fact]
    public void Fold_bo_dau() => Assert.Equal("bo qua dong", Text.Fold("Bỏ qua Đồng"));
}

/// <summary>Phạm vi plugin: chỉ repo add-in Revit (hawee.project.yaml có profiles chứa revit-addin).</summary>
public class ScopeTests
{
    [Fact]
    public void Repo_Revit_mo_duoc()
    {
        using var repo = new TestRepo();
        Assert.Contains(ProjectContext.RevitProfile, repo.Ctx().Profiles);
    }

    [Theory]
    [InlineData("profiles: [revit-addin]", "profiles: [autocad-addin]")]
    [InlineData("profiles: [revit-addin]", "profiles: []")]
    [InlineData("profiles: [revit-addin]\n", "\n")]
    public void Repo_khong_phai_Revit_bi_tu_choi(string from, string to)
    {
        using var repo = new TestRepo();
        repo.Write("hawee.project.yaml", repo.Read("hawee.project.yaml").Replace(from, to));
        var ex = Assert.Throws<FlowException>(() => repo.Ctx());
        Assert.Contains("không phải dự án add-in Revit", ex.Message);
        Assert.Contains("chỉ dùng khi phát triển add-in", ex.Message);
    }

    [Fact]
    public void Khong_co_hawee_project_yaml_bi_tu_choi()
    {
        var dir = Directory.CreateTempSubdirectory("noscope").FullName;
        try
        {
            var ex = Assert.Throws<FlowException>(() => ProjectContext.Open(dir));
            Assert.Contains("không phải repo add-in Revit", ex.Message);
        }
        finally { Directory.Delete(dir, true); }
    }
}
