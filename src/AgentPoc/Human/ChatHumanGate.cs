using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using AgentPoc.Harness;

namespace AgentPoc.Human;

/// <summary>Pedidos que um subagente esta esperando em memoria (o historico dele nao e persistido para retomada).</summary>
public sealed class InMemoryWaiters
{
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource> _waiting = new();

    public Task WaitAsync(Guid id)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiting[id] = tcs;
        return tcs.Task;
    }

    /// <summary>Acorda quem espera este pedido. Retorna false se ninguem esta esperando em memoria (pedido duravel).</summary>
    public bool Signal(Guid id) => _waiting.TryRemove(id, out var tcs) && tcs.TrySetResult();

    public void Forget(Guid id) => _waiting.TryRemove(id, out _);
}

/// <summary>
/// Human-in-the-loop do chat. Cada pedido vira uma linha em approvals e um evento approval_request.
/// No agente raiz da conversa a espera e duravel: o run e suspenso (waiting_human) sem segurar processo e,
/// quando a decisao chega, e retomado do historico persistido, mesmo depois de um restart. Subagentes esperam
/// em memoria (ate o tempo limite), porque o historico deles nao e persistido.
/// Na retomada a mesma tool call e reexecutada e encontra aqui a decisao ja gravada.
/// </summary>
public sealed class ChatHumanGate : IHumanGate
{
    private readonly HumanInputStore _store;
    private readonly EventLog _events;
    private readonly InMemoryWaiters _waiters;
    private readonly TimeSpan _inMemoryTimeout;

    public ChatHumanGate(HumanInputStore store, EventLog events, InMemoryWaiters waiters, TimeSpan inMemoryTimeout)
    {
        _store = store;
        _events = events;
        _waiters = waiters;
        _inMemoryTimeout = inMemoryTimeout;
    }

    public async Task<ApprovalDecision> ApproveAsync(RunContext ctx, string toolCallId, string tool, JsonObject args, string policyReason, CancellationToken ct)
    {
        var request = await _store.FindAsync(ctx.RunId, toolCallId);
        if (request == null)
        {
            var id = await _store.CreateApprovalAsync(ctx.RunId, ctx.ConversationId, toolCallId, tool, args, policyReason);
            await AnnounceAsync(ctx, id, "approval", new JsonObject { ["tool"] = tool, ["args"] = args.DeepClone(), ["policy_reason"] = policyReason });
            request = await WaitAsync(ctx, id, $"aprovacao de {tool}", ct);
        }
        return request.Status switch
        {
            "approved" => new ApprovalDecision(true, request.Reason ?? "aprovado pela pessoa", request.EditedArgs),
            "pending" => throw new HumanInputRequiredException(request.Id, $"aprovacao de {tool}"),
            _ => new ApprovalDecision(false, request.Reason ?? $"pedido {request.Status}")
        };
    }

    public async Task<string> AskAsync(RunContext ctx, string toolCallId, string question, IReadOnlyList<string> options, CancellationToken ct)
    {
        var request = await _store.FindAsync(ctx.RunId, toolCallId);
        if (request == null)
        {
            var id = await _store.CreateQuestionAsync(ctx.RunId, ctx.ConversationId, toolCallId, question, options);
            await AnnounceAsync(ctx, id, "question", new JsonObject
            {
                ["question"] = question,
                ["options"] = new JsonArray(options.Select(o => (JsonNode?)JsonValue.Create(o)).ToArray())
            });
            request = await WaitAsync(ctx, id, "resposta a uma pergunta", ct);
        }
        return request.Status switch
        {
            "answered" => request.Answer ?? "",
            "pending" => throw new HumanInputRequiredException(request.Id, $"resposta a: {question}"),
            _ => $"SEM RESPOSTA: a pergunta expirou ({request.Reason}). Siga com a opcao mais segura e deixe a suposicao explicita."
        };
    }

    private Task AnnounceAsync(RunContext ctx, Guid id, string kind, JsonObject data)
    {
        data["approval_id"] = id.ToString();
        data["kind"] = kind;
        data["durable"] = IsDurable(ctx);
        return _events.AddAsync(ctx, "approval_request", data);
    }

    /// <summary>So o agente raiz de uma conversa pode ser suspenso e retomado a partir do historico persistido.</summary>
    private static bool IsDurable(RunContext ctx) => ctx.Depth == 0 && ctx.ConversationId != null;

    private async Task<HumanRequest> WaitAsync(RunContext ctx, Guid id, string what, CancellationToken ct)
    {
        if (IsDurable(ctx)) throw new HumanInputRequiredException(id, what);

        var signal = _waiters.WaitAsync(id);
        try
        {
            await signal.WaitAsync(_inMemoryTimeout, ct);
        }
        catch (TimeoutException)
        {
            await _store.DecideAsync(id, "expired", new HumanDecision(null, $"sem resposta em {_inMemoryTimeout.TotalMinutes:0} min", null, null));
        }
        catch (OperationCanceledException)
        {
            await _store.DecideAsync(id, "expired", new HumanDecision(null, "turno cancelado antes da decisao", null, null));
            throw;
        }
        finally
        {
            _waiters.Forget(id);
        }
        return (await _store.GetAsync(id))!;
    }
}
