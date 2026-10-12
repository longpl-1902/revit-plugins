using Hawee.Flow.Core.Infra;

namespace Hawee.Flow.Core.Testing;

public sealed record OutputSpec(string Role, string NamePrefix, string SaveAs);

/// <summary>Khối revit_test của hawee.project.yaml (README profile revit-addin) ở dạng đã đọc.</summary>
public sealed class RunnerConfig
{
    public string Gateway { get; init; } = "";
    public string ToolListInstances { get; init; } = "";
    public string ToolListTools { get; init; } = "";
    public string ToolCall { get; init; } = "";
    public string ToolOpen { get; init; } = "";
    public string ToolDocInfo { get; init; } = "";
    public string ToolClose { get; init; } = "";
    public string CaptureSource { get; init; } = "bim-tracking";
    public string TrackingMcp { get; init; } = "";
    public string RunIdLog { get; init; } = "";
    public string RunIdPattern { get; init; } = "";
    public string CaptureStep { get; init; } = "";
    public string StepStatusTool { get; init; } = "run_timeline";
    public string OutputsTool { get; init; } = "run_outputs";
    public List<OutputSpec> Outputs { get; init; } = new();
    public List<Checkpoint> Checkpoints { get; init; } = new();
    public string LocalFallback { get; init; } = "";
    public string DiagnoseLog { get; init; } = "";
    public List<string> ErrorPatterns { get; init; } = new();
    public string ProgressPattern { get; init; } = "";
    public int MaxRuns { get; init; }
    public string BuildCommand { get; init; } = "";
    public string AddinDll { get; init; } = "";
    public bool RequireSigned { get; init; }

    public int MaxFixes => MaxRuns > 0 ? MaxRuns - 1 : 3;

    public static RunnerConfig From(ProjectContext ctx)
    {
        var b = ctx.TestBlock;
        if (!b.Exists) throw new FlowException($"hawee.project.yaml chưa có khối {ctx.TestBlockName}");
        var t = b["tools"];
        var cap = b["capture"];
        var dia = b["diagnose"];
        var fix = b["fix_loop"];
        var cfg = new RunnerConfig
        {
            Gateway = b["mcp"].S(),
            ToolListInstances = t["list_instances"].S(),
            ToolListTools = t["list_tools"].S(),
            ToolCall = t["call"].S(),
            ToolOpen = t["open_document"].S(),
            ToolDocInfo = t["document_info"].S(),
            ToolClose = t["close_document"].S(),
            CaptureSource = cap["source"].S("bim-tracking"),
            TrackingMcp = cap["tracking_mcp"].S("bim-tracking"),
            RunIdLog = Environment.ExpandEnvironmentVariables(cap.At("run_id.log").S()),
            RunIdPattern = cap.At("run_id.pattern").S(),
            CaptureStep = cap["step"].S(),
            StepStatusTool = cap["step_status"].S("run_timeline"),
            OutputsTool = cap["outputs_tool"].S("run_outputs"),
            Outputs = cap["outputs"].Items.Select(o => new OutputSpec(o["role"].S(), o["name_prefix"].S(), o["save_as"].S())).ToList(),
            Checkpoints = Suite.ParseCheckpoints(cap["checkpoints"]),
            LocalFallback = cap["local_fallback"].S(),
            DiagnoseLog = Environment.ExpandEnvironmentVariables(dia["log"].S()),
            ErrorPatterns = dia["error_patterns"].Strings(),
            ProgressPattern = dia["progress_pattern"].S(),
            MaxRuns = fix["max_runs"].Int(0),
            BuildCommand = fix["build"].S(),
            AddinDll = fix["addin_dll"].S(),
            RequireSigned = fix["require_signed"].Bool(),
        };
        var missing = new[] { ("mcp", cfg.Gateway), ("tools.list_instances", cfg.ToolListInstances), ("tools.call", cfg.ToolCall),
                              ("tools.open_document", cfg.ToolOpen), ("tools.document_info", cfg.ToolDocInfo), ("tools.close_document", cfg.ToolClose) }
            .Where(x => x.Item2.Length == 0).Select(x => x.Item1).ToList();
        if (missing.Count > 0) throw new FlowException($"{ctx.TestBlockName} thiếu: {string.Join(", ", missing)} — không tự bịa tên tool");
        return cfg;
    }

    public string LogFile => DiagnoseLog.Length > 0 ? DiagnoseLog : RunIdLog;
}
