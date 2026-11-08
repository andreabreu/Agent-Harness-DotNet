using System.Collections.Concurrent;
using System.Text;
using AgentPoc.Core;
using AgentPoc.Harness;
using AgentPoc.Human;

namespace AgentPoc.Chat;

public sealed class ConversationBusyException : Exception
{
    public ConversationBusyException(Guid id) : base($"A conversa {Text.Short(id)} ja tem um turno em andamento.") { }
}

/// <summary>Resultado de registrar uma decisao humana: se o run precisa ser retomado pelo chamador.</summary>
public sealed record DecisionOutcome(HumanRequest Request, bool NeedsResume);

/// <summary>
/// Um turno de chat: restaura historico, resumo, ferramentas descobertas e memorias, entrega resultados de
/// tarefas assincronas, roda o agente e persiste cada mensagem. Tambem registra decisoes humanas e retoma
/// o turno suspenso esperando por elas. Usado pela CLI e pela API web.
/// </summary>
public sealed class ChatService
{
    private const int MemoriesRecalledPerTurn = 5;

    private readonly ConversationStore _store;
    private readonly ChatCompactor _compactor;
    private readonly AgentRunner _runner;
    private readonly ConfigRepo _config;
    private readonly MemoryStore _memory;
    private readonly HumanInputStore _human;
    private readonly InMemoryWaiters _waiters;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _turnLocks = new();

    public ChatService(ConversationStore store, ChatCompactor compactor, AgentRunner runner, ConfigRepo config, MemoryStore memory,
                       HumanInputStore human, InMemoryWaiters waiters)
    {
        _store = store;
        _compactor = compactor;
        _runner = runner;
        _config = config;
        _memory = memory;
        _human = human;
        _waiters = waiters;
    }

    public ConversationStore Store => _store;
    public HumanInputStore Human => _human;

    public async Task<Guid> StartAsync(string agentId)
    {
        if (await _config.GetAgentAsync(agentId) == null)
            throw new InvalidOperationException($"Agente '{agentId}' nao existe ou esta desabilitado.");
        return await _store.CreateAsync(agentId);
    }

    public bool IsBusy(Guid conversationId) =>
        _turnLocks.TryGetValue(conversationId, out var gate) && gate.CurrentCount == 0;

