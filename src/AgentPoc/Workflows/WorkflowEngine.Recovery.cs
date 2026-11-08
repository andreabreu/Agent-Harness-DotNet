using System.Text.Json.Nodes;
using AgentPoc.Core;
using AgentPoc.Harness;

namespace AgentPoc.Workflows;

public enum WorkflowCancelResult { NotFound, AlreadyFinished, Cancelled, Requested }

public sealed partial class WorkflowEngine
{
    private const string CancelledOutput = "Cancelado a pedido.";

    /// <summary>
    /// Reivindica a execucao para retomar (status running com o lease desta instancia). Recusa se outra instancia
    /// ainda e dona (lease valido); a propria instancia pode retomar o que o despachante dela reivindicou.
    /// </summary>
    private async Task<Dictionary<string, object?>> ClaimForResumeAsync(Guid wfRunId)
    {
        var current = await _db.QueryOneAsync(
                          "select status, owner, lease_until, coalesce(status = 'running' and lease_until > now(), false) as live from workflow_runs where id = @id",
                          ("id", wfRunId))
                      ?? throw new InvalidOperationException($"workflow run {wfRunId} nao encontrado.");
        if (current.B("live") && current.SN("owner") != Owner)
            throw new InvalidOperationException(
                $"workflow run {Text.Short(wfRunId)} ainda esta executando na instancia {current.SN("owner")} (lease ate {current.TN("lease_until"):o}).");
        return await _db.QueryOneAsync(
                   @"update workflow_runs w set status = 'running', owner = @owner, lease_until = now() + make_interval(secs => @lease), finished_at = null
                      where id = @id and (owner = @owner or not coalesce(status = 'running' and lease_until > now(), false))
                  returning workflow_id, input, approval_mode, approval_mode_explicit, definition::text as definition, @previous as previous_status,
                            extract(epoch from deadline_at - now()) as deadline_in,
                            (select e.data->>'error' from workflow_events e
                              where e.workflow_run_id = w.id and e.type = 'compensation_started' order by e.id limit 1) as compensation_error",
                   ("id", wfRunId), ("owner", Owner), ("lease", _lease.Lease.TotalSeconds), ("previous", current.S("status")))
               ?? throw new InvalidOperationException($"workflow run {Text.Short(wfRunId)} foi reivindicado por outra instancia.");
    }

    /// <summary>Versao fixa: a copia gravada no inicio; execucoes antigas, sem copia, usam a definicao atual e isso fica registrado.</summary>
    private async Task<JsonObject> PinnedDefinitionAsync(Guid wfRunId, string workflowId, string? pinned)
    {
        if (pinned != null)
            return JsonNode.Parse(pinned) as JsonObject ?? throw new InvalidOperationException($"Copia da definicao do run {wfRunId} invalida.");
        await WorkflowEvents.AddAsync(_db, wfRunId, WorkflowEvents.DefinitionNotPinned, null, new JsonObject { ["workflow_id"] = workflowId });
        Log.Warn($"workflow run {Text.Short(wfRunId)} sem copia da definicao: usando a definicao atual de '{workflowId}'.");
        return await LoadDefinitionAsync(workflowId);
    }

    /// <summary>
    /// Execucoes running cujo dono parou de renovar o lease voltam para ready (workflow_recovered registra o dono
    /// anterior) e o despachante e acordado; as que tem pedido de cancelamento sao canceladas em vez de retomadas.
    /// SKIP LOCKED: varias instancias podem varrer juntas.
    /// </summary>
    public async Task<int> RecoverOrphansAsync(string recoveredBy)
    {
        var cancelled = await CancelStoppedAsync(null, recoveredBy);
        var orphans = await _db.QueryAsync(
            @"with orphans as (
                  select id, owner, lease_until from workflow_runs
                   where status = 'running' and lease_until < now() and cancel_requested_at is null
                   for update skip locked)
              update workflow_runs w set status = 'ready', owner = null, lease_until = null
                from orphans o where w.id = o.id
            returning w.id, o.owner as previous_owner, o.lease_until as expired_at");
        foreach (var orphan in orphans)
        {
            await WorkflowEvents.AddAsync(_db, orphan.G("id"), WorkflowEvents.Recovered, null, new JsonObject
            {
                ["previous_owner"] = orphan.SN("previous_owner"),
                ["lease_expired_at"] = orphan.TN("expired_at")?.ToString("o"),
                ["recovered_by"] = recoveredBy
            });
            await _db.NotifyAsync(RunChain.ReadyChannel, orphan.G("id").ToString());
        }
        if (orphans.Count > 0) Log.Warn($"{recoveredBy} recuperou {orphans.Count} execucao(oes) de workflow orfa(s).");
        return orphans.Count + cancelled;
    }

