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
/// roda e quem e pulado vem do <see cref="WorkflowScheduler"/>; nos prontos rodam em paralelo; checkpoint por no em
/// workflow_node_runs (inclusive o desvio escolhido) permite retomada apos queda.
/// </summary>
public sealed class WorkflowEngine
{
    private const string RetryFeedback = "Sua resposta anterior nao seguiu o formato pedido. Corrija estes erros:";

    private readonly Db _db;
    private readonly AgentRunner _runner;

    public WorkflowEngine(Db db, AgentRunner runner)
    {
        _db = db;
        _runner = runner;
    }

    private sealed record NodeResult(string Node, bool Ok, string Output, Guid? RunId = null,
                                     IReadOnlyList<string>? ChosenNext = null, string? EndStatus = null);

    /// <param name="approval">Modo de aprovacao da execucao; nulo nega (padrao) ou, na retomada, mantem o gravado.</param>
    public async Task<WorkflowResult> RunAsync(string workflowId, string? input, Guid? resumeRunId = null, string? crashAfter = null,
                                               WorkflowApprovalMode? approval = null, CancellationToken ct = default)
    {
        Guid wfRunId;
        string wfInput;
        JsonObject rawDefinition;
        WorkflowDefinition definition;
        var checkpoint = new List<Dictionary<string, object?>>();
        var reused = new List<string>();

        if (resumeRunId is Guid rid)
        {
            var run = await _db.QueryOneAsync(
                          "select workflow_id, input, status, approval_mode, approval_mode_explicit from workflow_runs where id = @id", ("id", rid))
                      ?? throw new InvalidOperationException($"workflow run {rid} nao encontrado.");
            workflowId = run.S("workflow_id");
            wfInput = run.S("input");
            wfRunId = rid;
            rawDefinition = await LoadDefinitionAsync(workflowId);
            definition = WorkflowDefinition.Parse(rawDefinition, workflowId);
            approval = await ResumedApprovalAsync(rid, run, approval);
            checkpoint = await _db.QueryAsync(
                "select node_id, status, output, chosen_next from workflow_node_runs where workflow_run_id = @id and status in ('completed', 'skipped')",
                ("id", rid));
            await _db.ExecAsync("update workflow_runs set status = 'running', finished_at = null where id = @id", ("id", rid));
            Log.Info($"Retomando workflow '{workflowId}' run {Text.Short(rid)} (status anterior: {run.S("status")}).");
        }
        else
        {
            wfInput = input ?? "";
            approval ??= WorkflowApprovalMode.Default;
            var version = await _db.ScalarAsync("select version from workflows where id = @id and enabled", ("id", workflowId))
                          ?? throw new InvalidOperationException($"Workflow '{workflowId}' nao existe ou esta desabilitado.");
            rawDefinition = await LoadDefinitionAsync(workflowId);
            definition = WorkflowDefinition.Parse(rawDefinition, workflowId);
            EnsureInputMatchesSchema(workflowId, definition, wfInput);
            await EnsureToolRulesMatchRegisteredToolsAsync(workflowId, WorkflowToolRules.Parse(rawDefinition));
            wfRunId = (Guid)(await _db.ScalarAsync(
                @"insert into workflow_runs (workflow_id, workflow_version, input, approval_mode, approval_mode_explicit)
                  values (@w, @v, @i, @mode, @explicit) returning id",
                ("w", workflowId), ("v", Convert.ToInt32(version)), ("i", wfInput), ("mode", approval.Mode), ("explicit", approval.Explicit)))!;
            Log.Info($"Workflow '{workflowId}' v{version} iniciado: run {Text.Short(wfRunId)}");
        }
        Log.Info($"aprovacao: {approval.Describe()}");
        var gate = approval.Gate();
        var rules = WorkflowToolRules.Parse(rawDefinition);

        var values = new WorkflowValues(wfInput);
        var scheduler = new WorkflowScheduler(definition);
        RestoreCheckpoint(checkpoint, definition, scheduler, values, reused);
        if (resumeRunId != null)
            Log.Info($"Nos reaproveitados do checkpoint: {(reused.Count == 0 ? "nenhum" : string.Join(", ", reused))}");

        try
        {
            while (true)
            {
                var step = scheduler.Next();
                await SaveSkippedAsync(wfRunId, step.Skipped, values);
                if (step.Ready.Count == 0)
                {
                    if (!scheduler.HasPending) break;
                    throw new InvalidOperationException("Workflow sem progresso possivel (ciclo nas arestas?).");
                }
                Log.Write(0, ConsoleColor.Blue, "workflow",
                    step.Ready.Count > 1 ? $"executando em paralelo: {string.Join(" || ", step.Ready)}" : $"executando no: {step.Ready[0]}");

                var results = await Task.WhenAll(step.Ready.Select(n => RunNodeAsync(wfRunId, definition.Nodes[n], values, gate, rules, ct)));
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
                    return await FinishAsync(wfRunId, workflowId, WorkflowRunStatus.Failed, msg, reused);
                }

                if (FirstEnd(results, definition) is { } end)
                {
                    await SaveSkippedAsync(wfRunId, scheduler.Finish(), values);
                    return await FinishAsync(wfRunId, workflowId, end.EndStatus!, end.Output, reused);
                }

                if (crashAfter != null && step.Ready.Contains(crashAfter))
                {
                    await _db.ExecAsync("update workflow_runs set status = 'crashed' where id = @id", ("id", wfRunId));
                    throw new SimulatedCrashException(wfRunId, crashAfter);
                }
            }
        }
        catch (SimulatedCrashException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await _db.ExecAsync("update workflow_runs set status = 'failed', output = @o, finished_at = now() where id = @id", ("o", ex.Message), ("id", wfRunId));
            throw;
        }

