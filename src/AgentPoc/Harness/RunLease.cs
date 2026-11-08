using System.Globalization;
using AgentPoc.Core;

namespace AgentPoc.Harness;

/// <summary>
/// Quanto tempo um run em execucao fica reservado para a instancia dona sem renovar (RUN_LEASE_SECONDS, padrao 60)
/// e de quanto em quanto tempo ela renova (RUN_HEARTBEAT_SECONDS, padrao 1/3 do lease).
/// </summary>
public sealed record RunLeaseOptions(TimeSpan Lease, TimeSpan Heartbeat)
{
    private static readonly TimeSpan MaxSweep = TimeSpan.FromSeconds(30);

    /// <summary>Varredura do despachante: frequente o bastante para recuperar orfaos logo depois do lease vencer.</summary>
    public TimeSpan Sweep => Heartbeat < MaxSweep ? Heartbeat : MaxSweep;

    public static RunLeaseOptions FromEnv() => From(Env.Get("RUN_LEASE_SECONDS"), Env.Get("RUN_HEARTBEAT_SECONDS"));

    public static RunLeaseOptions From(string? leaseSeconds, string? heartbeatSeconds)
    {
        var lease = TimeSpan.FromSeconds(Seconds(leaseSeconds) ?? 60);
        var heartbeat = Seconds(heartbeatSeconds) is { } h ? TimeSpan.FromSeconds(h) : lease / 3;
        return new RunLeaseOptions(lease, heartbeat < lease ? heartbeat : lease / 3);
    }

    /// <summary>Run executando cujo dono parou de renovar: orfao, pode ser recuperado por outra instancia.</summary>
    public static bool IsOrphan(string status, DateTime? leaseUntil, DateTime now) =>
        status == RunStatus.Running && leaseUntil is { } until && until < now;

    private static double? Seconds(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : null;
}

/// <summary>A instancia perdeu a posse do run (lease vencido e recuperado por outra): para de gravar e sai.</summary>
public sealed class LeaseLostException : Exception
{
    public LeaseLostException(Guid runId) : base($"Run {Text.Short(runId)} perdeu o lease; outra instancia assumiu.") { }
}

/// <summary>
/// Heartbeat do lease de um run ou de uma execucao de workflow enquanto executa (mesmo padrao do heartbeat das
/// tarefas da fila). Se a renovacao falhar porque outra instancia assumiu, ou se houver pedido de cancelamento,
/// cancela a execucao local.
/// </summary>
public sealed class RunLeaseKeeper : IAsyncDisposable
{
    private const string RenewRunSql =
        @"update runs set lease_until = now() + make_interval(secs => @lease)
           where id = @id and owner = @owner and status = 'running'
       returning false as cancel_requested";

    private const string RenewWorkflowSql =
        @"update workflow_runs set lease_until = now() + make_interval(secs => @lease)
           where id = @id and owner = @owner and status = 'running'
       returning cancel_requested_at is not null as cancel_requested";

    private readonly CancellationTokenSource _cts;
    private readonly Task _loop;

    private RunLeaseKeeper(Db db, string renewSql, Guid id, string owner, RunLeaseOptions options, CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loop = Task.Run(async () =>
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    await Task.Delay(options.Heartbeat, _cts.Token);
                    var renewed = await db.QueryOneAsync(renewSql, ("lease", options.Lease.TotalSeconds), ("id", id), ("owner", owner));
                    if (renewed == null) Lost = true;
                    else if (renewed.B("cancel_requested")) CancelRequested = true;
                    else continue;
                    _cts.Cancel();
                }
            }
            catch (OperationCanceledException) { /* fim da execucao */ }
        });
    }

    public static RunLeaseKeeper ForRun(Db db, Guid runId, string owner, RunLeaseOptions options, CancellationToken ct) =>
        new(db, RenewRunSql, runId, owner, options, ct);

    public static RunLeaseKeeper ForWorkflow(Db db, Guid workflowRunId, string owner, RunLeaseOptions options, CancellationToken ct) =>
        new(db, RenewWorkflowSql, workflowRunId, owner, options, ct);

    /// <summary>Cancelado quando o chamador cancela, quando o lease e perdido ou quando ha pedido de cancelamento.</summary>
    public CancellationToken Token => _cts.Token;

    public bool Lost { get; private set; }

    /// <summary>A execucao de workflow recebeu pedido de cancelamento (cancel_requested_at) e o token foi cancelado.</summary>
    public bool CancelRequested { get; private set; }

    public async ValueTask DisposeAsync()
    {
        if (!_cts.IsCancellationRequested) _cts.Cancel();
        await _loop;
        _cts.Dispose();
    }
}
