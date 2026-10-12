using System.Text.Json;
using System.Text.Json.Nodes;
using Hawee.Flow.Core.Infra;

namespace Hawee.Flow.Core.Testing;

public sealed record SuiteStep(int Index, string Tool, JsonObject Args, string Purpose, string Why, string OnError)
{
    public bool IsSetup => Purpose.Equals("setup", StringComparison.OrdinalIgnoreCase);
}

public sealed record CheckpointExpect(string Role, string Kind, int Min, string NamePrefix);
public sealed record Checkpoint(string Step, List<CheckpointExpect> Expect);

/// <summary>suite.yaml (suite-format.md §1): kịch bản, model, dataset, timeouts, steps, checkpoints, manual_steps.</summary>
public sealed class Suite
{
    public required string Dir { get; init; }
    public required string Name { get; init; }
    public string Dataset { get; init; } = "";
    public string Spec { get; init; } = "";
    public string RevitExe { get; init; } = "";
    public string RevitVersion { get; init; } = "";
    public string Model { get; init; } = "";
    public bool KeepModelCopy { get; init; }
    public bool AllowDraw { get; init; }
    public int StartupMin { get; init; } = 5;
    public int OpenModelMin { get; init; } = 10;
    public int StepMin { get; init; } = 30;
    public List<SuiteStep> Steps { get; init; } = new();
    public List<Checkpoint> Checkpoints { get; init; } = new();
    public List<string> ManualSteps { get; init; } = new();
    public Y Raw { get; init; } = Y.Missing;

    public static Suite Load(string dir)
    {
        var file = Path.Combine(dir, "suite.yaml");
        if (!File.Exists(file)) throw new FlowException($"không có {file}");
        Y y;
        try { y = Y.Load(file); }
        catch (Exception ex) { throw new FlowException($"suite.yaml đọc lỗi: {ex.Message}"); }
        var t = y["timeouts"];
        return new Suite
        {
            Dir = dir,
            Name = y["suite"].S(Path.GetFileName(dir)),
            Dataset = y["dataset"].S(),
            Spec = y["spec"].S(),
            RevitExe = y["revit_exe"].S(),
            RevitVersion = y["revit_version"].S(),
            Model = y["model"].S(),
            KeepModelCopy = y["keep_model_copy"].Bool(),
            AllowDraw = y["allow_draw"].Bool(),
            StartupMin = t["startup_min"].Int(5),
            OpenModelMin = t["open_model_min"].Int(10),
            StepMin = t["step_min"].Int(30),
            Steps = y["steps"].Items.Select((s, i) => new SuiteStep(i + 1, s["tool"].S(), ToJson(s["args"]),
                s["purpose"].S("action"), s["why"].S(), s["on_error"].S("stop"))).ToList(),
            Checkpoints = ParseCheckpoints(y["checkpoints"]),
            ManualSteps = y["manual_steps"].Strings(),
            Raw = y,
        };
    }

    public static List<Checkpoint> ParseCheckpoints(Y node) =>
        node.Items.Select(c => new Checkpoint(c["step"].S(),
            c["expect"].Items.Select(e => new CheckpointExpect(e["role"].S(), e["kind"].S("file"), e["min"].Int(1), e["name_prefix"].S())).ToList()))
            .Where(c => c.Step.Length > 0).ToList();

    /// <summary>Map YAML → JsonObject (số / bool giữ kiểu).</summary>
    public static JsonObject ToJson(Y node)
    {
        var plain = node.ToPlain();
        return plain is Dictionary<string, object?> d ? (JsonObject)ToNode(d)! : new JsonObject();
    }

    private static JsonNode? ToNode(object? o) => o switch
    {
        null => null,
        Dictionary<string, object?> d => new JsonObject(d.Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, ToNode(kv.Value)))),
        List<object?> l => new JsonArray(l.Select(ToNode).ToArray()),
        string s when long.TryParse(s, out var n) => JsonValue.Create(n),
        string s when double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f) && s.Contains('.') => JsonValue.Create(f),
        "true" => JsonValue.Create(true),
        "false" => JsonValue.Create(false),
        string s => JsonValue.Create(s),
        _ => JsonValue.Create(o.ToString()),
    };

    /// <summary>Thay {dataset} và đường dẫn tương đối trong args (giá trị chuỗi có \ hoặc {dataset}).</summary>
    public JsonObject ResolveArgs(SuiteStep step, TestPaths paths)
    {
        var copy = (JsonObject)JsonNode.Parse(step.Args.ToJsonString())!;
        Walk(copy);
        return copy;

        void Walk(JsonNode? n)
        {
            switch (n)
            {
                case JsonObject o:
                    foreach (var k in o.Select(kv => kv.Key).ToList())
                    {
                        if (o[k] is JsonValue v && v.TryGetValue<string>(out var s) && s.Contains("{dataset}"))
                            o[k] = paths.Resolve(Dir, s, Dataset.Length > 0 ? Dataset : null);
                        else Walk(o[k]);
                    }
                    break;
                case JsonArray a:
                    for (var i = 0; i < a.Count; i++)
                    {
                        if (a[i] is JsonValue v && v.TryGetValue<string>(out var s) && s.Contains("{dataset}"))
                            a[i] = paths.Resolve(Dir, s, Dataset.Length > 0 ? Dataset : null);
                        else Walk(a[i]);
                    }
                    break;
            }
        }
    }

    public static string Compact(JsonObject o) => Text.Clip(o.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), 200);
}
