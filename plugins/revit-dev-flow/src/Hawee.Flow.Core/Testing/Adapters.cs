using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hawee.Flow.Core.Infra;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Hawee.Flow.Core.Testing;

public sealed record ToolCall(bool Ok, string Text, JsonNode? Json);

/// <summary>Gọi tool (gateway hawee vào Revit, hoặc bim-tracking). Hai chỗ hiện thực: MCP client (mặc định) và bản giả cho test.</summary>
public interface IToolClient : IAsyncDisposable
{
    Task<ToolCall> Call(string tool, JsonObject args, TimeSpan timeout);
}

/// <summary>Tiến trình Revit trên máy test.</summary>
public interface IHostProcess
{
    int Start(string exe);
    bool IsAlive(int pid);
    void Kill(int pid);
    /// <summary>Tiêu đề cửa sổ hộp thoại đang mở của tiến trình (hộp thoại khởi động chặn MCP).</summary>
    IReadOnlyList<string> DialogTitles(int pid);
}

/// <summary>
/// MCP client tới một server khai trong .mcp.json của repo dự án, .hawee-flow/mcp.json, ~/.claude.json (claude mcp add) hoặc biến HAWEE_FLOW_MCP_&lt;TÊN&gt; = JSON cấu hình.
/// Runner gọi gateway / tracking trực tiếp bằng kênh này — agent không phải gọi từng tool, output không vào context.
/// Khi có protocol pipe / HTTP API riêng, thêm một IToolClient khác, phần còn lại không đổi.
/// </summary>
public sealed class McpToolClient : IToolClient
{
    private readonly McpClient _client;
    private McpToolClient(McpClient c) => _client = c;

    public static async Task<McpToolClient> Connect(ProjectContext ctx, string serverName, CancellationToken ct = default)
    {
        var cfg = ResolveConfig(ctx, serverName) ?? throw new FlowException(
            $"không thấy cấu hình MCP '{serverName}' (thêm vào .mcp.json của repo hoặc `claude mcp add`, hoặc đặt HAWEE_FLOW_MCP_{Env(serverName)} = JSON {{command,args,env}} / {{url}})");
        IClientTransport transport;
        if (cfg["url"]?.GetValue<string>() is { Length: > 0 } url)
            transport = new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri(url), Name = serverName });
        else
        {
            var cmd = cfg["command"]?.GetValue<string>() ?? throw new FlowException($"cấu hình MCP '{serverName}' thiếu command/url");
            var opts = new StdioClientTransportOptions
            {
                Name = serverName,
                Command = cmd,
                Arguments = cfg["args"] is JsonArray a ? a.Select(x => x!.GetValue<string>()).ToList() : new List<string>(),
                WorkingDirectory = ctx.Root,
            };
            if (cfg["env"] is JsonObject env)
            {
                opts.EnvironmentVariables = new Dictionary<string, string?>();
                foreach (var (k, v) in env) opts.EnvironmentVariables[k] = Environment.ExpandEnvironmentVariables(v?.GetValue<string>() ?? "");
            }
            transport = new StdioClientTransport(opts);
        }
        var client = await McpClient.CreateAsync(transport, cancellationToken: ct);
        return new McpToolClient(client);
    }

    private static string Env(string name) => new string(name.ToUpperInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());

    internal static JsonObject? ResolveConfig(ProjectContext ctx, string serverName, string? claudeJson = null)
    {
        if (Environment.GetEnvironmentVariable("HAWEE_FLOW_MCP_" + Env(serverName)) is { Length: > 0 } json)
            return JsonNode.Parse(json) as JsonObject;
        foreach (var file in new[] { Path.Combine(ctx.Root, ".mcp.json"), Path.Combine(ctx.Root, ".hawee-flow", "mcp.json") })
        {
            if (!File.Exists(file)) continue;
            var root = JsonNode.Parse(File.ReadAllText(file)) as JsonObject;
            if (root?["mcpServers"]?[serverName] is JsonObject o) return o;
        }
        // khai bằng `claude mcp add`: ~/.claude.json — cấp local (projects["<repo>"].mcpServers) rồi cấp user (mcpServers)
        if ((claudeJson ?? ClaudeJson()) is { } user && File.Exists(user) && JsonNode.Parse(File.ReadAllText(user)) is JsonObject cj)
        {
            if (cj["projects"] is JsonObject projects)
            {
                var root = Norm(ctx.Root);
                var local = projects.Where(p => p.Value?["mcpServers"]?[serverName] is JsonObject)
                    .Where(p => Norm(p.Key) is var k && (root.Equals(k, StringComparison.OrdinalIgnoreCase) || root.StartsWith(k + "/", StringComparison.OrdinalIgnoreCase)))
                    .OrderByDescending(p => p.Key.Length).FirstOrDefault();
                if (local.Value?["mcpServers"]?[serverName] is JsonObject lo) return lo;
            }
            if (cj["mcpServers"]?[serverName] is JsonObject uo) return uo;
        }
        return null;

        static string Norm(string p) => p.Replace('\\', '/').TrimEnd('/');
    }

    /// <summary>Đường dẫn ~/.claude.json (HAWEE_FLOW_CLAUDE_JSON → $CLAUDE_CONFIG_DIR/.claude.json → thư mục người dùng).</summary>
    private static string? ClaudeJson()
    {
        if (Environment.GetEnvironmentVariable("HAWEE_FLOW_CLAUDE_JSON") is { Length: > 0 } f) return f;
        if (Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } d && File.Exists(Path.Combine(d, ".claude.json"))) return Path.Combine(d, ".claude.json");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return home.Length > 0 ? Path.Combine(home, ".claude.json") : null;
    }

    public async Task<ToolCall> Call(string tool, JsonObject args, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var dict = args.ToDictionary(kv => kv.Key, kv => (object?)(kv.Value is null ? null : JsonSerializer.Deserialize<JsonElement>(kv.Value.ToJsonString())));
        try
        {
            var r = await _client.CallToolAsync(tool, dict, cancellationToken: cts.Token);
            var text = string.Join("\n", r.Content.OfType<TextContentBlock>().Select(t => t.Text));
            JsonNode? json = r.StructuredContent is { } sc ? JsonNode.Parse(sc.GetRawText()) : TryParse(text);
            return new ToolCall(r.IsError != true, text, json);
        }
        catch (OperationCanceledException) { return new ToolCall(false, $"TIMEOUT sau {timeout.TotalSeconds:0} s gọi {tool}", null); }
        catch (Exception ex) { return new ToolCall(false, $"{ex.GetType().Name}: {ex.Message}", null); }
    }

    internal static JsonNode? TryParse(string text)
    {
        var t = text.Trim();
        if (t.Length == 0 || (t[0] != '{' && t[0] != '[')) return null;
        try { return JsonNode.Parse(t); } catch { return null; }
    }

    public ValueTask DisposeAsync() => _client.DisposeAsync();
}

