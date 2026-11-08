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
/// Cada execucao tem dono e lease (heartbeat) e guarda a copia da definicao com que comecou: se a instancia cai, o
/// despachante de outra a recupera (<see cref="IWorkflowRecovery"/>) e continua na mesma versao, retomando o run
/// do agente interrompido pelo historico.
/// </summary>
public sealed class WorkflowEngine : IWorkflowRecovery
{
    private const string RetryFeedback = "Sua resposta anterior nao seguiu o formato pedido. Corrija estes erros:";

    /// <summary>Status devolvido (nao gravado) quando outra instancia assumiu a execucao.</summary>
    public const string LeaseLostStatus = "lease_lost";

    private readonly Db _db;
    private readonly AgentRunner _runner;
    private readonly RunLeaseOptions _lease;

    public WorkflowEngine(Db db, AgentRunner runner, RunLeaseOptions lease)
    {
        _db = db;
        _runner = runner;
        _lease = lease;
    }

    private string Owner => _runner.InstanceId;

    private sealed record NodeResult(string Node, bool Ok, string Output, Guid? RunId = null,
                                     IReadOnlyList<string>? ChosenNext = null, string? EndStatus = null);

    private sealed record AgentAttempt(Guid RunId, string Status, string Output);

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
            var run = await ClaimForResumeAsync(rid);
            workflowId = run.S("workflow_id");
            wfInput = run.S("input");
            wfRunId = rid;
            rawDefinition = await PinnedDefinitionAsync(rid, workflowId, run.SN("definition"));
            definition = WorkflowDefinition.Parse(rawDefinition, workflowId);
            approval = await ResumedApprovalAsync(rid, run, approval);
            checkpoint = await _db.QueryAsync(
                "select node_id, status, output, chosen_next from workflow_node_runs where workflow_run_id = @id and status in ('completed', 'skipped')",
                ("id", rid));
            Log.Info($"Retomando workflow '{workflowId}' run {Text.Short(rid)} (status anterior: {run.S("previous_status")}).");
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
                @"insert into workflow_runs (workflow_id, workflow_version, input, approval_mode, approval_mode_explicit, definition,
                                             owner, lease_until)
                  values (@w, @v, @i, @mode, @explicit, @def, @owner, now() + make_interval(secs => @lease)) returning id",
                ("w", workflowId), ("v", Convert.ToInt32(version)), ("i", wfInput), ("mode", approval.Mode), ("explicit", approval.Explicit),
                ("def", rawDefinition), ("owner", Owner), ("lease", _lease.Lease.TotalSeconds)))!;
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

        await using var lease = RunLeaseKeeper.ForWorkflow(_db, wfRunId, Owner, _lease, ct);
        var token = lease.Token;
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

                var results = await Task.WhenAll(step.Ready.Select(n => RunNodeAsync(wfRunId, definition.Nodes[n], values, gate, rules, token)));
                if (lease.Lost) return LeaseLost(wfRunId, workflowId, reused);
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
                    await _db.ExecAsync("update workflow_runs set status = 'crashed', owner = null, lease_until = null where id = @id and owner = @owner",
                        ("id", wfRunId), ("owner", Owner));
                    throw new SimulatedCrashException(wfRunId, crashAfter);
                }
            }
        }
        catch (SimulatedCrashException)
        {
            throw;
        }
        catch (Exception ex) when (lease.Lost || ex is LeaseLostException)
        {
            return LeaseLost(wfRunId, workflowId, reused);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await _db.ExecAsync(
                "update workflow_runs set status = 'failed', output = @o, owner = null, lease_until = null, finished_at = now() where id = @id and owner = @owner",
                ("o", ex.Message), ("id", wfRunId), ("owner", Owner));
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
        var recorded = await _db.ExecAsync(
            "update workflow_runs set status = @s, output = @o, owner = null, lease_until = null, finished_at = now() where id = @id and owner = @owner",
            ("s", status), ("o", output), ("id", wfRunId), ("owner", Owner));
        if (recorded == 0) return LeaseLost(wfRunId, workflowId, reused);
        if (status == WorkflowRunStatus.Completed) Log.Ok($"Workflow '{workflowId}' concluido (run {Text.Short(wfRunId)}).");
        else Log.Error($"Workflow '{workflowId}' terminou como {status} (run {Text.Short(wfRunId)}): {Text.Trunc(output, 300)}");
        return new WorkflowResult(wfRunId, status, output, reused);
    }

    private static WorkflowResult LeaseLost(Guid wfRunId, string workflowId, List<string> reused)
    {
        Log.Warn($"Workflow '{workflowId}' run {Text.Short(wfRunId)}: posse perdida para outra instancia; nada mais gravado.");
        return new WorkflowResult(wfRunId, LeaseLostStatus, "Outra instancia assumiu a execucao.", reused);
    }

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
                  returning workflow_id, input, approval_mode, approval_mode_explicit, definition::text as definition, @previous as previous_status",
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
    /// anterior) e o despachante e acordado. SKIP LOCKED: varias instancias podem varrer juntas.
    /// </summary>
    public async Task<int> RecoverOrphansAsync(string recoveredBy)
    {
        var orphans = await _db.QueryAsync(
            @"with orphans as (
                  select id, owner, lease_until from workflow_runs
                   where status = 'running' and lease_until < now()
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
        return orphans.Count;
    }

    /// <summary>Pega uma execucao ready (FOR UPDATE SKIP LOCKED), assume o lease e continua. False se nao havia nenhuma.</summary>
    public async Task<bool> ResumeNextAsync(CancellationToken ct)
    {
        if (await _db.ScalarAsync(
                @"update workflow_runs set status = 'running', owner = @owner, lease_until = now() + make_interval(secs => @lease), finished_at = null
                   where id = (select id from workflow_runs where status = 'ready' order by started_at limit 1 for update skip locked)
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

    private async Task<NodeResult> RunNodeAsync(Guid wfRunId, WorkflowNode node, WorkflowValues values, IHumanGate gate,
                                                WorkflowToolRules? rules, CancellationToken ct)
    {
        var start = (await _db.QueryOneAsync(
            @"with previous as (select status from workflow_node_runs where workflow_run_id = @w and node_id = @n)
              insert into workflow_node_runs (workflow_run_id, node_id, status) values (@w, @n, 'running')
              on conflict (workflow_run_id, node_id) do update
                 set status = 'running',
                     attempt = case when workflow_node_runs.status = 'running' then workflow_node_runs.attempt else workflow_node_runs.attempt + 1 end,
                     started_at = case when workflow_node_runs.status = 'running' then workflow_node_runs.started_at else now() end,
                     finished_at = null
              returning attempt, started_at, (select status from previous) = 'running' as interrupted",
            ("w", wfRunId), ("n", node.Id)))!;
        var attempt = start.I("attempt");
        var interrupted = start.B("interrupted");
        if (interrupted) Log.Write(0, ConsoleColor.Yellow, "workflow", $"no '{node.Id}' foi interrompido por uma queda; retomando a tentativa {attempt}");

        NodeResult result;
        try
        {
            result = node.Type switch
            {
                WorkflowNodeType.Agent => await RunAgentNodeAsync(wfRunId, node, values, new RunHooks(null, gate, rules),
                    interrupted ? start.T("started_at") : null, ct),
                WorkflowNodeType.Tool => await RunToolNodeAsync(wfRunId, node, values, gate, rules, attempt, ct),
                WorkflowNodeType.Condition => RunCondition(node, values),
                WorkflowNodeType.Transform => new NodeResult(node.Id, true, AsOutputText(values.RenderJson(node.Raw["output"]))),
                _ => new NodeResult(node.Id, true, AsOutputText(values.RenderJson(node.Raw["output"])), EndStatus: node.Text("status"))
            };
        }
        catch (WorkflowValueException ex)
        {
            result = new NodeResult(node.Id, false, $"ERRO: {ex.Message}");
        }

        var saved = await _db.ExecAsync(
            @"update workflow_node_runs set status = @s, output = @o, agent_run_id = @r, chosen_next = @chosen, finished_at = now()
               where workflow_run_id = @w and node_id = @n
                 and exists (select 1 from workflow_runs where id = @w and owner = @owner)",
            ("s", result.Ok ? WorkflowRunStatus.Completed : WorkflowRunStatus.Failed), ("o", result.Output), ("r", result.RunId),
            ("chosen", result.ChosenNext?.ToArray()), ("w", wfRunId), ("n", node.Id), ("owner", Owner));
        if (saved == 0) throw new LeaseLostException(wfRunId);
        Log.Write(0, result.Ok ? ConsoleColor.Green : ConsoleColor.Red, "workflow",
            $"no '{node.Id}' {(result.Ok ? "concluido" : "falhou")} (checkpoint gravado)" +
            (result.ChosenNext != null ? $" -> {(result.ChosenNext.Count == 0 ? "nenhum destino" : string.Join(", ", result.ChosenNext))}" : ""));
        return result;
    }

    /// <summary>
    /// Com output_schema, pede o JSON no prompt, valida a resposta e repete com os erros ate output_retries vezes;
    /// a saida gravada e o JSON validado. Um no interrompido por queda continua a tentativa em que parou (o run do
    /// agente e retomado do historico) e conta as tentativas ja feitas.
    /// </summary>
    /// <param name="interruptedSince">Inicio da tentativa do no interrompida por queda; nulo numa execucao normal.</param>
    private async Task<NodeResult> RunAgentNodeAsync(Guid wfRunId, WorkflowNode node, WorkflowValues values, RunHooks hooks,
                                                     DateTime? interruptedSince, CancellationToken ct)
    {
        var prompt = values.RenderText(node.Text("input"));
        var agent = node.Text("agent");
        var schema = node.Raw["output_schema"];
        var maxAttempts = schema == null ? 1 : (node.Raw["output_retries"]?.GetValue<int>() ?? WorkflowDefinition.DefaultOutputRetries) + 1;
        var (current, attemptsDone) = interruptedSince is { } since ? await ResumeInterruptedAgentAsync(wfRunId, node.Id, since, hooks, ct) : (null, 0);
        var structuredPrompt = schema == null ? prompt : $"{prompt}\n\n{JsonSchemaLite.DescribeForPrompt(schema)}";
        var errors = new List<string>();
        Guid? lastRun = null;
        for (var attempt = Math.Max(1, attemptsDone); attempt <= maxAttempts; attempt++)
        {
            var attemptPrompt = errors.Count == 0 ? structuredPrompt : $"{structuredPrompt}\n\n{RetryFeedback}\n- {string.Join("\n- ", errors)}";
            var r = current ?? await StartAgentAsync(agent, attemptPrompt, wfRunId, node.Id, hooks, ct);
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

    private async Task<AgentAttempt> StartAgentAsync(string agent, string prompt, Guid wfRunId, string nodeId, RunHooks hooks, CancellationToken ct)
    {
        var r = await _runner.RunAsync(agent, prompt, workflowRunId: wfRunId, hooks: hooks, workflowNodeId: nodeId, ct: ct);
        return new AgentAttempt(r.RunId, r.Status, r.Output);
    }

    /// <summary>
    /// O run do agente que o no interrompido tinha iniciado, segundo <see cref="NodeResumeDecision"/>: resultado ja
    /// gravado, run assumido e retomado do historico, ou nenhum (a tentativa recomeca). Devolve tambem quantas
    /// tentativas a tentativa do no ja tinha feito.
    /// </summary>
    private async Task<(AgentAttempt? Current, int AttemptsDone)> ResumeInterruptedAgentAsync(Guid wfRunId, string nodeId, DateTime since,
                                                                                              RunHooks hooks, CancellationToken ct)
    {
        while (true)
        {
            var last = await _db.QueryOneAsync(
                @"select id, status, output, lease_until, now() as db_now,
                         count(*) over () as attempts
                    from runs where workflow_run_id = @w and workflow_node_id = @n and parent_run_id is null and started_at >= @since
                   order by started_at desc limit 1",
                ("w", wfRunId), ("n", nodeId), ("since", since));
            var now = last?.T("db_now") ?? DateTime.UtcNow;
            switch (NodeResumeDecision.For(last?.SN("status"), last?.TN("lease_until"), now))
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
                    var attempts = orphan.I("attempts");
                    var claimed = await _db.ExecAsync(
                        @"update runs set status = 'running', owner = @owner, lease_until = now() + make_interval(secs => @lease)
                           where id = @id and (status = 'ready' or (status = 'running' and lease_until < now()))",
                        ("owner", Owner), ("lease", _lease.Lease.TotalSeconds), ("id", runId));
                    if (claimed == 0) continue;
                    Log.Write(0, ConsoleColor.Yellow, "workflow", $"no '{nodeId}': assumindo o run interrompido {Text.Short(runId)} e retomando do historico");
                    var r = await _runner.ResumeAsync(runId, null, hooks, Owner, ct);
                    return (new AgentAttempt(r.RunId, r.Status, r.Output), attempts);
                default:
                    await Task.Delay(_lease.Heartbeat, ct);
                    break;
            }
        }
    }

    private async Task<NodeResult> RunToolNodeAsync(Guid wfRunId, WorkflowNode node, WorkflowValues values, IHumanGate gate,
                                                    WorkflowToolRules? rules, int attempt, CancellationToken ct)
    {
        var args = values.RenderJson(node.Raw["args"]) as JsonObject ?? new JsonObject();
        var r = await _runner.RunToolAsync(node.Text("tool"), args, wfRunId, node.Id, attempt, gate, rules, ct);
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