    public async Task<RunResult> SendAsync(Guid conversationId, string text, RunHooks hooks, CancellationToken ct)
    {
        var gate = _turnLocks.GetOrAdd(conversationId, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, ct)) throw new ConversationBusyException(conversationId);
        try
        {
            var conv = await _store.GetAsync(conversationId)
                       ?? throw new InvalidOperationException($"Conversa {conversationId} nao encontrada.");
            await DiscardWaitingTurnAsync(conv.Id);
            conv = await _compactor.CompactIfNeededAsync(conv, ct);

            var stored = await _store.LoadMessagesAsync(conv.Id, conv.SummarizedUpto);
            var history = ChatHistory.Repair(stored.Select(m => m.Message).ToList());
            var memories = await _memory.RecallAsync(conv.AgentId, text, MemoriesRecalledPerTurn);
            var input = WithTaskNotices(text, await _store.TakeTaskNoticesAsync(conv.Id));

            var turn = new ChatTurn(conv.Id, history, conv.ActiveTools, conv.Summary,
                memories.Count > 0 ? MemoryStore.Format(memories) : null,
                (runId, message) => _store.AppendAsync(conv.Id, runId, message));
            var result = await _runner.RunAsync(conv.AgentId, input, chat: turn, hooks: hooks, ct: ct);
            await _store.UpdateAfterTurnAsync(conv.Id, result.ActiveTools, text);
            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Registra a decisao de uma pessoa. Se um subagente espera em memoria, ele e acordado e segue sozinho;
    /// se o pedido e duravel (agente raiz suspenso), o chamador deve retomar o turno com ResumeAsync.
    /// </summary>
    public async Task<DecisionOutcome> DecideAsync(Guid requestId, HumanDecision decision)
    {
        var request = await _human.GetAsync(requestId)
                      ?? throw new KeyNotFoundException($"Pedido {requestId} nao encontrado.");
        if (request.Status != "pending")
            throw new InvalidOperationException($"O pedido ja foi decidido ({request.Status}).");

        var (status, normalized) = Normalize(request, decision);
        if (!await _human.DecideAsync(requestId, status, normalized))
            throw new InvalidOperationException("O pedido ja foi decidido por outra pessoa ou expirou.");
        var resumedInMemory = _waiters.Signal(requestId);
        return new DecisionOutcome(request, NeedsResume: !resumedInMemory);
    }

    /// <summary>Continua o turno suspenso da conversa, depois que todos os pedidos dele foram decididos.</summary>
    public async Task<RunResult> ResumeAsync(Guid conversationId, RunHooks hooks, CancellationToken ct)
    {
        var gate = _turnLocks.GetOrAdd(conversationId, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, ct)) throw new ConversationBusyException(conversationId);
        try
        {
            var conv = await _store.GetAsync(conversationId)
                       ?? throw new InvalidOperationException($"Conversa {conversationId} nao encontrada.");
            var runId = await _store.WaitingRunAsync(conv.Id)
                        ?? throw new InvalidOperationException("Nao ha turno aguardando decisao nesta conversa.");
            var pending = (await _human.PendingAsync(conv.Id)).Count(r => r.RunId == runId);
            if (pending > 0) throw new InvalidOperationException($"Ainda ha {pending} pedido(s) sem decisao.");

            var stored = await _store.LoadMessagesAsync(conv.Id, conv.SummarizedUpto);
            var history = ChatHistory.Repair(stored.Select(m => m.Message).ToList(), keepTrailingPending: true);
            var turn = new ChatTurn(conv.Id, history, conv.ActiveTools, conv.Summary, null,
                (rid, message) => _store.AppendAsync(conv.Id, rid, message));
            var result = await _runner.ResumeAsync(runId, turn, hooks, ct);
            await _store.UpdateAfterTurnAsync(conv.Id, result.ActiveTools, "");
            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>A pessoa mandou outra mensagem sem decidir: o turno suspenso e encerrado e o pedido expira.</summary>
    private async Task DiscardWaitingTurnAsync(Guid conversationId)
    {
        if (await _store.WaitingRunAsync(conversationId) is not { } waiting) return;
        await _human.ExpirePendingAsync(conversationId, "a pessoa seguiu a conversa sem decidir");
        await _store.CloseRunAsync(waiting, "cancelled", "Encerrado: a pessoa seguiu a conversa sem decidir o pedido pendente.");
    }

    private static (string Status, HumanDecision Decision) Normalize(HumanRequest request, HumanDecision decision)
    {
        if (request.Kind == "question")
        {
            var answer = decision.Answer?.Trim();
            if (string.IsNullOrEmpty(answer)) throw new ArgumentException("Informe a resposta.");
            return ("answered", decision with { Answer = answer, Approved = null, EditedArgs = null });
        }
        if (decision.Approved is not { } approved) throw new ArgumentException("Informe approved = true ou false.");
        var note = string.IsNullOrWhiteSpace(decision.Reason) ? null : decision.Reason.Trim();
        if (!approved)
            return ("denied", decision with { EditedArgs = null, Reason = note == null ? "negado pela pessoa" : $"negado pela pessoa: {note}" });
        var edited = decision.EditedArgs != null;
        var reason = edited ? "aprovado pela pessoa com argumentos editados" : "aprovado pela pessoa";
        return ("approved", decision with { Reason = note == null ? reason : $"{reason}: {note}" });
    }

    private static string WithTaskNotices(string text, IReadOnlyList<TaskNotice> notices)
    {
        if (notices.Count == 0) return text;
        var sb = new StringBuilder("[Aviso do sistema: tarefas assincronas desta conversa terminaram]\n");
        foreach (var n in notices)
            sb.AppendLine($"- tarefa {n.TaskId} ({n.Agent}): {n.Status}. {Text.Trunc(n.Result ?? n.Error ?? "", 3000)}");
        sb.AppendLine().AppendLine("[Mensagem do usuario]").Append(text);
        return sb.ToString();
    }
}
