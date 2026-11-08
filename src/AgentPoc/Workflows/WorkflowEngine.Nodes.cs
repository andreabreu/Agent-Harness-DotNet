using System.Text.Json.Nodes;
using AgentPoc.Core;
using AgentPoc.Harness;

namespace AgentPoc.Workflows;

public sealed partial class WorkflowEngine
{
    private const string RetryFeedback = "Sua resposta anterior nao seguiu o formato pedido. Corrija estes erros:";

    private sealed record AgentAttempt(Guid RunId, string Status, string Output);

    /// <summary>Como uma tentativa de no terminou e o que decide se ela pode ser repetida.</summary>
    private sealed record AttemptOutcome(NodeResult Result, string Failure, bool EffectStarted = false, bool SideEffect = false);

    private sealed record NodeStart(int Attempt, DateTime StartedAt, bool Interrupted);

    /// <summary>
    /// Executa o no com as tentativas de retry (espera crescente entre elas) e o prazo de cada tentativa. Falha
    /// definitiva de no opcional fica gravada como failed, mas volta como concluida para o escalonador.
    /// Cancelamento ou prazo do workflow interrompem sem gravar o resultado.
    /// </summary>
    private async Task<NodeResult> RunNodeAsync(Execution execution, WorkflowNode node, CancellationToken token)
    {
        var start = await StartNodeAsync(execution.Id, node.Id);
        var attempt = start.Attempt;
        DateTime? interruptedSince = start.Interrupted ? start.StartedAt : null;
        if (start.Interrupted) Log.Write(0, ConsoleColor.Yellow, "workflow", $"no '{node.Id}' foi interrompido por uma queda; retomando a tentativa {attempt}");

        AttemptOutcome outcome;
        while (true)
        {
            outcome = await RunAttemptAsync(execution, node, attempt, interruptedSince, token);
            interruptedSince = null;
            token.ThrowIfCancellationRequested();
            if (outcome.Result.Ok || !node.Retry.ShouldRetry(attempt, outcome.Failure, outcome.EffectStarted, outcome.SideEffect)) break;
            var wait = node.Retry.Wait(attempt);
            await WorkflowEvents.AddAsync(_db, execution.Id, WorkflowEvents.NodeRetry, node.Id, new JsonObject
            {
                ["attempt"] = attempt,
                ["reason"] = outcome.Failure,
                ["wait_seconds"] = Math.Round(wait.TotalSeconds, 3),
                ["error"] = Text.Trunc(outcome.Result.Output, 300)
            });
            Log.Warn($"no '{node.Id}': tentativa {attempt}/{node.Retry.MaxAttempts} falhou ({outcome.Failure}); nova tentativa em {wait.TotalSeconds:0.###}s");
            await Task.Delay(wait, token);
            attempt = await NextAttemptAsync(execution.Id, node.Id, outcome.Result.Output);
        }

        var result = outcome.Result;
        await SaveNodeResultAsync(execution.Id, node.Id, result);
        if (result.Ok || !node.Optional) return result;
        await WorkflowEvents.AddAsync(_db, execution.Id, WorkflowEvents.NodeFailedOptional, node.Id,
            new JsonObject { ["attempts"] = attempt, ["error"] = Text.Trunc(result.Output, 500) });
        Log.Warn($"no opcional '{node.Id}' falhou; o workflow segue com o erro como saida");
        return result with { Ok = true };
    }

    /// <summary>Marca o no running; um no que ja estava running (queda) continua na mesma tentativa.</summary>
    private async Task<NodeStart> StartNodeAsync(Guid wfRunId, string nodeId)
    {
        var row = (await _db.QueryOneAsync(
            @"with previous as (select status from workflow_node_runs where workflow_run_id = @w and node_id = @n)
              insert into workflow_node_runs (workflow_run_id, node_id, status) values (@w, @n, 'running')
              on conflict (workflow_run_id, node_id) do update
                 set status = 'running',
                     attempt = case when workflow_node_runs.status = 'running' then workflow_node_runs.attempt else workflow_node_runs.attempt + 1 end,
                     started_at = case when workflow_node_runs.status = 'running' then workflow_node_runs.started_at else now() end,
                     finished_at = null
              returning attempt, started_at, (select status from previous) = 'running' as interrupted",
            ("w", wfRunId), ("n", nodeId)))!;
        return new NodeStart(row.I("attempt"), row.T("started_at"), row.B("interrupted"));
    }

