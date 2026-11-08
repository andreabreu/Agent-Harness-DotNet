using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AgentPoc.Core;
using AgentPoc.Harness;
using AgentPoc.Tools;

namespace AgentPoc.Workflows;

public sealed class SimulatedCrashException : Exception
{
    public Guid WorkflowRunId { get; }
    public SimulatedCrashException(Guid id, string node) : base($"Queda simulada apos o no '{node}' (workflow run {id}).") => WorkflowRunId = id;
}

public sealed record WorkflowResult(Guid WorkflowRunId, string Status, string Output, List<string> ReusedNodes);

/// <summary>
/// Motor de workflow deterministico definido em JSON no banco.
/// Nos 'agent' ou 'tool'; arestas em "next"; nos prontos rodam em paralelo;
/// checkpoint por no em workflow_node_runs permite retomada apos queda.
/// </summary>
public sealed class WorkflowEngine
{
    private static readonly Regex Var = new(@"\{\{\s*([A-Za-z0-9_]+)\s*\}\}", RegexOptions.Compiled);
    private readonly Db _db;
    private readonly AgentRunner _runner;

    public WorkflowEngine(Db db, AgentRunner runner)
    {
        _db = db;
        _runner = runner;
    }

    /// <param name="approval">Modo de aprovacao da execucao; nulo nega (padrao) ou, na retomada, mantem o gravado.</param>
    public async Task<WorkflowResult> RunAsync(string workflowId, string? input, Guid? resumeRunId = null, string? crashAfter = null,
                                               WorkflowApprovalMode? approval = null, CancellationToken ct = default)
    {
        Guid wfRunId;
        string wfInput;
        JsonObject definition;
        var outputs = new Dictionary<string, string>(StringComparer.Ordinal);
        var reused = new List<string>();

        if (resumeRunId is Guid rid)
        {
            var run = await _db.QueryOneAsync(
                          "select workflow_id, input, status, approval_mode, approval_mode_explicit from workflow_runs where id = @id", ("id", rid))
                      ?? throw new InvalidOperationException($"workflow run {rid} nao encontrado.");
            workflowId = run.S("workflow_id");
            wfInput = run.S("input");
            wfRunId = rid;
            approval = await ResumedApprovalAsync(rid, run, approval);
            definition = await LoadDefinitionAsync(workflowId);
            foreach (var n in await _db.QueryAsync("select node_id, output from workflow_node_runs where workflow_run_id = @id and status = 'completed'", ("id", rid)))
            {
                outputs[n.S("node_id")] = n.S("output");
                reused.Add(n.S("node_id"));
            }
            await _db.ExecAsync("update workflow_runs set status = 'running', finished_at = null where id = @id", ("id", rid));
            Log.Info($"Retomando workflow '{workflowId}' run {Text.Short(rid)} (status anterior: {run.S("status")}). Nos reaproveitados do checkpoint: {(reused.Count == 0 ? "nenhum" : string.Join(", ", reused))}");
        }
        else
        {
            wfInput = input ?? "";
            approval ??= WorkflowApprovalMode.Default;
            var version = await _db.ScalarAsync("select version from workflows where id = @id and enabled", ("id", workflowId))
                          ?? throw new InvalidOperationException($"Workflow '{workflowId}' nao existe ou esta desabilitado.");
            definition = await LoadDefinitionAsync(workflowId);
            await EnsureToolRulesMatchRegisteredToolsAsync(workflowId, WorkflowToolRules.Parse(definition));
            wfRunId = (Guid)(await _db.ScalarAsync(
                @"insert into workflow_runs (workflow_id, workflow_version, input, approval_mode, approval_mode_explicit)
                  values (@w, @v, @i, @mode, @explicit) returning id",
                ("w", workflowId), ("v", Convert.ToInt32(version)), ("i", wfInput), ("mode", approval.Mode), ("explicit", approval.Explicit)))!;
            Log.Info($"Workflow '{workflowId}' v{version} iniciado: run {Text.Short(wfRunId)}");
        }
        Log.Info($"aprovacao: {approval.Describe()}");
        var gate = approval.Gate();
        var rules = WorkflowToolRules.Parse(definition);

        var nodes = definition["nodes"] as JsonObject
                    ?? throw new InvalidOperationException("Definicao de workflow invalida: falta 'nodes'.");

        var preds = nodes.ToDictionary(kv => kv.Key, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var (id, node) in nodes)
            if (node?["next"] is JsonArray next)
                foreach (var n in next)
                {
                    var target = HttpTool.ArgString(n);
                    if (!preds.ContainsKey(target)) throw new InvalidOperationException($"No '{id}' aponta para '{target}', que nao existe.");
                    preds[target].Add(id);
                }

        var done = new HashSet<string>(outputs.Keys, StringComparer.Ordinal);
        var pending = nodes.Select(kv => kv.Key).Where(k => !done.Contains(k)).ToList();

        try
        {
            while (pending.Count > 0)
            {
                var ready = pending.Where(n => preds[n].All(done.Contains)).ToList();
                if (ready.Count == 0) throw new InvalidOperationException("Workflow sem progresso possivel (ciclo nas arestas?).");
                Log.Write(0, ConsoleColor.Blue, "workflow", ready.Count > 1 ? $"executando em paralelo: {string.Join(" || ", ready)}" : $"executando no: {ready[0]}");

                var results = await Task.WhenAll(ready.Select(n => RunNodeAsync(wfRunId, n, (JsonObject)nodes[n]!, wfInput, outputs, gate, rules, ct)));
                var failed = results.Where(x => !x.Ok).ToList();
                foreach (var res in results.Where(x => x.Ok))
                {
                    outputs[res.Node] = res.Output;
                    done.Add(res.Node);
                    pending.Remove(res.Node);
                }
                if (failed.Count > 0)
                {
                    var msg = string.Join("; ", failed.Select(f => $"{f.Node}: {Text.Trunc(f.Output, 300)}"));
                    await _db.ExecAsync("update workflow_runs set status = 'failed', output = @o, finished_at = now() where id = @id", ("o", msg), ("id", wfRunId));
                    Log.Error($"Workflow falhou: {msg}");
                    return new WorkflowResult(wfRunId, "failed", msg, reused);
                }

                if (crashAfter != null && ready.Contains(crashAfter))
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

        var terminals = nodes.Where(kv => kv.Value?["next"] is not JsonArray a || a.Count == 0).Select(kv => kv.Key).ToList();
        var final = string.Join("\n\n", terminals.Select(t => outputs.TryGetValue(t, out var o) ? o : ""));
        await _db.ExecAsync("update workflow_runs set status = 'completed', output = @o, finished_at = now() where id = @id", ("o", final), ("id", wfRunId));
        Log.Ok($"Workflow '{workflowId}' concluido (run {Text.Short(wfRunId)}).");
        return new WorkflowResult(wfRunId, "completed", final, reused);
    }

    private sealed record NodeResult(string Node, bool Ok, string Output);

    private static string Render(string template, string input, IReadOnlyDictionary<string, string> outputs) =>
        Var.Replace(template, m =>
        {
            var key = m.Groups[1].Value;
            if (key == "input") return input;
            return outputs.TryGetValue(key, out var v) ? v : "";
        });

    private static JsonNode? RenderNode(JsonNode? node, string input, IReadOnlyDictionary<string, string> outputs) => node switch
    {
        JsonObject o => new JsonObject(o.Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, RenderNode(kv.Value, input, outputs)))),
        JsonArray a => new JsonArray(a.Select(x => RenderNode(x, input, outputs)).ToArray()),
        JsonValue v when v.TryGetValue<string>(out var s) => JsonValue.Create(Render(s, input, outputs)),
        _ => node?.DeepClone()
    };

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