        try
        {
            return await FinishAsync(wfRunId, workflowId, WorkflowRunStatus.Completed, FinalOutput(definition, scheduler, values), reused);
        }
        catch (WorkflowValueException ex)
        {
            return await FinishAsync(wfRunId, workflowId, WorkflowRunStatus.Failed, $"output: {ex.Message}", reused);
        }
    }

    private static void RestoreCheckpoint(List<Dictionary<string, object?>> checkpoint, WorkflowDefinition definition,
                                          WorkflowScheduler scheduler, WorkflowValues values, List<string> reused)
    {
        foreach (var row in checkpoint.Where(r => definition.Nodes.ContainsKey(r.S("node_id"))))
        {
            var node = row.S("node_id");
            if (row.S("status") == "skipped")
            {
                scheduler.Restore(node, NodeState.Skipped);
                values.MarkSkipped(node);
                continue;
            }
            scheduler.Restore(node, NodeState.Completed, row["chosen_next"] as string[]);
            values.SetOutput(node, row.S("output"));
            reused.Add(node);
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

    private async Task<WorkflowResult> FinishAsync(Guid wfRunId, string workflowId, string status, string output, List<string> reused)
    {
        await _db.ExecAsync("update workflow_runs set status = @s, output = @o, finished_at = now() where id = @id",
            ("s", status), ("o", output), ("id", wfRunId));
        if (status == WorkflowRunStatus.Completed) Log.Ok($"Workflow '{workflowId}' concluido (run {Text.Short(wfRunId)}).");
        else Log.Error($"Workflow '{workflowId}' terminou como {status} (run {Text.Short(wfRunId)}): {Text.Trunc(output, 300)}");
        return new WorkflowResult(wfRunId, status, output, reused);
    }

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
        var run = await _db.QueryOneAsync("select workflow_id, approval_mode, approval_mode_explicit from workflow_runs where id = @id", ("id", wfRunId))
                  ?? throw new InvalidOperationException($"workflow run {wfRunId} nao encontrado.");
        var definition = await LoadDefinitionAsync(run.S("workflow_id"));
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

    private async Task<NodeResult> RunNodeAsync(Guid wfRunId, WorkflowNode node, WorkflowValues values, IHumanGate gate,
                                                WorkflowToolRules? rules, CancellationToken ct)
    {
        await _db.ExecAsync(
            @"insert into workflow_node_runs (workflow_run_id, node_id, status) values (@w, @n, 'running')
              on conflict (workflow_run_id, node_id) do update
                 set status = 'running', attempt = workflow_node_runs.attempt + 1, started_at = now(), finished_at = null",
            ("w", wfRunId), ("n", node.Id));

        NodeResult result;
        try
        {
            result = node.Type switch
            {
                WorkflowNodeType.Agent => await RunAgentNodeAsync(wfRunId, node, values, new RunHooks(null, gate, rules), ct),
                WorkflowNodeType.Tool => await RunToolNodeAsync(wfRunId, node, values, gate, rules, ct),
                WorkflowNodeType.Condition => RunCondition(node, values),
                WorkflowNodeType.Transform => new NodeResult(node.Id, true, AsOutputText(values.RenderJson(node.Raw["output"]))),
                _ => new NodeResult(node.Id, true, AsOutputText(values.RenderJson(node.Raw["output"])), EndStatus: node.Text("status"))
            };
        }
        catch (WorkflowValueException ex)
        {
            result = new NodeResult(node.Id, false, $"ERRO: {ex.Message}");
        }

        await _db.ExecAsync(
            @"update workflow_node_runs set status = @s, output = @o, agent_run_id = @r, chosen_next = @chosen, finished_at = now()
               where workflow_run_id = @w and node_id = @n",
            ("s", result.Ok ? WorkflowRunStatus.Completed : WorkflowRunStatus.Failed), ("o", result.Output), ("r", result.RunId),
            ("chosen", result.ChosenNext?.ToArray()), ("w", wfRunId), ("n", node.Id));
        Log.Write(0, result.Ok ? ConsoleColor.Green : ConsoleColor.Red, "workflow",
            $"no '{node.Id}' {(result.Ok ? "concluido" : "falhou")} (checkpoint gravado)" +
            (result.ChosenNext != null ? $" -> {(result.ChosenNext.Count == 0 ? "nenhum destino" : string.Join(", ", result.ChosenNext))}" : ""));
        return result;
    }

    /// <summary>
    /// Com output_schema, pede o JSON no prompt, valida a resposta e repete com os erros ate output_retries vezes;
    /// a saida gravada e o JSON validado.
    /// </summary>
    private async Task<NodeResult> RunAgentNodeAsync(Guid wfRunId, WorkflowNode node, WorkflowValues values, RunHooks hooks, CancellationToken ct)
    {
        var prompt = values.RenderText(node.Text("input"));
        var agent = node.Text("agent");
        if (node.Raw["output_schema"] is not JsonNode schema)
        {
            var r = await _runner.RunAsync(agent, prompt, workflowRunId: wfRunId, hooks: hooks, ct: ct);
            return new NodeResult(node.Id, r.Status == WorkflowRunStatus.Completed, r.Output, r.RunId);
        }

        var retries = node.Raw["output_retries"]?.GetValue<int>() ?? WorkflowDefinition.DefaultOutputRetries;
        var structuredPrompt = $"{prompt}\n\n{JsonSchemaLite.DescribeForPrompt(schema)}";
        var errors = new List<string>();
        Guid? lastRun = null;
        for (var attempt = 1; attempt <= retries + 1; attempt++)
        {
            var attemptPrompt = errors.Count == 0 ? structuredPrompt : $"{structuredPrompt}\n\n{RetryFeedback}\n- {string.Join("\n- ", errors)}";
            var r = await _runner.RunAsync(agent, attemptPrompt, workflowRunId: wfRunId, hooks: hooks, ct: ct);
            lastRun = r.RunId;
            if (r.Status != WorkflowRunStatus.Completed) return new NodeResult(node.Id, false, r.Output, r.RunId);
            var json = JsonSchemaLite.ExtractJson(r.Output);
            errors = json == null ? new List<string> { "$: a resposta nao contem um JSON valido" } : JsonSchemaLite.Validate(schema, json);
            if (errors.Count == 0) return new NodeResult(node.Id, true, json!.ToJsonString(), r.RunId);
            Log.Warn($"no '{node.Id}': saida fora do output_schema (tentativa {attempt}/{retries + 1}): {string.Join("; ", errors)}");
        }
        return new NodeResult(node.Id, false, $"ERRO: saida fora do output_schema apos {retries + 1} tentativas: {string.Join("; ", errors)}", lastRun);
    }

    private async Task<NodeResult> RunToolNodeAsync(Guid wfRunId, WorkflowNode node, WorkflowValues values, IHumanGate gate,
                                                    WorkflowToolRules? rules, CancellationToken ct)
    {
        var args = values.RenderJson(node.Raw["args"]) as JsonObject ?? new JsonObject();
        var r = await _runner.RunToolAsync(node.Text("tool"), args, wfRunId, gate, rules, ct);
        return new NodeResult(node.Id, r.Ok, r.Output, r.RunId);
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
}
