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
        var rv = store.RecordVerdict("1-main", "", "R-03", "", "PASS", "label nằm trên, cách keyplan 12 mm ở cả hai ảnh");
        Assert.Contains("test-summary: PASS", rv);
        Assert.Contains("R-03", store.Detail("1-main", "", "rule", "R-03", 10));
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
        Assert.Contains("in_task_diff true", store.SetDiagnosis("1-main", "", "CODE", "KeyplanService.cs:142", "true", "", "", ""));
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
