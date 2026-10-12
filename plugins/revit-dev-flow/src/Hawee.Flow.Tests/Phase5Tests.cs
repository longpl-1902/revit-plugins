using Hawee.Flow.Core.Infra;
using Hawee.Flow.Core.Tasks;

namespace Hawee.Flow.Tests;

public class Phase5Tests
{
    private static TestRepo Repo()
    {
        var repo = new TestRepo();
        new TaskOpener(repo.Ctx(), repo.Runner).Open(new OpenInput("21", "keyplan"));
        repo.Runner.Handler = (file, args) =>
        {
            if (file != "git") return null;
            if (args.Contains("--verify")) return new ProcResult(args.Any(a => a.StartsWith("origin/DEV")) ? 0 : 1, "", false);
            if (args.Contains("--stat")) return new ProcResult(0, " src/A.cs | 10 +++++-----\n 1 file changed\n", false);
            if (args.Contains("-U3")) return new ProcResult(0, "diff --git a/src/A.cs b/src/A.cs\n+new line\n-old line\n", false);
            if (args.Contains("--numstat")) return new ProcResult(0, "5\t5\tsrc/A.cs\n", false);
            if (args.Contains("--oneline")) return new ProcResult(0, "abc1234 feat(keyplan): x\n", false);
            if (args.Contains("--porcelain")) return new ProcResult(0, "", false);
            if (args.Contains("--abbrev-ref")) return new ProcResult(0, "an_21-keyplan\n", false);
            return new ProcResult(0, "abc1234567\n", false);
        };
        return repo;
    }

    [Fact]
    public void Goi_review_co_diff_va_checklist()
    {
        using var repo = Repo();
        var r = new Closing(repo.Ctx(), repo.Runner).ReviewPacket("21", null);
        Assert.Contains("diff +5/-5 so origin/DEV", r);
        var file = Directory.GetFiles(Path.Combine(repo.Root, "docs/tasks/21-keyplan"), "review-packet-*.md").Single();
        var text = Text.Read(file);
        Assert.Contains("+new line", text);
        Assert.Contains("## Checklist G5", text);
        Assert.Contains("## Plan §5.6 — Bất biến", text);
        var diffArgs = repo.Runner.Calls.Single(c => c.Args.Contains("-U3")).Args;
        Assert.Contains(":(exclude)docs/tasks", diffArgs);
        Assert.Contains(":(exclude,glob)**/*.Designer.cs", diffArgs);
    }

    [Fact]
    public void Handoff_nhap_tu_git_va_status()
    {
        using var repo = Repo();
        new GateRecorder(repo.Ctx()).Record("21", new GateInput("G1", "PASS", "An", "issue"));
        var c = new Closing(repo.Ctx(), repo.Runner);
        var r = c.HandoffDraft("21");
        var dir = Path.Combine(repo.Root, "docs/tasks/21-keyplan");
        var text = Text.Read(Path.Combine(dir, "handoff-2026-10-12.md"));
        Assert.Contains("- Chủ đề: `keyplan`", text);
        Assert.Contains("đầu chuỗi", text);
        Assert.Contains("DEC-2026-001", text);
        Assert.Contains("| Nhánh | an_21-keyplan |", text);
        Assert.Contains("abc1234 feat(keyplan): x", text);
        Assert.Contains("chưa chạy trên Revit", text.Replace("<chưa kiểm gì — không làm tròn>", "chưa chạy trên Revit"));
        Assert.Contains("ô <…>", r);
        // lần 2 cùng ngày → -2, nối chuỗi với bản trước
        c.HandoffDraft("21");
        var second = Text.Read(Path.Combine(dir, "handoff-2026-10-12-2.md"));
        Assert.Contains("handoff-2026-10-12.md", second);
        Assert.Equal(new[] { "handoff-2026-10-12.md", "handoff-2026-10-12-2.md" }, StatusFile.ForTask(dir).Data["handoffs"].Strings());
    }
}
