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

/// <summary>
/// Um turno de chat: restaura historico, resumo, ferramentas descobertas e memorias, entrega resultados de
/// tarefas assincronas, roda o agente e persiste cada mensagem. Decisoes humanas so sao gravadas aqui: quem
/// retoma o turno suspenso e o despachante (em qualquer instancia). Usado pela CLI e pela API web.
/// </summary>
public sealed class ChatService
{
    private const int MemoriesRecalledPerTurn = 5;
    private const string NewMessageReason = "a pessoa seguiu a conversa sem decidir o pedido pendente";
    private const string CancelReason = "cancelado pela pessoa";

    private readonly ConversationStore _store;
    private readonly ChatCompactor _compactor;
    private readonly AgentRunner _runner;
    private readonly ConfigRepo _config;
    private readonly MemoryStore _memory;
    private readonly HumanInputStore _human;
    private readonly RunChain _chain;
    private readonly RunExecutions _executions;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _turnLocks = new();

    public ChatService(ConversationStore store, ChatCompactor compactor, AgentRunner runner, ConfigRepo config, MemoryStore memory,
                       HumanInputStore human, RunChain chain, RunExecutions executions)
    {
        _store = store;
        _compactor = compactor;
        _runner = runner;
        _config = config;
        _memory = memory;
        _human = human;
        _chain = chain;
        _executions = executions;
    }

    public ConversationStore Store => _store;
    public HumanInputStore Human => _human;

    public async Task<Guid> StartAsync(string agentId)
    {
        if (await _config.GetAgentAsync(agentId) == null)
            throw new InvalidOperationException($"Agente '{agentId}' nao existe ou esta desabilitado.");
        return await _store.CreateAsync(agentId);
    }

    /// <summary>
    /// Ha um turno andando: neste processo ou, segundo o banco, em qualquer instancia (executando, pronto ou
    /// com uma transicao automatica pendente). Um turno parado esperando uma pessoa nao conta como ocupado.
    /// </summary>
    public async Task<bool> IsBusyAsync(Guid conversationId)
    {
        if (_turnLocks.TryGetValue(conversationId, out var gate) && gate.CurrentCount == 0) return true;
        if (await _store.LatestRootRunAsync(conversationId) is not { } root || RunStatus.IsTerminal(root.Status)) return false;
        if (root.Status == RunStatus.Running) await RecoverIfOrphanAsync();
        return !await _chain.IsSettledAsync(root.RunId);
    }

    /// <summary>
    /// Turno "executando" cuja instancia caiu (lease vencido): volta para ready agora, sem esperar a varredura, e o
    /// despachante o retoma. A conversa continua ocupada enquanto o turno anda, mas nunca fica presa.
    /// </summary>
    private Task RecoverIfOrphanAsync() => _chain.RecoverOrphansAsync(_runner.InstanceId);

    /// <summary>
    /// Roda um turno neste processo ate terminar ou suspender. Se o turno anterior estava suspenso esperando
    /// uma pessoa, a cadeia dele e cancelada e os pedidos pendentes sao fechados.
    /// </summary>
    public async Task<RunResult> SendAsync(Guid conversationId, string text, RunHooks hooks, CancellationToken ct)
    {
        var gate = _turnLocks.GetOrAdd(conversationId, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, ct)) throw new ConversationBusyException(conversationId);
        try
        {
            var conv = await _store.GetAsync(conversationId)
                       ?? throw new InvalidOperationException($"Conversa {conversationId} nao encontrada.");
            await DiscardSuspendedTurnAsync(conv.Id);
            conv = await _compactor.CompactIfNeededAsync(conv, ct);

            var stored = await _store.LoadMessagesAsync(conv.Id, conv.SummarizedUpto);
            var history = ChatHistory.Repair(stored.Select(m => m.Message).ToList());
            var memories = await _memory.RecallAsync(conv.AgentId, text, MemoriesRecalledPerTurn);
            var input = WithTaskNotices(text, await _store.TakeTaskNoticesAsync(conv.Id));

            var turn = new ChatTurn(conv.Id, history, conv.ActiveTools, conv.Summary,
                memories.Count > 0 ? MemoryStore.Format(memories) : null,
                (runId, message) => _store.AppendAsync(conv.Id, runId, message));
            var rootRunId = Guid.NewGuid();
            using var execution = _executions.Register(rootRunId, ct);
            var result = await _runner.RunAsync(conv.AgentId, input, chat: turn, hooks: hooks, runId: rootRunId, ct: execution.Token);
            await _store.UpdateAfterTurnAsync(conv.Id, result.ActiveTools, text);
            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Grava a decisao de uma pessoa e deixa o run que pediu pronto para retomar. Nao retoma nada aqui:
    /// o despachante (de qualquer instancia) pega o run pronto.
    /// </summary>
    public async Task<HumanRequest> DecideAsync(Guid requestId, HumanDecision decision)
    {
        var request = await _human.GetAsync(requestId)
                      ?? throw new KeyNotFoundException($"Pedido {requestId} nao encontrado.");
        if (request.Status != "pending")
            throw new InvalidOperationException($"O pedido ja foi decidido ({request.Status}).");

        var (status, normalized) = Normalize(request, decision);
        if (!await _human.DecideAsync(requestId, status, normalized))
            throw new InvalidOperationException("O pedido ja foi decidido por outra pessoa ou expirou.");
        await _chain.MarkReadyIfDecidedAsync(request.RunId);
        return (await _human.GetAsync(requestId))!;
    }

    /// <summary>Cancela o turno da conversa: execucoes deste processo, das outras instancias e a cadeia suspensa.</summary>
    public async Task<bool> CancelAsync(Guid conversationId)
    {
        if (await _store.LatestRootRunAsync(conversationId) is not { } root) return false;
        var runningHere = _executions.Cancel(root.RunId);
        var suspended = await _chain.CancelTreeAsync(root.RunId, CancelReason);
        return runningHere || suspended > 0 || root.Status == RunStatus.Running;
    }

    /// <summary>
    /// Nova mensagem com o turno anterior parado esperando uma pessoa: a cadeia e cancelada e os pedidos fechados.
    /// Se o turno anterior ainda anda sozinho (executando ou retomando), a conversa esta ocupada.
    /// </summary>
    private async Task DiscardSuspendedTurnAsync(Guid conversationId)
    {
        if (await _store.LatestRootRunAsync(conversationId) is not { } root || RunStatus.IsTerminal(root.Status)) return;
        if (!RunStatus.IsSuspended(root.Status) || !await _chain.IsSettledAsync(root.RunId))
            throw new ConversationBusyException(conversationId);
        await _chain.CancelTreeAsync(root.RunId, NewMessageReason);
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
