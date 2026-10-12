using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hawee.Flow.Core.Docs;
using Hawee.Flow.Core.Infra;
using Hawee.Flow.Core.Tasks;

namespace Hawee.Flow.Core.Checks;

public sealed record BuildResult(string Name, string Command, int Exit, int Errors, int Warnings, double Seconds);
public sealed record TestResult(string Name, string Level, string Command, int Exit, int? Passed, int? Failed, int? Skipped, int Known, List<string> FailedTests, string Log);
public sealed record BaselineData(string Commit, string Branch, string Date, int Dirty, List<BuildResult> Build, List<TestResult> Tests, string Json);

/// <summary>Đọc / chạy test-baseline.ps1 của Base và so hai lần đo (fail mới theo tên test).</summary>
public sealed class Baseline(ProjectContext ctx, IProcRunner? runner = null)
{
    private readonly IProcRunner _run = runner ?? ProcRunner.Instance;

    public BaselineData Measure(bool build, string level = "", string only = "", int timeoutMin = 60)
    {
        var script = ctx.BaseTool("test-baseline.ps1");
        if (!File.Exists(script)) throw new FlowException("không có test-baseline.ps1 trong Base");
        var args = new List<string>();
        if (build) args.Add("-Build");
        if (level.Length > 0) { args.Add("-Level"); args.Add(level); }
        if (only.Length > 0) { args.Add("-Only"); args.Add(only); }
        var r = Ps.RunFile(_run, script, args, ctx.Root, TimeSpan.FromMinutes(timeoutMin));
        var json = r.Lines.Select(l => Regex.Match(l, @"\[test-baseline\] ghi (.+\.json)\s*$")).FirstOrDefault(m => m.Success)?.Groups[1].Value.Trim();
        if (json is null || !File.Exists(json))
            throw new FlowException($"test-baseline không ra baseline.json (exit {r.ExitCode}): {Text.Clip(string.Join(" | ", r.Lines.TakeLast(3)), 300)}");
        return Load(json);
    }

