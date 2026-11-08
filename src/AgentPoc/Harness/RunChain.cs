using System.Text.Json.Nodes;
using AgentPoc.Core;
using AgentPoc.Llm;

namespace AgentPoc.Harness;

/// <summary>Como um trecho de execucao do run terminou: suspenso esperando algo ou em estado final.</summary>
public sealed record RunOutcome(
    string Status, string Output, int Steps, long TokensIn, long TokensOut, decimal Cost,
    IReadOnlyCollection<string> ActiveTools, RunSuspendedException? Suspension);

/// <summary>O harness grava como cada trecho de execucao terminou (e acorda quem depende dele).</summary>
public interface IRunLifecycle
{
    /// <summary>Grava o resultado se <paramref name="ctx"/> ainda for o dono do run. Retorna false se o lease foi perdido.</summary>
    Task<bool> RecordOutcomeAsync(RunContext ctx, RunOutcome outcome);
}

/// <summary>Historico do agente raiz de uma conversa (implementado pelo chat, sem o harness depender dele).</summary>
public interface IConversationHistory
{
    /// <summary>Grava a mensagem na conversa usando <paramref name="db"/> (pode ser uma transacao).</summary>
    Task AppendAsync(IDbExecutor db, Guid conversationId, Guid runId, ChatMessage message);
    Task<ChatTurn> ResumeTurnAsync(Guid conversationId, IReadOnlyCollection<string> activeTools);
    Task UpdateActiveToolsAsync(Guid conversationId, IEnumerable<string> activeTools);
}

/// <summary>Entrega do resultado de um filho ao pai: o tool_result do delegate_task que o pai esta esperando.</summary>
public sealed record ParentDelivery(string ToolCallId, ChatMessage ToolMessage, bool Success)
{
    /// <summary>O que gravar no pai, ou null se nao e hora (filho nao terminou ou o pai nao esta parado nele).</summary>
    public static ParentDelivery? Plan(Guid childId, string childAgent, string childStatus, int childSteps, string childOutput,
                                       string parentStatus, Guid? parentWaitingOn, string? pendingToolCallId)
    {
        if (string.IsNullOrEmpty(pendingToolCallId) ||
            !RunStatus.ShouldDeliverToParent(childStatus, childId, parentStatus, parentWaitingOn))
            return null;
        var content = DelegationResult.Format(childAgent, childStatus, childSteps, childOutput);
        return new ParentDelivery(pendingToolCallId, ChatMessage.Tool(pendingToolCallId, "delegate_task", content), DelegationResult.IsSuccess(content));
    }
}

/// <summary>
/// Transicoes duraveis entre runs, todas no Postgres:
/// - fim de um run e entrega ao pai numa unica transacao (o filho conclui, a saida vira o tool_result do pai e o
///   pai fica ready, ou nada disso acontece);
/// - decisao gravada ou prazo vencido -> o run que pediu fica ready;
/// - run running com lease vencido (instancia caiu) -> ready, para outra instancia retomar (run_recovered);
/// - cancelamento -> a cadeia parada inteira vira cancelled.
/// Cada run que fica ready e anunciado em NOTIFY para o despachante.
/// </summary>
public sealed class RunChain : IRunLifecycle
{
    public const string ReadyChannel = "agent_run_ready";
    public const string CancelChannel = "agent_run_cancel";