    /// <summary>Retomada mantem o modo gravado; um modo explicito novo substitui e fica registrado.</summary>
    private async Task<WorkflowApprovalMode> ResumedApprovalAsync(Guid wfRunId, Dictionary<string, object?> run, WorkflowApprovalMode? requested)
    {
        var stored = run.SN("approval_mode") is { } mode ? new WorkflowApprovalMode(mode, run.B("approval_mode_explicit")) : WorkflowApprovalMode.Default;
        if (requested is not { Explicit: true } || requested == stored) return stored;
        await _db.ExecAsync("update workflow_runs set approval_mode = @mode, approval_mode_explicit = true where id = @id",
            ("mode", requested.Mode), ("id", wfRunId));
        Log.Warn($"Retomada troca o modo de aprovacao de {stored.Describe()} para {requested.Describe()}.");
        return requested;
    }

    private async Task<NodeResult> RunNodeAsync(Guid wfRunId, string nodeId, JsonObject node, string input, IReadOnlyDictionary<string, string> outputs,
                                                IHumanGate gate, WorkflowToolRules? rules, CancellationToken ct)
    {
        await _db.ExecAsync(
            @"insert into workflow_node_runs (workflow_run_id, node_id, status) values (@w, @n, 'running')
              on conflict (workflow_run_id, node_id) do update
                 set status = 'running', attempt = workflow_node_runs.attempt + 1, started_at = now(), finished_at = null",
            ("w", wfRunId), ("n", nodeId));

        var type = HttpTool.ArgString(node["type"]);
        bool ok;
        string output;
        Guid runId;
        if (type == "agent")
        {
            var prompt = Render(HttpTool.ArgString(node["input"]), input, outputs);
            var r = await _runner.RunAsync(HttpTool.ArgString(node["agent"]), prompt, workflowRunId: wfRunId, hooks: new RunHooks(null, gate, rules), ct: ct);
            ok = r.Status == "completed";
            output = r.Output;
            runId = r.RunId;
        }
        else if (type == "tool")
        {
            var args = RenderNode(node["args"], input, outputs) as JsonObject ?? new JsonObject();
            var r = await _runner.RunToolAsync(HttpTool.ArgString(node["tool"]), args, wfRunId, gate, rules, ct);
            ok = r.Ok;
            output = r.Output;
            runId = r.RunId;
        }
        else
        {
            return new NodeResult(nodeId, false, $"tipo de no desconhecido: '{type}'");
        }

        await _db.ExecAsync(
            "update workflow_node_runs set status = @s, output = @o, agent_run_id = @r, finished_at = now() where workflow_run_id = @w and node_id = @n",
            ("s", ok ? "completed" : "failed"), ("o", output), ("r", runId), ("w", wfRunId), ("n", nodeId));
        Log.Write(0, ok ? ConsoleColor.Green : ConsoleColor.Red, "workflow", $"no '{nodeId}' {(ok ? "concluido" : "falhou")} (checkpoint gravado)");
        return new NodeResult(nodeId, ok, output);
    }
}
