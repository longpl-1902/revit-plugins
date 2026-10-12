using System.Diagnostics;
using System.Text;

namespace Hawee.Flow.Core.Infra;

public sealed record ProcResult(int ExitCode, string Output, bool TimedOut)
{
    public string[] Lines => Output.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
}

public interface IProcRunner
{
    ProcResult Run(string file, IEnumerable<string> args, string cwd, TimeSpan? timeout = null, IDictionary<string, string>? env = null);
}

/// <summary>Chạy tiến trình con, gom stdout+stderr. Không bao giờ ghi ra stdout của server MCP.</summary>
public sealed class ProcRunner : IProcRunner
{
    public static readonly ProcRunner Instance = new();

    public ProcResult Run(string file, IEnumerable<string> args, string cwd, TimeSpan? timeout = null, IDictionary<string, string>? env = null)
    {
        var psi = new ProcessStartInfo(file)
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (env is not null) foreach (var (k, v) in env) psi.Environment[k] = v;

        var sb = new StringBuilder();
        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (sb) sb.AppendLine(e.Data); };
        try { p.Start(); }
        catch (Exception ex) { return new ProcResult(-1, $"không chạy được {file}: {ex.Message}", false); }
        p.StandardInput.Close();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        var ms = (int)(timeout ?? TimeSpan.FromMinutes(30)).TotalMilliseconds;
        if (!p.WaitForExit(ms))
        {
            try { p.Kill(entireProcessTree: true); } catch { /* đã thoát */ }
            return new ProcResult(-1, sb.ToString(), true);
        }
        p.WaitForExit();
        return new ProcResult(p.ExitCode, sb.ToString(), false);
    }
}

/// <summary>Gọi script PowerShell của BIM_KB_Base (Windows: powershell.exe 5.1; nơi khác: pwsh).</summary>
public static class Ps
{
    public static string Exe =>
        Environment.GetEnvironmentVariable("HAWEE_FLOW_POWERSHELL") is { Length: > 0 } e ? e
        : OperatingSystem.IsWindows() ? "powershell.exe" : "pwsh";

    public static ProcResult RunFile(IProcRunner runner, string script, IEnumerable<string> args, string cwd, TimeSpan? timeout = null)
    {
        var all = new List<string> { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script };
        all.AddRange(args);
        return runner.Run(Exe, all, cwd, timeout);
    }
}