    private async Task<int> NextAttemptAsync(Guid wfRunId, string nodeId, string lastError) =>
        Convert.ToInt32(await _db.ScalarAsync(
            @"update workflow_node_runs set attempt = attempt + 1, started_at = now(), output = @o
               where workflow_run_id = @w and node_id = @n and exists (select 1 from workflow_runs where id = @w and owner = @owner)
           returning attempt", ("o", lastError), ("w", wfRunId), ("n", nodeId), ("owner", Owner))
            ?? throw new LeaseLostException(wfRunId));

    private async Task SaveNodeResultAsync(Guid wfRunId, string nodeId, NodeResult result)
    {
        var saved = await _db.ExecAsync(
            @"update workflow_node_runs set status = @s, output = @o, agent_run_id = @r, chosen_next = @chosen, finished_at = now()
               where workflow_run_id = @w and node_id = @n
                 and exists (select 1 from workflow_runs where id = @w and owner = @owner)",
            ("s", result.Ok ? WorkflowRunStatus.Completed : WorkflowRunStatus.Failed), ("o", result.Output), ("r", result.RunId),
            ("chosen", result.ChosenNext?.ToArray()), ("w", wfRunId), ("n", nodeId), ("owner", Owner));
        if (saved == 0) throw new LeaseLostException(wfRunId);
        Log.Write(0, result.Ok ? ConsoleColor.Green : ConsoleColor.Red, "workflow",
            $"no '{nodeId}' {(result.Ok ? "concluido" : "falhou")} (checkpoint gravado)" +
            (result.ChosenNext != null ? $" -> {(result.ChosenNext.Count == 0 ? "nenhum destino" : string.Join(", ", result.ChosenNext))}" : ""));
    }

    /// <summary>Uma tentativa, com o prazo do no (timeout_seconds): vencido, o run e cancelado e a falha e timeout.</summary>
    private async Task<AttemptOutcome> RunAttemptAsync(Execution execution, WorkflowNode node, int attempt, DateTime? interruptedSince,
                                                       CancellationToken token)
    {
        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        if (node.TimeoutSeconds is { } seconds) attemptCts.CancelAfter(TimeSpan.FromSeconds(seconds));
        AttemptOutcome outcome;
        try
        {
            outcome = node.Type switch
            {
                WorkflowNodeType.Agent => new AttemptOutcome(await RunAgentNodeAsync(execution, node, interruptedSince, attemptCts.Token), NodeFailure.Error),
                WorkflowNodeType.Tool => await RunToolNodeAsync(execution, node, attempt, attemptCts.Token),
                WorkflowNodeType.Condition => new AttemptOutcome(RunCondition(node, execution.Values), NodeFailure.Error),
                WorkflowNodeType.Transform => new AttemptOutcome(
                    new NodeResult(node.Id, true, AsOutputText(execution.Values.RenderJson(node.Raw["output"]))), NodeFailure.Error),
                _ => new AttemptOutcome(new NodeResult(node.Id, true, AsOutputText(execution.Values.RenderJson(node.Raw["output"])),
                    EndStatus: node.Text("status")), NodeFailure.Error)
            };
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && attemptCts.IsCancellationRequested)
        {
            outcome = new AttemptOutcome(new NodeResult(node.Id, false, ""), NodeFailure.Timeout);
        }
        catch (WorkflowValueException ex)
        {
            outcome = new AttemptOutcome(new NodeResult(node.Id, false, $"ERRO: {ex.Message}"), NodeFailure.Error);
        }

        if (outcome.Result.Ok || token.IsCancellationRequested || !attemptCts.IsCancellationRequested) return outcome;
        await WorkflowEvents.AddAsync(_db, execution.Id, WorkflowEvents.NodeTimeout, node.Id,
            new JsonObject { ["attempt"] = attempt, ["timeout_seconds"] = node.TimeoutSeconds });
        Log.Warn($"no '{node.Id}': prazo de {node.TimeoutSeconds}s vencido na tentativa {attempt}");
        return outcome with
        {
            Result = outcome.Result with { Output = $"ERRO: prazo de {node.TimeoutSeconds}s do no vencido (tentativa {attempt})" },
            Failure = NodeFailure.Timeout
        };
    }

    /// <summary>
    /// Com output_schema, pede o JSON no prompt, valida a resposta e repete com os erros ate output_retries vezes;
    /// a saida gravada e o JSON validado. Um no interrompido por queda continua a tentativa em que parou (o run do
    /// agente e retomado do historico) e conta as tentativas ja feitas.
    /// </summary>
    /// <param name="interruptedSince">Inicio da tentativa do no interrompida por queda; nulo numa execucao normal.</param>
    private async Task<NodeResult> RunAgentNodeAsync(Execution execution, WorkflowNode node, DateTime? interruptedSince, CancellationToken ct)
    {
        var prompt = execution.Values.RenderText(node.Text("input"));
        var agent = node.Text("agent");
        var schema = node.Raw["output_schema"];
        var maxAttempts = schema == null ? 1 : (node.Raw["output_retries"]?.GetValue<int>() ?? WorkflowDefinition.DefaultOutputRetries) + 1;
        var (current, attemptsDone) = interruptedSince is { } since
            ? await ResumeInterruptedAgentAsync(execution, node.Id, since, ct)
            : (null, 0);
        var structuredPrompt = schema == null ? prompt : $"{prompt}\n\n{JsonSchemaLite.DescribeForPrompt(schema)}";
        var errors = new List<string>();
        Guid? lastRun = null;
        for (var attempt = Math.Max(1, attemptsDone); attempt <= maxAttempts; attempt++)
        {
            var attemptPrompt = errors.Count == 0 ? structuredPrompt : $"{structuredPrompt}\n\n{RetryFeedback}\n- {string.Join("\n- ", errors)}";
            var r = current ?? await StartAgentAsync(agent, attemptPrompt, execution, node.Id, ct);
            current = null;
            lastRun = r.RunId;
            if (r.Status != WorkflowRunStatus.Completed) return new NodeResult(node.Id, false, r.Output, r.RunId);
            if (schema == null) return new NodeResult(node.Id, true, r.Output, r.RunId);
            var json = JsonSchemaLite.ExtractJson(r.Output);
            errors = json == null ? new List<string> { "$: a resposta nao contem um JSON valido" } : JsonSchemaLite.Validate(schema, json);
            if (errors.Count == 0) return new NodeResult(node.Id, true, json!.ToJsonString(), r.RunId);
            Log.Warn($"no '{node.Id}': saida fora do output_schema (tentativa {attempt}/{maxAttempts}): {string.Join("; ", errors)}");
        }
        return new NodeResult(node.Id, false, $"ERRO: saida fora do output_schema apos {maxAttempts} tentativas: {string.Join("; ", errors)}", lastRun);
    }

    private async Task<AgentAttempt> StartAgentAsync(string agent, string prompt, Execution execution, string nodeId, CancellationToken ct)
    {
        var r = await _runner.RunAsync(agent, prompt, workflowRunId: execution.Id, hooks: execution.Hooks, workflowNodeId: nodeId, ct: ct);
        return new AgentAttempt(r.RunId, r.Status, r.Output);
    }

    /// <summary>
    /// O run do agente que o no interrompido tinha iniciado, segundo <see cref="NodeResumeDecision"/>: resultado ja
    /// gravado, run assumido e retomado do historico, ou nenhum (a tentativa recomeca). Devolve tambem quantas
    /// tentativas a tentativa do no ja tinha feito.
    /// </summary>
    private async Task<(AgentAttempt? Current, int AttemptsDone)> ResumeInterruptedAgentAsync(Execution execution, string nodeId, DateTime since,
                                                                                              CancellationToken ct)
    {
        while (true)
        {
            var last = await _db.QueryOneAsync(
                @"select id, status, output, lease_until, now() as db_now, count(*) over () as attempts
                    from runs where workflow_run_id = @w and workflow_node_id = @n and parent_run_id is null and started_at >= @since
                   order by started_at desc limit 1",
                ("w", execution.Id), ("n", nodeId), ("since", since));
            switch (NodeResumeDecision.For(last?.SN("status"), last?.TN("lease_until"), last?.T("db_now") ?? DateTime.UtcNow))
            {
                case NodeResumeAction.RunFromScratch:
                    return (null, 0);
                case NodeResumeAction.UseResult:
                    var finished = last!;
                    Log.Write(0, ConsoleColor.Yellow, "workflow", $"no '{nodeId}': run {Text.Short(finished.G("id"))} ja tinha terminado ({finished.S("status")}); usando o resultado");
                    return (new AgentAttempt(finished.G("id"), finished.S("status"), finished.S("output")), finished.I("attempts"));
                case NodeResumeAction.TakeOverAndResume:
                    var orphan = last!;
                    var runId = orphan.G("id");
                    var claimed = await _db.ExecAsync(
                        @"update runs set status = 'running', owner = @owner, lease_until = now() + make_interval(secs => @lease)
                           where id = @id and (status = 'ready' or (status = 'running' and lease_until < now()))",
                        ("owner", Owner), ("lease", _lease.Lease.TotalSeconds), ("id", runId));
                    if (claimed == 0) continue;
                    Log.Write(0, ConsoleColor.Yellow, "workflow", $"no '{nodeId}': assumindo o run interrompido {Text.Short(runId)} e retomando do historico");
                    var r = await _runner.ResumeAsync(runId, null, execution.Hooks, Owner, ct);
                    return (new AgentAttempt(r.RunId, r.Status, r.Output), orphan.I("attempts"));
                default:
                    await Task.Delay(_lease.Heartbeat, ct);
                    break;
            }
        }
    }

    private async Task<AttemptOutcome> RunToolNodeAsync(Execution execution, WorkflowNode node, int attempt, CancellationToken ct)
    {
        var args = execution.Values.RenderJson(node.Raw["args"]) as JsonObject ?? new JsonObject();
        var r = await _runner.RunToolAsync(node.Text("tool"), args, execution.Id, node.Id, attempt, execution.Gate, execution.Rules, ct);
        return new AttemptOutcome(new NodeResult(node.Id, r.Ok, r.Output, r.RunId), NodeFailure.Error, r.EffectStarted, r.SideEffect);
    }

    /// <summary>Primeiro caso verdadeiro vence; sem nenhum, o default (ou nenhum destino).</summary>
    private static NodeResult RunCondition(WorkflowNode node, WorkflowValues values)
    {
        var index = Enumerable.Range(0, node.Cases.Count).Cast<int?>().FirstOrDefault(i => ConditionEvaluator.Evaluate(node.Cases[i!.Value].When, values));
        var chosen = index is { } i ? node.Cases[i].Next : node.Default;
        var output = new JsonObject
        {
            ["case"] = index is { } c ? JsonValue.Create(c) : node.Default.Count > 0 ? JsonValue.Create("default") : null,
            ["next"] = new JsonArray(chosen.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray())
        };
        return new NodeResult(node.Id, true, output.ToJsonString(), ChosenNext: chosen);
    }

    /// <summary>Compensacao de um no: o no embutido em compensate, com ids proprios (&lt;no&gt;.compensate) e retomavel.</summary>
    private async Task<NodeResult> RunCompensationAsync(Execution execution, string nodeId, WorkflowNode compensation, CancellationToken token)
    {
        var id = $"{nodeId}.compensate";
        try
        {
            if (compensation.Type == WorkflowNodeType.Tool)
            {
                var args = execution.Values.RenderJson(compensation.Raw["args"]) as JsonObject ?? new JsonObject();
                var tool = await _runner.RunToolAsync(compensation.Text("tool"), args, execution.Id, id, 1, execution.Gate, execution.Rules, token);
                return new NodeResult(id, tool.Ok, tool.Output, tool.RunId);
            }
            var agent = await StartAgentAsync(compensation.Text("agent"), execution.Values.RenderText(compensation.Text("input")), execution, id, token);
            return new NodeResult(id, agent.Status == WorkflowRunStatus.Completed, agent.Output, agent.RunId);
        }
        catch (WorkflowValueException ex)
        {
            return new NodeResult(id, false, $"ERRO: {ex.Message}");
        }
    }
}
