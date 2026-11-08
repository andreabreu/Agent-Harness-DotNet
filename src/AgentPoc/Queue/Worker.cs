using AgentPoc.Core;
using AgentPoc.Harness;
using AgentPoc.Workflows;

namespace AgentPoc.Queue;

/// <summary>
/// Worker da fila de tarefas: claim atomico com FOR UPDATE SKIP LOCKED, heartbeat,
/// retentativa ate max_attempts e recuperacao de tarefas presas.
/// </summary>
public sealed class Worker
{
    private readonly Db _db;
    private readonly AgentRunner _runner;
    private readonly WorkflowEngine _workflows;

    public Worker(Db db, AgentRunner runner, WorkflowEngine workflows)
    {
        _db = db;
        _runner = runner;
        _workflows = workflows;
    }

    public async Task<int> RecoverStaleAsync()
    {
        var n = await _db.ExecAsync(
            @"update tasks set status = 'queued', locked_by = null, updated_at = now(),
                     error = coalesce(error, '') || ' [recuperada: heartbeat expirado]'
               where status = 'running' and heartbeat_at < now() - interval '2 minutes' and attempts < max_attempts");
        if (n > 0) Log.Warn($"{n} tarefa(s) presa(s) devolvida(s) para a fila.");
        return n;
    }

    /// <summary>Processa a fila com N workers concorrentes. untilEmpty=true encerra quando a fila esvazia.</summary>
    public async Task<int> RunAsync(int concurrency, bool untilEmpty, CancellationToken ct)
    {
        await RecoverStaleAsync();
        var processed = 0;
        var workers = Enumerable.Range(1, Math.Max(1, concurrency)).Select(i => Task.Run(async () =>
        {
            var name = $"{Environment.MachineName}-w{i}";
            Log.Write(0, ConsoleColor.DarkYellow, "worker", $"{name} iniciado");
            while (!ct.IsCancellationRequested)
            {
                var task = await ClaimAsync(name);
                if (task == null)
                {
                    if (untilEmpty) break;
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                    continue;
                }
                Interlocked.Increment(ref processed);
                await ProcessAsync(task, name, ct);
            }
            Log.Write(0, ConsoleColor.DarkYellow, "worker", $"{name} encerrado");
        }, ct)).ToArray();
        await Task.WhenAll(workers);
        return processed;
    }

    private async Task<Dictionary<string, object?>?> ClaimAsync(string worker) =>
        await _db.QueryOneAsync(
            @"update tasks set status = 'running', locked_by = @w, locked_at = now(), heartbeat_at = now(),
                     attempts = attempts + 1, updated_at = now()
               where id = (select id from tasks where status = 'queued'
                            order by priority desc, created_at
                            limit 1 for update skip locked)
           returning id, agent_id, input, attempts, max_attempts, workflow_run_id", ("w", worker));

    private async Task ProcessAsync(Dictionary<string, object?> task, string worker, CancellationToken ct)
    {
        var id = task.G("id");
        var agent = task.S("agent_id");
        Log.Write(0, ConsoleColor.DarkYellow, "fila", $"{worker} pegou tarefa {Text.Short(id)} ({agent}, tentativa {task.I("attempts")}/{task.I("max_attempts")})");

        using var hbCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heartbeat = Task.Run(async () =>
        {
            try
            {
                while (!hbCts.Token.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), hbCts.Token);
                    await _db.ExecAsync("update tasks set heartbeat_at = now() where id = @id", ("id", id));
                }
            }
            catch (OperationCanceledException) { /* fim */ }
        });

        try
        {
            var workflowRun = task.GN("workflow_run_id");
            var hooks = workflowRun is { } wf ? await _workflows.HooksForRunAsync(wf) : null;
            var result = await _runner.RunAsync(agent, task.S("input"), workflowRunId: workflowRun, hooks: hooks, ct: ct);
            if (result.Status == "completed")
            {
                await _db.ExecAsync(
                    "update tasks set status = 'done', result = @r, result_run_id = @run, updated_at = now() where id = @id",
                    ("r", result.Output), ("run", result.RunId), ("id", id));
                Log.Write(0, ConsoleColor.Green, "fila", $"tarefa {Text.Short(id)} concluida por {worker}");
            }
            else
            {
                await FailAsync(task, $"run terminou com status {result.Status}: {Text.Trunc(result.Output, 500)}", result.RunId);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await FailAsync(task, ex.Message, null);
        }
        finally
        {
            hbCts.Cancel();
            await heartbeat;
        }
    }

    private async Task FailAsync(Dictionary<string, object?> task, string error, Guid? runId)
    {
        var retry = task.I("attempts") < task.I("max_attempts");
        await _db.ExecAsync(
            @"update tasks set status = @s, error = @e, result_run_id = coalesce(@run, result_run_id), locked_by = null, updated_at = now()
               where id = @id",
            ("s", retry ? "queued" : "failed"), ("e", error), ("run", runId), ("id", task.G("id")));
        Log.Write(0, ConsoleColor.Red, "fila", $"tarefa {Text.Short(task.G("id"))} falhou: {Text.Trunc(error, 200)} -> {(retry ? "volta para a fila" : "falha definitiva")}");
    }
}