    public static BaselineData Load(string json)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(json));
        var root = doc.RootElement;
        string S(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind != JsonValueKind.Null ? v.ToString() : "";
        int I(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
        int? NI(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
        IEnumerable<JsonElement> Arr(JsonElement e, string k) =>
            e.TryGetProperty(k, out var v) ? (v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : v.ValueKind == JsonValueKind.Object ? new[] { v } : Enumerable.Empty<JsonElement>()) : Enumerable.Empty<JsonElement>();
        var builds = Arr(root, "build").Select(b => new BuildResult(S(b, "name"), S(b, "command"), I(b, "exit"), I(b, "errors"), I(b, "warnings"),
            b.TryGetProperty("seconds", out var sec) && sec.ValueKind == JsonValueKind.Number ? sec.GetDouble() : 0)).ToList();
        var tests = Arr(root, "tests").Select(t => new TestResult(S(t, "name"), S(t, "level"), S(t, "command"), I(t, "exit"),
            NI(t, "passed"), NI(t, "failed"), NI(t, "skipped"), I(t, "known_failures"),
            Arr(t, "failed_tests").Select(x => x.ValueKind == JsonValueKind.Object ? S(x, "name") : x.ToString()).ToList(), S(t, "log"))).ToList();
        return new BaselineData(S(root, "commit"), S(root, "branch"), S(root, "date"), I(root, "uncommitted_files"), builds, tests, json);
    }

    /// <summary>Bước 4a: đo trên commit gốc, chép baseline.json vào folder task, điền bảng plan §1.3.</summary>
    public string RunForPlan(string? task, bool build, string level, string only)
    {
        var dir = ctx.ResolveTask(task);
        var data = Measure(build, level, only);
        var keep = Path.Combine(dir, "baseline.json");
        File.Copy(data.Json, keep, overwrite: true);
        var status = StatusFile.ForTask(dir);
        status.Set("baseline", ctx.Rel(keep));
        status.Save();

        var rows = Rows(data).ToList();
        var plan = Path.Combine(dir, "plan.md");
        var wrote = false;
        if (File.Exists(plan))
        {
            var doc = new MdDoc(plan);
            if (doc.Section("1.3") is { } s)
            {
                // bỏ các dòng mẫu còn <placeholder>, giữ dòng người tự thêm (vd. đo hiệu năng)
                for (var i = s.End - 1; i > s.Start; i--)
                    if (doc.Lines[i].StartsWith('|') && (doc.Lines[i].Contains('<') || doc.Lines[i].Contains("| |")) && !doc.Lines[i].StartsWith("|---"))
                        doc.Lines.RemoveAt(i);
                doc.AppendRows("1.3", rows);
                doc.Save();
                wrote = true;
            }
        }
        var sb = new StringBuilder();
        foreach (var r in rows) sb.AppendLine(r);
        var preexisting = data.Tests.Where(t => (t.Failed ?? 0) > 0).ToList();
        if (preexisting.Count > 0)
            sb.AppendLine("! fail sẵn có phải có KI ID (không có → ghi 'fail chưa có KI' là phát hiện): " +
                          string.Join("; ", preexisting.Select(t => $"{t.Name}: {string.Join(", ", t.FailedTests.Take(5))}{(t.FailedTests.Count > 5 ? ", …" : "")}")));
        var noTrx = data.Tests.Where(t => t.Passed is null).ToList();
        if (noTrx.Count > 0) sb.AppendLine("! không có TRX (chỉ có exit code): " + string.Join(", ", noTrx.Select(t => $"{t.Name} exit {t.Exit} — {t.Log}")));
        sb.Append(wrote ? $"Đã điền plan §1.3 · baseline: {ctx.Rel(keep)}" : $"plan.md không có §1.3 — dán bảng trên · baseline: {ctx.Rel(keep)}");
        return sb.ToString();
    }

    public static IEnumerable<string> Rows(BaselineData d)
    {
        var hash = d.Commit.Length >= 8 ? d.Commit[..8] : d.Commit;
        yield return $"| Nhánh tích hợp · commit gốc | `{d.Branch}` @ `{hash}`{(d.Dirty > 0 ? $" ({d.Dirty} file chưa commit)" : "")} | `git rev-parse HEAD` |";
        foreach (var b in d.Build) yield return $"| Build ({b.Name}) | exit {b.Exit} · {b.Errors} lỗi / {b.Warnings} cảnh báo · {b.Seconds:0} s | `{Text.Cell(b.Command)}` |";
        foreach (var t in d.Tests)
            yield return t.Passed is null
                ? $"| Test {t.Name} ({t.Level}) | exit {t.Exit} (không có TRX) | `{Text.Cell(t.Command)}` |"
                : $"| Test {t.Name} ({t.Level}) | pass {t.Passed} · fail {t.Failed} · skip {t.Skipped} (đã biết {t.Known}) | `{Text.Cell(t.Command)}` |";
    }

    /// <summary>So lần đo mới với baseline: build hỏng, fail mới theo tên test (không có TRX thì theo số / exit).</summary>
    public static List<string> NewProblems(BaselineData baseline, BaselineData now)
    {
        var problems = new List<string>();
        foreach (var b in now.Build.Where(b => b.Exit != 0 || b.Errors > 0))
            problems.Add($"build {b.Name}: exit {b.Exit}, {b.Errors} lỗi (log trong {Path.GetDirectoryName(now.Json)})");
        foreach (var t in now.Tests)
        {
            var old = baseline.Tests.FirstOrDefault(x => x.Name == t.Name);
            if (t.Passed is null)
            {
                if (t.Exit != 0 && (old is null || old.Exit == 0)) problems.Add($"test {t.Name}: exit {t.Exit} (baseline {old?.Exit.ToString() ?? "không có"}) — {t.Log}");
                continue;
            }
            var newly = t.FailedTests.Except(old?.FailedTests ?? new List<string>()).ToList();
            if (newly.Count > 0) problems.Add($"test {t.Name}: {newly.Count} fail mới — {string.Join(", ", newly.Take(6))}{(newly.Count > 6 ? ", …" : "")}");
            else if (t.FailedTests.Count == 0 && (t.Failed ?? 0) > Math.Max(t.Known, old?.Failed ?? 0))
                problems.Add($"test {t.Name}: fail {t.Failed} > baseline {old?.Failed ?? 0} / đã biết {t.Known}");
        }
        return problems;
    }
}
