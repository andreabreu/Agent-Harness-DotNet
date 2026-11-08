using System.Text.RegularExpressions;

namespace AgentPoc.Core;

/// <summary>Variaveis de ambiente, arquivo .env e caminhos do repositorio.</summary>
public static class Env
{
    public static string RepoRoot { get; private set; } = Directory.GetCurrentDirectory();
    public static string Workspace => Path.Combine(RepoRoot, "workspace");
    public static string EvidenceDir => Path.Combine(RepoRoot, "evidence");
    public static string SqlDir => Path.Combine(RepoRoot, "sql");

    public static void Init()
    {
        RepoRoot = FindRepoRoot() ?? Directory.GetCurrentDirectory();
        LoadDotEnv(Path.Combine(RepoRoot, ".env"));
        Directory.CreateDirectory(Workspace);
        Directory.CreateDirectory(EvidenceDir);
    }

    private static string? FindRepoRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(start);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "docker-compose.yml")) &&
                    Directory.Exists(Path.Combine(dir.FullName, "sql")))
                    return dir.FullName;
                dir = dir.Parent;
            }
        }
        return null;
    }

    private static void LoadDotEnv(string path)
    {
        if (!File.Exists(path)) return;
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith("export ")) line = line.Substring(7).Trim();
            var idx = line.IndexOf('=');
            if (idx <= 0) continue;
            var key = line.Substring(0, idx).Trim();
            var val = line.Substring(idx + 1).Trim();
            if (val.Length >= 2 && ((val[0] == '"' && val[^1] == '"') || (val[0] == '\'' && val[^1] == '\'')))
                val = val.Substring(1, val.Length - 2);
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key)))
                Environment.SetEnvironmentVariable(key, val);
        }
    }

    public static string Get(string key, string fallback = "")
    {
        var v = Environment.GetEnvironmentVariable(key);
        return string.IsNullOrEmpty(v) ? fallback : v;
    }

    private static readonly Regex VarRx = new(@"\$\{([A-Za-z_][A-Za-z0-9_]*)\}", RegexOptions.Compiled);

    /// <summary>Substitui ${VAR} pelo valor da variavel de ambiente.</summary>
    public static string Expand(string value) =>
        VarRx.Replace(value, m => Environment.GetEnvironmentVariable(m.Groups[1].Value) ?? "");

    public static List<string> MissingVars(string value) =>
        VarRx.Matches(value)
            .Select(m => m.Groups[1].Value)
            .Where(v => string.IsNullOrEmpty(Environment.GetEnvironmentVariable(v)))
            .Distinct()
            .ToList();
}
