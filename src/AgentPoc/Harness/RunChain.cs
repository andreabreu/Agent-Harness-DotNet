using System.Text.Json.Nodes;
using AgentPoc.Core;
using AgentPoc.Llm;

namespace AgentPoc.Harness;

/// <summary>O harness avisa quando um run suspende ou termina (para acordar quem depende dele).</summary>
public interface IRunLifecycle
{
    Task OnSuspendedAsync(RunContext ctx, RunSuspendedException suspension);
    Task OnFinishedAsync(RunContext ctx, string status);
}

/// <summary>Historico do agente raiz de uma conversa (implementado pelo chat, sem o harness depender dele).</summary>
public interface IConversationHistory
{
    Task AppendAsync(Guid conversationId, Guid runId, ChatMessage message);
    Task<ChatTurn> ResumeTurnAsync(Guid conversationId, IReadOnlyCollection<string> activeTools);
    Task UpdateActiveToolsAsync(Guid conversationId, IEnumerable<string> activeTools);
}

/// <summary>
/// Transicoes duraveis entre runs, todas no Postgres e protegidas contra corrida por updates condicionais:
/// decisao gravada ou prazo vencido -> o run que pediu fica ready; filho terminou -> a saida dele vira o
/// tool_result do delegate_task no pai e o pai fica ready; cancelamento -> a cadeia suspensa inteira vira cancelled.
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

    private readonly Db _db;
    private readonly EventLog _events;
    private readonly RunMessageStore _messages;
    private readonly IConversationHistory _conversations;

    public RunChain(Db db, EventLog events, RunMessageStore messages, IConversationHistory conversations)
    {
        _db = db;
        _events = events;
        _messages = messages;
        _conversations = conversations;
    }

    public Task OnSuspendedAsync(RunContext ctx, RunSuspendedException suspension) =>
        suspension is ChildSuspendedException child
            ? TryDeliverToParentAsync(child.ChildRunId)
            : MarkReadyIfDecidedAsync(ctx.RunId);

    public Task OnFinishedAsync(RunContext ctx, string status) =>
        ctx.ParentRunId == null ? Task.CompletedTask : TryDeliverToParentAsync(ctx.RunId);

    /// <summary>Se nao ha mais pedido pendente do run, ele fica pronto para retomar.</summary>
    public async Task MarkReadyIfDecidedAsync(Guid runId)
    {
        var changed = await _db.ExecAsync(
            @"update runs set status = 'ready'
               where id = @r and status = 'waiting_human'
                 and not exists (select 1 from approvals where run_id = @r and status = 'pending')", ("r", runId));
        if (changed > 0) await _db.NotifyAsync(ReadyChannel, runId.ToString());
    }

    /// <summary>
    /// Se o filho terminou e o pai esta parado nele, grava o resultado como tool_result do delegate_task no
    /// historico do pai e deixa o pai pronto. Idempotente: so quem limpar waiting_on_run_id entrega.
    /// </summary>
    public async Task TryDeliverToParentAsync(Guid childRunId)
    {
        var child = await _db.QueryOneAsync(
            "select id, parent_run_id, agent_id, status, output, steps, tokens_in + tokens_out as tokens from runs where id = @id", ("id", childRunId));
        if (child?.GN("parent_run_id") is not { } parentId) return;
        var parent = await _db.QueryOneAsync("select status, waiting_on_run_id from runs where id = @id", ("id", parentId));
        if (parent == null || !RunStatus.ShouldDeliverToParent(child.S("status"), childRunId, parent.S("status"), parent.GN("waiting_on_run_id")))
            return;

        var claimed = await _db.QueryOneAsync(
            @"update runs set waiting_on_run_id = null
               where id = @p and status = 'waiting_child' and waiting_on_run_id = @c
           returning pending_tool_call_id, depth, conversation_id", ("p", parentId), ("c", childRunId));
        if (claimed == null) return;

        var result = DelegationResult.Format(child.S("agent_id"), child.S("status"), child.I("steps"), child.S("output"));
        var message = ChatMessage.Tool(claimed.S("pending_tool_call_id"), "delegate_task", result);
        if (claimed.I("depth") == 0 && claimed.GN("conversation_id") is { } conversationId)
            await _conversations.AppendAsync(conversationId, parentId, message);
        else
            await _messages.AppendAsync(parentId, message);

        await _events.AddDetachedAsync(parentId, "delegation_end", new JsonObject
        {
            ["to_agent"] = child.S("agent_id"),
            ["child_run_id"] = childRunId.ToString(),
            ["status"] = child.S("status"),
            ["tokens"] = child.L("tokens")
        });
        await _events.AddDetachedAsync(parentId, "tool_result", new JsonObject
        {
            ["name"] = "delegate_task",
            ["ok"] = DelegationResult.IsSuccess(result),
            ["ms"] = 0,
            ["output"] = Text.Trunc(result, 2000)
        });
        var ready = await _db.ExecAsync(
            "update runs set status = 'ready', pending_tool_call_id = null where id = @p and status = 'waiting_child'", ("p", parentId));
        if (ready > 0) await _db.NotifyAsync(ReadyChannel, parentId.ToString());
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
    /// Cancela a cadeia do run raiz: runs suspensos viram cancelled e os pedidos pendentes sao fechados.
    /// Runs executando agora em qualquer instancia recebem o aviso por NOTIFY e cancelam sozinhos.
    /// </summary>
    public async Task<int> CancelTreeAsync(Guid rootRunId, string reason)
    {
        var cancelled = await _db.QueryAsync(
            SubtreeCte + @"
            update runs set status = 'cancelled', output = @reason, waiting_on_run_id = null, finished_at = now()
             where id in (select id from tree) and status = any(@suspended)
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

    private static readonly string[] TerminalStatuses =
        { RunStatus.Completed, RunStatus.Failed, RunStatus.BudgetExceeded, RunStatus.MaxSteps, RunStatus.Cancelled };

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
}
