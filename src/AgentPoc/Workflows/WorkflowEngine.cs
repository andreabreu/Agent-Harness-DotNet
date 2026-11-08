using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentPoc.Core;
using AgentPoc.Harness;

namespace AgentPoc.Workflows;

public sealed class SimulatedCrashException : Exception
{
    public Guid WorkflowRunId { get; }
    public SimulatedCrashException(Guid id, string node) : base($"Queda simulada apos o no '{node}' (workflow run {id}).") => WorkflowRunId = id;
}

public sealed record WorkflowResult(Guid WorkflowRunId, string Status, string Output, List<string> ReusedNodes);

/// <summary>
/// Motor de workflow deterministico definido em JSON no banco. Nos agent, tool, condition, transform e end; quem
/// roda e quem e pulado vem do <see cref="WorkflowScheduler"/>; nos prontos rodam em paralelo (ate max_parallel);
/// checkpoint por no em workflow_node_runs (inclusive o desvio escolhido) permite retomada apos queda.
/// Cada execucao tem dono e lease (heartbeat) e guarda a copia da definicao com que comecou: se a instancia cai, o
/// despachante de outra a recupera (<see cref="IWorkflowRecovery"/>) e continua na mesma versao, retomando o run
/// do agente interrompido pelo historico. Prazo total, cancelamento e compensacao encerram a execucao.
/// </summary>
public sealed partial class WorkflowEngine : IWorkflowRecovery
{
    /// <summary>Status devolvido (nao gravado) quando outra instancia assumiu a execucao.</summary>
    public const string LeaseLostStatus = "lease_lost";

    private readonly Db _db;
    private readonly AgentRunner _runner;
    private readonly RunLeaseOptions _lease;
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _running = new();

    public WorkflowEngine(Db db, AgentRunner runner, RunLeaseOptions lease)
    {
        _db = db;
        _runner = runner;
        _lease = lease;
    }

    private string Owner => _runner.InstanceId;

    private sealed record NodeResult(string Node, bool Ok, string Output, Guid? RunId = null,
                                     IReadOnlyList<string>? ChosenNext = null, string? EndStatus = null);

    /// <summary>Uma execucao carregada: definicao fixa, valores e escalonador restaurados do checkpoint.</summary>
    private sealed class Execution
    {
        public required Guid Id { get; init; }
        public required string WorkflowId { get; init; }
        public required WorkflowDefinition Definition { get; init; }
        public required WorkflowValues Values { get; init; }
        public required WorkflowScheduler Scheduler { get; init; }
        public required IHumanGate Gate { get; init; }
        public required WorkflowToolRules? Rules { get; init; }
        public List<string> Reused { get; } = new();

        /// <summary>Quanto falta para o prazo total (deadline_at); nulo, sem prazo.</summary>
        public TimeSpan? DeadlineIn { get; init; }

        /// <summary>Erro que iniciou a compensacao, quando a execucao caiu no meio dela.</summary>
        public string? CompensationError { get; init; }

        public RunHooks Hooks => new(null, Gate, Rules);
    }

    /// <param name="approval">Modo de aprovacao da execucao; nulo nega (padrao) ou, na retomada, mantem o gravado.</param>
    public async Task<WorkflowResult> RunAsync(string workflowId, string? input, Guid? resumeRunId = null, string? crashAfter = null,
                                               WorkflowApprovalMode? approval = null, CancellationToken ct = default)
    {
        var execution = resumeRunId is Guid rid
            ? await ResumeExecutionAsync(rid, approval)
            : await StartExecutionAsync(workflowId, input ?? "", approval ?? WorkflowApprovalMode.Default);
        return await ExecuteAsync(execution, crashAfter, ct);
    }

