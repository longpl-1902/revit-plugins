namespace Hawee.Flow.Core.Infra;

/// <summary>Repo dự án: gốc repo (có hawee.project.yaml), cấu hình, thư mục BIM_KB_Base, thư mục task.</summary>
public sealed class ProjectContext
{
    public const string ConfigFile = "hawee.project.yaml";

    public string Root { get; }
    public Y Config { get; }
    public string BaseDir { get; }
    public IClock Clock { get; }

    private ProjectContext(string root, IClock clock)
    {
        Root = root;
        Clock = clock;
        Config = Y.Load(Path.Combine(root, ConfigFile));
        BaseDir = Path.GetFullPath(Path.Combine(root, Config.At("base.path").S("BIM_KB_Base")));
    }

    /// <summary>Tìm gốc repo đi ngược từ <paramref name="start"/> (mặc định CLAUDE_PROJECT_DIR hoặc thư mục hiện tại).</summary>
    public static ProjectContext Open(string? start = null, IClock? clock = null)
    {
        start ??= Environment.GetEnvironmentVariable("CLAUDE_PROJECT_DIR");
        if (string.IsNullOrWhiteSpace(start)) start = Directory.GetCurrentDirectory();
        var dir = new DirectoryInfo(Path.GetFullPath(start));
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, ConfigFile))) dir = dir.Parent;
        if (dir is null) throw new FlowException($"Không thấy {ConfigFile} từ {start} đi lên — mở Claude Code ở repo dự án.");
        return new ProjectContext(dir.FullName, clock ?? SystemClock.Instance);
    }

    public string TasksDir => Full(Config.At("paths.tasks").S("docs/tasks"));
    public string HandoffsDir => Full(Config.At("paths.handoffs").S("docs/handoffs"));
    public string CodeMapsDir => Full(Config.At("paths.code_maps").S("docs/architecture/code-map"));
    public string DecisionLog => Full(Config.At("paths.decision_log").S("docs/decisions/decision-log.md"));
    public string BaseTool(string name) => Path.Combine(BaseDir, "core", "tools", name);
    public string BaseTemplate(string name) => Path.Combine(BaseDir, "core", "templates", name);
    public List<string> Profiles => Config["profiles"].Strings();

    public string Full(string relOrAbs) => Path.GetFullPath(Path.IsPathRooted(relOrAbs) ? relOrAbs : Path.Combine(Root, relOrAbs));
    public string Rel(string full) => Path.GetRelativePath(Root, full).Replace('\\', '/');

    /// <summary>Khối test của profile (revit-addin → revit_test).</summary>
    public string TestBlockName => Profiles.Contains("autocad-addin") && !Profiles.Contains("revit-addin") ? "autocad_test" : "revit_test";
    public Y TestBlock => Config[TestBlockName];

    /// <summary>Tìm folder task: id đầy đủ, tiền tố số issue, hoặc (trống) task đang làm gần nhất.</summary>
    public string ResolveTask(string? task)
    {
        if (!Directory.Exists(TasksDir)) throw new FlowException($"Chưa có thư mục task {Rel(TasksDir)} — chạy task_open trước.");
        var dirs = Directory.GetDirectories(TasksDir).Where(d => File.Exists(Path.Combine(d, "status.yaml"))).ToList();
        if (!string.IsNullOrWhiteSpace(task))
        {
            var t = task.Trim().TrimStart('#');
            var exact = dirs.FirstOrDefault(d => Path.GetFileName(d).Equals(t, StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return exact;
            var pref = dirs.Where(d => Path.GetFileName(d).StartsWith(t + "-", StringComparison.OrdinalIgnoreCase)
                                       || Path.GetFileName(d).StartsWith(t, StringComparison.OrdinalIgnoreCase)).ToList();
            if (pref.Count == 1) return pref[0];
            if (pref.Count > 1) throw new FlowException($"'{task}' khớp nhiều task: {string.Join(", ", pref.Select(Path.GetFileName))}");
            throw new FlowException($"Không thấy task '{task}' trong {Rel(TasksDir)}.");
        }
        var active = dirs
            .Select(d => (Dir: d, St: Y.Load(Path.Combine(d, "status.yaml"))["state"].S()))
            .Where(x => x.St is not ("done" or "parked"))
            .OrderByDescending(x => File.GetLastWriteTimeUtc(Path.Combine(x.Dir, "status.yaml")))
            .ToList();
        if (active.Count == 0) throw new FlowException("Không có task đang làm — truyền tên task.");
        return active[0].Dir;
    }
}

public sealed class FlowException(string message) : Exception(message);

public interface IClock { DateTime Now { get; } }
public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();
    public DateTime Now => DateTime.Now;
}
public sealed class FixedClock(DateTime now) : IClock { public DateTime Now { get; set; } = now; }
