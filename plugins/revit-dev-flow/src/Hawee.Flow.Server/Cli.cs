using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace Hawee.Flow.Server;

/// <summary>Gọi tool MCP từ dòng lệnh: <c>hawee-flow call flow_next '{"task":"123"}'</c>. Dùng để kiểm tay và trong test.</summary>
public static class Cli
{
    public static string Version => typeof(Cli).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public static IEnumerable<(string Name, MethodInfo Method)> Tools() =>
        typeof(Cli).Assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Select(m => (Attr: m.GetCustomAttribute<McpServerToolAttribute>(), Method: m))
            .Where(x => x.Attr is not null)
            .Select(x => (x.Attr!.Name ?? x.Method.Name, x.Method));

    public static int Run(string[] args)
    {
        if (args[0] == "tools")
        {
            foreach (var (name, m) in Tools().OrderBy(t => t.Name))
                Console.WriteLine($"{name,-18} {m.GetCustomAttribute<DescriptionAttribute>()?.Description}");
            return 0;
        }
        if (args.Length < 2) { Console.Error.WriteLine("dùng: hawee-flow call <tool> [json]"); return 2; }
        var tool = Tools().FirstOrDefault(t => t.Name == args[1]);
        if (tool.Method is null) { Console.Error.WriteLine($"không có tool {args[1]}"); return 2; }
        using var doc = JsonDocument.Parse(args.Length > 2 ? args[2] : "{}");
        Console.WriteLine(Invoke(tool.Method, doc.RootElement));
        return 0;
    }

    public static string Invoke(MethodInfo m, JsonElement json)
    {
        var ps = m.GetParameters();
        var values = new object?[ps.Length];
        for (var i = 0; i < ps.Length; i++)
        {
            var p = ps[i];
            if (json.ValueKind == JsonValueKind.Object && json.TryGetProperty(p.Name!, out var v))
                values[i] = v.Deserialize(p.ParameterType);
            else if (p.HasDefaultValue) values[i] = p.DefaultValue;
            else throw new ArgumentException($"thiếu tham số '{p.Name}'");
        }
        var result = m.Invoke(null, values);
        if (result is Task<string> ts) return ts.GetAwaiter().GetResult();
        return result?.ToString() ?? "";
    }
}
