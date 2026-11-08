using System.Text.Json.Nodes;
using AgentPoc.Core;

namespace AgentPoc.Human;

public sealed record HumanRequest(
    Guid Id, Guid RunId, Guid? ConversationId, string Kind, string? ToolCallId, string Tool, JsonObject Args,
    string? PolicyReason, string? Question, string[] Options, string Status, string? Reason, JsonObject? EditedArgs,
    string? Answer, DateTime CreatedAt);

/// <summary>Decisao de uma pessoa: aprovar/negar (com motivo e argumentos corrigidos) ou responder uma pergunta.</summary>
public sealed record HumanDecision(bool? Approved, string? Reason, JsonObject? EditedArgs, string? Answer);

/// <summary>Pedidos de human-in-the-loop na tabela approvals.</summary>
public sealed class HumanInputStore
{
    private readonly Db _db;
    public HumanInputStore(Db db) => _db = db;

    public async Task<HumanRequest?> GetAsync(Guid id)
    {
        var r = await _db.QueryOneAsync($"{Select} where id = @id", ("id", id));
        return r == null ? null : Map(r);
    }

    /// <summary>Pedido ja feito para esta tool call do run (a retomada encontra a decisao por aqui).</summary>
    public async Task<HumanRequest?> FindAsync(Guid runId, string toolCallId)
    {
        var r = await _db.QueryOneAsync($"{Select} where run_id = @r and tool_call_id = @c order by created_at desc limit 1",
            ("r", runId), ("c", toolCallId));
        return r == null ? null : Map(r);
    }

    public async Task<List<HumanRequest>> PendingAsync(Guid conversationId) =>
        (await _db.QueryAsync($"{Select} where conversation_id = @c and status = 'pending' order by created_at", ("c", conversationId)))
        .Select(Map).ToList();

    public async Task<Guid> CreateApprovalAsync(Guid runId, Guid? conversationId, string toolCallId, string tool, JsonObject args, string policyReason) =>
        (Guid)(await _db.ScalarAsync(
            @"insert into approvals (run_id, conversation_id, kind, tool_call_id, tool, args, policy_reason)
              values (@r, @c, 'approval', @call, @t, @a, @p) returning id",
            ("r", runId), ("c", conversationId), ("call", toolCallId), ("t", tool), ("a", args), ("p", policyReason)))!;

    public async Task<Guid> CreateQuestionAsync(Guid runId, Guid? conversationId, string toolCallId, string question, IReadOnlyList<string> options) =>
        (Guid)(await _db.ScalarAsync(
            @"insert into approvals (run_id, conversation_id, kind, tool_call_id, tool, question, options)
              values (@r, @c, 'question', @call, 'ask_human', @q, @o) returning id",
            ("r", runId), ("c", conversationId), ("call", toolCallId), ("q", question),
            ("o", new JsonArray(options.Select(o => (JsonNode?)JsonValue.Create(o)).ToArray()))))!;

    /// <summary>Grava a decisao se o pedido ainda estiver pendente. Retorna false se ja tinha sido decidido.</summary>
    public async Task<bool> DecideAsync(Guid id, string status, HumanDecision decision) =>
        await _db.ExecAsync(
            @"update approvals set status = @s, reason = @reason, edited_args = @edited, answer = @answer, decided_at = now()
               where id = @id and status = 'pending'",
            ("s", status), ("reason", decision.Reason), ("edited", decision.EditedArgs), ("answer", decision.Answer), ("id", id)) > 0;

    public Task<int> ExpirePendingAsync(Guid conversationId, string reason) =>
        _db.ExecAsync(
            "update approvals set status = 'expired', reason = @reason, decided_at = now() where conversation_id = @c and status = 'pending'",
            ("reason", reason), ("c", conversationId));

    private const string Select =
        @"select id, run_id, conversation_id, kind, tool_call_id, tool, args::text as args, policy_reason, question,
                 options::text as options, status, reason, edited_args::text as edited_args, answer, created_at
            from approvals";

    private static HumanRequest Map(Dictionary<string, object?> r) => new(
        r.G("id"), r.G("run_id"), r.GN("conversation_id"), r.S("kind"), r.SN("tool_call_id"), r.S("tool"),
        JsonNode.Parse(r.S("args")) as JsonObject ?? new JsonObject(), r.SN("policy_reason"), r.SN("question"),
        r.SN("options") is { } o && JsonNode.Parse(o) is JsonArray arr ? arr.Select(x => x?.ToString() ?? "").ToArray() : Array.Empty<string>(),
        r.S("status"), r.SN("reason"), r.SN("edited_args") is { } e ? JsonNode.Parse(e) as JsonObject : null,
        r.SN("answer"), r.T("created_at"));
}
