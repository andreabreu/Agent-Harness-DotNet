namespace AgentPoc.Core;

public sealed record MemoryItem(string Content, DateTime CreatedAt);

/// <summary>Memoria de longo prazo por agente (busca textual em memories).</summary>
public sealed class MemoryStore
{
    private readonly Db _db;
    public MemoryStore(Db db) => _db = db;

    public Task SaveAsync(string agentId, string content, Guid runId) =>
        _db.ExecAsync("insert into memories (agent_id, content, run_id) values (@a, @c, @r)", ("a", agentId), ("c", content), ("r", runId));

    public async Task<List<MemoryItem>> RecallAsync(string agentId, string query, int limit = 5)
    {
        var tsq = TextSearch.ToTsQuery(query);
        if (tsq == null) return new List<MemoryItem>();
        var rows = await _db.QueryAsync(
            @"select content, created_at from memories
               where agent_id = @a and search @@ to_tsquery('simple', @q)
               order by ts_rank(search, to_tsquery('simple', @q)) desc, created_at desc limit @l",
            ("a", agentId), ("q", tsq), ("l", limit));
        return rows.Select(r => new MemoryItem(r.S("content"), r.T("created_at"))).ToList();
    }

    public static string Format(IEnumerable<MemoryItem> items) =>
        string.Join("\n", items.Select(m => $"- ({m.CreatedAt:yyyy-MM-dd}) {m.Content}"));
}