/// <summary>Revit trên Windows: Start-Process, kill đúng PID, liệt kê hộp thoại bằng EnumWindows.</summary>
public sealed class WindowsHostProcess : IHostProcess
{
    public int Start(string exe)
    {
        if (!File.Exists(exe)) throw new FlowException($"không thấy {exe}");
        var p = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true }) ?? throw new FlowException($"không mở được {exe}");
        return p.Id;
    }

    public bool IsAlive(int pid)
    {
        try { return !Process.GetProcessById(pid).HasExited; } catch { return false; }
    }

    public void Kill(int pid)
    {
        try { Process.GetProcessById(pid).Kill(entireProcessTree: false); } catch { /* đã thoát */ }
    }

    public IReadOnlyList<string> DialogTitles(int pid)
    {
        var titles = new List<string>();
        if (!OperatingSystem.IsWindows()) return titles;
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out var owner);
            if (owner != (uint)pid || !IsWindowVisible(h)) return true;
            var cls = new StringBuilder(64);
            GetClassName(h, cls, cls.Capacity);
            if (cls.ToString() != "#32770") return true; // lớp hộp thoại chuẩn của Windows
            var sb = new StringBuilder(256);
            GetWindowText(h, sb, sb.Capacity);
            titles.Add(sb.ToString());
            return true;
        }, IntPtr.Zero);
        return titles;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder s, int n);
}

/// <summary>Chữ ký Authenticode của DLL add-in (máy test không có người bấm hộp thoại "publisher").</summary>
public static class Authenticode
{
    public static string Check(string file)
    {
        if (!OperatingSystem.IsWindows()) return "không kiểm được (không phải Windows)";
        var info = new WINTRUST_FILE_INFO { cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(), pcwszFilePath = file };
        var pFile = Marshal.AllocHGlobal(Marshal.SizeOf(info));
        try
        {
            Marshal.StructureToPtr(info, pFile, false);
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(), dwUIChoice = 2 /*WTD_UI_NONE*/, fdwRevocationChecks = 0,
                dwUnionChoice = 1 /*WTD_CHOICE_FILE*/, pFile = pFile, dwStateAction = 0, dwProvFlags = 0x00000010 /*cache only url*/,
            };
            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
            var hr = WinVerifyTrust(IntPtr.Zero, action, ref data);
            return hr == 0 ? "Valid" : $"0x{hr:X8}";
        }
        finally { Marshal.FreeHGlobal(pFile); }
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid action, ref WINTRUST_DATA data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO { public uint cbStruct; public string pcwszFilePath; public IntPtr hFile; public IntPtr pgKnownSubject; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct; public IntPtr pPolicyCallbackData; public IntPtr pSIPClientData; public uint dwUIChoice;
        public uint fdwRevocationChecks; public uint dwUnionChoice; public IntPtr pFile; public uint dwStateAction;
        public IntPtr hWVTStateData; public IntPtr pwszURLReference; public uint dwProvFlags; public uint dwUIContext; public IntPtr pSignatureSettings;
    }
}
