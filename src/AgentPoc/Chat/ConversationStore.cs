using System.Text.Json.Nodes;
using AgentPoc.Core;
using AgentPoc.Llm;

namespace AgentPoc.Chat;

public sealed record Conversation(
    Guid Id, string AgentId, string? Title, string[] ActiveTools, string? Summary, int SummarizedUpto,
    DateTime CreatedAt, DateTime UpdatedAt);

public sealed record StoredMessage(int Seq, ChatMessage Message, Guid? RunId, DateTime CreatedAt);

public sealed record TaskNotice(Guid TaskId, string Agent, string Status, string? Result, string? Error);

/// <summary>Persistencia das conversas e mensagens do chat no Postgres.</summary>
public sealed class ConversationStore
{
    private readonly Db _db;
    public ConversationStore(Db db) => _db = db;

    public async Task<Guid> CreateAsync(string agentId) =>
        (Guid)(await _db.ScalarAsync("insert into conversations (agent_id) values (@a) returning id", ("a", agentId)))!;

    public async Task<Conversation?> GetAsync(Guid id)
    {
        var r = await _db.QueryOneAsync("select * from conversations where id = @id", ("id", id));
        return r == null ? null : Map(r);
    }

    /// <summary>Aceita o id completo ou um prefixo (como o exibido na CLI).</summary>
    public async Task<Conversation?> FindAsync(string idOrPrefix)
    {
        if (Guid.TryParse(idOrPrefix, out var id)) return await GetAsync(id);
        var prefix = idOrPrefix.Replace("-", "").ToLowerInvariant();
        var r = await _db.QueryOneAsync(
            "select * from conversations where replace(id::text, '-', '') like @p order by updated_at desc limit 1", ("p", prefix + "%"));
        return r == null ? null : Map(r);
    }

    public async Task<List<Conversation>> ListAsync(int limit) =>
        (await _db.QueryAsync("select * from conversations order by updated_at desc limit @l", ("l", limit))).Select(Map).ToList();

    public async Task<List<StoredMessage>> LoadMessagesAsync(Guid conversationId, int afterSeq = 0) =>
        (await _db.QueryAsync(
            @"select seq, role, content, tool_calls::text as tool_calls, tool_call_id, tool_name, run_id, created_at
                from conversation_messages where conversation_id = @c and seq > @s order by seq",
            ("c", conversationId), ("s", afterSeq)))
        .Select(r => new StoredMessage(r.I("seq"), MapMessage(r), r.GN("run_id"), r.T("created_at")))
        .ToList();

    public async Task<int> AppendAsync(Guid conversationId, Guid runId, ChatMessage m)
    {
        var seq = await _db.ScalarAsync(
            @"insert into conversation_messages (conversation_id, seq, role, content, tool_calls, tool_call_id, tool_name, run_id)
              select @c, coalesce(max(seq), 0) + 1, @role, @content, @calls, @callId, @tool, @run
                from conversation_messages where conversation_id = @c
              returning seq",
            ("c", conversationId), ("role", m.Role), ("content", m.Content), ("calls", ToolCallsJson(m.ToolCalls)),
            ("callId", m.ToolCallId), ("tool", m.ToolName), ("run", runId));
        await _db.ExecAsync("update conversations set updated_at = now() where id = @c", ("c", conversationId));
        return Convert.ToInt32(seq);
    }

    public Task UpdateAfterTurnAsync(Guid conversationId, IEnumerable<string> activeTools, string firstUserText) =>
        _db.ExecAsync(
            "update conversations set active_tools = @t, title = coalesce(title, @title), updated_at = now() where id = @c",
            ("t", activeTools.OrderBy(t => t).ToArray()), ("title", Text.Trunc(firstUserText.Replace('\n', ' '), 60)), ("c", conversationId));

    public Task SaveSummaryAsync(Guid conversationId, string summary, int summarizedUpto) =>
        _db.ExecAsync("update conversations set summary = @s, summarized_upto = @u where id = @c",
            ("s", summary), ("u", summarizedUpto), ("c", conversationId));

    /// <summary>Resultados de tarefas assincronas criadas nesta conversa que ainda nao foram entregues; marca como entregues.</summary>
    public async Task<List<TaskNotice>> TakeTaskNoticesAsync(Guid conversationId) =>
        (await _db.QueryAsync(
            @"update tasks t set notified_at = now()
                from runs r
               where r.id = t.created_by_run and r.conversation_id = @c
                 and t.status in ('done', 'failed') and t.notified_at is null
           returning t.id, t.agent_id, t.status, t.result, t.error", ("c", conversationId)))
        .Select(r => new TaskNotice(r.G("id"), r.S("agent_id"), r.S("status"), r.SN("result"), r.SN("error")))
        .ToList();

    /// <summary>Run raiz da conversa suspenso esperando uma pessoa (no maximo um por vez).</summary>
    public async Task<Guid?> WaitingRunAsync(Guid conversationId) =>
        await _db.ScalarAsync(
            @"select id from runs where conversation_id = @c and depth = 0 and status = 'waiting_human'
               order by started_at desc limit 1", ("c", conversationId)) as Guid?;

    public Task CloseRunAsync(Guid runId, string status, string output) =>
        _db.ExecAsync("update runs set status = @s, output = @o, finished_at = now() where id = @id",
            ("s", status), ("o", output), ("id", runId));

    private static Conversation Map(Dictionary<string, object?> r) => new(
        r.G("id"), r.S("agent_id"), r.SN("title"), r.A("active_tools"), r.SN("summary"), r.I("summarized_upto"),
        r.T("created_at"), r.T("updated_at"));

    private static ChatMessage MapMessage(Dictionary<string, object?> r) => new()
    {
        Role = r.S("role"),
        Content = r.SN("content"),
        ToolCalls = ParseToolCalls(r.SN("tool_calls")),
        ToolCallId = r.SN("tool_call_id"),
        ToolName = r.SN("tool_name")
    };

    private static Jsonb? ToolCallsJson(List<ToolCall>? calls)
    {
        if (calls is not { Count: > 0 }) return null;
        var arr = new JsonArray();
        foreach (var c in calls) arr.Add(new JsonObject { ["id"] = c.Id, ["name"] = c.Name, ["arguments"] = c.ArgumentsJson });
        return new Jsonb(arr.ToJsonString());
    }

    private static List<ToolCall>? ParseToolCalls(string? json)
    {
        if (string.IsNullOrEmpty(json) || JsonNode.Parse(json) is not JsonArray arr || arr.Count == 0) return null;
        return arr.Select(c => new ToolCall(
            c?["id"]?.GetValue<string>() ?? "", c?["name"]?.GetValue<string>() ?? "", c?["arguments"]?.GetValue<string>() ?? "{}")).ToList();
    }
}
