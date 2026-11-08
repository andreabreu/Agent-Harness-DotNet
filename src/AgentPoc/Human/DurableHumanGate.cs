using System.Text.Json.Nodes;
using AgentPoc.Harness;

namespace AgentPoc.Human;

/// <summary>
/// Human-in-the-loop duravel, em qualquer profundidade (agente raiz ou subagente). Cada pedido vira uma linha em
/// approvals (com prazo em expires_at) e um evento approval_request; o run e suspenso (waiting_human) sem segurar
/// processo. Quando a decisao chega ou o prazo vence, o despachante retoma o run: a mesma tool call e reexecutada
/// e encontra aqui a decisao ja gravada.
/// </summary>
public sealed class DurableHumanGate : IHumanGate
{
    private readonly HumanInputStore _store;
    private readonly EventLog _events;
    private readonly TimeSpan _expiresIn;

    public DurableHumanGate(HumanInputStore store, EventLog events, TimeSpan expiresIn)
    {
        _store = store;
        _events = events;
        _expiresIn = expiresIn;
    }

    public async Task<ApprovalDecision> ApproveAsync(RunContext ctx, string toolCallId, string tool, JsonObject args, string policyReason, CancellationToken ct)
    {
        var request = await _store.FindAsync(ctx.RunId, toolCallId);
        if (request == null)
        {
            var id = await _store.CreateApprovalAsync(ctx.RunId, ctx.ConversationId, toolCallId, tool, args, policyReason, _expiresIn);
            await AnnounceAsync(ctx, id, "approval", new JsonObject { ["tool"] = tool, ["args"] = args.DeepClone(), ["policy_reason"] = policyReason });
            throw new HumanInputRequiredException(id, toolCallId, $"aprovacao de {tool}");
        }
        return request.Status switch
        {
            "approved" => new ApprovalDecision(true, request.Reason ?? "aprovado pela pessoa", request.EditedArgs),
            "pending" => throw new HumanInputRequiredException(request.Id, toolCallId, $"aprovacao de {tool}"),
            _ => new ApprovalDecision(false, request.Reason ?? $"pedido {request.Status}")
        };
    }

    public async Task<string> AskAsync(RunContext ctx, string toolCallId, string question, IReadOnlyList<string> options, CancellationToken ct)
    {
        var request = await _store.FindAsync(ctx.RunId, toolCallId);
        if (request == null)
        {
            var id = await _store.CreateQuestionAsync(ctx.RunId, ctx.ConversationId, toolCallId, question, options, _expiresIn);
            await AnnounceAsync(ctx, id, "question", new JsonObject
            {
                ["question"] = question,
                ["options"] = new JsonArray(options.Select(o => (JsonNode?)JsonValue.Create(o)).ToArray())
            });
            throw new HumanInputRequiredException(id, toolCallId, $"resposta a: {question}");
        }
        return request.Status switch
        {
            "answered" => request.Answer ?? "",
            "pending" => throw new HumanInputRequiredException(request.Id, toolCallId, $"resposta a: {question}"),
            _ => $"SEM RESPOSTA: a pergunta {request.Status} ({request.Reason}). Replaneje: siga com a opcao mais segura e deixe a suposicao explicita."
        };
    }

    private Task AnnounceAsync(RunContext ctx, Guid id, string kind, JsonObject data)
    {
        data["approval_id"] = id.ToString();
        data["kind"] = kind;
        data["durable"] = true;
        data["expires_at"] = DateTime.UtcNow.Add(_expiresIn).ToString("o");
        return _events.AddAsync(ctx, "approval_request", data);
    }
}
