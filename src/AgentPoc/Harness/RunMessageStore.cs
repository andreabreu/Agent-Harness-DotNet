using System.Text.Json.Nodes;
using AgentPoc.Core;
using AgentPoc.Llm;

namespace AgentPoc.Harness;

/// <summary>
/// Historico de mensagens de runs que nao sao o agente raiz de uma conversa (subagentes, tarefas, nos de workflow).
/// O agente raiz do chat usa conversation_messages; os dois permitem retomar um run suspenso a partir do banco.
/// </summary>
public sealed class RunMessageStore
{
    private readonly Db _db;
    public RunMessageStore(Db db) => _db = db;

    public Task AppendAsync(Guid runId, ChatMessage m) =>
        _db.ExecAsync(
            @"insert into run_messages (run_id, seq, role, content, tool_calls, tool_call_id, tool_name)
              select @r, coalesce(max(seq), 0) + 1, @role, @content, @calls, @callId, @tool
                from run_messages where run_id = @r",
            ("r", runId), ("role", m.Role), ("content", m.Content), ("calls", ToolCallsJson(m.ToolCalls)),
            ("callId", m.ToolCallId), ("tool", m.ToolName));

    public async Task<List<ChatMessage>> LoadAsync(Guid runId) =>
        (await _db.QueryAsync(
            @"select role, content, tool_calls::text as tool_calls, tool_call_id, tool_name
                from run_messages where run_id = @r order by seq", ("r", runId)))
        .Select(MapMessage)
        .ToList();

    public static ChatMessage MapMessage(Dictionary<string, object?> r) => new()
    {
        Role = r.S("role"),
        Content = r.SN("content"),
        ToolCalls = ParseToolCalls(r.SN("tool_calls")),
        ToolCallId = r.SN("tool_call_id"),
        ToolName = r.SN("tool_name")
    };

    public static Jsonb? ToolCallsJson(List<ToolCall>? calls)
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
