using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentPoc.Core;

public sealed record ProviderCfg(string Id, string Kind, string BaseUrl, string? ApiKeyEnv, string AuthHeader, string ExtraHeadersJson);

public sealed record ModelCfg(
    string Id, ProviderCfg Provider, string ModelName, bool SupportsTools, bool SupportsTemperature,
    string MaxTokensParam, int MaxOutputTokens, int ContextWindow, decimal CostInPerMTok, decimal CostOutPerMTok,
    bool SupportsStreaming = false, string? ReasoningEffort = null)
{
    public string ResolvedName => Env.Expand(ModelName);
}

public sealed record AgentCfg(
    string Id, string Description, string ModelId, string SystemPrompt, string[] InitialTools, string[] AllowedTools,
    string[] CanDelegateTo, int MaxSteps, int MaxTokens, float? Temperature, int Version);

/// <param name="SideEffect">Pode ter efeito fora do agente; se interrompida por uma queda, nao e repetida sem uma pessoa decidir.</param>
public sealed record ToolDef(string Name, string Description, string Kind, string ParametersJson, string Policy, string ConfigJson,
                             bool SideEffect = true);

/// <summary>Le agentes, modelos e provedores do banco. Nada de configuracao em codigo.</summary>
public sealed class ConfigRepo
{
    private readonly Db _db;
    public ConfigRepo(Db db) => _db = db;

    public async Task<AgentCfg?> GetAgentAsync(string id)
    {
        var r = await _db.QueryOneAsync("select * from agents where id = @id and enabled", ("id", id));
        return r == null ? null : MapAgent(r);
    }

    public async Task<List<AgentCfg>> ListAgentsAsync() =>
        (await _db.QueryAsync("select * from agents where enabled order by id")).Select(MapAgent).ToList();

    private static AgentCfg MapAgent(Dictionary<string, object?> r) => new(
        r.S("id"), r.S("description"), r.S("model_id"), r.S("system_prompt"), r.A("initial_tools"), r.A("allowed_tools"),
        r.A("can_delegate_to"), r.I("max_steps"), r.I("max_tokens"), r.FN("temperature"), r.I("version"));

    public async Task<ModelCfg> GetModelAsync(string id)
    {
        var r = await _db.QueryOneAsync(
            @"select m.*, p.kind, p.base_url, p.api_key_env, p.auth_header, p.extra_headers::text as extra_headers
                from models m join providers p on p.id = m.provider_id
               where m.id = @id", ("id", id))
            ?? throw new InvalidOperationException($"Modelo '{id}' nao encontrado na tabela models.");
        var prov = new ProviderCfg(r.S("provider_id"), r.S("kind"), r.S("base_url"), r.SN("api_key_env"), r.S("auth_header"), r.S("extra_headers"));
        return new ModelCfg(r.S("id"), prov, r.S("model_name"), r.B("supports_tools"), r.B("supports_temperature"),
            r.S("max_tokens_param"), r.I("max_output_tokens"), r.I("context_window"), r.D("cost_in_per_mtok"), r.D("cost_out_per_mtok"),
            r.B("supports_streaming"), r.SN("reasoning_effort"));
    }

    /// <summary>Lista problemas de configuracao (variaveis ausentes) para um modelo.</summary>
    public static List<string> Problems(ModelCfg m)
    {
        var list = new List<string>();
        foreach (var v in Env.MissingVars(m.Provider.BaseUrl)) list.Add($"{v} (base_url do provedor {m.Provider.Id})");
        foreach (var v in Env.MissingVars(m.ModelName)) list.Add($"{v} (nome do modelo {m.Id})");
        if (m.Provider.AuthHeader != "none" && !string.IsNullOrEmpty(m.Provider.ApiKeyEnv) && string.IsNullOrEmpty(Env.Get(m.Provider.ApiKeyEnv)))
            list.Add($"{m.Provider.ApiKeyEnv} (chave do provedor {m.Provider.Id})");
        return list;
    }
}

public static class Glob
{
    /// <summary>'*' casa tudo; 'memory_*' casa por prefixo; senao igualdade.</summary>
    public static bool Match(IEnumerable<string> patterns, string name) =>
        patterns.Any(p => p == "*" || p == name ||
                          (p.EndsWith('*') && name.StartsWith(p.TrimEnd('*'), StringComparison.Ordinal)));
}

/// <summary>Monta consultas de busca textual (tsquery) tolerantes a acentos e prefixos.</summary>
public static class TextSearch
{
    private static readonly HashSet<string> Stop = new()
    {
        "de", "da", "do", "das", "dos", "um", "uma", "para", "por", "com", "que", "em", "no", "na", "nos", "nas",
        "os", "as", "se", "ao", "the", "and", "or", "of", "to", "for", "in", "on", "is"
    };

    public static string? ToTsQuery(string query)
    {
        var normalized = RemoveDiacritics((query ?? "").ToLowerInvariant());
        var terms = Regex.Matches(normalized, "[a-z0-9]+")
            .Select(m => m.Value)
            .Where(w => w.Length >= 2 && !Stop.Contains(w))
            .Distinct()
            .Take(12)
            .ToList();
        return terms.Count == 0 ? null : string.Join(" | ", terms.Select(t => t + ":*"));
    }

    public static string RemoveDiacritics(string s)
    {
        var n = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(n.Length);
        foreach (var c in n)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}
