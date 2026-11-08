using System.Collections.Concurrent;
using AgentPoc.Core;

namespace AgentPoc.Harness;

/// <summary>Execucoes de runs neste processo, por run raiz, para o cancelamento alcancar a cadeia toda.</summary>
public sealed class RunExecutions
{
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<CancellationTokenSource, byte>> _byRoot = new();

    public Registration Register(Guid rootRunId, CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _byRoot.GetOrAdd(rootRunId, _ => new()).TryAdd(cts, 0);
        return new Registration(this, rootRunId, cts);
    }

    public bool Cancel(Guid rootRunId)
    {
        if (!_byRoot.TryGetValue(rootRunId, out var running) || running.IsEmpty) return false;
        foreach (var cts in running.Keys) cts.Cancel();
        return true;
    }

    private void Remove(Guid rootRunId, CancellationTokenSource cts)
    {
        if (_byRoot.TryGetValue(rootRunId, out var running)) running.TryRemove(cts, out _);
        cts.Dispose();
    }

    public sealed class Registration : IDisposable
    {
        private readonly RunExecutions _owner;
        private readonly Guid _root;
        private readonly CancellationTokenSource _cts;

        public Registration(RunExecutions owner, Guid root, CancellationTokenSource cts)
        {
            _owner = owner;
            _root = root;
            _cts = cts;
        }

        public CancellationToken Token => _cts.Token;
        public void Dispose() => _owner.Remove(_root, _cts);
    }
}

/// <summary>Recuperacao de execucoes de workflow orfas (implementada pelo motor de workflow, sem o harness depender dele).</summary>
public interface IWorkflowRecovery
{
    /// <summary>Execucoes running com lease vencido viram ready. Retorna quantas.</summary>
    Task<int> RecoverOrphansAsync(string recoveredBy);

    /// <summary>Reivindica uma execucao ready e a continua ate o fim. False se nao havia nenhuma.</summary>
    Task<bool> ResumeNextAsync(CancellationToken ct);
}

/// <summary>Sinal para acordar quem espera (NOTIFY recebido), com tempo limite para a varredura periodica.</summary>
internal sealed class Wakeup
{
    private TaskCompletionSource _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Pulse() => Interlocked.Exchange(ref _signal, new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();

    public async Task WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        try { await _signal.Task.WaitAsync(timeout, ct); }
        catch (TimeoutException) { /* varredura periodica */ }
    }
}

/// <summary>
/// Retoma runs prontos (status ready) de qualquer origem: decisao humana, prazo vencido, filho que terminou ou
/// run orfao (lease vencido porque a instancia dona caiu). Pega cada run com FOR UPDATE SKIP LOCKED e o marca
/// running com o lease desta instancia no mesmo comando, entao varias instancias podem rodar o despachante sem
/// retomar o mesmo run duas vezes. Acorda por LISTEN/NOTIFY e varre periodicamente: expira pedidos vencidos,
/// recupera orfaos e reconcilia pais parados em filhos ja terminados. Tambem retoma execucoes de workflow orfas,
/// com um limite proprio de execucoes simultaneas.
/// </summary>
public sealed class ResumeDispatcher
{
    private readonly Db _db;
    private readonly AgentRunner _runner;
    private readonly RunChain _chain;
    private readonly IConversationHistory _conversations;
    private readonly RunExecutions _executions;
    private readonly IHumanGate _human;
    private readonly Func<Guid?, IRunObserver?> _observerFor;
    private readonly RunLeaseOptions _lease;
    private readonly IWorkflowRecovery? _workflows;
    private readonly Wakeup _wakeup = new();

    private const int WorkflowConcurrency = 2;

    public ResumeDispatcher(Db db, AgentRunner runner, RunChain chain, IConversationHistory conversations, RunExecutions executions,
                            IHumanGate human, Func<Guid?, IRunObserver?> observerFor, RunLeaseOptions lease, IWorkflowRecovery? workflows = null)
    {
        _workflows = workflows;
        _db = db;
        _runner = runner;
        _chain = chain;
        _conversations = conversations;
        _executions = executions;
        _human = human;
        _observerFor = observerFor;
        _lease = lease;
        Name = runner.InstanceId;
    }

    public string Name { get; }