    /// <summary>Pega uma execucao ready (FOR UPDATE SKIP LOCKED), assume o lease e continua. False se nao havia nenhuma.</summary>
    public async Task<bool> ResumeNextAsync(CancellationToken ct)
    {
        if (await _db.ScalarAsync(
                @"update workflow_runs set status = 'running', owner = @owner, lease_until = now() + make_interval(secs => @lease), finished_at = null
                   where id = (select id from workflow_runs where status = 'ready' and cancel_requested_at is null
                                order by started_at limit 1 for update skip locked)
               returning id", ("owner", Owner), ("lease", _lease.Lease.TotalSeconds)) is not Guid id)
            return false;
        try
        {
            await RunAsync("", null, resumeRunId: id, ct: ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Log.Error($"despachante {Owner}: workflow run {Text.Short(id)} terminou com erro: {ex.Message}");
        }
        return true;
    }

    /// <summary>
    /// Pede o cancelamento. Execucao parada (ready, crashed ou orfa) e cancelada aqui mesmo; em execucao, o dono
    /// recebe o pedido pelo heartbeat (cancel_requested_at) ou por NOTIFY e para o que estiver rodando.
    /// </summary>
    public async Task<WorkflowCancelResult> CancelAsync(Guid wfRunId)
    {
        var requested = await _db.ExecAsync(
            @"update workflow_runs set cancel_requested_at = coalesce(cancel_requested_at, now())
               where id = @id and status in ('running', 'ready', 'crashed')", ("id", wfRunId));
        if (requested == 0)
            return await _db.ScalarAsync("select 1 from workflow_runs where id = @id", ("id", wfRunId)) == null
                ? WorkflowCancelResult.NotFound
                : WorkflowCancelResult.AlreadyFinished;
        if (await CancelStoppedAsync(wfRunId, Owner) > 0) return WorkflowCancelResult.Cancelled;
        CancelRunning(wfRunId);
        await _db.NotifyAsync(RunChain.CancelChannel, wfRunId.ToString());
        return WorkflowCancelResult.Requested;
    }

    /// <summary>Cancela a execucao se ela roda nesta instancia (pedido recebido por NOTIFY ou pelo proprio processo).</summary>
    public void CancelRunning(Guid wfRunId)
    {
        if (_running.TryGetValue(wfRunId, out var cancel)) cancel.Cancel();
    }

    /// <summary>
    /// Execucoes com pedido de cancelamento sem dono vivo (ready, crashed ou orfas) viram cancelled sem retomar; nos
    /// e runs de agente que ficaram parados nelas tambem. Nulo: todas; com id: so aquela.
    /// </summary>
    private async Task<int> CancelStoppedAsync(Guid? wfRunId, string by)
    {
        var cancelled = await _db.QueryAsync(
            @"update workflow_runs set status = 'cancelled', output = @m, owner = null, lease_until = null, finished_at = now()
               where id in (select id from workflow_runs
                             where cancel_requested_at is not null and (@id::uuid is null or id = @id)
                               and (status in ('ready', 'crashed') or (status = 'running' and lease_until < now()))
                             for update skip locked)
           returning id", ("m", CancelledOutput), ("id", wfRunId));
        foreach (var id in cancelled.Select(r => r.G("id")))
        {
            var interrupted = await InterruptRunningNodesAsync(id, CancelledOutput);
            await _db.ExecAsync(
                @"update runs set status = 'cancelled', output = @m, owner = null, lease_until = null, finished_at = now()
                   where workflow_run_id = @w and (status in ('ready', 'waiting_human', 'waiting_child')
                                                   or (status = 'running' and coalesce(lease_until < now(), true)))",
                ("m", CancelledOutput), ("w", id));
            await WorkflowEvents.AddAsync(_db, id, WorkflowEvents.Cancelled, null,
                new JsonObject { ["by"] = by, ["without_owner"] = true, ["nodes_interrupted"] = ToJsonArray(interrupted) });
            Log.Warn($"workflow run {Text.Short(id)} cancelado sem dono vivo ({by}).");
        }
        return cancelled.Count;
    }
}
