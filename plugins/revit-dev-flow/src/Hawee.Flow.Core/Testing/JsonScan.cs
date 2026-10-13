using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Hawee.Flow.Core.Testing;

/// <summary>Đọc kết quả tool có hình dạng chưa biết trước: tìm theo tên khoá, đệ quy.</summary>
public static class JsonScan
{
    public static IEnumerable<JsonObject> Objects(JsonNode? n)
    {
        switch (n)
        {
            case JsonObject o:
                yield return o;
                foreach (var (_, v) in o) foreach (var x in Objects(v)) yield return x;
                break;
            case JsonArray a:
                foreach (var v in a) foreach (var x in Objects(v)) yield return x;
                break;
        }
    }

    public static string? Str(JsonObject o, params string[] keys)
    {
        foreach (var k in keys)
        {
            var hit = o.FirstOrDefault(kv => kv.Key.Equals(k, StringComparison.OrdinalIgnoreCase));
            if (hit.Value is JsonValue v) return v.ToString();
        }
        return null;
    }

    /// <summary>PID trong kết quả list_instances: khoá pid / processId, hoặc "pid 1234" trong văn bản.</summary>
    public static List<int> Pids(JsonNode? json, string text)
    {
        var pids = Objects(json).Select(o => Str(o, "pid", "processId", "process_id", "Pid", "ProcessId"))
            .Where(s => int.TryParse(s, out _)).Select(s => int.Parse(s!)).ToList();
        if (pids.Count == 0)
            pids = Regex.Matches(text, @"(?i)\b(?:pid|process\s*id)\D{0,3}(\d{2,7})").Select(m => int.Parse(m.Groups[1].Value)).ToList();
        return pids.Distinct().ToList();
    }

    /// <summary>Trạng thái step chuẩn hoá từ kết quả run_timeline: mọi object có stepCode + status.</summary>
    public static List<(string Step, string Status)> Steps(JsonNode? json) =>
        Objects(json)
            .Select(o => (Step: Str(o, "stepCode", "step_code", "StepCode", "code"), Status: Str(o, "status", "Status", "state")))
            .Where(x => x.Step is { Length: > 0 } && x.Status is { Length: > 0 })
            .Select(x => (x.Step!, x.Status!.ToUpperInvariant()))
            .GroupBy(x => x.Item1).Select(g => g.Last()).ToList();

    public sealed record Artifact(string Role, string Name, string? Url, string? Path, JsonNode? Inline, string Step);

    /// <summary>Artifact trong kết quả run_outputs: object có role (hoặc artifactRole) + tên + url/path hoặc nội dung metadata.</summary>
    public static List<Artifact> Artifacts(JsonNode? json) =>
        Objects(json)
            .Select(o => new Artifact(
                Str(o, "role", "artifactRole", "ArtifactRole") ?? "",
                Str(o, "name", "fileName", "file_name", "Name") ?? "",
                Str(o, "url", "downloadUrl", "download_url", "uri", "href"),
                Str(o, "path", "filePath", "file_path", "localPath"),
                o.FirstOrDefault(kv => kv.Key is "metadata" or "content" or "data" && kv.Value is JsonObject or JsonArray).Value,
                Str(o, "stepCode", "step_code", "StepCode") ?? ""))
            .Where(a => a.Role.Length > 0 && (a.Name.Length > 0 || a.Url is not null || a.Path is not null || a.Inline is not null))
            .ToList();

    public static bool HasMore(JsonNode? json, int page)
    {
        foreach (var o in Objects(json).Take(3))
        {
            if (Str(o, "hasMore", "has_more", "hasNextPage") is "true" or "True") return true;
            if (int.TryParse(Str(o, "totalPages", "total_pages", "pageCount"), out var total)) return page < total;
        }
        return false;
    }
}