    public async Task RunAsync(int concurrency, TimeSpan sweepInterval, CancellationToken ct)
    {
        await using var listener = await _db.ListenAsync(new[] { RunChain.ReadyChannel, RunChain.CancelChannel }, OnNotification, ct);
        Log.Write(0, ConsoleColor.DarkYellow, "despachante", $"{Name} ativo ({concurrency} em paralelo, varredura a cada {sweepInterval.TotalSeconds:0}s)");
        var workers = Enumerable.Range(0, Math.Max(1, concurrency)).Select(_ => Task.Run(() => WorkAsync(sweepInterval, ct), ct)).ToList();
        if (_workflows != null)
            workers.AddRange(Enumerable.Range(0, WorkflowConcurrency).Select(_ => Task.Run(() => WorkflowWorkAsync(_workflows, sweepInterval, ct), ct)));
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await SweepAsync();
                await Task.Delay(sweepInterval, ct);
            }
        }
        catch (OperationCanceledException) { /* encerrando */ }
        _wakeup.Pulse();
        try { await Task.WhenAll(workers); } catch (OperationCanceledException) { /* encerrando */ }
    }

    private async Task SweepAsync()
    {
        try
        {
            var changed = await _chain.ExpireDueAsync() + await _chain.RecoverOrphansAsync(Name) + await _chain.ReconcileAsync() +
                          (_workflows != null ? await _workflows.RecoverOrphansAsync(Name) : 0);
            if (changed > 0) _wakeup.Pulse();
        }
        catch (Exception ex)
        {
            Log.Error($"despachante {Name}: varredura falhou: {ex.Message}");
        }
    }

    private void OnNotification(string channel, string payload)
    {
        if (channel == RunChain.ReadyChannel) _wakeup.Pulse();
        else if (channel == RunChain.CancelChannel && Guid.TryParse(payload, out var root)) _executions.Cancel(root);
    }

    private async Task WorkAsync(TimeSpan sweepInterval, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var claimed = await ClaimAsync();
                if (claimed == null)
                {
                    await _wakeup.WaitAsync(sweepInterval, ct);
                    continue;
                }
                await ResumeAsync(claimed, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (InjectedFaultException ex)
            {
                Log.Warn($"despachante {Name}: {ex.Message} O run fica como estava; o lease vence e outra varredura o recupera.");
            }
            catch (Exception ex)
            {
                Log.Error($"despachante {Name}: {ex.Message}");
            }
        }
    }

    private async Task WorkflowWorkAsync(IWorkflowRecovery workflows, TimeSpan sweepInterval, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!await workflows.ResumeNextAsync(ct)) await _wakeup.WaitAsync(sweepInterval, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Error($"despachante {Name}: workflow: {ex.Message}");
            }
        }
    }

    private Task<Dictionary<string, object?>?> ClaimAsync() =>
        _db.QueryOneAsync(
            @"update runs set status = 'running', owner = @owner, lease_until = now() + make_interval(secs => @lease)
               where id = (select id from runs where status = 'ready' order by started_at limit 1 for update skip locked)
           returning id, root_run_id, depth, conversation_id, active_tools",
            ("owner", Name), ("lease", _lease.Lease.TotalSeconds));

    private async Task ResumeAsync(Dictionary<string, object?> run, CancellationToken ct)
    {
        var runId = run.G("id");
        var conversationId = run.GN("conversation_id");
        var activeTools = run.A("active_tools");
        using var execution = _executions.Register(run.G("root_run_id"), ct);
        try
        {
            var chat = run.I("depth") == 0 && conversationId is { } conv
                ? await _conversations.ResumeTurnAsync(conv, activeTools)
                : null;
            var result = await _runner.ResumeAsync(runId, chat, new RunHooks(_observerFor(conversationId), _human), Name, execution.Token);
            if (chat != null) await _conversations.UpdateActiveToolsAsync(chat.ConversationId, result.ActiveTools);
        }
        catch (Exception ex) when (ex is not InjectedFaultException && (ex is not OperationCanceledException || !ct.IsCancellationRequested))
        {
            Log.Error($"despachante {Name}: falha ao retomar run {Text.Short(runId)}: {ex.Message}");
            await _chain.FailRunAsync(runId, $"Erro ao retomar: {ex.Message}");
        }
    }
}
