using Hawee.Flow.Core.Checks;
using Hawee.Flow.Core.Docs;
using Hawee.Flow.Core.Infra;
using Hawee.Flow.Core.Tasks;

namespace Hawee.Flow.Tests;

public class Phase3Tests
{
    private const string BaseJson = """
        { "commit": "1111111111", "branch": "DEV", "date": "d", "uncommitted_files": 0,
          "build": [ { "name": "build", "command": "msbuild", "exit": 0, "seconds": 1, "errors": 0, "warnings": 0 } ],
          "tests": [ { "name": "unit", "level": "L0", "command": "dotnet test", "exit": 1, "known_failures": 1, "passed": 9, "failed": 1, "skipped": 0, "failed_tests": ["T.Old"], "log": "l" } ] }
        """;

    /// <summary>Repo giả: git trả diff cố định, test-baseline trả JSON đặt trước, lint theo bảng.</summary>
    private static (TestRepo Repo, string Dir) Setup(string nowJson, Dictionary<string, ProcResult>? lints = null, string type = "feature")
    {
        var repo = new TestRepo();
        new TaskOpener(repo.Ctx(), repo.Runner).Open(new OpenInput("12", "keyplan", Bug: type == "bug"));
        var dir = Path.Combine(repo.Root, "docs/tasks/12-keyplan");
        Text.Write(Path.Combine(dir, "baseline.json"), BaseJson);
        var st = StatusFile.ForTask(dir);
        st.Set("baseline", "docs/tasks/12-keyplan/baseline.json");
        st.Save();
        foreach (var s in new[] { "test-baseline.ps1", "code-lint.ps1", "csproj-twin-lint.ps1", "kb-lint.ps1", "project-lint.ps1", "doc-lint.ps1" })
            repo.Write("BIM_KB_Base/core/tools/" + s, "# giả");
        var json = Path.Combine(repo.Root, "now.json");
        File.WriteAllText(json, nowJson);
        repo.Write("src/Draw/KeyplanTest.cs", "// test cho #12\n");
        repo.Runner.Handler = (file, args) =>
        {
            if (file == "git")
            {
                if (args.Contains("--verify")) return new ProcResult(args.Any(a => a.StartsWith("origin/DEV")) ? 0 : 1, "", false);
                if (args.Contains("--name-only")) return new ProcResult(0, "src/Draw/Keyplan.cs\nsrc/Draw/KeyplanTest.cs\n", false);
                if (args.Contains("--porcelain")) return new ProcResult(0, " M src/Draw/Keyplan.cs\n", false);
                if (args.Contains("--numstat")) return new ProcResult(0, "30\t5\tsrc/Draw/Keyplan.cs\n", false);
                if (args.Contains("HEAD")) return new ProcResult(0, "abcdef123456\n", false);
            }
            var script = args.FirstOrDefault(a => a.EndsWith(".ps1"));
            if (script is null) return null;
            var name = Path.GetFileName(script);
            if (name == "test-baseline.ps1") return new ProcResult(0, $"[test-baseline] ghi {json}\n", false);
            return lints is not null && lints.TryGetValue(name, out var r) ? r : new ProcResult(0, "", false);
        };
        return (repo, dir);
    }

    [Fact]
    public void Quick_pass_khong_tang_vong()
    {
        var (repo, dir) = Setup(BaseJson);
        using var _ = repo;
        var c = new CheckAll(repo.Ctx(), repo.Runner);
        var r = c.Run("12", "quick", null, true);
        Assert.True(r.Ok, c.Summarize(r));
        Assert.Equal("origin/DEV", r.BaseRef);
        Assert.Equal(0, StatusFile.ForTask(dir).Data["loops"]["build_test"].Int());
        Assert.Equal(3, r.Lines.Count);                                   // build+L0, code-lint, csproj-twin-lint
        Assert.Contains("-OrphanAsError", repo.Runner.Calls.SelectMany(c => c.Args));
        Assert.True(File.Exists(Path.Combine(dir, CheckAll.LastFile)));
    }

    [Fact]
    public void Fail_moi_va_lint_loi_tang_vong()
    {
        var now = BaseJson.Replace("\"failed\": 1, \"skipped\": 0, \"failed_tests\": [\"T.Old\"]", "\"failed\": 2, \"skipped\": 0, \"failed_tests\": [\"T.Old\", \"T.New\"]");
        var (repo, dir) = Setup(now, new() { ["code-lint.ps1"] = new ProcResult(1, "[code-lint] file quet: 2, luat: 9\n\nwarn [W1] x - 1 cho\n  A.cs:1 x\n\nerror [REVIT-VER-001] API 2024-only (rule GEN-VER-001) - 1 cho\n  src/Draw/Keyplan.cs:12 doc.Foo()\n\n[code-lint] error: 1, warn: 1, info: 0\n", false) });
        using var _ = repo;
        var c = new CheckAll(repo.Ctx(), repo.Runner);
        var r = c.Run("12", "quick", null, true);
        Assert.False(r.Ok);
        var text = c.Summarize(r);
        Assert.Contains("T.New", text);
        Assert.DoesNotContain("T.Old", text.Split("T.New")[0].Split('\n').Last());
        Assert.Contains("Keyplan.cs:12", text);
        Assert.DoesNotContain("[code-lint]", text);
        Assert.DoesNotContain("A.cs:1", text);
        Assert.Equal(1, StatusFile.ForTask(dir).Data["loops"]["build_test"].Int());
        for (var i = 0; i < 4; i++) r = c.Run("12", "quick", null, true);
        Assert.Contains("DỪNG: hết 5 vòng", c.Summarize(r));
    }

    [Fact]
    public void Dod_report_ghi_file_va_state()
    {
        var (repo, dir) = Setup(BaseJson, type: "bug");
        using var _ = repo;
        var r = new DodReport(repo.Ctx(), repo.Runner).Build("12", null);
        Assert.Contains("DoD PASS", r);
        Assert.Contains("fix(keyplan):", r);
        Assert.Contains("Issue: #12", r);
        var report = Text.Read(Path.Combine(dir, "dod-report.md"));
        Assert.Contains("test hồi quy (bug)", report);
        Assert.Contains("+30/-5", report);
        Assert.Equal("commit", StatusFile.ForTask(dir).Get("state"));
    }

    [Fact]
    public void Lech_lon_quay_lai_G2()
    {
        using var repo = new TestRepo();
        new TaskOpener(repo.Ctx(), repo.Runner).Open(new OpenInput("13", "x"));
        var d = new Deviation(repo.Ctx());
        Assert.Throws<FlowException>(() => d.Add("13", "a", "b", "lớn", ""));
        Assert.Contains("đi tiếp", d.Add("13", "thêm overload", "caller cũ", "nhỏ", ""));
        Assert.Contains("DỪNG", d.Add("13", "đổi chữ ký", "caller ngoài file", "lớn", "G2"));
        var dir = Path.Combine(repo.Root, "docs/tasks/13-x");
        var st = StatusFile.ForTask(dir).Data;
        Assert.Equal(2, st["deviations"].Int());
        Assert.Equal("plan", st["state"].S());
        Assert.Equal(2, new MdDoc(Path.Combine(dir, "plan.md")).TableRows("13").Count);
    }
}
