using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hawee.Flow.Core.Infra;
using Hawee.Flow.Core.Tasks;

namespace Hawee.Flow.Core.Checks;

public sealed record CheckLine(string Name, string Command, bool Ok, string Summary, List<string> Details);
public sealed record CheckRun(string Scope, string Date, string Commit, string BaseRef, bool Ok, int Loop, List<CheckLine> Lines, List<string> ChangedFiles);

/// <summary>
/// Bước 5–6 và 8: build + test L0 so baseline (fail mới theo tên test) + bộ lint của Base, chạy bằng máy, trả kết quả gọn.
/// quick (trong lúc code): build, L0, code-lint -Diff, csproj-twin-lint. full (DoD): thêm kb-lint, project-lint, doc-lint, codemap-lint.
/// Có lỗi và count_loop → status.loops.build_test += 1; hết 5 vòng → dừng.
/// </summary>
public sealed class CheckAll(ProjectContext ctx, IProcRunner? runner = null)
{
    private readonly IProcRunner _run = runner ?? ProcRunner.Instance;
    public const string LastFile = "checks-last.json";

    public CheckRun Run(string? task, string scope, string? baseRef, bool countLoop)
    {
        var dir = ctx.ResolveTask(task);
        var status = StatusFile.ForTask(dir);
        var st = status.Data;
        var git = new Git(ctx, _run);
        var bref = git.IntegrationRef(baseRef);
        var changed = git.ChangedFiles(bref);
        var lines = new List<CheckLine>();

        // build + L0 so baseline
        var bl = new Baseline(ctx, _run);
        BaselineData? now = null;
        try { now = bl.Measure(build: true, level: "L0"); }
        catch (FlowException ex) { lines.Add(new CheckLine("build+L0", "test-baseline -Build -Level L0", false, Text.Clip(ex.Message, 200), new())); }
        if (now is not null)
        {
            var basePath = st["baseline"].S();
            var baseline = basePath.Length > 0 && File.Exists(ctx.Full(basePath)) ? Baseline.Load(ctx.Full(basePath)) : null;
            var problems = baseline is null ? Baseline.NewProblems(EmptyBaseline(), now) : Baseline.NewProblems(baseline, now);
            var builds = now.Build.Select(b => $"{b.Name} {b.Errors} lỗi/{b.Warnings} cảnh báo");
            var tests = now.Tests.Select(t => t.Passed is null ? $"{t.Name} exit {t.Exit}" : $"{t.Name} {t.Passed}/{t.Failed}/{t.Skipped}");
            lines.Add(new CheckLine("build+L0", "test-baseline -Build -Level L0", problems.Count == 0,
                $"{string.Join(", ", builds)} · test pass/fail/skip: {string.Join(", ", tests)}{(baseline is null ? " · CHƯA CÓ BASELINE (so với 0 fail)" : "")}",
                problems));
        }

        // lint
        var csAdded = changed.Any(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase));
        var lints = new List<(string Name, string Script, string[] Args)>
        {
            ("code-lint", "code-lint.ps1", new[] { "-Diff", bref }),
            ("csproj-twin-lint", "csproj-twin-lint.ps1", csAdded ? new[] { "-OrphanAsError" } : Array.Empty<string>()),
        };
        if (scope == "full")
        {
            lints.Add(("kb-lint", "kb-lint.ps1", Array.Empty<string>()));
            lints.Add(("project-lint", "project-lint.ps1", Array.Empty<string>()));
            lints.Add(("doc-lint", "doc-lint.ps1", new[] { "-Path", dir }));
            foreach (var map in TaskCodeMaps(st))
                lints.Add(($"codemap-lint {Path.GetFileName(map)}", "codemap-lint.ps1", new[] { "-Path", ctx.Full(map), "-Strict" }));
        }
        foreach (var (name, script, args) in lints)
        {
            var path = ctx.BaseTool(script);
            if (!File.Exists(path)) { lines.Add(new CheckLine(name, script, true, "không có script — bỏ qua", new())); continue; }
            var r = Ps.RunFile(_run, path, args, ctx.Root, TimeSpan.FromMinutes(10));
            var errs = ErrorLines(r.Lines, script.Replace(".ps1", "")).ToList();
            var warns = r.Lines.Count(l => l.TrimStart().StartsWith("WARN"));
            lines.Add(new CheckLine(name, $"{script} {string.Join(' ', args)}".Trim(), r.ExitCode == 0,
                r.ExitCode == 0 ? $"OK{(warns > 0 ? $" ({warns} cảnh báo)" : "")}" : $"exit {r.ExitCode} · {errs.Count} lỗi",
                r.ExitCode == 0 ? new() : (errs.Count > 0 ? errs : r.Lines.TakeLast(5).ToList())));
        }

        var ok = lines.All(l => l.Ok);
        var loop = st["loops"]["build_test"].Int();
        if (!ok && countLoop)
        {
            loop++;
            status.SetInMap("loops", "build_test", loop.ToString());
            status.Save();
        }
        var run = new CheckRun(scope, ctx.Clock.Now.ToString("yyyy-MM-dd HH:mm"), git.Head(), bref, ok, loop, lines, changed);
        Text.Write(Path.Combine(dir, LastFile), JsonSerializer.Serialize(run, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        return run;
    }

    public string Summarize(CheckRun r)
    {
        var max = 5;
        var sb = new StringBuilder();
        sb.AppendLine($"check_all ({r.Scope}) — {(r.Ok ? "PASS" : "FAIL")} · vòng sửa build/test {r.Loop}/{max} · so với {r.BaseRef} · {r.ChangedFiles.Count} file thay đổi");
        foreach (var l in r.Lines)
        {
            sb.AppendLine($"{(l.Ok ? "ok  " : "FAIL")} {l.Name}: {l.Summary}");
            foreach (var d in Text.Cap(l.Details, 4)) sb.AppendLine("     " + Text.Clip(d, 200));
        }
        if (!r.Ok && r.Loop >= max) sb.AppendLine("DỪNG: hết 5 vòng sửa — báo người lệnh đã chạy + lỗi nguyên văn (checks-last.json), giả thuyết, việc đã thử.");
        else if (!r.Ok) sb.AppendLine("Sửa đúng lỗi do diff của task rồi chạy lại. Không nới test / thêm known_failures.");
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Dòng lỗi của script lint Base: bỏ dòng tổng kết "[tên] …", WARN / warn / info và dòng con thụt lề của chúng;
    /// giữ "error [ID] …" (code-lint) + dòng vị trí bên dưới, và dòng lỗi "[mã] …" của các lint khác.
    /// </summary>
    internal static IEnumerable<string> ErrorLines(IEnumerable<string> lines, string scriptName)
    {
        var skipChildren = false;
        foreach (var raw in lines)
        {
            if (raw.Trim().Length == 0) continue;
            if (raw.StartsWith("  ") || raw.StartsWith("\t"))
            {
                if (!skipChildren) yield return raw.Trim();
                continue;
            }
            var l = raw.Trim();
            if (l.StartsWith($"[{scriptName}]") || l.StartsWith("WARN") || l.StartsWith("warn ") || l.StartsWith("info ") || l.StartsWith("OK"))
            {
                skipChildren = true;
                continue;
            }
            skipChildren = false;
            yield return l;
        }
    }

    private static IEnumerable<string> TaskCodeMaps(Y st) =>
        st["code_maps"].Items.Select(i => i.IsMap ? i["map"].S() : i.S()).Where(s => s.Length > 0);

    private static BaselineData EmptyBaseline() => new("", "", "", 0, new(), new(), "");
}
