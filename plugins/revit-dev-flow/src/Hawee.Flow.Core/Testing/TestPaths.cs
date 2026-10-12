using Hawee.Flow.Core.Infra;

namespace Hawee.Flow.Core.Testing;

/// <summary>Thư mục kho dữ liệu, suite, đường dẫn trong suite (thay {dataset}, tương đối theo folder suite).</summary>
public sealed class TestPaths(ProjectContext ctx)
{
    public Y Cfg => ctx.TestBlock;

    public string SuitesDir => Env("HAWEE_REVIT_TASKS_DIR") ?? Required("suites_dir");
    public string DatasetsDir => Env("HAWEE_REVIT_DATASETS_DIR") ?? Required("datasets_dir");

    public string Suite(string name) => Path.Combine(SuitesDir, name);
    public string Dataset(string name) => Path.Combine(DatasetsDir, name);

    private static string? Env(string k) => Environment.GetEnvironmentVariable(k) is { Length: > 0 } v ? v : null;

    private string Required(string key)
    {
        var v = Cfg[key].S();
        if (v.Length == 0) throw new FlowException($"hawee.project.yaml thiếu {ctx.TestBlockName}.{key}");
        return ctx.Full(v);
    }

    /// <summary>Đường dẫn trong suite.yaml: thay {dataset}, tương đối → tính từ folder suite. Giữ dấu \ của Windows.</summary>
    public string Resolve(string suiteDir, string value, string? dataset)
    {
        var v = value;
        if (v.Contains("{dataset}"))
        {
            if (dataset is null) throw new FlowException($"'{value}' dùng {{dataset}} nhưng suite không khai dataset");
            v = v.Replace("{dataset}", Dataset(dataset));
        }
        v = v.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        return Path.IsPathRooted(v) || (v.Length > 1 && v[1] == ':') ? v : Path.GetFullPath(Path.Combine(suiteDir, v));
    }
}
