using System.Text.Json.Nodes;
using AgentPoc.Core;
using AgentPoc.Harness;
using AgentPoc.Llm;

namespace AgentPoc.Chat;

public sealed record Conversation(
    Guid Id, string AgentId, string? Title, string[] ActiveTools, string? Summary, int SummarizedUpto,
    DateTime CreatedAt, DateTime UpdatedAt);

public sealed record StoredMessage(int Seq, ChatMessage Message, Guid? RunId, DateTime CreatedAt);

public sealed record TaskNotice(Guid TaskId, string Agent, string Status, string? Result, string? Error);

/// <summary>Persistencia das conversas e mensagens do chat no Postgres.</summary>
public sealed class ConversationStore : IConversationHistory
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
        .Select(r => new StoredMessage(r.I("seq"), RunMessageStore.MapMessage(r), r.GN("run_id"), r.T("created_at")))
        .ToList();

    public Task<int> AppendAsync(Guid conversationId, Guid runId, ChatMessage m) => AppendAsync(_db, conversationId, runId, m);

    private static async Task<int> AppendAsync(IDbExecutor db, Guid conversationId, Guid runId, ChatMessage m)
    {
        var seq = await db.ScalarAsync(
            @"insert into conversation_messages (conversation_id, seq, role, content, tool_calls, tool_call_id, tool_name, run_id)
              select @c, coalesce(max(seq), 0) + 1, @role, @content, @calls, @callId, @tool, @run
                from conversation_messages where conversation_id = @c
              returning seq",
            ("c", conversationId), ("role", m.Role), ("content", m.Content), ("calls", RunMessageStore.ToolCallsJson(m.ToolCalls)),
            ("callId", m.ToolCallId), ("tool", m.ToolName), ("run", runId));
        await db.ExecAsync("update conversations set updated_at = now() where id = @c", ("c", conversationId));
        return Convert.ToInt32(seq);
    }

    Task IConversationHistory.AppendAsync(IDbExecutor db, Guid conversationId, Guid runId, ChatMessage message) =>
        AppendAsync(db, conversationId, runId, message);

    public Task UpdateActiveToolsAsync(Guid conversationId, IEnumerable<string> activeTools) =>
        _db.ExecAsync("update conversations set active_tools = @t, updated_at = now() where id = @c",
            ("t", activeTools.OrderBy(t => t).ToArray()), ("c", conversationId));

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

    /// <summary>Run raiz do turno mais recente da conversa, com o status atual.</summary>
    public async Task<(Guid RunId, string Status)?> LatestRootRunAsync(Guid conversationId)
    {
        var r = await _db.QueryOneAsync(
            "select id, status from runs where conversation_id = @c and depth = 0 order by started_at desc limit 1", ("c", conversationId));
        return r == null ? null : (r.G("id"), r.S("status"));
    }

    /// <summary>
    /// Historico para retomar o agente raiz da conversa: as tool calls pendentes da ultima mensagem continuam
    /// sem resultado, para a retomada executa-las; as mensagens novas continuam sendo gravadas na conversa.
    /// </summary>
    public async Task<ChatTurn> ResumeTurnAsync(Guid conversationId, IReadOnlyCollection<string> activeTools)
    {
        var conv = await GetAsync(conversationId) ?? throw new InvalidOperationException($"Conversa {conversationId} nao encontrada.");
        var stored = await LoadMessagesAsync(conv.Id, conv.SummarizedUpto);
        var history = ChatHistory.Repair(stored.Select(m => m.Message).ToList(), keepTrailingPending: true);
        return new ChatTurn(conv.Id, history, activeTools, conv.Summary, null, (runId, message) => AppendAsync(conv.Id, runId, message));
    }

    private static Conversation Map(Dictionary<string, object?> r) => new(
        r.G("id"), r.S("agent_id"), r.SN("title"), r.A("active_tools"), r.SN("summary"), r.I("summarized_upto"),
        r.T("created_at"), r.T("updated_at"));
}