    private async Task<Execution> StartExecutionAsync(string workflowId, string input, WorkflowApprovalMode approval)
    {
        var version = await _db.ScalarAsync("select version from workflows where id = @id and enabled", ("id", workflowId))
                      ?? throw new InvalidOperationException($"Workflow '{workflowId}' nao existe ou esta desabilitado.");
        var raw = await LoadDefinitionAsync(workflowId);
        var definition = WorkflowDefinition.Parse(raw, workflowId);
        EnsureInputMatchesSchema(workflowId, definition, input);
        await EnsureToolRulesMatchRegisteredToolsAsync(workflowId, WorkflowToolRules.Parse(raw));
        var id = (Guid)(await _db.ScalarAsync(
            @"insert into workflow_runs (workflow_id, workflow_version, input, approval_mode, approval_mode_explicit, definition,
                                         owner, lease_until, deadline_at)
              values (@w, @v, @i, @mode, @explicit, @def, @owner, now() + make_interval(secs => @lease),
                      now() + make_interval(secs => @timeout::double precision)) returning id",
            ("w", workflowId), ("v", Convert.ToInt32(version)), ("i", input), ("mode", approval.Mode), ("explicit", approval.Explicit),
            ("def", raw), ("owner", Owner), ("lease", _lease.Lease.TotalSeconds), ("timeout", (double?)definition.TimeoutSeconds)))!;
        Log.Info($"Workflow '{workflowId}' v{version} iniciado: run {Text.Short(id)}");
        Log.Info($"aprovacao: {approval.Describe()}");
        return new Execution
        {
            Id = id, WorkflowId = workflowId, Definition = definition, Values = new WorkflowValues(input),
            Scheduler = new WorkflowScheduler(definition), Gate = approval.Gate(), Rules = WorkflowToolRules.Parse(raw),
            DeadlineIn = definition.TimeoutSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null
        };
    }

    private async Task<Execution> ResumeExecutionAsync(Guid id, WorkflowApprovalMode? requested)
    {
        var run = await ClaimForResumeAsync(id);
        var workflowId = run.S("workflow_id");
        var raw = await PinnedDefinitionAsync(id, workflowId, run.SN("definition"));
        var definition = WorkflowDefinition.Parse(raw, workflowId);
        var approval = await ResumedApprovalAsync(id, run, requested);
        var execution = new Execution
        {
            Id = id, WorkflowId = workflowId, Definition = definition, Values = new WorkflowValues(run.S("input")),
            Scheduler = new WorkflowScheduler(definition), Gate = approval.Gate(), Rules = WorkflowToolRules.Parse(raw),
            DeadlineIn = run["deadline_in"] is { } left ? TimeSpan.FromSeconds(Convert.ToDouble(left)) : null,
            CompensationError = run.SN("compensation_error")
        };
        var checkpoint = await _db.QueryAsync(
            "select node_id, status, output, chosen_next from workflow_node_runs where workflow_run_id = @id and status in ('completed', 'skipped', 'failed')",
            ("id", id));
        RestoreCheckpoint(checkpoint, execution);
        Log.Info($"Retomando workflow '{workflowId}' run {Text.Short(id)} (status anterior: {run.S("previous_status")}).");
        Log.Info($"aprovacao: {approval.Describe()}");
        Log.Info($"Nos reaproveitados do checkpoint: {(execution.Reused.Count == 0 ? "nenhum" : string.Join(", ", execution.Reused))}");
        return execution;
    }

    private async Task<WorkflowResult> ExecuteAsync(Execution execution, string? crashAfter, CancellationToken ct)
    {
        await using var lease = RunLeaseKeeper.ForWorkflow(_db, execution.Id, Owner, _lease, ct);
        using var cancelHere = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource();
        if (execution.DeadlineIn is { } left) deadline.CancelAfter(left > TimeSpan.Zero ? left : TimeSpan.Zero);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(lease.Token, cancelHere.Token, deadline.Token);
        _running[execution.Id] = cancelHere;

        string? StopStatus() =>
            lease.Lost ? LeaseLostStatus
            : lease.CancelRequested || cancelHere.IsCancellationRequested ? WorkflowRunStatus.Cancelled
            : deadline.IsCancellationRequested ? WorkflowRunStatus.Expired
            : null;

        try
        {
            return await LoopAsync(execution, crashAfter, stop.Token, StopStatus);
        }
        catch (SimulatedCrashException)
        {
            throw;
        }
        catch (Exception ex) when (StopStatus() is not null && ex is OperationCanceledException or LeaseLostException)
        {
            return await StopAsync(execution, StopStatus()!);
        }
        catch (LeaseLostException)
        {
            return LeaseLost(execution);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await _db.ExecAsync(
                "update workflow_runs set status = 'failed', output = @o, owner = null, lease_until = null, finished_at = now() where id = @id and owner = @owner",
                ("o", ex.Message), ("id", execution.Id), ("owner", Owner));
            throw;
        }
        finally
        {
            _running.TryRemove(execution.Id, out _);
        }
    }

    private async Task<WorkflowResult> LoopAsync(Execution execution, string? crashAfter, CancellationToken token, Func<string?> stopStatus)
    {
        var (definition, scheduler, values) = (execution.Definition, execution.Scheduler, execution.Values);
        if (execution.CompensationError is { } compensationError) return await FailAsync(execution, compensationError, token);
        while (true)
        {
            var step = scheduler.Next(definition.MaxParallel);
            await SaveSkippedAsync(execution.Id, step.Skipped, values);
            if (step.Ready.Count == 0)
            {
                if (!scheduler.HasPending) break;
                throw new InvalidOperationException("Workflow sem progresso possivel (ciclo nas arestas?).");
            }
            Log.Write(0, ConsoleColor.Blue, "workflow",
                step.Ready.Count > 1 ? $"executando em paralelo: {string.Join(" || ", step.Ready)}" : $"executando no: {step.Ready[0]}");

            var results = await Task.WhenAll(step.Ready.Select(n => RunNodeAsync(execution, definition.Nodes[n], token)));
            if (stopStatus() is { } stopped) return await StopAsync(execution, stopped);
            foreach (var res in results.Where(x => x.Ok))
            {
                values.SetOutput(res.Node, res.Output);
                scheduler.Complete(res.Node, res.ChosenNext);
            }
            var failed = results.Where(x => !x.Ok).ToList();
            if (failed.Count > 0)
            {
                var msg = string.Join("; ", failed.Select(f => $"{f.Node}: {Text.Trunc(f.Output, 300)}"));
                Log.Error($"Workflow falhou: {msg}");
                return await FailAsync(execution, msg, token);
            }

            if (FirstEnd(results, definition) is { } end)
            {
                await SaveSkippedAsync(execution.Id, scheduler.Finish(), values);
                return await FinishAsync(execution, end.EndStatus!, end.Output);
            }

            if (crashAfter != null && step.Ready.Contains(crashAfter))
            {
                await _db.ExecAsync("update workflow_runs set status = 'crashed', owner = null, lease_until = null where id = @id and owner = @owner",
                    ("id", execution.Id), ("owner", Owner));
                throw new SimulatedCrashException(execution.Id, crashAfter);
            }
        }

        try
        {
            return await FinishAsync(execution, WorkflowRunStatus.Completed, FinalOutput(definition, scheduler, values));
        }
        catch (WorkflowValueException ex)
        {
            return await FailAsync(execution, $"output: {ex.Message}", token);
        }
    }

    /// <summary>No opcional que falhou conta como concluido (com o erro como saida); os demais failed voltam a rodar.</summary>
    private static void RestoreCheckpoint(List<Dictionary<string, object?>> checkpoint, Execution execution)
    {
        var (definition, scheduler, values) = (execution.Definition, execution.Scheduler, execution.Values);
        foreach (var row in checkpoint.Where(r => definition.Nodes.ContainsKey(r.S("node_id"))))
        {
            var node = row.S("node_id");
            switch (row.S("status"))
            {
                case "skipped":
                    scheduler.Restore(node, NodeState.Skipped);
                    values.MarkSkipped(node);
                    break;
                case WorkflowRunStatus.Failed when !definition.Nodes[node].Optional:
                    break;
                default:
                    scheduler.Restore(node, NodeState.Completed, row["chosen_next"] as string[]);
                    values.SetOutput(node, row.S("output"));
                    execution.Reused.Add(node);
                    break;
            }
        }
    }

    /// <summary>Dois end no mesmo lote: failed vence; empate, o primeiro na ordem da definicao.</summary>
    private static NodeResult? FirstEnd(NodeResult[] results, WorkflowDefinition definition) =>
        results.Where(r => r.EndStatus != null)
            .OrderBy(r => r.EndStatus == WorkflowRunStatus.Failed ? 0 : 1)
            .ThenBy(r => definition.Ordered.ToList().FindIndex(n => n.Id == r.Node))
            .FirstOrDefault();

    /// <summary>Saida declarada em "output"; sem ela, as saidas dos nos finais que executaram.</summary>
    private static string FinalOutput(WorkflowDefinition definition, WorkflowScheduler scheduler, WorkflowValues values)
    {
        if (definition.Output != null) return AsOutputText(values.RenderJson(definition.Output));
        var terminals = definition.Ordered.Where(n => n.Next.Count == 0 && scheduler.StateOf(n.Id) == NodeState.Completed);
        return string.Join("\n\n", terminals.Select(n => values.OutputOf(n.Id) ?? ""));
    }

    private static string AsOutputText(JsonNode? value) =>
        value is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : value?.ToJsonString() ?? "";

    private async Task<WorkflowResult> FinishAsync(Execution execution, string status, string output)
    {
        var recorded = await _db.ExecAsync(
            "update workflow_runs set status = @s, output = @o, owner = null, lease_until = null, finished_at = now() where id = @id and owner = @owner",
            ("s", status), ("o", output), ("id", execution.Id), ("owner", Owner));
        if (recorded == 0) return LeaseLost(execution);
        if (status == WorkflowRunStatus.Completed) Log.Ok($"Workflow '{execution.WorkflowId}' concluido (run {Text.Short(execution.Id)}).");
        else Log.Error($"Workflow '{execution.WorkflowId}' terminou como {status} (run {Text.Short(execution.Id)}): {Text.Trunc(output, 300)}");
        return new WorkflowResult(execution.Id, status, output, execution.Reused);
    }

    private static WorkflowResult LeaseLost(Execution execution)
    {
        Log.Warn($"Workflow '{execution.WorkflowId}' run {Text.Short(execution.Id)}: posse perdida para outra instancia; nada mais gravado.");
        return new WorkflowResult(execution.Id, LeaseLostStatus, "Outra instancia assumiu a execucao.", execution.Reused);
    }

    /// <summary>Cancelamento ou prazo total vencido: nos que estavam executando ficam failed e a execucao termina.</summary>
    private async Task<WorkflowResult> StopAsync(Execution execution, string status)
    {
        if (status == LeaseLostStatus) return LeaseLost(execution);
        var (type, message) = status == WorkflowRunStatus.Cancelled
            ? (WorkflowEvents.Cancelled, CancelledOutput)
            : (WorkflowEvents.Expired, $"Prazo total de {execution.Definition.TimeoutSeconds}s vencido.");
        var interrupted = await InterruptRunningNodesAsync(execution.Id, message);
        await WorkflowEvents.AddAsync(_db, execution.Id, type, null, new JsonObject
        {
            ["nodes_interrupted"] = ToJsonArray(interrupted),
            ["timeout_seconds"] = execution.Definition.TimeoutSeconds
        });
        return await FinishAsync(execution, status, message);
    }

    private async Task<List<string>> InterruptRunningNodesAsync(Guid wfRunId, string message) =>
        (await _db.QueryAsync(
            @"update workflow_node_runs set status = 'failed', output = @m, finished_at = now()
               where workflow_run_id = @w and status = 'running' returning node_id", ("m", message), ("w", wfRunId)))
        .Select(r => r.S("node_id")).ToList();

    private static JsonArray ToJsonArray(IEnumerable<string> items) => new(items.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray());

    /// <summary>
    /// O workflow falhou: compensa os nos concluidos que declaram compensate, do ultimo para o primeiro. Cada
    /// compensacao concluida fica em compensated_at, entao uma queda no meio continua de onde parou.
    /// </summary>
    private async Task<WorkflowResult> FailAsync(Execution execution, string message, CancellationToken token)
    {
        var plan = CompensationPlan.Order(await CompensationCandidatesAsync(execution));
        if (plan.Count == 0 && execution.CompensationError == null) return await FinishAsync(execution, WorkflowRunStatus.Failed, message);
        if (execution.CompensationError == null)
            await WorkflowEvents.AddAsync(_db, execution.Id, WorkflowEvents.CompensationStarted, null,
                new JsonObject { ["nodes"] = ToJsonArray(plan), ["error"] = message });
        Log.Write(0, ConsoleColor.Yellow, "workflow", $"compensando: {string.Join(" -> ", plan)}");
        foreach (var node in plan)
        {
            var result = await RunCompensationAsync(execution, node, execution.Definition.Nodes[node].Compensate!, token);
            if (!result.Ok)
            {
                await WorkflowEvents.AddAsync(_db, execution.Id, WorkflowEvents.CompensationFailed, node,
                    new JsonObject { ["node"] = node, ["error"] = Text.Trunc(result.Output, 500) });
                return await FinishAsync(execution, WorkflowRunStatus.CompensationFailed,
                    $"{message}; compensacao de '{node}' falhou: {Text.Trunc(result.Output, 300)}");
            }
            await _db.ExecAsync("update workflow_node_runs set compensated_at = now() where workflow_run_id = @w and node_id = @n",
                ("w", execution.Id), ("n", node));
            await WorkflowEvents.AddAsync(_db, execution.Id, WorkflowEvents.NodeCompensated, node, new JsonObject { ["node"] = node });
            Log.Write(0, ConsoleColor.Green, "workflow", $"no '{node}' compensado");
        }
        return await FinishAsync(execution, WorkflowRunStatus.Failed, message);
    }

    private async Task<IEnumerable<CompensationCandidate>> CompensationCandidatesAsync(Execution execution) =>
        (await _db.QueryAsync(
            "select node_id, finished_at, compensated_at from workflow_node_runs where workflow_run_id = @w and status = 'completed'",
            ("w", execution.Id)))
        .Where(r => execution.Definition.Nodes.ContainsKey(r.S("node_id")))
        .Select(r => new CompensationCandidate(r.S("node_id"), r.T("finished_at"),
            execution.Definition.Nodes[r.S("node_id")].Compensate != null, r.TN("compensated_at") != null));

    private async Task SaveSkippedAsync(Guid wfRunId, IReadOnlyList<string> skipped, WorkflowValues values)
    {
        foreach (var node in skipped)
        {
            values.MarkSkipped(node);
            await _db.ExecAsync(
                @"insert into workflow_node_runs (workflow_run_id, node_id, status, finished_at) values (@w, @n, 'skipped', now())
                  on conflict (workflow_run_id, node_id) do update set status = 'skipped', finished_at = now()",
                ("w", wfRunId), ("n", node));
            Log.Write(0, ConsoleColor.DarkGray, "workflow", $"no '{node}' pulado");
        }
    }

    private static void EnsureInputMatchesSchema(string workflowId, WorkflowDefinition definition, string input)
    {
        if (definition.InputSchema == null) return;
        JsonNode? parsed;
        try { parsed = JsonNode.Parse(input); }
        catch (JsonException) { parsed = null; }
        var errors = parsed == null ? new List<string> { "$: a entrada deve ser um JSON" } : JsonSchemaLite.Validate(definition.InputSchema, parsed);
        if (errors.Count > 0)
            throw new InvalidOperationException($"Workflow '{workflowId}' nao iniciado: entrada fora do input_schema:\n{string.Join("\n", errors)}");
    }

    private async Task<JsonObject> LoadDefinitionAsync(string workflowId)
    {
        var json = (string?)await _db.ScalarAsync("select definition::text from workflows where id = @id", ("id", workflowId))
                   ?? throw new InvalidOperationException($"Workflow '{workflowId}' sem definicao.");
        return JsonNode.Parse(json) as JsonObject ?? throw new InvalidOperationException($"Definicao do workflow '{workflowId}' nao e um objeto JSON.");
    }

    private async Task EnsureToolRulesMatchRegisteredToolsAsync(string workflowId, WorkflowToolRules? rules)
    {
        if (rules == null) return;
        var registered = (await _db.QueryAsync("select name from tools")).Select(r => r.S("name"));
        var unmatched = rules.UnmatchedPatterns(registered);
        if (unmatched.Length > 0)
            throw new InvalidOperationException(
                $"Workflow '{workflowId}' nao iniciado: regras de ferramenta sem correspondencia no registro: {string.Join(", ", unmatched)}.");
    }

    /// <summary>
    /// Modo de aprovacao e regras de ferramenta de uma execucao, para trabalho que ela disparou fora do motor
    /// (tarefas da fila criadas por agentes dos nos).
    /// </summary>
    public async Task<RunHooks> HooksForRunAsync(Guid wfRunId)
    {
        var run = await _db.QueryOneAsync(
                      "select workflow_id, approval_mode, approval_mode_explicit, definition::text as definition from workflow_runs where id = @id",
                      ("id", wfRunId))
                  ?? throw new InvalidOperationException($"workflow run {wfRunId} nao encontrado.");
        var definition = run.SN("definition") is { } pinned
            ? JsonNode.Parse(pinned) as JsonObject ?? new JsonObject()
            : await LoadDefinitionAsync(run.S("workflow_id"));
        return new RunHooks(null, StoredApproval(run).Gate(), WorkflowToolRules.Parse(definition));
    }

    private static WorkflowApprovalMode StoredApproval(Dictionary<string, object?> run) =>
        run.SN("approval_mode") is { } mode ? new WorkflowApprovalMode(mode, run.B("approval_mode_explicit")) : WorkflowApprovalMode.Default;

    /// <summary>Retomada mantem o modo gravado; um modo explicito novo substitui e fica registrado.</summary>
    private async Task<WorkflowApprovalMode> ResumedApprovalAsync(Guid wfRunId, Dictionary<string, object?> run, WorkflowApprovalMode? requested)
    {
        var stored = StoredApproval(run);
        if (requested is not { Explicit: true } || requested == stored) return stored;
        await _db.ExecAsync("update workflow_runs set approval_mode = @mode, approval_mode_explicit = true where id = @id",
            ("mode", requested.Mode), ("id", wfRunId));
        Log.Warn($"Retomada troca o modo de aprovacao de {stored.Describe()} para {requested.Describe()}.");
        return requested;
    }
}
