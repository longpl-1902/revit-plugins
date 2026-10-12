using Hawee.Flow.Core.Infra;

namespace Hawee.Flow.Core.Checks;

/// <summary>Lệnh git chỉ đọc: HEAD, nhánh, file thay đổi so với nhánh tích hợp.</summary>
public sealed class Git(ProjectContext ctx, IProcRunner? runner = null)
{
    private readonly IProcRunner _run = runner ?? ProcRunner.Instance;

    private ProcResult G(params string[] args) => _run.Run("git", args, ctx.Root, TimeSpan.FromMinutes(2));

    public string Head() => G("rev-parse", "HEAD").Lines.FirstOrDefault()?.Trim() ?? "";
    public string Branch() => G("rev-parse", "--abbrev-ref", "HEAD").Lines.FirstOrDefault()?.Trim() ?? "";
    public List<string> Uncommitted() => G("status", "--porcelain").Lines.Select(l => l.Length > 3 ? l[3..].Trim() : l).ToList();

    /// <summary>
    /// Nhánh tích hợp: tham số → HAWEE_INTEGRATION_BRANCH → origin/DEV, DEV, origin/main, main (cái nào có thật).
    /// </summary>
    public string IntegrationRef(string? given)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(given)) candidates.Add(given.Trim());
        if (Environment.GetEnvironmentVariable("HAWEE_INTEGRATION_BRANCH") is { Length: > 0 } env) candidates.Add(env);
        candidates.AddRange(new[] { "origin/DEV", "DEV", "origin/main", "main", "origin/master", "master" });
        foreach (var c in candidates.Distinct())
            if (G("rev-parse", "--verify", "--quiet", c + "^{commit}").ExitCode == 0) return c;
        throw new FlowException("không xác định được nhánh tích hợp — truyền base_ref (vd. origin/DEV)");
    }

    /// <summary>File thay đổi của task: diff so với nhánh tích hợp + working tree + untracked.</summary>
    public List<string> ChangedFiles(string baseRef)
    {
        var files = new HashSet<string>(G("diff", "--name-only", baseRef + "...HEAD").Lines.Select(l => l.Trim()));
        foreach (var f in Uncommitted()) files.Add(f.Contains(" -> ") ? f.Split(" -> ")[1] : f);
        return files.Where(f => f.Length > 0).OrderBy(f => f).ToList();
    }

    public (int Added, int Deleted) DiffSize(string baseRef)
    {
        int a = 0, d = 0;
        foreach (var l in G("diff", "--numstat", baseRef).Lines)
        {
            var p = l.Split('\t');
            if (p.Length >= 2 && int.TryParse(p[0], out var x) && int.TryParse(p[1], out var y)) { a += x; d += y; }
        }
        return (a, d);
    }
}
