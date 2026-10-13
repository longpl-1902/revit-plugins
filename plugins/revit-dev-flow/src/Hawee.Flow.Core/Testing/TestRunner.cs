using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Hawee.Flow.Core.Checks;
using Hawee.Flow.Core.Infra;
using Hawee.Flow.Core.Tasks;

namespace Hawee.Flow.Core.Testing;

public sealed record RunRequest(
    string Suite, string Purpose = "task", string Session = "", string Autonomy = "", bool Build = false, string Task = "", bool NewSession = false);

/// <summary>
/// Một lần chạy suite trọn vòng (bước 0–10 của /revit-test cũ) bằng máy: kiểm môi trường, mở Revit, bản sao model, chạy step
/// (chặn tool cấm ngay tại runner), thu step_status + output điểm kiểm, cắt log, đối chiếu (host-test-reconcile.ps1),
/// chẩn đoán gợi ý, chấm luật (Grader), evidence, report.md / result.json, dọn dẹp, test-summary.ps1.
/// Agent nhận bản tóm tắt ≤ ~25 dòng; chỉ luật cần mắt (ảnh) hoặc chẩn đoán mơ hồ mới quay lại agent.
/// </summary>
public sealed class TestRunner(
    ProjectContext ctx, RunnerConfig cfg, Func<string, Task<IToolClient>> connect, IHostProcess host, IProcRunner proc)
{
    private readonly TestPaths _paths = new(ctx);
    private static readonly JsonSerializerOptions JsonOut = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private sealed class RunState
    {
        public required Suite Suite;
        public required RulesFile Rules;
        public required string RunDir;
        public required string Data;
        public readonly StringBuilder Log = new();
        public readonly List<(string Step, string Start, string Result)> Journal = new();
        public JsonObject Info = new();
        public string? PipelineError;
        public string? EnvError;
        public int Pid;
        public string ModelCopy = "";
        public string ModelHashBefore = "";
        public string ExternalRunId = "";
        public string DataSource = "";
        public long LogOffset = -1;
        public DateTime StepsStart, StepsEnd;
        public readonly List<string> ToolErrors = new();
        public readonly List<string> ConfigFiles = new();
        public readonly List<string> Notes = new();
        public JsonObject? CloseSchema;
    }

    public async Task<string> Run(RunRequest req, CancellationToken ct = default)
    {
        var suiteDir = _paths.Suite(req.Suite);
        if (!File.Exists(Path.Combine(suiteDir, "suite.yaml")))
        {
            var list = Directory.Exists(_paths.SuitesDir) ? Directory.GetDirectories(_paths.SuitesDir).Select(Path.GetFileName).Take(20) : Enumerable.Empty<string?>();
            throw new FlowException($"không có suite {req.Suite} trong {_paths.SuitesDir}. Có: {string.Join(", ", list)}");
        }
        var rulesPath = Path.Combine(suiteDir, "rules.md");
        if (!File.Exists(rulesPath)) throw new FlowException($"suite {req.Suite} thiếu rules.md — không tự bịa luật");
        var taskDir = req.Task.Length > 0 ? ctx.ResolveTask(req.Task) : null;
        if (taskDir is not null) CheckApprovedHashes(taskDir, req.Suite, suiteDir);
        var unapproved = taskDir is null ? UnapprovedNote(suiteDir) : null;

        var stamp = ctx.Clock.Now.ToString("yyyyMMdd-HHmmss");
        var runDir = Path.Combine(suiteDir, "runs", stamp);
        Directory.CreateDirectory(Path.Combine(runDir, "data"));
        var s = new RunState { Suite = Suite.Load(suiteDir), Rules = RulesFile.Load(rulesPath), RunDir = runDir, Data = Path.Combine(runDir, "data") };

        var (session, loop) = Session(req, suiteDir, taskDir, stamp);
        var git = new Git(ctx, proc);
        var autonomy = req.Autonomy.Length > 0 ? req.Autonomy : taskDir is not null ? StatusFile.ForTask(taskDir).Get("autonomy") : "";
        s.Info = new JsonObject
        {
            ["suite"] = s.Suite.Name, ["run"] = stamp, ["session"] = session, ["purpose"] = req.Purpose, ["loop"] = loop,
            ["commit"] = git.Head(), ["branch"] = git.Branch(), ["autonomy"] = autonomy.Length > 0 ? autonomy : "ask",
            ["manual_steps"] = new JsonArray(s.Suite.ManualSteps.Select(m => (JsonNode)m).ToArray()),
            ["runner"] = "hawee-flow", ["task"] = taskDir is not null ? Path.GetFileName(taskDir) : "",
        };
        if (unapproved is not null) { s.Info["rules_approved"] = false; s.Notes.Add(unapproved); }
        WriteInfo(s);

        IToolClient? gw = null;
        try
        {
            Step(s, "0 chuẩn bị", () => Prepare(s, req));
            if (s.EnvError is null && req.Build) await StepAsync(s, "0b build add-in", () => BuildAddin(s));
            if (s.EnvError is null)
            {
                gw = await connect(cfg.Gateway);
                var before = await ListPids(gw);
                await StepAsync(s, "2-3 mở Revit, chờ pipe", () => StartRevit(s, gw, before, ct));
                // Revit chưa sẵn sàng (EnvError) hoặc không có PID → không gọi gì vào Revit: gateway có thể đẩy lệnh sang phiên Revit khác
                bool Ready() => s.EnvError is null && s.PipelineError is null && s.Pid > 0;
                if (Ready()) await StepAsync(s, "4 mở bản sao model", () => OpenCopy(s, gw));
                if (Ready()) await StepAsync(s, "5 chạy kịch bản", () => RunSteps(s, gw));
                if (s.StepsStart != default) await StepAsync(s, "6 thu kết quả", () => Capture(s, ct));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            s.PipelineError ??= $"{ex.GetType().Name}: {ex.Message}";
            Log(s, "LỖI: " + s.PipelineError);
        }
        finally
        {
            await Cleanup(s, gw);
            if (gw is not null) await gw.DisposeAsync();
        }

        Step(s, "6a đối chiếu log", () => Reconcile(s));
        Step(s, "6b chẩn đoán", () => Diagnose(s, taskDir is not null ? git.ChangedFiles(SafeBase(git)) : null));
        var graded = Grade(s);
        Step(s, "7b evidence", () => Evidence(s, graded));
        Step(s, "8 báo cáo", () => Report(s, graded));
        Step(s, "8b máy chấm Base (rule-eval)", () => CrossCheck(s, graded));
        File.WriteAllText(Path.Combine(s.RunDir, "run.log"), s.Log.ToString());

        var summary = Summary(s.RunDir, autonomy);
        if (taskDir is not null && req.Purpose == "task")
        {
            var st = StatusFile.ForTask(taskDir);
            st.Set("test_session", session);
            st.Set("test_summary", Path.Combine(s.RunDir, "test-summary.md"));
            st.SetInMap("loops", "host_test", summary.FixesDone.ToString());
            if (st.Get("state") is "code" or "g2") st.Set("state", "test");
            st.Save();
        }
        return Format(s, graded, summary);
    }

    // ---- bước 0 --------------------------------------------------------------------------------------------

    private void CheckApprovedHashes(string taskDir, string suite, string suiteDir)
    {
        var h = StatusFile.ForTask(taskDir).Data["suite_hashes"][suite];
        if (!h.Exists) return;
        var sy = Text.Sha256File(Path.Combine(suiteDir, "suite.yaml"));
        var rm = Text.Sha256File(Path.Combine(suiteDir, "rules.md"));
        if (!sy.Equals(h["suite_yaml"].S(), StringComparison.OrdinalIgnoreCase) || !rm.Equals(h["rules_md"].S(), StringComparison.OrdinalIgnoreCase))
            throw new FlowException($"suite {suite}: suite.yaml/rules.md đổi sau khi duyệt ở G2 ({h["approved_by"].S()} {h["date"].S()}) — người duyệt lại rồi mới chạy");
    }

    /// <summary>Chạy lẻ trên suite đã khoá ở G2 mà suite.yaml / rules.md đã đổi → chạy nhưng đánh dấu; ma trận AC không lấy lần chạy này.</summary>
    private static string? UnapprovedNote(string suiteDir)
    {
        var a = ReadJson(Path.Combine(suiteDir, "approved.json"));
        if (a is null) return null;
        var sy = Text.Sha256File(Path.Combine(suiteDir, "suite.yaml"));
        var rm = Text.Sha256File(Path.Combine(suiteDir, "rules.md"));
        return sy.Equals(a["suite_yaml"]?.ToString(), StringComparison.OrdinalIgnoreCase) && rm.Equals(a["rules_md"]?.ToString(), StringComparison.OrdinalIgnoreCase)
            ? null
            : $"LUẬT CHƯA DUYỆT: suite.yaml/rules.md đổi sau G2 ({a["approved_by"]} {a["date"]}) — kết quả lần chạy này không dùng cho G3; duyệt lại ở G2 rồi chạy /verify.";
    }

    private (string Session, int Loop) Session(RunRequest req, string suiteDir, string? taskDir, string stamp)
    {
        if (req.Purpose == "baseline") return (req.Session, 0);
        var session = req.Session;
        if (session.Length == 0 && !req.NewSession && taskDir is not null) session = StatusFile.ForTask(taskDir).Get("test_session");
        if (session.Length == 0) session = "verify-" + stamp;
        var prior = Directory.Exists(Path.Combine(suiteDir, "runs"))
            ? Directory.GetDirectories(Path.Combine(suiteDir, "runs"))
                .Select(d => Path.Combine(d, "run-info.json")).Where(File.Exists)
                .Select(f => JsonNode.Parse(File.ReadAllText(f)))
                .Count(j => j?["session"]?.ToString() == session && j?["purpose"]?.ToString() != "baseline")
            : 0;
        return (session, prior);
    }

    private void Prepare(RunState s, RunRequest req)
    {
        // suite-lint -Hash (kho dữ liệu bị sửa → dừng)
        var lint = ctx.BaseTool("suite-lint.ps1");
        if (File.Exists(lint))
        {
            var r = Ps.RunFile(proc, lint, new[] { "-Suite", s.Suite.Dir, "-Hash", "-Block", ctx.TestBlockName }, ctx.Root, TimeSpan.FromMinutes(5));
            if (r.ExitCode == 1)
            {
                var first = r.Lines.FirstOrDefault(l => Regex.IsMatch(l.Trim(), @"^\[[a-z-]+\]") && !l.Contains("rule-pending")) ?? r.Lines.LastOrDefault() ?? "";
                if (r.Lines.Any(l => Regex.IsMatch(l.Trim(), @"^\[[a-z-]+\]") && !l.Contains("rule-pending")))
                    s.EnvError = "suite/bộ dữ liệu lỗi — " + Text.Clip(first.Trim(), 200);
            }
        }
        // AddInId trùng (Revit hiện hộp thoại khởi động, mọi lời gọi MCP treo)
        var ver = s.Suite.RevitVersion.Length > 0 ? s.Suite.RevitVersion : Regex.Match(s.Suite.RevitExe, @"20\d\d").Value;
        var dup = DuplicateAddinIds(ver);
        if (dup.Count > 0) s.EnvError ??= "AddInId trùng: " + string.Join("; ", dup);
        // DLL add-in nạp thẳng + chữ ký
        var env = new JsonObject { ["addin_ids"] = dup.Count == 0 ? "ok" : string.Join("; ", dup) };
        if (cfg.AddinDll.Length > 0)
        {
            var dll = ctx.Full(cfg.AddinDll);
            if (!File.Exists(dll)) s.EnvError ??= $"không có {cfg.AddinDll}";
            else if (cfg.RequireSigned)
            {
                var sig = Authenticode.Check(dll);
                env["addin_dll"] = sig;
                if (sig != "Valid" && OperatingSystem.IsWindows()) s.EnvError ??= $"{cfg.AddinDll} chữ ký {sig} (require_signed)";
            }
        }
        if (s.Suite.RevitExe.Length == 0) s.EnvError ??= "suite.yaml thiếu revit_exe";
        if (s.Suite.Model.Length == 0) s.EnvError ??= "suite.yaml thiếu model";
        env["dataset_hash"] = s.EnvError?.StartsWith("suite") == true ? "lệch" : "ok";
        s.Info["env_check"] = env;
        if (s.EnvError is not null) Log(s, "ENV: " + s.EnvError);
        WriteInfo(s);
    }

    private static List<string> DuplicateAddinIds(string version)
    {
        var dirs = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Autodesk", "Revit", "Addins", version),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Autodesk", "Revit", "Addins", version),
        };
        var ids = new List<(string Id, string File)>();
        foreach (var d in dirs.Where(Directory.Exists))
            foreach (var f in Directory.GetFiles(d, "*.addin"))
            {
                try
                {
                    var x = XDocument.Load(f);
                    ids.AddRange(x.Descendants().Where(e => e.Name.LocalName is "AddInId" or "ClientId").Select(e => (e.Value.Trim().ToLowerInvariant(), Path.GetFileName(f))));
                }
                catch { ids.Add(("(đọc lỗi)", Path.GetFileName(f))); }
            }
        return ids.GroupBy(i => i.Id).Where(g => g.Count() > 1 && g.Key != "(đọc lỗi)")
            .Select(g => $"{g.Key} ở {string.Join(", ", g.Select(x => x.File).Distinct())}").ToList();
    }

    private Task BuildAddin(RunState s)
    {
        if (cfg.BuildCommand.Length == 0) { Log(s, "không khai fix_loop.build — bỏ qua build"); return Task.CompletedTask; }
        var started = DateTime.Now;
        var shellArgs = new[] { "-NoProfile", "-Command", cfg.BuildCommand };
        var r = proc.Run(Ps.Exe, shellArgs, ctx.Root, TimeSpan.FromMinutes(30));
        if (r.ExitCode != 0) { s.EnvError = $"build lỗi (exit {r.ExitCode}): {Text.Clip(string.Join(" | ", r.Lines.TakeLast(3)), 200)}"; return Task.CompletedTask; }
        if (cfg.AddinDll.Length > 0)
        {
            var dll = ctx.Full(cfg.AddinDll);
            if (!File.Exists(dll) || File.GetLastWriteTime(dll) < started.AddSeconds(-5)) s.EnvError = $"{cfg.AddinDll} không mới hơn lúc build (Revit đang mở khoá DLL?)";
        }
        return Task.CompletedTask;
    }

    // ---- bước 2–5 ----------------------------------------------------------------------------------------

    private async Task<List<int>> ListPids(IToolClient gw)
    {
        var r = await gw.Call(cfg.ToolListInstances, new JsonObject(), TimeSpan.FromSeconds(30));
        return r.Ok ? JsonScan.Pids(r.Json, r.Text) : new List<int>();
    }

    private async Task StartRevit(RunState s, IToolClient gw, List<int> before, CancellationToken ct)
    {
        var procPid = host.Start(s.Suite.RevitExe);
        Log(s, $"Revit pid {procPid}: {s.Suite.RevitExe}");
        var deadline = DateTime.Now.AddMinutes(s.Suite.StartupMin);
        while (DateTime.Now < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(Poll), ct);
            var now = await ListPids(gw);
            var fresh = now.Except(before).ToList();
            if (fresh.Contains(procPid) || fresh.Count == 1) { s.Pid = fresh.Contains(procPid) ? procPid : fresh[0]; s.Info["pid"] = s.Pid; return; }
            var dialogs = host.DialogTitles(procPid);
            if (dialogs.Count > 0) { s.EnvError = $"Revit kẹt hộp thoại khởi động: {string.Join(" | ", dialogs)}"; s.Pid = procPid; return; }
            if (!host.IsAlive(procPid)) { s.EnvError = "Revit thoát khi khởi động"; return; }
        }
        s.Pid = procPid;
        s.EnvError = $"Revit không sẵn sàng sau {s.Suite.StartupMin} phút (pipe MCP không trả lời — có thể kẹt hộp thoại)";
    }

    /// <summary>Giây giữa hai lần hỏi list_instances (test đặt 0).</summary>
    public double Poll { get; init; } = 10;

    private async Task OpenCopy(RunState s, IToolClient gw)
    {
        var model = _paths.Resolve(s.Suite.Dir, s.Suite.Model, s.Suite.Dataset.Length > 0 ? s.Suite.Dataset : null);
        if (!File.Exists(model)) { s.PipelineError = $"không thấy model {model}"; return; }
        s.ModelHashBefore = Text.Sha256File(model);
        s.ModelCopy = Path.Combine(s.RunDir, Path.GetFileName(model));
        File.Copy(model, s.ModelCopy);
        if (cfg.ToolListTools.Length > 0)
        {
            var tools = await gw.Call(cfg.ToolListTools, new JsonObject { ["pid"] = s.Pid }, TimeSpan.FromSeconds(60));
            s.CloseSchema = JsonScan.Objects(tools.Json).FirstOrDefault(o => JsonScan.Str(o, "name") == cfg.ToolClose)?["inputSchema"] as JsonObject;
        }

        var open = await CallInRevit(gw, s, cfg.ToolOpen, new JsonObject { ["path"] = s.ModelCopy }, TimeSpan.FromMinutes(s.Suite.OpenModelMin));
        if (!open.Ok) { s.PipelineError = "mở bản sao lỗi: " + Text.Clip(open.Text, 200); return; }
        var info = await CallInRevit(gw, s, cfg.ToolDocInfo, new JsonObject(), TimeSpan.FromSeconds(60));
        var pathName = JsonScan.Objects(info.Json).Select(o => JsonScan.Str(o, "pathName", "PathName", "path")).FirstOrDefault(p => p is not null) ?? "";
        if (!Policy.IsInside(pathName, s.RunDir)) s.PipelineError = $"document đang mở '{pathName}' không nằm trong RUN_DIR — dừng";
    }

    private Task<ToolCall> CallInRevit(IToolClient gw, RunState s, string tool, JsonObject args, TimeSpan timeout) =>
        gw.Call(cfg.ToolCall, new JsonObject { ["tool_name"] = tool, ["args"] = args, ["pid"] = s.Pid }, timeout);

    private async Task RunSteps(RunState s, IToolClient gw)
    {
        var policy = new Policy(ctx.TestBlock);
        s.LogOffset = File.Exists(cfg.LogFile) ? new FileInfo(cfg.LogFile).Length : 0;
        s.StepsStart = DateTime.Now;
        foreach (var step in s.Suite.Steps)
        {
            var args = s.Suite.ResolveArgs(step, _paths);
            foreach (var v in JsonScan.Objects(args).SelectMany(o => o.Select(kv => kv.Value)).OfType<JsonValue>())
                if (v.TryGetValue<string>(out var str) && File.Exists(str) && !s.ConfigFiles.Contains(str)) s.ConfigFiles.Add(str);
            var decision = policy.Check(step, args, s.Suite.AllowDraw);
            if (!decision.Allowed) { s.PipelineError = "CHẶN: " + decision.Reason; Log(s, s.PipelineError); break; }
            // mọi step chỉ chạy trên bản sao trong RUN_DIR (step trước có thể đã mở / đổi document)
            var pathName = await ActiveDocument(gw, s);
            if (!Policy.IsInside(pathName, s.RunDir)) { s.PipelineError = $"trước step {step.Index}: document đang mở '{pathName}' không nằm trong RUN_DIR — dừng"; Log(s, s.PipelineError); break; }
            if (step.IsSetup) Log(s, $"setup {step.Tool} trên {pathName} — {step.Why}");
            var t0 = DateTime.Now;
            var r = await CallInRevit(gw, s, step.Tool, args, TimeSpan.FromMinutes(s.Suite.StepMin));
            SaveStep(s, step, r);
            Log(s, $"step {step.Index} {step.Tool} {Suite.Compact(args)} → {(r.Ok ? "OK" : "LỖI")} {(DateTime.Now - t0).TotalSeconds:0} s {Text.Clip(r.Text.Replace('\n', ' '), 300)}");
            if (!r.Ok)
            {
                s.ToolErrors.Add($"step {step.Index} {step.Tool}: {Text.Clip(r.Text, 300)}");
                if (!step.OnError.Equals("continue", StringComparison.OrdinalIgnoreCase)) { s.PipelineError = $"step {step.Index} {step.Tool} lỗi"; break; }
            }
        }
        if (s.PipelineError is null && s.Suite.Steps.Count > 0)
        {
            var after = await ActiveDocument(gw, s);
            if (!Policy.IsInside(after, s.RunDir)) { s.PipelineError = $"sau step cuối: document đang mở '{after}' không nằm trong RUN_DIR"; Log(s, s.PipelineError); }
        }
        s.StepsEnd = DateTime.Now;
    }

    private async Task<string> ActiveDocument(IToolClient gw, RunState s)
    {
        var info = await CallInRevit(gw, s, cfg.ToolDocInfo, new JsonObject(), TimeSpan.FromSeconds(60));
        return JsonScan.Objects(info.Json).Select(o => JsonScan.Str(o, "pathName", "PathName", "path")).FirstOrDefault(p => p is not null) ?? "";
    }

    /// <summary>Kết quả tool của từng step → data\steps\NN-&lt;tool&gt;.json (JSON của tool; không parse được thì { ok, text }) để luật chấm và agent xem.</summary>
    internal static string StepFile(SuiteStep step) => $"{step.Index:00}-{Regex.Replace(step.Tool, @"[^\w.-]", "_")}.json";

    private static void SaveStep(RunState s, SuiteStep step, ToolCall r)
    {
        var dir = Path.Combine(s.Data, "steps");
        Directory.CreateDirectory(dir);
        var node = r.Ok && r.Json is not null ? r.Json.DeepClone() : new JsonObject { ["ok"] = r.Ok, ["text"] = r.Text, ["json"] = r.Json?.DeepClone() };
        File.WriteAllText(Path.Combine(dir, StepFile(step)), node.ToJsonString(JsonOut));
    }

    // ---- bước 6 ------------------------------------------------------------------------------------------

    private async Task Capture(RunState s, CancellationToken ct)
    {
        // log add-in trong khoảng chạy kịch bản
        var addinLog = Path.Combine(s.Data, "addin.log");
        if (File.Exists(cfg.LogFile) && s.LogOffset >= 0)
        {
            using var fs = new FileStream(cfg.LogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            fs.Seek(Math.Min(s.LogOffset, fs.Length), SeekOrigin.Begin);
            using var rd = new StreamReader(fs, Encoding.UTF8);
            File.WriteAllText(addinLog, await rd.ReadToEndAsync(ct));
        }
        else s.Notes.Add(File.Exists(cfg.LogFile) ? "không cắt được log (chưa tới bước chạy kịch bản)" : $"không thấy log add-in {cfg.LogFile}");

        if (s.Suite.NoCapture)
        {
            File.WriteAllText(Path.Combine(s.Data, "step_status.json"), new JsonObject
            {
                ["externalRunId"] = "", ["source"] = "capture: none", ["ok"] = false,
                ["message"] = "suite khai capture: none — không đọc tracking / điểm kiểm", ["steps"] = new JsonArray(),
            }.ToJsonString(JsonOut));
            s.Info["externalRunId"] = "";
            s.DataSource = "data\\steps";
            s.Notes.Add("Suite khai capture: none — không đọc tracking / điểm kiểm, không đối chiếu log; luật chấm trên data\\steps\\NN-<tool>.json.");
            return;
        }

        if (cfg.RunIdPattern.Length > 0 && File.Exists(addinLog))
        {
            var rx = new Regex(cfg.RunIdPattern);
            foreach (var l in File.ReadLines(addinLog)) { var m = rx.Match(l); if (m.Success) s.ExternalRunId = m.Groups.Count > 1 ? m.Groups[1].Value : m.Value; }
        }
        s.Info["externalRunId"] = s.ExternalRunId;

        var statusJson = new JsonObject { ["externalRunId"] = s.ExternalRunId, ["source"] = cfg.StepStatusTool, ["ok"] = false, ["message"] = "", ["steps"] = new JsonArray() };
        if (cfg.CaptureSource != "bim-tracking") { s.Notes.Add($"capture.source={cfg.CaptureSource}: runner chưa hỗ trợ — agent thu tay"); }
        else if (s.ExternalRunId.Length == 0) { statusJson["message"] = "không tìm thấy externalRunId trong log add-in"; s.Notes.Add("add-in không ghi tracking / không thấy id run trong log (GEN-LOG-002)"); }
        else
        {
            await using var tr = await connect(cfg.TrackingMcp);
            var deadline = DateTime.Now.AddMinutes(3);
            List<(string Step, string Status)> steps = new();
            ToolCall r;
            do
            {
                r = await tr.Call(cfg.StepStatusTool, new JsonObject { ["externalRunId"] = s.ExternalRunId }, TimeSpan.FromSeconds(60));
                steps = JsonScan.Steps(r.Json);
                var done = cfg.CaptureStep.Length == 0 || steps.Any(x => x.Step == cfg.CaptureStep && x.Status == "SUCCEEDED");
                var failed = steps.Any(x => x.Status is "FAILED" or "TIMED_OUT" or "CANCELLED");
                if (done || failed || !r.Ok) break;
                await Task.Delay(TimeSpan.FromSeconds(Poll), ct);
            } while (DateTime.Now < deadline);
            statusJson["ok"] = r.Ok && steps.Count > 0;
            statusJson["message"] = r.Ok ? "" : "nguồn đọc tracking không khả dụng: " + Text.Clip(r.Text, 200);
            statusJson["steps"] = new JsonArray(steps.Select(x => (JsonNode)new JsonObject { ["stepCode"] = x.Step, ["status"] = x.Status }).ToArray());

            // output chung + từng điểm kiểm
            foreach (var o in cfg.Outputs)
            {
                var arts = await Outputs(tr, s.ExternalRunId, null);
                var hit = arts.Where(a => a.Role == o.Role && (o.NamePrefix.Length == 0 || a.Name.StartsWith(o.NamePrefix))).ToList();
                if (hit.Count == 0) { s.Notes.Add($"không có output role {o.Role}"); continue; }
                var target = Path.Combine(s.Data, o.SaveAs.Length > 0 ? o.SaveAs : o.Role);
                if (hit.Count == 1 && Path.HasExtension(target)) await Download(hit[0], target, s);
                else foreach (var a in hit) await Download(a, Path.Combine(target, a.Name.Length > 0 ? a.Name : a.Role), s);
            }
            var checkpoints = s.Suite.Checkpoints.Count > 0 ? s.Suite.Checkpoints : cfg.Checkpoints;
            foreach (var cp in checkpoints)
            {
                var dir = Path.Combine(s.Data, "checkpoints", cp.Step);
                Directory.CreateDirectory(dir);
                var arts = await Outputs(tr, s.ExternalRunId, cp.Step);
                var list = new JsonArray();
                foreach (var a in arts)
                {
                    var kind = a.Inline is not null && a.Url is null && a.Path is null ? "metadata"
                        : Regex.IsMatch(a.Name, @"\.(png|jpe?g|bmp|gif)$", RegexOptions.IgnoreCase) ? "image" : "file";
                    var name = a.Name.Length > 0 ? a.Name : $"{a.Role}.json";
                    if (kind == "metadata" && !name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) name += ".json";
                    var ok = await Download(a, Path.Combine(dir, name), s);
                    list.Add(new JsonObject { ["role"] = a.Role, ["kind"] = kind, ["name"] = a.Name, ["file"] = ok ? name : null });
                }
                File.WriteAllText(Path.Combine(dir, "outputs.json"), list.ToJsonString(JsonOut));
            }
            s.DataSource = "run_outputs";
        }
        File.WriteAllText(Path.Combine(s.Data, "step_status.json"), statusJson.ToJsonString(JsonOut));
        LocalFallback(s);
    }

    private async Task<List<JsonScan.Artifact>> Outputs(IToolClient tr, string runId, string? step)
    {
        var all = new List<JsonScan.Artifact>();
        for (var page = 1; page <= 20; page++)
        {
            var args = new JsonObject { ["externalRunId"] = runId, ["Page"] = page };
            if (step is not null) args["StepCode"] = step;
            var r = await tr.Call(cfg.OutputsTool, args, TimeSpan.FromSeconds(120));
            if (!r.Ok) break;
            all.AddRange(JsonScan.Artifacts(r.Json));
            if (!JsonScan.HasMore(r.Json, page)) break;
        }
        return all;
    }

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    private static async Task<bool> Download(JsonScan.Artifact a, string target, RunState s)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        try
        {
            if (a.Path is { } p && File.Exists(p)) { File.Copy(p, target, true); return true; }
            if (a.Url is { } u && Uri.TryCreate(u, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            {
                await File.WriteAllBytesAsync(target, await Http.GetByteArrayAsync(uri));
                return true;
            }
            if (a.Inline is not null) { File.WriteAllText(target, a.Inline.ToJsonString(JsonOut)); return true; }
        }
        catch (Exception ex) { s.Notes.Add($"không tải được {a.Role}/{a.Name}: {Text.Clip(ex.Message, 120)}"); }
        return false;
    }

    private void LocalFallback(RunState s)
    {
        if (cfg.LocalFallback.Length == 0 || s.StepsStart == default) return;
        var dir = Regex.Replace(Environment.ExpandEnvironmentVariables(cfg.LocalFallback), @"\{date:([^}]+)\}", m => s.StepsStart.ToString(m.Groups[1].Value));
        if (!Directory.Exists(dir)) return;
        var copied = 0;
        foreach (var f in Directory.GetFiles(dir))
        {
            var t = File.GetLastWriteTime(f);
            if (t < s.StepsStart.AddMinutes(-1) || t > s.StepsEnd.AddMinutes(5)) continue;
            var target = Path.Combine(s.Data, "local_fallback", Path.GetFileName(f));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (!File.Exists(target)) { File.Copy(f, target); copied++; }
        }
        if (copied > 0) { s.Notes.Add($"lấy {copied} file từ local_fallback {dir}"); if (s.DataSource.Length == 0) s.DataSource = "local_fallback"; }
    }

    // ---- bước 10 -----------------------------------------------------------------------------------------

    private async Task Cleanup(RunState s, IToolClient? gw)
    {
        try
        {
            if (gw is not null && s.Pid > 0 && s.ModelCopy.Length > 0)
            {
                var args = new JsonObject();
                if (s.CloseSchema?["properties"] is JsonObject props)
                    foreach (var (k, _) in props)
                    {
                        var f = Text.Fold(k);
                        if (f.Contains("save")) args[k] = false;
                        if (f.Contains("discard")) args[k] = true;
                    }
                var r = await CallInRevit(gw, s, cfg.ToolClose, args, TimeSpan.FromSeconds(60));
                Log(s, $"10 đóng document (không lưu): {(r.Ok ? "OK" : "lỗi bỏ qua — " + Text.Clip(r.Text, 120))}");
            }
        }
        catch (Exception ex) { Log(s, "10 đóng document lỗi: " + ex.Message); }
        if (s.Pid > 0 && host.IsAlive(s.Pid)) { host.Kill(s.Pid); Log(s, $"10 kill pid {s.Pid}"); }
        if (s.ModelHashBefore.Length > 0)
        {
            var model = _paths.Resolve(s.Suite.Dir, s.Suite.Model, s.Suite.Dataset.Length > 0 ? s.Suite.Dataset : null);
            if (Text.Sha256File(model) != s.ModelHashBefore) { s.PipelineError = "file gốc bị thay đổi — " + (s.PipelineError ?? ""); s.Notes.Insert(0, "ERROR: file model gốc bị thay đổi"); }
        }
        if (!s.Suite.KeepModelCopy && s.ModelCopy.Length > 0 && File.Exists(s.ModelCopy))
        {
            try { File.Delete(s.ModelCopy); } catch (IOException) { s.Notes.Add("không xoá được bản sao model (Revit còn giữ file?)"); }
        }
    }

    // ---- bước 6a, 6b -------------------------------------------------------------------------------------

    private void Reconcile(RunState s)
    {
        if (s.Suite.NoCapture) return;   // không có tracking / điểm kiểm để đối chiếu (ghi chú ở bước 6)
        var script = ctx.BaseTool("host-test-reconcile.ps1");
        if (!File.Exists(script)) { s.Notes.Add("không có host-test-reconcile.ps1"); return; }
        var r = Ps.RunFile(proc, script, new[] { "-RunDir", s.RunDir, "-Suite", Path.Combine(s.Suite.Dir, "suite.yaml"), "-Config", Path.Combine(ctx.Root, ProjectContext.ConfigFile), "-Block", ctx.TestBlockName }, ctx.Root, TimeSpan.FromMinutes(5));
        if (r.ExitCode == 2) s.Notes.Add("reconcile: đầu vào sai — " + Text.Clip(r.Lines.FirstOrDefault() ?? "", 160));
    }

    private void Diagnose(RunState s, List<string>? changedFiles)
    {
        var rec = ReadJson(Path.Combine(s.Data, "reconcile.json"));
        var first = rec?["log"]?["firstError"];
        var hasProblem = s.EnvError is not null || s.PipelineError is not null || s.ToolErrors.Count > 0 || first is not null;
        if (!hasProblem) return;
        string cls, evidence;
        int? line = first?["line"]?.GetValue<int>();
        var text = first?["text"]?.ToString() ?? "";
        if (s.EnvError is not null) { cls = "ENV"; evidence = s.EnvError; }
        else
        {
            evidence = first is not null ? $"addin.log:{line} {Text.Clip(text, 200)}" : string.Join("; ", s.ToolErrors.Take(2)).Length > 0 ? string.Join("; ", s.ToolErrors.Take(2)) : s.PipelineError ?? "";
            cls = Classify(text + " " + string.Join(" ", s.ToolErrors) + " " + s.PipelineError);
        }
        var frames = StackFiles(Path.Combine(s.Data, "addin.log"), line);
        var inDiff = changedFiles is not null ? InDiff(frames, changedFiles) : null;
        var d = new JsonObject
        {
            ["class"] = cls, ["evidence"] = evidence, ["first_error_line"] = line, ["ki"] = "",
            ["in_task_diff"] = inDiff, ["in_task_diff_machine"] = inDiff, ["stack_files"] = new JsonArray(frames.Select(f => (JsonNode)f).ToArray()),
            ["suggested_by"] = "hawee-flow (máy gợi ý — agent xác nhận / sửa bằng diagnosis_set)",
        };
        File.WriteAllText(Path.Combine(s.Data, "diagnosis.json"), d.ToJsonString(JsonOut));
    }

    internal static string Classify(string t)
    {
        var f = Text.Fold(t);
        if (Regex.IsMatch(f, @"timeout|time out|license|dialog|hop thoai|target_not_ready|\bbusy\b|out of memory")) return "ENV";
        if (Regex.IsMatch(f, @"tracking|http\s*[45]\d\d|status code [45]\d\d|\b[45]\d\d\b.*(api|tracking)")) return "TRACKING";
        if (Regex.IsMatch(f, @"validation|khong hop le|invalid input|not found in csv|file ?not ?found|missing column|thieu cot|sai dinh dang|format")) return "INPUT";
        if (Regex.IsMatch(f, @"exception|null ?reference|invalidoperation|argument|index ?out ?of ?range|keynotfound")) return "CODE";
        return "UNKNOWN";
    }

    /// <summary>Có file nào trong <paramref name="files"/> (đuôi đường dẫn hoặc tên file) nằm trong diff của task; không có file nào để so → null.</summary>
    internal static bool? InDiff(IReadOnlyCollection<string> files, IReadOnlyCollection<string> changedFiles)
    {
        if (files.Count == 0) return null;
        return files.Any(f => changedFiles.Any(c => c.EndsWith(f, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(c).Equals(f, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Tên file .cs trong stack trace quanh dòng lỗi đầu tiên ("in X:\...\File.cs:line N" / "File.cs:123").</summary>
    internal static List<string> StackFiles(string log, int? line)
    {
        if (!File.Exists(log) || line is null) return new();
        var lines = File.ReadAllLines(log);
        var from = Math.Max(0, line.Value - 1);
        return lines.Skip(from).Take(40)
            .SelectMany(l => Regex.Matches(l, @"([\w.\-\\/]+\.cs)(?::line \d+|:\d+)").Select(m => m.Groups[1].Value.Replace('\\', '/')))
            .Select(p => p.Contains('/') ? string.Join('/', p.Split('/').TakeLast(3)) : p)
            .Distinct().Take(10).ToList();
    }

    // ---- bước 7 ------------------------------------------------------------------------------------------

    public sealed record GradedRule(TestRule Rule, string Group, string Verdict, string Expected, string Actual, string Evidence, string Reason, string ReusedFrom, List<string> Snapshots);

    private List<GradedRule> Grade(RunState s)
    {
        var started = DateTime.Now;
        var data = new GradingData(s.RunDir, s.Suite.Dir, s.ConfigFiles);
        var rec = ReadJson(Path.Combine(s.Data, "reconcile.json"));
        var missingCp = (rec?["findings"] as JsonArray)?.Select(f => f?["code"]?.ToString() is "CHECKPOINT_MISSING" or "CHECKPOINT_NOT_RUN" ? f?["step"]?.ToString() : null)
            .Where(x => x is not null).ToHashSet() ?? new HashSet<string?>();
        var prevAgent = PreviousAgentVerdicts(s);
        var keys = new JsonObject();
        var result = new List<GradedRule>();
        foreach (var rule in s.Rules.Active)
        {
            var sheets = SheetsOf(rule.AppliesTo);
            var g = new Grader(data) { DefaultTol = cfg.DefaultTolMm }.Grade(rule, sheets);
            var key = GradingKey(rule, g.FilesRead, s.Data, g.Verdict);
            keys[rule.Id] = key;
            // luật cần agent (kể cả khi chỉ một phép là AGENT) → một dòng cho agent chấm cả luật
            if (g.Groups.Count == 0 || g.Verdict == Verdicts.Agent)
            {
                var v = g.Verdict;
                var reason = g.Reason;
                if (v == Verdicts.NotMeasurable && s.PipelineError is not null && reason.StartsWith("thiếu dữ liệu")) { v = Verdicts.Error; reason = "pipeline lỗi — " + reason; }
                if (missingCp.Any(cp => cp is not null && rule.Block.Contains(cp)) && v == Verdicts.Pass) { v = Verdicts.NotMeasurable; reason = "điểm kiểm thiếu (reconcile)"; }
                var reused = "";
                if (v == Verdicts.Agent && prevAgent.TryGetValue(rule.Id + "|*", out var pa) && pa.Key == key) { v = pa.Verdict; reason = pa.Note; reused = pa.Run; }
                result.Add(new GradedRule(rule, "*", v, rule.Expect, "", g.Reason.Length > 0 ? g.Reason : "", reason, reused, new()));
            }
            else
                foreach (var gr in g.Groups)
                {
                    var v = gr.Verdict;
                    if (v == Verdicts.NotMeasurable && s.PipelineError is not null) v = Verdicts.Error;
                    result.Add(new GradedRule(rule, gr.Group, v, gr.Expected, gr.Actual, gr.Evidence, "", "", new()));
                }
        }
        Directory.CreateDirectory(Path.Combine(s.Data, "grading"));
        File.WriteAllText(Path.Combine(s.Data, "grading", "keys.json"), keys.ToJsonString(JsonOut));
        s.Info["grading"] = new JsonObject { ["started"] = started.ToString("s"), ["ended"] = DateTime.Now.ToString("s"), ["engine"] = "hawee-flow Grader", ["reused"] = result.Count(r => r.ReusedFrom.Length > 0) };
        WriteInfo(s);
        return result;
    }

    internal static List<string> SheetsOf(string appliesTo)
    {
        var f = Text.Fold(appliesTo);
        if (f.Length == 0 || f.Contains("moi sheet") || f.Contains("tat ca")) return new();
        return appliesTo.Split(new[] { ',', ';' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(x => Regex.Replace(x, @"\s*\(.*\)$", "")).Where(x => x.Length > 0).ToList();
    }

    private Dictionary<string, (string Verdict, string Note, string Key, string Run)> PreviousAgentVerdicts(RunState s)
    {
        var res = new Dictionary<string, (string, string, string, string)>();
        var runs = Path.GetDirectoryName(s.RunDir)!;
        var prev = Directory.GetDirectories(runs).Where(d => string.CompareOrdinal(Path.GetFileName(d), Path.GetFileName(s.RunDir)) < 0)
            .OrderByDescending(d => d).FirstOrDefault(d => File.Exists(Path.Combine(d, "data", "grading", "agent.json")));
        if (prev is null) return res;
        if (ReadJson(Path.Combine(prev, "data", "grading", "agent.json")) is JsonObject o)
            foreach (var (k, v) in o)
                res[k] = (v?["verdict"]?.ToString() ?? "", v?["note"]?.ToString() ?? "", v?["key"]?.ToString() ?? "", Path.GetFileName(prev));
        return res;
    }

    // ---- bước 7b, 8 -------------------------------------------------------------------------------------

    /// <summary>Ảnh minh chứng cho một luật: ảnh của nhóm (sheet) có tên khớp từ trong cột Evidence, không có thì mọi ảnh của nhóm (FINAL_ trước).</summary>
    internal static List<string> PickImages(IEnumerable<string> images, TestRule rule, string group)
    {
        var pool = images.Where(i => group == "*" || Path.GetFileName(i).Contains(group, StringComparison.OrdinalIgnoreCase)).ToList();
        var tokens = Regex.Matches(rule.Evidence, @"[A-Za-z_][A-Za-z0-9_]{3,}").Select(m => m.Value).ToList();
        var pick = pool.Where(i => tokens.Any(t => i.Contains(t, StringComparison.OrdinalIgnoreCase))).ToList();
        return pick.Count > 0 ? pick : pool.OrderByDescending(i => Path.GetFileName(i).StartsWith("FINAL_", StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>
    /// Khoá dùng lại verdict của agent: luật + nội dung file máy đã đọc + nội dung ảnh agent sẽ nhìn.
    /// Luật cần mắt (ảnh) không đọc file nào khi chấm → phải băm ảnh, nếu không verdict cũ bị dùng lại sau khi code đổi.
    /// Không có ảnh khớp → băm mọi file trong data\ (trừ grading\): đổi gì cũng chấm lại.
    /// </summary>
    internal static string GradingKey(TestRule rule, IEnumerable<string> filesRead, string dataDir, string verdict)
    {
        var parts = new List<string> { rule.Block };
        parts.AddRange(filesRead.Select(Text.Sha256File));
        if (verdict == Verdicts.Agent && Directory.Exists(dataDir))
        {
            var all = Directory.EnumerateFiles(dataDir, "*.*", SearchOption.AllDirectories)
                .Where(f => !Path.GetRelativePath(dataDir, f).Replace('\\', '/').StartsWith("grading/", StringComparison.Ordinal)).ToList();
            var imgs = PickImages(all.Where(f => Regex.IsMatch(f, @"\.(png|jpe?g)$", RegexOptions.IgnoreCase)), rule, "*");
            var files = imgs.Count > 0 ? imgs : all;
            parts.AddRange(files.Select(Text.Sha256File).OrderBy(h => h, StringComparer.Ordinal));
        }
        return Text.Sha256(string.Join("|", parts));
    }

    private static void Evidence(RunState s, List<GradedRule> graded)
    {
        var images = Directory.Exists(s.Data)
            ? Directory.EnumerateFiles(s.Data, "*.*", SearchOption.AllDirectories).Where(f => Regex.IsMatch(f, @"\.(png|jpe?g)$", RegexOptions.IgnoreCase)).ToList()
            : new List<string>();
        var index = new StringBuilder();
        index.AppendLine($"# Evidence — {s.Suite.Name} · run {Path.GetFileName(s.RunDir)}");
        index.AppendLine();
        index.AppendLine($"Commit {s.Info["commit"]} · nhánh {s.Info["branch"]} · Revit {s.Suite.RevitVersion} · externalRunId {(s.ExternalRunId.Length > 0 ? s.ExternalRunId : "không có")} · nguồn ảnh {(s.DataSource.Length > 0 ? s.DataSource : "—")}");
        index.AppendLine();
        index.AppendLine("| Tiêu chí | Sheet | Kỳ vọng | Thực tế | Verdict | Ảnh |").AppendLine("|---|---|---|---|---|---|");
        foreach (var g in graded)
        {
            var spec = Text.Fold(g.Rule.Evidence);
            if (spec.StartsWith("khong"))
            {
                index.AppendLine($"| {g.Rule.Id} | {g.Group} | {Text.Cell(Text.Clip(g.Expected, 60))} | {Text.Cell(Text.Clip(g.Actual, 60))} | {g.Verdict} | — |");
                continue;
            }
            var pick = PickImages(images, g.Rule, g.Group);
            var dir = Path.Combine(s.RunDir, "evidence", g.Rule.Id);
            foreach (var img in pick.Take(3))
            {
                Directory.CreateDirectory(dir);
                var name = $"{(g.Group == "*" ? "chung" : g.Group)}__{Path.GetFileName(img)}";
                var target = Path.Combine(dir, name);
                if (!File.Exists(target)) File.Copy(img, target);
                g.Snapshots.Add($"evidence/{g.Rule.Id}/{name}");
            }
            var cell = g.Snapshots.Count > 0 ? string.Join(" ", g.Snapshots.Select(p => $"[{Path.GetFileName(p)}]({p[9..]})")) : "THIẾU — chưa có output ảnh khớp";
            index.AppendLine($"| {g.Rule.Id} | {g.Group} | {Text.Cell(Text.Clip(g.Expected, 60))} | {Text.Cell(Text.Clip(g.Actual, 60))} | {g.Verdict} | {Text.Cell(cell)} |");
        }
        Text.Write(Path.Combine(s.RunDir, "evidence", "index.md"), index.ToString());
    }

    private void Report(RunState s, List<GradedRule> graded)
    {
        // result.json theo dạng test-summary.ps1 đọc: [{sheet, verdict, rules:[{id, ac, expected, actual, evidence, verdict, snapshots, reused_from}]}]
        var sheets = graded.GroupBy(g => g.Group == "*" ? "(chung)" : g.Group).OrderBy(g => g.Key, StringComparer.Ordinal).Select(sg =>
        {
            var rules = sg.Select(g => new JsonObject
            {
                ["id"] = g.Rule.Id, ["ac"] = g.Rule.Ac, ["expected"] = g.Expected, ["actual"] = g.Actual,
                ["evidence"] = g.Evidence.Length > 0 ? g.Evidence : g.Reason,
                ["verdict"] = g.Verdict == Verdicts.Agent ? Verdicts.Suspect : g.Verdict,
                ["pending_agent"] = g.Verdict == Verdicts.Agent,
                ["snapshots"] = new JsonArray(g.Snapshots.Select(x => (JsonNode)x).ToArray()),
                ["reused_from"] = g.ReusedFrom,
            }).ToList();
            var verdicts = rules.Select(r => r["verdict"]!.ToString()).ToList();
            var sv = s.EnvError is not null ? Verdicts.Error
                : verdicts.Contains(Verdicts.Error) ? Verdicts.Error
                : verdicts.Contains(Verdicts.Fail) ? Verdicts.Fail
                : verdicts.Any(v => v != Verdicts.Pass) || s.Suite.ManualSteps.Count > 0 ? Verdicts.Suspect
                : Verdicts.Pass;
            return new JsonObject { ["sheet"] = sg.Key, ["verdict"] = sv, ["rules"] = new JsonArray(rules.ToArray()) };
        }).ToList();
        // lỗi môi trường / pipeline: một dòng sheet ERROR riêng để test-summary thấy (kể cả khi luật vẫn chấm được trên dữ liệu đã thu)
        if (s.EnvError is not null || s.PipelineError is not null)
            sheets.Insert(0, new JsonObject { ["sheet"] = "(pipeline)", ["verdict"] = Verdicts.Error, ["error"] = s.EnvError ?? s.PipelineError, ["rules"] = new JsonArray() });
        if (sheets.Count == 0)
            sheets.Add(new JsonObject { ["sheet"] = "(chung)", ["verdict"] = Verdicts.Suspect, ["error"] = "không có luật nào", ["rules"] = new JsonArray() });
        File.WriteAllText(Path.Combine(s.RunDir, "result.json"), new JsonArray(sheets.ToArray()).ToJsonString(JsonOut));

        var count = sheets.GroupBy(x => x["verdict"]!.ToString()).ToDictionary(g => g.Key, g => g.Count());
        var rec = ReadJson(Path.Combine(s.Data, "reconcile.json"));
        var md = new StringBuilder();
        md.AppendLine($"# /revit-test {s.Suite.Name} — {s.Info["run"]}");
        md.AppendLine($"Kết quả: {count.GetValueOrDefault(Verdicts.Pass)} PASS · {count.GetValueOrDefault(Verdicts.Fail)} FAIL · {count.GetValueOrDefault(Verdicts.Suspect)} SUSPECT · {count.GetValueOrDefault(Verdicts.Error)} ERROR | Revit {s.Suite.RevitVersion}, pid {s.Pid}");
        md.AppendLine($"Run add-in: {(s.ExternalRunId.Length > 0 ? s.ExternalRunId : "không tìm thấy")} · nguồn dữ liệu: {(s.DataSource.Length > 0 ? s.DataSource : "—")} · chạy bởi hawee-flow runner");
        if (s.EnvError is not null) md.AppendLine($"\n**ERROR môi trường:** {s.EnvError} — không mở Revit / dừng.");
        if (s.PipelineError is not null) md.AppendLine($"\n**Pipeline:** {s.PipelineError}");
        md.AppendLine().AppendLine("## Cần người xem (FAIL / SUSPECT / ERROR / cần agent)");
        md.AppendLine("| Sheet | Verdict | Luật | Kỳ vọng | Thực tế | Bằng chứng |").AppendLine("|---|---|---|---|---|---|");
        foreach (var g in graded.Where(g => g.Verdict != Verdicts.Pass))
            md.AppendLine($"| {g.Group} | {(g.Verdict == Verdicts.Agent ? "CẦN AGENT" : g.Verdict)} | {g.Rule.Id} {Text.Cell(g.Rule.Name)} | {Text.Cell(Text.Clip(g.Expected, 80))} | {Text.Cell(Text.Clip(g.Actual, 80))} | {Text.Cell(Text.Clip(g.Evidence.Length > 0 ? g.Evidence : g.Reason, 160))} |");
        var stepsDir = Path.Combine(s.Data, "steps");
        if (Directory.Exists(stepsDir))
        {
            md.AppendLine().AppendLine("## Kết quả step (data\\steps)");
            md.AppendLine("| File | Nội dung (đầu) |").AppendLine("|---|---|");
            foreach (var f in Directory.EnumerateFiles(stepsDir).OrderBy(f => f, StringComparer.Ordinal))
                md.AppendLine($"| steps\\{Path.GetFileName(f)} | {Text.Cell(Text.Clip(Regex.Replace(File.ReadAllText(f), @"\s+", " "), 200))} |");
        }
        md.AppendLine().AppendLine("## Đối chiếu log ↔ tracking");
        md.AppendLine(rec is null ? (s.Suite.NoCapture ? "Bỏ qua — suite khai capture: none." : "Không có reconcile.json.") : $"Kết quả: {rec["result"]} · phát hiện: {string.Join(", ", (rec["findings"] as JsonArray ?? new JsonArray()).Select(f => f?["code"]?.ToString()))} — chi tiết reconcile.md");
        if (File.Exists(Path.Combine(s.Data, "diagnosis.json")))
        {
            var d = ReadJson(Path.Combine(s.Data, "diagnosis.json"));
            md.AppendLine().AppendLine("## Chẩn đoán (máy gợi ý)").AppendLine($"Lỗi gốc: {d?["class"]} — {d?["evidence"]} · in_task_diff: {d?["in_task_diff"]?.ToString() ?? "null"}");
        }
        md.AppendLine().AppendLine("## Kết quả theo AC");
        md.AppendLine("| AC | Luật (verdict) | Kết luận AC |").AppendLine("|---|---|---|");
        foreach (var ac in graded.SelectMany(g => g.Rule.AcIds.Select(a => (Ac: a, G: g))).GroupBy(x => x.Ac).OrderBy(x => x.Key))
        {
            var vs = ac.Select(x => x.G.Verdict).ToList();
            var concl = vs.Any(v => v is Verdicts.Fail or Verdicts.Error) ? "FAIL" : vs.All(v => v == Verdicts.Pass) ? "PASS" : "SUSPECT";
            md.AppendLine($"| {ac.Key} | {string.Join(", ", ac.Select(x => $"{x.G.Rule.Id} {x.G.Group} {x.G.Verdict}").Distinct().Take(6))} | {concl} |");
        }
        md.AppendLine().AppendLine("## Nhật ký bước").AppendLine("| Bước | Bắt đầu | Kết quả |").AppendLine("|---|---|---|");
        foreach (var j in s.Journal) md.AppendLine($"| {j.Step} | {j.Start} | {Text.Cell(j.Result)} |");
        if (s.Notes.Count > 0) { md.AppendLine().AppendLine("## Ghi chú / đề xuất (không tự làm)"); foreach (var n in s.Notes) md.AppendLine("- " + n); }
        if (!Directory.Exists(Path.Combine(s.Suite.Dir, "baseline")) || !Directory.EnumerateFiles(Path.Combine(s.Suite.Dir, "baseline")).Any())
            md.AppendLine("- Chưa có baseline → người xem ảnh/dữ liệu của run này; đúng thì chép runs\\<run>\\data\\* sang baseline\\.");
        Text.Write(Path.Combine(s.RunDir, "report.md"), md.ToString());
    }

    // ---- bước 8b: chấm chéo bằng rule-eval.ps1 của Base --------------------------------------------------

    /// <summary>
    /// Base 0.8 chạy rule-eval.ps1 "bóng" sau khi ghi result.json. Ở plugin verdict máy (C#) là chính thức, rule-eval.ps1 là máy thứ hai:
    /// luật hai máy chấm ngược nhau (PASS ↔ FAIL) → chuyển agent chấm, ghi lại result.json / report.md. Không bao giờ tự PASS.
    /// </summary>
    private void CrossCheck(RunState s, List<GradedRule> graded)
    {
        var info = new JsonObject { ["tool"] = "rule-eval.ps1" };
        if (s.Info["grading"] is JsonObject gi) gi["cross_check"] = info;
        try
        {
            var script = ctx.BaseTool("rule-eval.ps1");
            if (!File.Exists(script))
            {
                info["status"] = "chưa chạy máy chấm — Base chưa có core/tools/rule-eval.ps1";
                s.Notes.Add("Máy chấm Base: chưa chạy (không có core/tools/rule-eval.ps1) — verdict chỉ từ bộ chấm C#.");
                return;
            }
            File.Delete(Path.Combine(s.Data, "rule-eval.json"));
            var r = Ps.RunFile(proc, script, new[] { "-RunDir", s.RunDir, "-Rules", Path.Combine(s.Suite.Dir, "rules.md"), "-Baseline", Path.Combine(s.Suite.Dir, "baseline") }, ctx.Root, TimeSpan.FromMinutes(5));
            info["exit"] = r.ExitCode;
            var j = ReadJson(Path.Combine(s.Data, "rule-eval.json"));
            if (j?["results"] is not JsonArray results)
            {
                info["status"] = $"chưa chạy máy chấm — rule-eval lỗi (exit {r.ExitCode}): {Text.Clip(string.Join(" | ", r.Lines.TakeLast(2)), 160)}";
                s.Notes.Add("Máy chấm Base: " + info["status"]);
                return;
            }
            var lech = results.OfType<JsonObject>().Where(x => x["match"]?.ToString() == "LECH").ToList();
            info["status"] = "đã chạy";
            info["mismatch"] = lech.Count;
            info["ids"] = new JsonArray(lech.Select(x => (JsonNode)(x["id"]?.ToString() ?? "")).ToArray());
            if (lech.Count == 0) return;
            foreach (var x in lech)
            {
                var id = x["id"]?.ToString() ?? "";
                var detail = string.Join("; ", (x["clauses"] as JsonArray ?? new JsonArray()).Select(c => $"{c?["verdict"]}: {c?["detail"]}"));
                var why = $"C# {x["agent"]} ≠ rule-eval {x["verdict"]} ({Text.Clip(detail, 160)}) — agent chấm";
                for (var i = 0; i < graded.Count; i++)
                    if (graded[i].Rule.Id == id) graded[i] = graded[i] with { Verdict = Verdicts.Agent, Reason = why, Evidence = why };
                s.Notes.Add($"Máy chấm lệch {id}: {why}. Xem rule-eval.md; sai ở bộ chấm C# hay cú pháp luật → báo người duy trì plugin.");
            }
            Report(s, graded);
        }
        finally { WriteInfo(s); }
    }

    // ---- test-summary + tóm tắt cho agent ------------------------------------------------------------------

    public sealed record SummaryInfo(string Outcome, string Scope, string Reason, string Next, int FixesDone, int MaxFixes);

    public SummaryInfo Summary(string runDir, string autonomy)
    {
        var script = ctx.BaseTool("test-summary.ps1");
        if (!File.Exists(script)) return new SummaryInfo("?", "", "không có test-summary.ps1", "", 0, cfg.MaxFixes);
        var args = new List<string> { "-RunDir", runDir, "-MaxFixes", cfg.MaxFixes.ToString() };
        if (autonomy is "ask" or "night") { args.Add("-Autonomy"); args.Add(autonomy); }
        var r = Ps.RunFile(proc, script, args, ctx.Root, TimeSpan.FromMinutes(5));
        var j = ReadJson(Path.Combine(runDir, "data", "test-summary.json"));
        if (j is null) return new SummaryInfo("?", "", $"test-summary lỗi (exit {r.ExitCode}): {Text.Clip(string.Join(" | ", r.Lines.TakeLast(2)), 200)}", "", 0, cfg.MaxFixes);
        return new SummaryInfo(j["outcome"]?.ToString() ?? "?", j["scope"]?.ToString() ?? "", j["reason"]?.ToString() ?? "", j["next"]?.ToString() ?? "",
            int.TryParse(j["fixesDone"]?.ToString(), out var f) ? f : 0, cfg.MaxFixes);
    }

    private string Format(RunState s, List<GradedRule> graded, SummaryInfo sum)
    {
        var sb = new StringBuilder();
        var counts = graded.GroupBy(g => g.Verdict).ToDictionary(g => g.Key, g => g.Count());
        sb.AppendLine($"test_run {s.Suite.Name} · run {s.Info["run"]} · {s.Info["purpose"]} · phiên {s.Info["session"]} lượt {s.Info["loop"]} · test-summary: {sum.Outcome}{(sum.Scope.Length > 0 ? $" ({sum.Scope})" : "")} · sửa {sum.FixesDone}/{sum.MaxFixes}");
        if (s.EnvError is not null) sb.AppendLine("ENV: " + s.EnvError);
        if (s.PipelineError is not null) sb.AppendLine("Pipeline: " + s.PipelineError);
        var rec = ReadJson(Path.Combine(s.Data, "reconcile.json"));
        sb.AppendLine($"Revit pid {s.Pid} · {s.Suite.Steps.Count} step, lỗi tool {s.ToolErrors.Count} · reconcile {rec?["result"]?.ToString() ?? "—"}{(rec?["findings"] is JsonArray fa && fa.Count > 0 ? ": " + string.Join(", ", fa.Select(f => f?["code"]?.ToString()).Distinct()) : "")}");
        sb.AppendLine($"Luật×nhóm: {counts.GetValueOrDefault(Verdicts.Pass)} PASS · {counts.GetValueOrDefault(Verdicts.Fail)} FAIL · {counts.GetValueOrDefault(Verdicts.NotMeasurable)} NOT_MEASURABLE · {counts.GetValueOrDefault(Verdicts.Error)} ERROR · {counts.GetValueOrDefault(Verdicts.Agent)} cần agent");
        foreach (var g in graded.Where(g => g.Verdict is Verdicts.Fail or Verdicts.Error).Take(6))
            sb.AppendLine($"{g.Verdict} {g.Rule.Id} {g.Group}: kỳ vọng {Text.Clip(g.Expected, 50)} · thực tế {Text.Clip(g.Actual, 60)}{(g.Reason.Length > 0 ? " · " + Text.Clip(g.Reason, 80) : "")}");
        foreach (var g in graded.Where(g => g.Verdict == Verdicts.NotMeasurable).Take(3))
            sb.AppendLine($"NOT_MEASURABLE {g.Rule.Id} {g.Group}: {Text.Clip(g.Reason.Length > 0 ? g.Reason : g.Evidence, 120)}");
        if (s.Info["grading"]?["cross_check"] is JsonObject cc)
            sb.AppendLine(cc["status"]?.ToString() == "đã chạy"
                ? $"rule-eval: lệch {cc["mismatch"]}{(cc["ids"] is JsonArray ids && ids.Count > 0 ? " (" + string.Join(", ", ids) + ") → chuyển agent chấm" : "")}"
                : $"rule-eval: {cc["status"]}");
        var agent = graded.Where(g => g.Verdict == Verdicts.Agent).ToList();
        if (agent.Count > 0) sb.AppendLine($"Cần agent chấm: {string.Join(", ", agent.Select(a => a.Rule.Id).Distinct())} → get_detail suite={s.Suite.Name} kind=rule ref=<R-xx> (kết quả step: kind=steps), xem ảnh / dữ liệu, rồi record_verdict suite={s.Suite.Name}");
        if (File.Exists(Path.Combine(s.Data, "diagnosis.json")))
        {
            var d = ReadJson(Path.Combine(s.Data, "diagnosis.json"));
            sb.AppendLine($"Chẩn đoán máy gợi ý: {d?["class"]} · {Text.Clip(d?["evidence"]?.ToString() ?? "", 140)} · in_task_diff {d?["in_task_diff"]?.ToString() ?? "null"} → xác nhận/sửa bằng diagnosis_set trước khi sửa code");
        }
        else if (counts.GetValueOrDefault(Verdicts.Fail) > 0) sb.AppendLine("FAIL không kèm lỗi trong log → agent chẩn đoán (get_detail) rồi diagnosis_set.");
        if (sum.Reason.Length > 0) sb.AppendLine("Lý do: " + Text.Clip(sum.Reason, 200));
        if (sum.Next.Length > 0) sb.AppendLine("Tiếp: " + Text.Clip(sum.Next, 200));
        sb.Append($"Files: {Path.Combine(s.RunDir, "report.md")} · test-summary.md · evidence\\index.md");
        return sb.ToString();
    }

    // ---- tiện ích ----------------------------------------------------------------------------------------

    private static JsonNode? ReadJson(string path)
    {
        if (!File.Exists(path)) return null;
        try { return JsonNode.Parse(File.ReadAllText(path)); } catch (JsonException) { return null; }
    }

    private static void WriteInfo(RunState s) => File.WriteAllText(Path.Combine(s.RunDir, "run-info.json"), s.Info.ToJsonString(JsonOut));

    private static void Log(RunState s, string line) => s.Log.AppendLine($"{DateTime.Now:HH:mm:ss} {line}");

    private static void Step(RunState s, string name, Action a)
    {
        var start = DateTime.Now.ToString("HH:mm:ss");
        try { a(); s.Journal.Add((name, start, "xong")); }
        catch (Exception ex) { s.Journal.Add((name, start, "LỖI " + Text.Clip(ex.Message, 120))); Log(s, $"{name} lỗi: {ex.Message}"); }
    }

    private static async Task StepAsync(RunState s, string name, Func<Task> a)
    {
        var start = DateTime.Now.ToString("HH:mm:ss");
        var sw = Stopwatch.StartNew();
        await a();
        s.Journal.Add((name, start, (s.EnvError ?? s.PipelineError) is { } e ? "dừng: " + Text.Clip(e, 100) : $"xong {sw.Elapsed.TotalSeconds:0} s"));
    }

    internal static string SafeBase(Git git)
    {
        try { return git.IntegrationRef(null); } catch (FlowException) { return "HEAD"; }
    }
}
