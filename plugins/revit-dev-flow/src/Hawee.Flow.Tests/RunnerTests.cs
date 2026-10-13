using System.Text.Json.Nodes;
using Hawee.Flow.Core.Infra;
using Hawee.Flow.Core.Tasks;
using Hawee.Flow.Core.Testing;

namespace Hawee.Flow.Tests;

/// <summary>Gateway giả: Revit có pid sau khi Start, mở bản sao, step ghi log như add-in thật.</summary>
internal sealed class FakeGateway(FakeHost host, string logFile) : IToolClient
{
    public string OpenedPath = "";
    public List<string> Called { get; } = new();
    public Func<string, JsonObject, ToolCall?>? StepHook;

    public Task<ToolCall> Call(string tool, JsonObject args, TimeSpan timeout)
    {
        if (tool == "list_instances")
            return Task.FromResult(new ToolCall(true, "", new JsonArray(host.Alive.Select(p => (JsonNode)new JsonObject { ["pid"] = p }).ToArray())));
        if (tool == "list_tools") return Task.FromResult(new ToolCall(true, "", new JsonArray(new JsonObject { ["name"] = "close_document", ["inputSchema"] = new JsonObject { ["properties"] = new JsonObject { ["save"] = new JsonObject() } } })));
        if (tool != "call_tool") return Task.FromResult(new ToolCall(false, "tool lạ " + tool, null));
        var inner = args["tool_name"]!.ToString();
        var a = (JsonObject)args["args"]!;
        Called.Add(inner + " " + a.ToJsonString());
        switch (inner)
        {
            case "open_document": OpenedPath = a["path"]!.ToString(); return Ok("opened");
            case "document_info": return Task.FromResult(new ToolCall(true, "", new JsonObject { ["pathName"] = OpenedPath }));
            case "close_document": return Ok("closed");
        }
        if (StepHook?.Invoke(inner, a) is { } hooked) return Task.FromResult(hooked);
        File.AppendAllText(logFile, $"2026-10-12 09:00:01 [5] INFO [Step] CREATE_SHEETS start externalRunId=run-777\n2026-10-12 09:00:09 [5] INFO [Step] SAVE done\n");
        return Ok("ok");

        static Task<ToolCall> Ok(string t) => Task.FromResult(new ToolCall(true, t, null));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FakeTracking : IToolClient
{
    public Task<ToolCall> Call(string tool, JsonObject args, TimeSpan timeout)
    {
        if (tool == "run_timeline")
            return Task.FromResult(new ToolCall(true, "", JsonNode.Parse("""{ "steps": [ { "stepCode": "CREATE_SHEETS", "status": "SUCCEEDED" }, { "stepCode": "SAVE", "status": "SUCCEEDED" } ] }""")));
        if (tool == "run_outputs" && args["StepCode"]?.ToString() == "SAVE")
            return Task.FromResult(new ToolCall(true, "", JsonNode.Parse("""
                { "items": [ { "role": "SheetComponents", "name": "sheet_components.json",
                    "metadata": { "sheets": [ { "SheetNumber": "A-101", "components": [ { "type": "Viewport", "bbox": [0,0,10,10] } ] } ] } } ] }
                """)));
        return Task.FromResult(new ToolCall(true, "", new JsonObject { ["items"] = new JsonArray() }));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FakeHost : IHostProcess
{
    public List<int> Alive { get; } = new() { 100 };
    public List<string> Dialogs { get; } = new();
    public int Start(string exe) { Alive.Add(4242); return 4242; }
    public bool IsAlive(int pid) => Alive.Contains(pid);
    public void Kill(int pid) => Alive.Remove(pid);
    public IReadOnlyList<string> DialogTitles(int pid) => Dialogs;
}

/// <summary>Revit thoát ngay khi khởi động: PID không bao giờ sống, gateway không thấy instance mới.</summary>
internal sealed class DeadHost : IHostProcess
{
    public int Start(string exe) => 5151;
    public bool IsAlive(int pid) => false;
    public void Kill(int pid) { }
    public IReadOnlyList<string> DialogTitles(int pid) => Array.Empty<string>();
}

public class RunnerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("runner").FullName;
    private string Suites => Path.Combine(_root, "tasks");
    private string Datasets => Path.Combine(_root, "datasets");
    private string LogFile => Path.Combine(_root, "addin", "Hawee.log");

    public void Dispose() => Directory.Delete(_root, true);

    private TestRepo Repo(string extraBlock = "") => new($$"""
        revit_test:
          mcp: hawee
          suites_dir: '{{Suites}}'
          datasets_dir: '{{Datasets}}'
          tools: { list_instances: list_instances, list_tools: list_tools, call: call_tool, open_document: open_document, document_info: document_info, close_document: close_document }
          forbidden:
            - { tool: delete_sheets, when: "dry_run=false" }
          setup_tools:
            - { tool: delete_sheets, approved_by: Minh, date: 2026-10-09, why: "K10" }
          forbidden_unless_allow_draw: { tools: [draw_pipes], blocked_by: "" }
          capture:
            source: bim-tracking
            tracking_mcp: bim-tracking
            run_id: { log: '{{LogFile}}', pattern: 'externalRunId=(\S+)' }
            step: SAVE
            checkpoints:
              - { step: SAVE, expect: [ { role: SheetComponents, kind: metadata, min: 1 } ] }
          fix_loop: { max_runs: 5 }
        {{extraBlock}}
        """);

    private void Suite(string steps, string rules = "", string name = "1-main")
    {
        Text.Write(Path.Combine(Datasets, "bo", "model.rvt"), "RVT-GOC");
        Text.Write(Path.Combine(Datasets, "bo", "cfg.json"), "{ \"Keyplan\": { \"Anchor\": \"TopRight\" } }");
        Text.Write(LogFile, "2026-10-12 08:00:00 [1] INFO dòng cũ trước lần chạy\n");
        Text.Write(Path.Combine(Suites, name, "suite.yaml"), $"""
            suite: {name}
            dataset: bo
            revit_version: 2024
            revit_exe: '{Path.Combine(_root, "Revit.exe")}'
            model: '{"{dataset}"}\model.rvt'
            steps:
            {steps}
            """);
        Text.Write(Path.Combine(Suites, name, "rules.md"), rules.Length > 0 ? rules : """
            # Luật

            ### R-01 — Đủ viewport
            - AC: AC1
            - Kỳ vọng: theo spec (AC1): 1
            - Sai số: không
            - Máy chấm: đếm(sheet_components.json → sheets.components[type=Viewport]) theo SheetNumber = 1

            ### R-02 — Neo keyplan
            - AC: AC2
            - Kỳ vọng: theo cài đặt (cfg.json → Keyplan.Anchor): TopRight
            - Sai số: không
            - Máy chấm: bằng(cài đặt(cfg.json, Keyplan.Anchor), TopRight)

            ### R-03 — Label không đè
            - AC: AC3
            - Kỳ vọng: theo ảnh: không đè
            - Sai số: không
            - Máy chấm: ảnh(label không đè keyplan)
            """);
    }

    /// <summary>Script PS giả: suite-lint sạch; reconcile ghi reconcile.json; test-summary ghi kết luận theo result.json.</summary>
    private static void FakeScripts(TestRepo repo)
    {
        foreach (var s in new[] { "suite-lint.ps1", "host-test-reconcile.ps1", "test-summary.ps1" }) repo.Write("BIM_KB_Base/core/tools/" + s, "# giả");
        repo.Runner.Handler = (file, args) =>
        {
            if (file == "git") return new ProcResult(1, "", false);
            var script = Path.GetFileName(args.FirstOrDefault(a => a.EndsWith(".ps1")) ?? "");
            var runDir = args.SkipWhile(a => a != "-RunDir").Skip(1).FirstOrDefault();
            if (script == "host-test-reconcile.ps1" && runDir is not null)
            {
                var log = Path.Combine(runDir, "data", "addin.log");
                var hasErr = File.Exists(log) && File.ReadAllText(log).Contains("ERROR");
                Text.Write(Path.Combine(runDir, "data", "reconcile.json"), hasErr
                    ? """{ "result": "MATCH", "findings": [], "log": { "firstError": { "line": 2, "thread": "5", "step": "CREATE_SHEETS", "text": "ERROR NullReferenceException at KeyplanService.cs:142" } } }"""
                    : """{ "result": "MATCH", "findings": [], "log": { "firstError": null } }""");
                return new ProcResult(0, "", false);
            }
            if (script == "test-summary.ps1" && runDir is not null)
            {
                var res = File.ReadAllText(Path.Combine(runDir, "result.json"));
                var outcome = res.Contains("\"FAIL\"") || res.Contains("\"ERROR\"") ? "STOP_G3" : res.Contains("SUSPECT") ? "STOP_G3" : "PASS";
                Text.Write(Path.Combine(runDir, "data", "test-summary.json"), $$"""{ "outcome": "{{outcome}}", "scope": "", "reason": "giả", "next": "giả", "fixesDone": 0 }""");
                return new ProcResult(0, "", false);
            }
            return new ProcResult(0, "", false);
        };
    }

    private (TestRunner Runner, FakeGateway Gw, FakeHost Host) Make(TestRepo repo)
    {
        var host = new FakeHost();
        var gw = new FakeGateway(host, LogFile);
        var runner = new TestRunner(repo.Ctx(), RunnerConfig.From(repo.Ctx()),
            name => Task.FromResult<IToolClient>(name == "hawee" ? gw : new FakeTracking()), host, repo.Runner) { Poll = 0 };
        return (runner, gw, host);
    }

    private string LatestRun(string suite = "1-main") => Directory.GetDirectories(Path.Combine(Suites, suite, "runs")).OrderBy(d => d).Last();

    [Fact]
    public async Task Chay_tron_vong_cham_va_bao_cao()
    {
        using var repo = Repo();
        Suite("  - { tool: import_csv, args: { config_path: '{dataset}\\cfg.json' } }");
        FakeScripts(repo);
        var (runner, gw, host) = Make(repo);
        var r = await runner.Run(new RunRequest("1-main"));

        Assert.Contains("2 PASS", r);
        Assert.Contains("Cần agent chấm: R-03", r);
        Assert.True(r.Split('\n').Length <= 25, r);
        var run = LatestRun();
        // bản sao mở trong RUN_DIR, model gốc không đổi, bản sao đã xoá, Revit đã đóng
        Assert.StartsWith(run, gw.OpenedPath);
        Assert.Equal("RVT-GOC", File.ReadAllText(Path.Combine(Datasets, "bo", "model.rvt")));
        Assert.False(File.Exists(gw.OpenedPath));
        Assert.DoesNotContain(4242, host.Alive);
        Assert.Contains(gw.Called, c => c.StartsWith("close_document") && c.Contains("\"save\":false"));
        // dữ liệu thu được
        Assert.Contains("externalRunId=run-777", File.ReadAllText(Path.Combine(run, "data", "addin.log")));
        Assert.DoesNotContain("dòng cũ", File.ReadAllText(Path.Combine(run, "data", "addin.log")));
        Assert.True(File.Exists(Path.Combine(run, "data", "checkpoints", "SAVE", "sheet_components.json")));
        var outputs = JsonNode.Parse(File.ReadAllText(Path.Combine(run, "data", "checkpoints", "SAVE", "outputs.json")))!.AsArray();
        Assert.Equal("metadata", outputs[0]!["kind"]!.ToString());
        var info = JsonNode.Parse(File.ReadAllText(Path.Combine(run, "run-info.json")))!;
        Assert.Equal("run-777", info["externalRunId"]!.ToString());
        Assert.Equal(4242, info["pid"]!.GetValue<int>());
        // result.json đúng dạng test-summary đọc
        var result = JsonNode.Parse(File.ReadAllText(Path.Combine(run, "result.json")))!.AsArray();
        var a101 = result.Single(x => x!["sheet"]!.ToString() == "A-101")!;
        Assert.Equal("PASS", a101["verdict"]!.ToString());
        var chung = result.Single(x => x!["sheet"]!.ToString() == "(chung)")!;
        Assert.Equal("SUSPECT", chung["verdict"]!.ToString());
        Assert.True(chung["rules"]!.AsArray().Single(x => x!["id"]!.ToString() == "R-03")!["pending_agent"]!.GetValue<bool>());
        Assert.True(File.Exists(Path.Combine(run, "report.md")));
        Assert.True(File.Exists(Path.Combine(run, "evidence", "index.md")));

        // agent chấm R-03 sau khi xem ảnh → hết pending, test-summary chạy lại ra PASS
        var store = new RunStore(repo.Ctx(), RunnerConfig.From(repo.Ctx()), repo.Runner);
        Assert.Throws<FlowException>(() => store.RecordVerdict("1-main", "", "R-03", "", "PASS", "ok"));
        // luật nhìn ảnh: run không có ảnh → không PASS được; có ảnh → note phải nêu đúng file đã xem
        var noImg = Assert.Throws<FlowException>(() => store.RecordVerdict("1-main", "", "R-03", "", "PASS", "label nằm trên, cách keyplan 12 mm ở cả hai ảnh"));
        Assert.Contains("không có ảnh nào", noImg.Message);
        File.WriteAllBytes(Path.Combine(run, "data", "checkpoints", "SAVE", "FINAL_A-101.png"), new byte[] { 1, 2, 3 });
        var wrongImg = Assert.Throws<FlowException>(() => store.RecordVerdict("1-main", "", "R-03", "", "PASS", "label nằm trên keyplan ở FINAL_A-999.png"));
        Assert.Contains("FINAL_A-101.png", wrongImg.Message);
        var rv = store.RecordVerdict("1-main", "", "R-03", "", "PASS", "FINAL_A-101.png: label nằm trên, cách keyplan 12 mm");
        Assert.Contains("test-summary: PASS", rv);
        Assert.Contains("R-03", store.Detail("1-main", "", "rule", "R-03", 10));
    }

    [Fact]
    public async Task May_cham_Base_lech_thi_chuyen_agent()
    {
        using var repo = Repo();
        Suite("  - { tool: import_csv, args: { config_path: '{dataset}\\cfg.json' } }");
        FakeScripts(repo);
        repo.Write("BIM_KB_Base/core/tools/rule-eval.ps1", "# giả");
        var inner = repo.Runner.Handler!;
        List<string>? seen = null;
        var resultBefore = "";
        repo.Runner.Handler = (file, args) =>
        {
            if (!args.Any(a => a.EndsWith("rule-eval.ps1"))) return inner(file, args);
            seen = args;
            var runDir = args.SkipWhile(a => a != "-RunDir").Skip(1).First();
            resultBefore = File.ReadAllText(Path.Combine(runDir, "result.json"));   // chạy sau khi đã có result.json
            Text.Write(Path.Combine(runDir, "data", "rule-eval.json"), """
                { "tool": "rule-eval", "mode": "shadow", "mismatch": 1, "results": [
                  { "id": "R-01", "verdict": "FAIL", "agent": "PASS", "match": "LECH", "clauses": [ { "verdict": "FAIL", "detail": "A-102=0" } ] },
                  { "id": "R-02", "verdict": "PASS", "agent": "PASS", "match": "khop", "clauses": [] } ] }
                """);
            return new ProcResult(1, "", false);
        };
        var (runner, _, _) = Make(repo);
        var r = await runner.Run(new RunRequest("1-main"));

        Assert.Contains("rule-eval: lệch 1 (R-01) → chuyển agent chấm", r);
        Assert.Contains("Cần agent chấm: R-01, R-03", r);
        Assert.Contains("\"PASS\"", resultBefore);
        var run = LatestRun();
        Assert.Equal(Path.Combine(Suites, "1-main", "rules.md"), seen!.SkipWhile(a => a != "-Rules").Skip(1).First());
        var r01 = JsonNode.Parse(File.ReadAllText(Path.Combine(run, "result.json")))!.AsArray()
            .SelectMany(x => x!["rules"]!.AsArray()).Where(x => x!["id"]!.ToString() == "R-01").ToList();
        Assert.NotEmpty(r01);
        Assert.All(r01, x => { Assert.Equal("SUSPECT", x!["verdict"]!.ToString()); Assert.True(x["pending_agent"]!.GetValue<bool>()); });
        var cc = JsonNode.Parse(File.ReadAllText(Path.Combine(run, "run-info.json")))!["grading"]!["cross_check"]!;
        Assert.Equal(1, cc["mismatch"]!.GetValue<int>());
        Assert.Contains("Máy chấm lệch R-01", File.ReadAllText(Path.Combine(run, "report.md")));
    }

    [Fact]
    public async Task Khong_co_rule_eval_thi_ghi_chu_khong_loi()
    {
        using var repo = Repo();
        Suite("  - { tool: import_csv, args: { config_path: '{dataset}\\cfg.json' } }");
        FakeScripts(repo);
        var (runner, _, _) = Make(repo);
        var r = await runner.Run(new RunRequest("1-main"));
        Assert.Contains("rule-eval: chưa chạy máy chấm", r);
        Assert.Contains("2 PASS", r);
    }

    [Fact]
    public async Task Capture_none_cham_tren_ket_qua_step()
    {
        using var repo = Repo();
        Suite("  - { tool: preview_zone_split, args: { scope_box: 'Scope Box 67' } }\n  - { tool: preview_zone_split, args: { scope_box: 'Scope Box 67' } }\ncapture: none", """
            # Luật

            ### R-01 — Scale nhỏ nhất và số zone
            - AC: AC1
            - Kỳ vọng: theo spec (AC1): MIN 1:145, 3 zone ở lần gọi thứ hai
            - Sai số: không
            - Máy chấm: `bằng(steps\01-preview_zone_split.json → MinScale, "1:145")` và `đếm(steps\02-preview_zone_split.json → Zones[]) = 3`
            """);
        FakeScripts(repo);
        var host = new FakeHost();
        var gw = new FakeGateway(host, LogFile)
        {
            StepHook = (tool, a) => new ToolCall(true, "", JsonNode.Parse("""{ "MinScale": "1:145", "Zones": [ { "Name": "Z1" }, { "Name": "Z2" }, { "Name": "Z3" } ] }""")),
        };
        var trackingConnects = 0;
        var runner = new TestRunner(repo.Ctx(), RunnerConfig.From(repo.Ctx()),
            name => { if (name != "hawee") trackingConnects++; return Task.FromResult<IToolClient>(name == "hawee" ? gw : new FakeTracking()); }, host, repo.Runner) { Poll = 0 };
        var r = await runner.Run(new RunRequest("1-main"));

        Assert.Contains("1 PASS", r);
        Assert.Contains("test-summary: PASS", r);
        Assert.Equal(0, trackingConnects);
        Assert.DoesNotContain(repo.Runner.Calls, c => c.Args.Any(a => a.EndsWith("host-test-reconcile.ps1")));
        var run = LatestRun();
        Assert.True(File.Exists(Path.Combine(run, "data", "steps", "01-preview_zone_split.json")));
        Assert.True(File.Exists(Path.Combine(run, "data", "steps", "02-preview_zone_split.json")));
        Assert.False(File.Exists(Path.Combine(run, "data", "reconcile.json")));
        Assert.Contains("capture: none", File.ReadAllText(Path.Combine(run, "data", "step_status.json")));
        Assert.Contains("Kết quả step", File.ReadAllText(Path.Combine(run, "report.md")));

        // get_detail / record_verdict không truyền suite → suite của lần chạy gần nhất
        var store = new RunStore(repo.Ctx(), RunnerConfig.From(repo.Ctx()), repo.Runner);
        Assert.Contains("01-preview_zone_split.json", store.Detail("", "", "steps", "", 15));
        Assert.Contains("R-01", store.Detail("", "", "rule", "R-01", 15));
        Assert.Contains("R-01: SUSPECT", store.RecordVerdict("", "", "R-01", "", "SUSPECT", "xem lại"));
    }

    [Fact]
    public async Task Ket_qua_step_luu_ca_khi_loi()
    {
        using var repo = Repo();
        Suite("  - { tool: preview_zone_split, args: {}, on_error: continue }");
        FakeScripts(repo);
        var (runner, gw, _) = Make(repo);
        gw.StepHook = (tool, a) => new ToolCall(false, "Scope box không tồn tại", null);
        await runner.Run(new RunRequest("1-main"));
        var f = JsonNode.Parse(File.ReadAllText(Path.Combine(LatestRun(), "data", "steps", "01-preview_zone_split.json")))!;
        Assert.False(f["ok"]!.GetValue<bool>());
        Assert.Equal("Scope box không tồn tại", f["text"]!.ToString());
    }

    [Fact]
    public async Task Tool_cam_bi_chan_tai_runner()
    {
        using var repo = Repo();
        Suite("  - { tool: delete_sheets, args: { sheet_numbers: [A-101], dry_run: false } }");
        FakeScripts(repo);
        var (runner, gw, _) = Make(repo);
        var r = await runner.Run(new RunRequest("1-main"));
        Assert.Contains("CHẶN: delete_sheets bị cấm", r);
        Assert.DoesNotContain(gw.Called, c => c.StartsWith("delete_sheets"));
    }

    [Fact]
    public async Task Buoc_dung_tinh_huong_hop_le_va_ve_khong_allow_draw()
    {
        using var repo = Repo();
        Suite("""
              - { tool: delete_sheets, purpose: setup, why: "K10 — tờ bị xoá", args: { sheet_numbers: [A-101], dry_run: false, confirm: true } }
              - { tool: draw_pipes, args: {} }
            """);
        FakeScripts(repo);
        var (runner, gw, _) = Make(repo);
        var r = await runner.Run(new RunRequest("1-main"));
        Assert.Contains(gw.Called, c => c.StartsWith("delete_sheets"));
        Assert.Contains("draw_pipes chỉ được gọi khi suite allow_draw", r);
        Assert.DoesNotContain(gw.Called, c => c.StartsWith("draw_pipes"));
    }

    [Fact]
    public async Task Loi_tool_co_chan_doan_goi_y()
    {
        using var repo = Repo();
        Suite("  - { tool: import_csv, args: {} }");
        FakeScripts(repo);
        var (runner, gw, _) = Make(repo);
        gw.StepHook = (tool, _) =>
        {
            File.AppendAllText(LogFile, "2026-10-12 09:00:01 [5] INFO [Step] CREATE_SHEETS externalRunId=run-9\n2026-10-12 09:00:02 [5] ERROR NullReferenceException\n   at Hawee.Draw.KeyplanService.Place() in D:\\src\\Draw\\KeyplanService.cs:line 142\n");
            return new ToolCall(false, "INTERNAL: Object reference not set", null);
        };
        var r = await runner.Run(new RunRequest("1-main"));
        Assert.Contains("Pipeline: step 1 import_csv lỗi", r);
        Assert.Contains("Chẩn đoán máy gợi ý: CODE", r);
        var d = JsonNode.Parse(File.ReadAllText(Path.Combine(LatestRun(), "data", "diagnosis.json")))!;
        Assert.Equal("CODE", d["class"]!.ToString());
        Assert.Contains("src/Draw/KeyplanService.cs", d["stack_files"]!.AsArray().Select(x => x!.ToString()));
        // dữ liệu thiếu vì pipeline lỗi → ERROR, không PASS
        Assert.Contains("ERROR", File.ReadAllText(Path.Combine(LatestRun(), "result.json")));
        var store = new RunStore(repo.Ctx(), RunnerConfig.From(repo.Ctx()), repo.Runner);
        Assert.Contains("NullReferenceException", store.Detail("1-main", "", "log", "", 5));
        // file:dòng phải có thật trong repo
        Assert.Contains("không có trong repo", Assert.Throws<FlowException>(() => store.SetDiagnosis("1-main", "", "CODE", "KeyplanService.cs:142", "true", "", "", "")).Message);
        Assert.Contains("file:dòng", Assert.Throws<FlowException>(() => store.SetDiagnosis("1-main", "", "CODE", "lỗi gì đó trong log", "", "", "", "")).Message);
        repo.Write("src/Draw/KeyplanService.cs", string.Concat(Enumerable.Repeat("//\n", 150)));
        Assert.Contains("không có trong repo", Assert.Throws<FlowException>(() => store.SetDiagnosis("1-main", "", "CODE", "KeyplanService.cs:999", "", "", "", "")).Message);
        // chạy lẻ (không có task) → không có diff để so, nhận giá trị agent khai
        Assert.Contains("in_task_diff true", store.SetDiagnosis("1-main", "", "CODE", @"D:\build\src\Draw\KeyplanService.cs:142", "true", "", "", ""));
    }

    [Fact]
    public async Task Chan_doan_in_task_diff_do_may_tinh()
    {
        using var repo = Repo();
        Suite("  - { tool: import_csv, args: {} }");
        FakeScripts(repo);
        var changed = new List<string>();
        var inner = repo.Runner.Handler!;
        repo.Runner.Handler = (file, args) => file == "git" && args.Contains("--name-only") ? new ProcResult(0, string.Join("\n", changed), false)
            : file == "git" && args.Contains("--verify") ? new ProcResult(0, "abc", false) : inner(file, args);
        new TaskOpener(repo.Ctx(), repo.Runner).Open(new OpenInput("1", "main"));
        repo.Write("src/Draw/KeyplanService.cs", string.Concat(Enumerable.Repeat("//\n", 150)));
        repo.Write("src/Ui/Dialog.cs", "//\n//\n");
        var (runner, gw, _) = Make(repo);
        gw.StepHook = (_, _) =>
        {
            File.AppendAllText(LogFile, "2026-10-12 09:00:01 [5] INFO [Step] CREATE_SHEETS externalRunId=run-9\n2026-10-12 09:00:02 [5] ERROR NullReferenceException\n   at Hawee.Draw.KeyplanService.Place() in D:\\src\\Draw\\KeyplanService.cs:line 142\n");
            return new ToolCall(false, "INTERNAL: Object reference not set", null);
        };
        changed.Add("src/Draw/KeyplanService.cs");
        await runner.Run(new RunRequest("1-main", Task: "1"));
        var d = JsonNode.Parse(File.ReadAllText(Path.Combine(LatestRun(), "data", "diagnosis.json")))!;
        Assert.True(d["in_task_diff"]!.GetValue<bool>());

        var store = new RunStore(repo.Ctx(), RunnerConfig.From(repo.Ctx()), repo.Runner);
        // bỏ trống → giữ giá trị máy (trước đây bị xoá về null)
        Assert.Contains("in_task_diff true (máy)", store.SetDiagnosis("1-main", "", "CODE", "KeyplanService.cs:142", "", "", "", ""));
        // khai ngược với máy → lỗi, không ghi
        var ex = Assert.Throws<FlowException>(() => store.SetDiagnosis("1-main", "", "CODE", "KeyplanService.cs:142", "false", "", "", ""));
        Assert.Contains("máy tính ra true", ex.Message);
        // lỗi gốc ở file ngoài diff → máy ra false
        Assert.Contains("in_task_diff false (máy)", store.SetDiagnosis("1-main", "", "CODE", "Ui/Dialog.cs:2", "", "KI-0007", "", ""));
        // ki bỏ trống → giữ
        store.SetDiagnosis("1-main", "", "CODE", "Ui/Dialog.cs:2", "", "", "", "");
        Assert.Equal("KI-0007", JsonNode.Parse(File.ReadAllText(Path.Combine(LatestRun(), "data", "diagnosis.json")))!["ki"]!.ToString());
    }

    [Fact]
    public async Task Revit_chet_khi_khoi_dong_thi_khong_goi_gi_vao_Revit()
    {
        using var repo = Repo();
        Suite("  - { tool: import_csv, args: {} }");
        FakeScripts(repo);
        var gw = new FakeGateway(new FakeHost(), LogFile);
        var r = await new TestRunner(repo.Ctx(), RunnerConfig.From(repo.Ctx()),
            name => Task.FromResult<IToolClient>(name == "hawee" ? gw : new FakeTracking()), new DeadHost(), repo.Runner) { Poll = 0 }
            .Run(new RunRequest("1-main"));
        Assert.Contains("ENV: Revit thoát khi khởi động", r);
        Assert.Empty(gw.Called);   // không open_document / document_info / step nào với pid 0
    }

    [Fact]
    public async Task Step_khong_duoc_mo_document_va_moi_step_chi_chay_tren_ban_sao()
    {
        using var repo = Repo();
        Suite("  - { tool: open_document, args: { path: '{dataset}\\model.rvt' } }");
        FakeScripts(repo);
        var (runner, gw, _) = Make(repo);
        var r = await runner.Run(new RunRequest("1-main"));
        Assert.Contains("tool runner tự quản", r);
        Assert.Single(gw.Called, c => c.StartsWith("open_document"));   // chỉ lần mở bản sao của runner

        // step trước đổi document sang model ngoài RUN_DIR → step sau không chạy
        using var repo2 = Repo();
        Suite("  - { tool: switch_doc, args: {} }\n  - { tool: import_csv, args: {} }");
        FakeScripts(repo2);
        var (runner2, gw2, _) = Make(repo2);
        gw2.StepHook = (tool, a) => { if (tool == "switch_doc") gw2.OpenedPath = Path.Combine(Datasets, "bo", "model.rvt"); return null; };
        var r2 = await runner2.Run(new RunRequest("1-main"));
        Assert.Contains("không nằm trong RUN_DIR", r2);
        Assert.DoesNotContain(gw2.Called, c => c.StartsWith("import_csv"));
    }

    [Fact]
    public void Dieu_kien_cam_thieu_tham_so_thi_chan()
    {
        Assert.True(Policy.WhenMatches("dry_run=false", new JsonObject()));
        Assert.False(Policy.WhenMatches("dry_run=false", new JsonObject { ["dry_run"] = true }));
        Assert.True(Policy.WhenMatches("dry_run=false", new JsonObject { ["dry_run"] = false }));
        var y = Y.Parse("forbidden:\n  - { tool: delete_sheets, when: \"dry_run=false\" }\n");
        var d = new Policy(y).Check(new SuiteStep(1, "delete_sheets", new JsonObject { ["sheets"] = "A-101" }, "action", "", "stop"), new JsonObject { ["sheets"] = "A-101" }, false);
        Assert.False(d.Allowed);
        Assert.Contains("args không có dry_run", d.Reason);
    }

    [Fact]
    public async Task Hop_thoai_khoi_dong_la_loi_moi_truong()
    {
        using var repo = Repo();
        Suite("  - { tool: import_csv, args: {} }");
        FakeScripts(repo);
        var (runner, gw, host) = Make(repo);
        host.Dialogs.Add("Duplicated AddInId");
        // pipe không lên: gateway không thấy pid mới
        var r = await new TestRunner(repo.Ctx(), RunnerConfig.From(repo.Ctx()),
            name => Task.FromResult<IToolClient>(name == "hawee" ? new NoPidGateway() : new FakeTracking()), host, repo.Runner) { Poll = 0 }
            .Run(new RunRequest("1-main"));
        Assert.Contains("ENV: Revit kẹt hộp thoại khởi động: Duplicated AddInId", r);
        Assert.DoesNotContain(4242, host.Alive);
    }

    [Fact]
    public async Task Chay_le_tren_suite_da_khoa_bi_danh_dau_va_ma_tran_chi_lay_run_cua_task()
    {
        using var repo = Repo();
        Suite("  - { tool: import_csv, args: {} }");
        FakeScripts(repo);
        new TaskOpener(repo.Ctx(), repo.Runner).Open(new OpenInput("1", "main"));
        var dir = Path.Combine(repo.Root, "docs/tasks/1-main");
        Text.Write(Path.Combine(dir, "spec.md"), "# Spec\n\n## 5. Hành vi đúng\n\n| # | Kịch bản | Khi | Thì | Đo bằng |\n|---|---|---|---|---|\n| AC1 | K1 | a | b | suite trong Revit |\n");
        var st = StatusFile.ForTask(dir);
        st.AddToList("suites", "1-main");
        st.Save();
        var (runner, _, _) = Make(repo);

        // lượt của task (phiên verify)
        await runner.Run(new RunRequest("1-main", Task: "1"));
        var taskRun = Path.GetFileName(LatestRun());

        // sau G2 có người sửa luật; chạy lẻ (/revit-test) → vẫn chạy nhưng bị đánh dấu
        Text.Write(Path.Combine(Suites, "1-main", "approved.json"), """{ "suite_yaml": "AAA", "rules_md": "BBB", "approved_by": "Minh", "date": "2026-10-10" }""");
        repo.Clock.Now = repo.Clock.Now.AddMinutes(5);
        Text.Write(LogFile, "2026-10-12 08:00:00 [1] INFO dòng cũ trước lần chạy\n");
        await runner.Run(new RunRequest("1-main", Purpose: "adhoc"));
        var adhocRun = LatestRun();
        Assert.NotEqual(taskRun, Path.GetFileName(adhocRun));
        Assert.Equal("false", JsonNode.Parse(File.ReadAllText(Path.Combine(adhocRun, "run-info.json")))!["rules_approved"]!.ToString());
        Assert.Contains("LUẬT CHƯA DUYỆT", File.ReadAllText(Path.Combine(adhocRun, "report.md")));

        // ma trận AC lấy run của task, không lấy run lẻ mới hơn
        new TestDesign(repo.Ctx()).BuildMatrix("1");
        var td = Text.Read(Path.Combine(dir, "test-design.md"));
        Assert.Contains(taskRun, td);
        Assert.DoesNotContain(Path.GetFileName(adhocRun), td);
    }

    [Fact]
    public void G2_chan_khi_suite_con_ban_de_xuat()
    {
        using var repo = Repo();
        Suite("  - { tool: import_csv, args: {} }");
        new TaskOpener(repo.Ctx(), repo.Runner).Open(new OpenInput("1", "main"));
        var dir = Path.Combine(repo.Root, "docs/tasks/1-main");
        var st = StatusFile.ForTask(dir);
        st.AddToList("suites", "1-main");
        st.Set("baseline", "baseline.json");
        st.Set("autonomy", "ask");
        st.Save();
        Text.Write(Path.Combine(Suites, "1-main", "rules.proposed.md"), "# đề xuất");
        var ex = Assert.Throws<FlowException>(() => new GateSubmit(repo.Ctx()).Submit("1", "G2"));
        Assert.Contains("rules.proposed.md", ex.Message);
        ex = Assert.Throws<FlowException>(() => new GateRecorder(repo.Ctx()).Record("1", new GateInput("G2", "PASS", "Minh", "chat", CoderConfirmed: true)));
        Assert.Contains("bản đề xuất", ex.Message);

        // đưa thành bản chính → G2 ghi hash vào status và approved.json trong folder suite
        File.Delete(Path.Combine(Suites, "1-main", "rules.proposed.md"));
        new GateRecorder(repo.Ctx()).Record("1", new GateInput("G2", "PASS", "Minh", "chat", CoderConfirmed: true));
        var approved = JsonNode.Parse(File.ReadAllText(Path.Combine(Suites, "1-main", "approved.json")))!;
        Assert.Equal(Text.Sha256File(Path.Combine(Suites, "1-main", "rules.md")), approved["rules_md"]!.ToString());
    }

    [Fact]
    public void Mcp_json_mo_rong_bien_nhu_Claude_Code()
    {
        Environment.SetEnvironmentVariable("HAWEE_TEST_GW_DIR", "C:/gw");
        Environment.SetEnvironmentVariable("HAWEE_TEST_UNSET", null);
        Assert.Equal("C:/gw/hawee.exe", McpToolClient.Expand("${HAWEE_TEST_GW_DIR}/hawee.exe"));
        Assert.Equal("8080", McpToolClient.Expand("${HAWEE_TEST_UNSET:-8080}"));
        Assert.Equal("", McpToolClient.Expand("${HAWEE_TEST_UNSET}"));
    }

    [Fact]
    public async Task Luat_doi_sau_G2_thi_dung()
    {
        using var repo = Repo();
        Suite("  - { tool: import_csv, args: {} }");
        FakeScripts(repo);
        new TaskOpener(repo.Ctx(), repo.Runner).Open(new OpenInput("1", "main"));
        var st = StatusFile.ForTask(Path.Combine(repo.Root, "docs/tasks/1-main"));
        st.SetMap("suite_hashes", new KeyValuePair<string, string>[] { new("1-main", "{ suite_yaml: AAA, rules_md: BBB, approved_by: Minh, date: 2026-10-10 }") });
        st.Save();
        var (runner, _, _) = Make(repo);
        var ex = await Assert.ThrowsAsync<FlowException>(() => runner.Run(new RunRequest("1-main", Task: "1")));
        Assert.Contains("đổi sau khi duyệt ở G2", ex.Message);
    }

    [Fact]
    public async Task Verdict_anh_chi_dung_lai_khi_du_lieu_khong_doi()
    {
        using var repo = Repo();
        Suite("  - { tool: import_csv, args: {} }");
        FakeScripts(repo);
        var (runner, gw, _) = Make(repo);
        await runner.Run(new RunRequest("1-main"));
        var store = new RunStore(repo.Ctx(), RunnerConfig.From(repo.Ctx()), repo.Runner);
        File.WriteAllBytes(Path.Combine(LatestRun(), "evidence", "FINAL_A-101.png"), new byte[] { 1, 2, 3 });
        store.RecordVerdict("1-main", "", "R-03", "", "PASS", "FINAL_A-101.png: label nằm trên, cách keyplan 12 mm");

        // lượt 2: dữ liệu như lượt 1 → dùng lại verdict của agent
        repo.Clock.Now = repo.Clock.Now.AddMinutes(5);
        Text.Write(LogFile, "2026-10-12 08:00:00 [1] INFO dòng cũ trước lần chạy\n");
        var r2 = await runner.Run(new RunRequest("1-main"));
        Assert.DoesNotContain("Cần agent chấm: R-03", r2);
        var info2 = JsonNode.Parse(File.ReadAllText(Path.Combine(LatestRun(), "run-info.json")))!;
        Assert.Equal(1, info2["grading"]!["reused"]!.GetValue<int>());

        // lượt 3: code đổi → add-in ghi khác → không được dùng lại verdict cũ
        repo.Clock.Now = repo.Clock.Now.AddMinutes(5);
        Text.Write(LogFile, "2026-10-12 08:00:00 [1] INFO dòng cũ trước lần chạy\n");
        gw.StepHook = (_, _) =>
        {
            File.AppendAllText(LogFile, "2026-10-12 09:10:01 [5] INFO [Step] CREATE_SHEETS start externalRunId=run-778\n2026-10-12 09:10:09 [5] INFO [Step] SAVE done (label dời)\n");
            return new ToolCall(true, "ok", null);
        };
        var r3 = await runner.Run(new RunRequest("1-main"));
        Assert.Contains("Cần agent chấm: R-03", r3);
    }

    [Fact]
    public void Suite_lint_canh_bao_luat_so_so_thuc_khong_co_sai_so()
    {
        using var repo = Repo("  default_tol_mm: 0.05");
        Suite("  - { tool: import_csv, args: {} }");
        FakeScripts(repo);
        var r = new TestDesign(repo.Ctx(), repo.Runner).SuiteLint(null, "1-main", false);
        Assert.Contains("[rule-tol] 1 luật", r);
        Assert.Contains("1-main/R-02", r);
        Assert.Contains("0.05 mm", r);
        Assert.Equal(0.05, RunnerConfig.From(repo.Ctx()).DefaultTolMm);
    }

    [Fact]
    public void Cau_hinh_mcp_tu_claude_json()
    {
        using var repo = Repo();
        var cj = Path.Combine(_root, ".claude.json");
        var key = repo.Root.Replace('\\', '/');
        Text.Write(cj, $$"""
            {
              "mcpServers": { "bim-tracking": { "type": "http", "url": "http://localhost:5000/mcp" }, "hawee": { "command": "user-cap" } },
              "projects": {
                "/khac/repo": { "mcpServers": { "hawee": { "command": "sai-du-an" } } },
                "{{key}}": { "mcpServers": { "hawee": { "command": "hawee-gateway.exe", "args": ["--pipe"] } } }
              }
            }
            """);
        // cấp local (đúng repo) thắng cấp user; server chỉ có ở cấp user vẫn tìm được
        Assert.Equal("hawee-gateway.exe", McpToolClient.ResolveConfig(repo.Ctx(), "hawee", cj)!["command"]!.ToString());
        Assert.Equal("http://localhost:5000/mcp", McpToolClient.ResolveConfig(repo.Ctx(), "bim-tracking", cj)!["url"]!.ToString());
        // .mcp.json của repo đứng trước
        repo.Write(".mcp.json", """{ "mcpServers": { "hawee": { "command": "tu-repo" } } }""");
        Assert.Equal("tu-repo", McpToolClient.ResolveConfig(repo.Ctx(), "hawee", cj)!["command"]!.ToString());
        Assert.Null(McpToolClient.ResolveConfig(repo.Ctx(), "khong-co", cj));
    }

    [Fact]
    public void Khoa_cham_anh_doi_khi_anh_doi()
    {
        var data = Directory.CreateDirectory(Path.Combine(_root, "data")).FullName;
        var rule = RulesFile.Parse("### R-03 — Label không đè\n- Kỳ vọng: theo ảnh\n- Máy chấm: ảnh(label)\n- Ảnh minh chứng: FINAL_A-101.png\n").Rules[0];
        File.WriteAllBytes(Path.Combine(data, "FINAL_A-101.png"), new byte[] { 1, 2, 3 });
        var k1 = TestRunner.GradingKey(rule, Array.Empty<string>(), data, Verdicts.Agent);
        Assert.Equal(k1, TestRunner.GradingKey(rule, Array.Empty<string>(), data, Verdicts.Agent));
        File.WriteAllBytes(Path.Combine(data, "FINAL_A-101.png"), new byte[] { 1, 2, 4 });
        Assert.NotEqual(k1, TestRunner.GradingKey(rule, Array.Empty<string>(), data, Verdicts.Agent));
        // thư mục grading\ (ghi sau khi chấm) không làm đổi khoá
        var k2 = TestRunner.GradingKey(rule, Array.Empty<string>(), data, Verdicts.Agent);
        Text.Write(Path.Combine(data, "grading", "agent.json"), "{}");
        Assert.Equal(k2, TestRunner.GradingKey(rule, Array.Empty<string>(), data, Verdicts.Agent));
    }

    [Theory]
    [InlineData("System.NullReferenceException: Object reference", "CODE")]
    [InlineData("Validation failed: cột Level trống", "INPUT")]
    [InlineData("TIMEOUT sau 1800 s", "ENV")]
    [InlineData("tracking API trả 500", "TRACKING")]
    [InlineData("không rõ", "UNKNOWN")]
    public void Phan_loai_loi(string text, string cls) => Assert.Equal(cls, TestRunner.Classify(text));

    [Fact]
    public void Sheet_ap_dung() =>
        Assert.Equal(new[] { "M-301", "M-302" }, TestRunner.SheetsOf("M-301, M-302   (hoặc \"mọi sheet\")".Split("   ")[0]));

    private sealed class NoPidGateway : IToolClient
    {
        public Task<ToolCall> Call(string tool, JsonObject args, TimeSpan timeout) => Task.FromResult(new ToolCall(true, "[]", new JsonArray()));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