    private const string SubtreeCte =
        @"with recursive tree as (
              select id from runs where id = @root
              union all
              select r.id from runs r join tree t on r.parent_run_id = t.id)";

    private static readonly string[] TerminalStatuses =
        { RunStatus.Completed, RunStatus.Failed, RunStatus.BudgetExceeded, RunStatus.MaxSteps, RunStatus.Cancelled };

    private readonly Db _db;
    private readonly EventLog _events;
    private readonly IConversationHistory _conversations;

    public RunChain(Db db, EventLog events, IConversationHistory conversations)
    {
        _db = db;
        _events = events;
        _conversations = conversations;
    }

    public async Task<bool> RecordOutcomeAsync(RunContext ctx, RunOutcome outcome)
    {
        if (outcome.Suspension is { } suspension) return await RecordSuspensionAsync(ctx, outcome, suspension);

        var finished = await _db.InTransactionAsync(async tx =>
        {
            var changed = await tx.ExecAsync(
                @"update runs set status = @st, output = @o, steps = @s, tokens_in = @ti, tokens_out = @to, cost_usd = @c,
                         active_tools = @tools, owner = null, lease_until = null, finished_at = now()
                   where id = @id and owner = @owner",
                ("st", outcome.Status), ("o", outcome.Output), ("s", outcome.Steps), ("ti", outcome.TokensIn), ("to", outcome.TokensOut),
                ("c", outcome.Cost), ("tools", outcome.ActiveTools.OrderBy(t => t).ToArray()), ("id", ctx.RunId), ("owner", ctx.Owner));
            if (changed == 0) return (Recorded: false, ReadyParent: (Guid?)null);
            if (ctx.ParentRunId is not { } parentId) return (true, null);
            FaultInjection.Point(FaultInjection.AfterChildFinished);
            var delivered = await DeliverInTransactionAsync(tx, ctx.RunId, ctx.Agent.Id, outcome.Status, outcome.Steps, outcome.Output,
                outcome.TokensIn + outcome.TokensOut, parentId);
            return (true, delivered ? parentId : null);
        });
        if (finished.ReadyParent is { } ready) await _db.NotifyAsync(ReadyChannel, ready.ToString());
        return finished.Recorded;
    }

    private async Task<bool> RecordSuspensionAsync(RunContext ctx, RunOutcome outcome, RunSuspendedException suspension)
    {
        var changed = await _db.ExecAsync(
            @"update runs set status = @st, output = @o, steps = @s, tokens_in = @ti, tokens_out = @to, cost_usd = @c,
                     waiting_on_run_id = @wait, pending_tool_call_id = @call, active_tools = @tools,
                     owner = null, lease_until = null, finished_at = null
               where id = @id and owner = @owner",
            ("st", outcome.Status), ("o", outcome.Output), ("s", outcome.Steps), ("ti", outcome.TokensIn), ("to", outcome.TokensOut),
            ("c", outcome.Cost), ("wait", (suspension as ChildSuspendedException)?.ChildRunId), ("call", suspension.ToolCallId),
            ("tools", outcome.ActiveTools.OrderBy(t => t).ToArray()), ("id", ctx.RunId), ("owner", ctx.Owner));
        if (changed == 0) return false;
        if (suspension is ChildSuspendedException child) await DeliverToParentAsync(child.ChildRunId);
        else await MarkReadyIfDecidedAsync(ctx.RunId);
        return true;
    }

    /// <summary>Encerra como failed um run que nao conseguiu retomar, entregando o erro ao pai na mesma transacao.</summary>
    public async Task FailRunAsync(Guid runId, string output)
    {
        var readyParent = await _db.InTransactionAsync(async tx =>
        {
            var run = await tx.QueryOneAsync(
                @"update runs set status = 'failed', output = @o, owner = null, lease_until = null, finished_at = now()
                   where id = @id and status = 'running'
               returning agent_id, steps, tokens_in + tokens_out as tokens, parent_run_id", ("o", output), ("id", runId));
            if (run?.GN("parent_run_id") is not { } parentId) return (Guid?)null;
            var delivered = await DeliverInTransactionAsync(tx, runId, run.S("agent_id"), RunStatus.Failed, run.I("steps"), output, run.L("tokens"), parentId);
            return delivered ? parentId : null;
        });
        if (readyParent is { } ready) await _db.NotifyAsync(ReadyChannel, ready.ToString());
    }

    /// <summary>
    /// Se o filho ja terminou e o pai esta parado nele, entrega agora (numa transacao). Cobre o pai que suspende
    /// depois que o filho terminou e a reconciliacao do despachante. Idempotente: o pai e travado e conferido.
    /// </summary>
    public async Task<bool> DeliverToParentAsync(Guid childRunId)
    {
        var child = await _db.QueryOneAsync("select parent_run_id from runs where id = @id", ("id", childRunId));
        if (child?.GN("parent_run_id") is not { } parentId) return false;
        var delivered = await _db.InTransactionAsync(async tx =>
        {
            var current = await tx.QueryOneAsync(
                "select agent_id, status, output, steps, tokens_in + tokens_out as tokens from runs where id = @id", ("id", childRunId));
            return current != null && await DeliverInTransactionAsync(tx, childRunId, current.S("agent_id"), current.S("status"),
                current.I("steps"), current.S("output"), current.L("tokens"), parentId);
        });
        if (delivered) await _db.NotifyAsync(ReadyChannel, parentId.ToString());
        return delivered;
    }

    /// <summary>Trava o pai e, se ele esta parado neste filho terminado, grava o tool_result e deixa o pai ready.</summary>
    private async Task<bool> DeliverInTransactionAsync(IDbExecutor tx, Guid childId, string childAgent, string childStatus, int steps,
                                                       string output, long tokens, Guid parentId)
    {
        var parent = await tx.QueryOneAsync(
            "select status, waiting_on_run_id, pending_tool_call_id, depth, conversation_id from runs where id = @p for update", ("p", parentId));
        if (parent == null) return false;
        var plan = ParentDelivery.Plan(childId, childAgent, childStatus, steps, output,
            parent.S("status"), parent.GN("waiting_on_run_id"), parent.SN("pending_tool_call_id"));
        if (plan == null) return false;

        if (parent.I("depth") == 0 && parent.GN("conversation_id") is { } conversationId)
            await _conversations.AppendAsync(tx, conversationId, parentId, plan.ToolMessage);
        else
            await RunMessageStore.AppendAsync(tx, parentId, plan.ToolMessage);

        await EventLog.AddDetachedAsync(tx, parentId, "delegation_end", new JsonObject
        {
            ["to_agent"] = childAgent,
            ["child_run_id"] = childId.ToString(),
            ["status"] = childStatus,
            ["tokens"] = tokens
        });
        await EventLog.AddDetachedAsync(tx, parentId, "tool_result", new JsonObject
        {
            ["name"] = "delegate_task",
            ["tool_call_id"] = plan.ToolCallId,
            ["ok"] = plan.Success,
            ["ms"] = 0,
            ["output"] = Text.Trunc(plan.ToolMessage.Content, 2000)
        });
        await tx.ExecAsync(
            "update runs set status = 'ready', waiting_on_run_id = null, pending_tool_call_id = null where id = @p", ("p", parentId));
        return true;
    }

    /// <summary>Cinto e suspensorio: pai parado em waiting_child com o filho ja terminado recebe a entrega agora.</summary>
    public async Task<int> ReconcileAsync()
    {
        var stuck = await _db.QueryAsync(
            @"select c.id from runs p join runs c on c.id = p.waiting_on_run_id
               where p.status = 'waiting_child' and c.status = any(@terminal)", ("terminal", TerminalStatuses));
        var fixedCount = 0;
        foreach (var child in stuck)
            if (await DeliverToParentAsync(child.G("id"))) fixedCount++;
        if (fixedCount > 0) Log.Warn($"Reconciliacao: {fixedCount} pai(s) parado(s) em waiting_child receberam o resultado do filho.");
        return fixedCount;
    }

    /// <summary>
    /// Runs de conversa em execucao cujo dono parou de renovar o lease (instancia caiu): voltam para ready, com o
    /// evento run_recovered registrando a instancia anterior. SKIP LOCKED: varias instancias podem varrer juntas.
    /// </summary>
    public async Task<int> RecoverOrphansAsync(string recoveredBy)
    {
        var orphans = await _db.QueryAsync(
            @"with orphans as (
                  select id, owner, lease_until from runs
                   where status = 'running' and lease_until < now() and conversation_id is not null
                   for update skip locked)
              update runs r set status = 'ready', owner = null, lease_until = null
                from orphans o where r.id = o.id
            returning r.id, o.owner as previous_owner, o.lease_until as expired_at");
        foreach (var orphan in orphans)
        {
            await _events.AddDetachedAsync(orphan.G("id"), "run_recovered", new JsonObject
            {
                ["previous_owner"] = orphan.SN("previous_owner"),
                ["lease_expired_at"] = orphan.TN("expired_at")?.ToString("o"),
                ["recovered_by"] = recoveredBy
            });
            await _db.NotifyAsync(ReadyChannel, orphan.G("id").ToString());
        }
        if (orphans.Count > 0) Log.Warn($"{recoveredBy} recuperou {orphans.Count} run(s) orfao(s) com lease vencido.");
        return orphans.Count;
    }

    /// <summary>Se nao ha mais pedido pendente do run, ele fica pronto para retomar.</summary>
    public async Task MarkReadyIfDecidedAsync(Guid runId)
    {
        var changed = await _db.ExecAsync(
            @"update runs set status = 'ready'
               where id = @r and status = 'waiting_human'
                 and not exists (select 1 from approvals where run_id = @r and status = 'pending')", ("r", runId));
        if (changed > 0) await _db.NotifyAsync(ReadyChannel, runId.ToString());
    }

    /// <summary>Nega como expired os pedidos com prazo vencido e deixa os runs prontos para replanejar.</summary>
    public async Task<int> ExpireDueAsync()
    {
        var expired = await _db.QueryAsync(
            @"update approvals set status = 'expired', reason = 'prazo de resposta vencido sem decisao', decided_at = now()
               where status = 'pending' and expires_at <= now()
           returning run_id");
        foreach (var runId in expired.Select(r => r.G("run_id")).Distinct()) await MarkReadyIfDecidedAsync(runId);
        return expired.Count;
    }

    /// <summary>
    /// Cancela a cadeia do run raiz: runs parados (e orfaos com lease vencido) viram cancelled e os pedidos pendentes
    /// sao fechados. Runs executando agora em qualquer instancia recebem o aviso por NOTIFY e cancelam sozinhos.
    /// </summary>
    public async Task<int> CancelTreeAsync(Guid rootRunId, string reason)
    {
        var cancelled = await _db.QueryAsync(
            SubtreeCte + @"
            update runs set status = 'cancelled', output = @reason, waiting_on_run_id = null, owner = null, lease_until = null, finished_at = now()
             where id in (select id from tree)
               and (status = any(@suspended) or (status = 'running' and lease_until < now()))
         returning id", ("root", rootRunId), ("reason", reason), ("suspended", RunStatus.Suspended));
        await _db.ExecAsync(
            SubtreeCte + @"
            update approvals set status = 'expired', reason = @reason, decided_at = now()
             where status = 'pending' and run_id in (select id from tree)", ("root", rootRunId), ("reason", reason));
        foreach (var runId in cancelled.Select(r => r.G("id")))
        {
            await _events.AddDetachedAsync(runId, "cancelled", new JsonObject { ["reason"] = reason });
            await _events.AddDetachedAsync(runId, "run_finished", new JsonObject { ["status"] = RunStatus.Cancelled, ["output"] = reason });
        }
        await _db.NotifyAsync(CancelChannel, rootRunId.ToString());
        return cancelled.Count;
    }

    /// <summary>
    /// A cadeia parou de andar sozinha: nada executando ou pronto, nenhum filho terminado esperando entrega ao pai
    /// e nenhum pedido ja decidido esperando o run ficar pronto. Ou terminou, ou depende de uma pessoa.
    /// </summary>
    public async Task<bool> IsSettledAsync(Guid rootRunId) =>
        await _db.ScalarAsync(
            SubtreeCte + @"
            select not exists (
                select 1 from runs r
                 where r.id in (select id from tree)
                   and (r.status in ('running', 'ready')
                        or (r.status = 'waiting_child' and (r.waiting_on_run_id is null or exists (
                                select 1 from runs c where c.id = r.waiting_on_run_id and c.status = any(@terminal))))
                        or (r.status = 'waiting_human' and not exists (
                                select 1 from approvals a where a.run_id = r.id and a.status = 'pending'))))",
            ("root", rootRunId), ("terminal", TerminalStatuses)) is true;

    public async Task<RunResult> WaitUntilSettledAsync(Guid rootRunId, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!await IsSettledAsync(rootRunId))
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Run {Text.Short(rootRunId)} nao assentou em {timeout.TotalSeconds:0}s.");
            await Task.Delay(100, ct);
        }
        return await SnapshotAsync(rootRunId);
    }

    public async Task<RunResult> SnapshotAsync(Guid runId)
    {
        var r = await _db.QueryOneAsync("select * from runs where id = @id", ("id", runId))
                ?? throw new InvalidOperationException($"Run {runId} nao encontrado.");
        var pending = await _db.ScalarAsync(
            SubtreeCte + @" select a.id from approvals a where a.status = 'pending' and a.run_id in (select id from tree)
                             order by a.created_at limit 1", ("root", runId)) as Guid?;
        return new RunResult(runId, r.G("root_run_id"), r.S("status"), r.S("output"), r.L("tokens_in"), r.L("tokens_out"), r.D("cost_usd"), r.I("steps"))
        {
            ActiveTools = r.A("active_tools"),
            WaitingFor = pending,
            WaitingOnRun = r.GN("waiting_on_run_id")
        };
    }

    /// <summary>A instancia ainda e dona do run (ninguem o recuperou por lease vencido).</summary>
    public async Task<bool> IsOwnerAsync(Guid runId, string owner) =>
        await _db.ScalarAsync("select exists (select 1 from runs where id = @id and owner = @owner and status = 'running')",
            ("id", runId), ("owner", owner)) is true;
}
