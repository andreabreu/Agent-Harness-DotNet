using AgentPoc.Core;

namespace AgentPoc.Tools;

/// <summary>Registro de ferramentas no banco + descoberta por busca textual.</summary>
public sealed class ToolRegistry
{
    private const string Cols = "name, description, kind, parameters::text as parameters, policy, config::text as config";
    private readonly Db _db;
    public ToolRegistry(Db db) => _db = db;

    private static ToolDef Map(Dictionary<string, object?> r) =>
        new(r.S("name"), r.S("description"), r.S("kind"), r.S("parameters"), r.S("policy"), r.S("config"));

    public async Task<Dictionary<string, ToolDef>> LoadAsync(IEnumerable<string> names)
    {
        var arr = names.Distinct().ToArray();
        var rows = await _db.QueryAsync($"select {Cols} from tools where enabled and name = any(@n)", ("n", arr));
        return rows.Select(Map).ToDictionary(t => t.Name, StringComparer.Ordinal);
    }

    public async Task<List<ToolDef>> ListAsync() =>
        (await _db.QueryAsync($"select {Cols} from tools where enabled order by kind, name")).Select(Map).ToList();

    /// <summary>
    /// Busca ferramentas por relevancia, filtrando pelo escopo (allowed_tools) do agente
    /// e ignorando as que ja estao carregadas.
    /// </summary>
    public async Task<List<ToolDef>> SearchAsync(string query, string[] allowed, ISet<string> alreadyLoaded, int limit)
    {
        var tsq = TextSearch.ToTsQuery(query);
        if (tsq == null) return new List<ToolDef>();
        var rows = await _db.QueryAsync(
            $@"select {Cols} from tools
                where enabled and search @@ to_tsquery('simple', @q)
                order by ts_rank(search, to_tsquery('simple', @q)) desc, name
                limit 40", ("q", tsq));
        return rows.Select(Map)
            .Where(t => Glob.Match(allowed, t.Name) && !alreadyLoaded.Contains(t.Name))
            .Take(limit)
            .ToList();
    }
}
