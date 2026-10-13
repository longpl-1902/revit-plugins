using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Hawee.Flow.Core.Infra;

namespace Hawee.Flow.Core.Testing;

/// <summary>
/// Luật an toàn khi chạy suite (thay cho hook revit-test-guard + phiếu setup-guard: runner gọi gateway trực tiếp nên kiểm ngay tại đây,
/// agent không gọi được tool ghi vào Revit). Fail closed: không chắc → chặn.
///  - revit_test.forbidden: chặn khi điều kiện 'when' khớp (không 'when' = luôn chặn)
///  - forbidden_unless_allow_draw: chỉ khi suite allow_draw và blocked_by rỗng
///  - step purpose: setup: tool phải nằm trong setup_tools (có approved_by); document của PID phải nằm trong RUN_DIR (kiểm ở runner)
///  - tool runner tự quản (tools.open_document / close_document / call / list_instances / list_tools): không được là step —
///    runner tự mở bản sao và đóng; document của PID phải nằm trong RUN_DIR trước mọi step (kiểm ở runner)
///  - 'when' dạng k=v mà args thiếu k → chặn (chặt hơn revit-test-guard.ps1 của Base: tool có thể mặc định dry_run=false)
/// </summary>
public sealed class Policy(Y testBlock)
{
    public sealed record Decision(bool Allowed, string Reason);

    public Decision Check(SuiteStep step, JsonObject args, bool allowDraw)
    {
        var tool = step.Tool;
        if (string.IsNullOrWhiteSpace(tool)) return new(false, $"step {step.Index}: thiếu tool");

        var owned = new[] { "open_document", "close_document", "call", "list_instances", "list_tools" }
            .Select(k => testBlock["tools"][k].S()).Where(t => t.Length > 0).ToHashSet();
        if (owned.Contains(tool)) return new(false, $"step {step.Index}: {tool} là tool runner tự quản (mở / đóng bản sao, gọi tool) — không dùng làm step");

        if (step.IsSetup)
        {
            var entry = testBlock["setup_tools"].Items.FirstOrDefault(s => (s.IsMap ? s["tool"].S() : s.S()) == tool);
            if (entry is null) return new(false, $"step {step.Index} purpose: setup dùng {tool} — không có trong {Key("setup_tools")}");
            if (entry.IsMap && entry["approved_by"].S().Length == 0) return new(false, $"{tool} trong setup_tools chưa có approved_by");
            if (DrawTools().Contains(tool)) return new(false, $"{tool} thuộc forbidden_unless_allow_draw — không được dùng làm bước dựng tình huống");
            if (step.Why.Length == 0) return new(false, $"step {step.Index} purpose: setup thiếu why (kịch bản nào)");
            return new(true, "setup_tools");
        }

        foreach (var f in testBlock["forbidden"].Items)
        {
            var ft = f.IsMap ? f["tool"].S() : f.S();
            if (ft != tool) continue;
            var when = f.IsMap ? f["when"].S() : "";
            if (WhenMatches(when, args))
            {
                var missing = MissingKeys(when, args);
                return new(false, $"{tool} bị cấm{(when.Length > 0 ? $" khi {when}" : "")} ({Key("forbidden")})"
                    + (missing.Count > 0 ? $" — args không có {string.Join(", ", missing)}: ghi rõ giá trị an toàn (vd. dry_run: true)" : ""));
            }
        }
        if (DrawTools().Contains(tool))
        {
            var blockedBy = testBlock.At("forbidden_unless_allow_draw.blocked_by").S();
            if (!allowDraw) return new(false, $"{tool} chỉ được gọi khi suite allow_draw: true");
            if (blockedBy.Length > 0) return new(false, $"{tool} đang bị chặn bởi known-issue {blockedBy}");
        }
        return new(true, "");
    }

    private HashSet<string> DrawTools() => testBlock.At("forbidden_unless_allow_draw.tools").Strings().ToHashSet();
    private static string Key(string k) => "revit_test." + k;

    /// <summary>
    /// 'when': mệnh đề nối bằng " hoặc " / " or "; "k=v" (so chuỗi không phân biệt hoa thường) hoặc "k" (có mặt, khác rỗng).
    /// Mệnh đề không hiểu → coi như khớp (chặn) để an toàn — giống revit-test-guard.ps1.
    /// "k=v" mà args không có k → coi như khớp (chặn): không biết giá trị mặc định của tool thì không đoán là an toàn.
    /// </summary>
    public static bool WhenMatches(string when, JsonObject args)
    {
        if (string.IsNullOrWhiteSpace(when)) return true;
        foreach (var term in Regex.Split(when, @"\s+(?:hoặc|or)\s+"))
        {
            var t = term.Trim();
            var kv = Regex.Match(t, @"^([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.+)$");
            if (kv.Success)
            {
                var v = Value(args, kv.Groups[1].Value);
                if (v is null || v.Equals(kv.Groups[2].Value.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
                continue;
            }
            var k = Regex.Match(t, @"^([A-Za-z_][A-Za-z0-9_]*)$");
            if (k.Success)
            {
                var v = Value(args, k.Groups[1].Value);
                if (!string.IsNullOrEmpty(v)) return true;
                continue;
            }
            return true;
        }
        return false;
    }

    private static List<string> MissingKeys(string when, JsonObject args) =>
        Regex.Matches(when, @"([A-Za-z_][A-Za-z0-9_]*)\s*=").Select(m => m.Groups[1].Value).Where(k => Value(args, k) is null).Distinct().ToList();

    private static string? Value(JsonObject args, string key) =>
        args.TryGetPropertyValue(key, out var n) && n is not null
            ? n is JsonValue v ? (v.TryGetValue<string>(out var s) ? s : v.ToJsonString().Trim('"')) : n.ToJsonString()
            : null;

    /// <summary>Đường dẫn <paramref name="path"/> có nằm trong <paramref name="dir"/> (không phân biệt hoa thường, chuẩn hoá \ /).</summary>
    public static bool IsInside(string path, string dir)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        static string N(string p) => p.Replace('\\', '/').TrimEnd('/').ToLowerInvariant();
        var full = N(Path.GetFullPath(path.Replace('\\', Path.DirectorySeparatorChar)));
        var root = N(Path.GetFullPath(dir.Replace('\\', Path.DirectorySeparatorChar)));
        return full.StartsWith(root + "/", StringComparison.Ordinal);
    }
}
