using System.Text;
using System.Text.Json.Nodes;
using AgentPoc.Core;
using AgentPoc.Llm;
using AgentPoc.Tools;

namespace AgentPoc.Harness;

/// <summary>Resultado de um no de ferramenta: se a chamada chegou a comecar e se a ferramenta tem efeito colateral.</summary>
public sealed record ToolNodeRun(bool Ok, string Output, Guid RunId, bool EffectStarted, bool SideEffect);

/// <summary>
/// O harness: carrega a configuracao do agente do banco e roda o loop
/// modelo -> tool calls -> resultados -> modelo, ate concluir ou bater um limite.
/// </summary>
public sealed class AgentRunner
{
    public const int MaxDepth = 3;

    private readonly Db _db;
    private readonly ConfigRepo _config;
    private readonly ToolRegistry _registry;
    private readonly ToolExecutor _executor;
    private readonly EventLog _events;
    private readonly RunMessageStore _messages;
    private readonly IRunLifecycle _lifecycle;
    private readonly RunChain _chain;
    private readonly RunLeaseOptions _lease;

    public AgentRunner(Db db, ConfigRepo config, ToolRegistry registry, ToolExecutor executor, EventLog events,
                       RunMessageStore messages, RunChain chain, RunLeaseOptions lease, string instanceId)
    {
        _db = db;
        _config = config;
        _registry = registry;
        _executor = executor;
        _events = events;
        _messages = messages;
        _lifecycle = chain;
        _chain = chain;
        _lease = lease;
        InstanceId = instanceId;
    }

    /// <summary>Esta instancia: dona dos runs que ela executa (lease renovado por heartbeat).</summary>
    public string InstanceId { get; }

    private static JsonArray ToArray(IEnumerable<string> items)
    {
        var arr = new JsonArray();
        foreach (var i in items) arr.Add(i);
        return arr;
    }

    /// <param name="chat">Continuacao de conversa: historico, ferramentas ja descobertas e persistencia de cada mensagem nova.</param>
    /// <param name="hooks">Observer ao vivo e quem responde pelo humano; quando nulos, herda do pai ou usa o padrao da CLI.</param>
    /// <param name="runId">Id do run definido por quem chama (para registrar a execucao antes de comecar).</param>
    /// <param name="parentToolCallId">Chamada delegate_task do pai que cria este run (torna a delegacao idempotente).</param>
    /// <param name="workflowNodeId">No do workflow que cria este run (a retomada do workflow acha o run interrompido por ele).</param>
    public async Task<RunResult> RunAsync(string agentId, string input, RunContext? parent = null, Guid? workflowRunId = null,
                                          long? budgetOverride = null, ChatTurn? chat = null, RunHooks? hooks = null,
                                          Guid? runId = null, string? parentToolCallId = null, string? workflowNodeId = null,
                                          CancellationToken ct = default)
    {
        var agent = await _config.GetAgentAsync(agentId)
                    ?? throw new InvalidOperationException($"Agente '{agentId}' nao existe ou esta desabilitado.");
        var model = await _config.GetModelAsync(agent.ModelId);
        var depth = parent == null ? 0 : parent.Depth + 1;
        var limit = budgetOverride ?? agent.MaxTokens;
        var budget = parent == null ? new Budget(limit) : parent.Budget.Child(limit);
        var id = runId ?? Guid.NewGuid();
        var rootId = parent?.RootRunId ?? id;
        var wfRun = workflowRunId ?? parent?.WorkflowRunId;
        var conversationId = chat?.ConversationId ?? parent?.ConversationId;

        await _db.ExecAsync(
            @"insert into runs (id, parent_run_id, root_run_id, workflow_run_id, conversation_id, agent_id, agent_version, model_id, input, depth,
                                budget_limit, parent_tool_call_id, workflow_node_id, owner, lease_until)
              values (@id, @p, @root, @wf, @conv, @a, @v, @m, @i, @d, @b, @call, @node, @owner, now() + make_interval(secs => @lease))",
            ("id", id), ("p", parent?.RunId), ("root", rootId), ("wf", wfRun), ("conv", conversationId), ("a", agent.Id),
            ("v", agent.Version), ("m", model.Id), ("i", input), ("d", depth), ("b", budget.Limit), ("call", parentToolCallId),
            ("node", workflowNodeId), ("owner", InstanceId), ("lease", _lease.Lease.TotalSeconds));

        var ctx = new RunContext
        {
            RunId = id,
            ParentRunId = parent?.RunId,
            RootRunId = rootId,
            WorkflowRunId = wfRun,
            ConversationId = conversationId,
            Owner = InstanceId,
            Agent = agent,
            Model = model,
            Depth = depth,
            Budget = budget,
            Observer = hooks?.Observer ?? parent?.Observer,
            Human = hooks?.Human ?? parent?.Human ?? ModeHumanGate.Instance,
            ToolRules = hooks?.ToolRules ?? parent?.ToolRules
        };
        foreach (var t in (await _registry.LoadAsync(ToolsToLoad(agent, chat))).Values) ctx.ActiveTools[t.Name] = t;
        await SaveActiveToolsAsync(ctx);

        await _events.AddAsync(ctx, "run_started", new JsonObject
        {
            ["agent"] = agent.Id,
            ["agent_version"] = agent.Version,
            ["model"] = model.Id,
            ["model_name"] = model.ResolvedName,
            ["provider"] = model.Provider.Id,
            ["depth"] = depth,
            ["parent_run_id"] = parent?.RunId.ToString(),
            ["budget_tokens"] = budget.Remaining,
            ["max_steps"] = agent.MaxSteps,
            ["initial_tools"] = ToArray(ctx.ActiveTools.Keys.OrderBy(k => k)),
            ["input"] = Text.Trunc(input, 3000)
        });
        Log.Write(depth, ConsoleColor.Magenta, "agente", $"{agent.Id} v{agent.Version} iniciou run {Text.Short(id)} | modelo {model.Id} ({model.ResolvedName}) | budget {budget.Remaining:N0} tokens");
        Log.Write(depth, ConsoleColor.Gray, "entrada", input);

        var loop = new LoopState(ctx, chat, chat?.History ?? Array.Empty<ChatMessage>(), _messages)
        {
            StillOwner = () => _chain.IsOwnerAsync(id, InstanceId)
        };
        await loop.AppendAsync(ChatMessage.User(input));
        return await RunLoopAsync(loop, Array.Empty<ToolCall>(), ct);
    }

    /// <summary>
    /// Retoma um run que o despachante ja reivindicou (running, com lease desta instancia): mesmo run, mesmos
    /// contadores e budget.
    /// O historico vem da conversa (agente raiz do chat) ou de run_messages (subagentes e demais runs). Primeiro
    /// conclui as tool calls que ficaram sem resultado (a decisao humana ja esta gravada) e segue o loop normal.
    /// </summary>
    public async Task<RunResult> ResumeAsync(Guid runId, ChatTurn? chat, RunHooks hooks, string resumedBy, CancellationToken ct)
    {
        var run = await _db.QueryOneAsync("select * from runs where id = @id", ("id", runId))
                  ?? throw new InvalidOperationException($"Run {runId} nao encontrado.");
        if (run.S("status") != RunStatus.Running || run.SN("owner") != InstanceId)
            throw new InvalidOperationException($"Run {Text.Short(runId)} precisa ser reivindicado por esta instancia antes de retomar (status {run.S("status")}).");
        var agent = await _config.GetAgentAsync(run.S("agent_id"))
                    ?? throw new InvalidOperationException($"Agente '{run.S("agent_id")}' nao existe ou esta desabilitado.");
        var model = await _config.GetModelAsync(agent.ModelId);
        var lastSeq = Convert.ToInt32(await _db.ScalarAsync("select coalesce(max(seq), 0) from run_events where run_id = @id", ("id", runId)));

        var budget = new Budget(run.SN("budget_limit") is { } limit ? long.Parse(limit) : agent.MaxTokens);
        budget.Consume(await SubtreeTokensAsync(runId));
        var depth = run.I("depth");
        var ctx = new RunContext
        {
            RunId = runId,
            ParentRunId = run.GN("parent_run_id"),
            RootRunId = run.G("root_run_id"),
            WorkflowRunId = run.GN("workflow_run_id"),
            ConversationId = run.GN("conversation_id"),
            Owner = InstanceId,
            Agent = agent,
            Model = model,
            Depth = depth,
            Budget = budget,
            Observer = hooks.Observer,
            Human = hooks.Human ?? ModeHumanGate.Instance,
            ToolRules = hooks.ToolRules,
            SeqStart = lastSeq
        };
        var activeTools = run.A("active_tools");
        foreach (var t in (await _registry.LoadAsync(agent.InitialTools.Concat(activeTools.Where(t => Glob.Match(agent.AllowedTools, t))))).Values)
            ctx.ActiveTools[t.Name] = t;

        await _events.AddAsync(ctx, "run_resumed", new JsonObject { ["after_steps"] = run.I("steps"), ["by"] = resumedBy });
        Log.Write(depth, ConsoleColor.Magenta, "agente", $"{agent.Id} retomou run {Text.Short(runId)} ({resumedBy})");

        var history = chat?.History ?? await _messages.LoadAsync(runId);
        var loop = new LoopState(ctx, chat, history, _messages)
        {
            StillOwner = () => _chain.IsOwnerAsync(runId, InstanceId),
            Steps = run.I("steps"),
            TokensIn = run.L("tokens_in"),
            TokensOut = run.L("tokens_out"),
            Cost = run.D("cost_usd")
        };
        return await RunLoopAsync(loop, UnansweredToolCalls(loop.Messages).ToList(), ct);
    }

    /// <summary>Tokens ja gastos pelo run e por todos os subagentes dele (o budget do pai inclui os filhos).</summary>
    private async Task<long> SubtreeTokensAsync(Guid runId) =>
        Convert.ToInt64(await _db.ScalarAsync(
            @"with recursive tree as (
                  select id from runs where id = @id
                  union all
                  select r.id from runs r join tree t on r.parent_run_id = t.id)
              select coalesce(sum(tokens_in + tokens_out), 0) from runs where id in (select id from tree)", ("id", runId)));

    /// <summary>Estado mutavel de um run em execucao (mensagens e contadores). Cada mensagem nova e persistida.</summary>
    private sealed class LoopState
    {
        private readonly ChatTurn? _chat;
        private readonly RunMessageStore _store;

        public LoopState(RunContext ctx, ChatTurn? chat, IEnumerable<ChatMessage> history, RunMessageStore store)
        {
            Ctx = ctx;
            _chat = chat;
            _store = store;
            Messages = new List<ChatMessage>(history);
        }

        public RunContext Ctx { get; }
        public ChatTurn? Chat => _chat;
        public List<ChatMessage> Messages { get; }
        public int Steps { get; set; }
        public long TokensIn { get; set; }
        public long TokensOut { get; set; }
        public decimal Cost { get; set; }

        /// <summary>Quem confere a posse antes de gravar (uma instancia que perdeu o lease nao escreve mais no historico).</summary>
        public Func<Task<bool>>? StillOwner { get; init; }

        public async Task AppendAsync(ChatMessage message)
        {
            if (StillOwner != null && !await StillOwner()) throw new LeaseLostException(Ctx.RunId);
            Messages.Add(message);
            if (_chat != null) await _chat.OnMessage(Ctx.RunId, message);
            else await _store.AppendAsync(Ctx.RunId, message);
        }
    }

    /// <summary>Status devolvido (nao gravado) quando esta instancia perdeu a posse do run para outra.</summary>
    public const string LeaseLostStatus = "lease_lost";

    private async Task<RunResult> RunLoopAsync(LoopState loop, IReadOnlyList<ToolCall> pendingCalls, CancellationToken ct)
    {
        var ctx = loop.Ctx;
        var agent = ctx.Agent;
        var model = ctx.Model;
        var depth = ctx.Depth;
        var status = RunStatus.Running;
        var output = "";
        RunSuspendedException? suspension = null;
        var leaseLost = false;
        var client = LlmFactory.For(model);
        var stream = StreamFor(ctx);
        await using var lease = RunLeaseKeeper.ForRun(_db, ctx.RunId, InstanceId, _lease, ct);
        var token = lease.Token;

        try
        {
            foreach (var call in pendingCalls)
                await loop.AppendAsync(ChatMessage.Tool(call.Id, call.Name, await _executor.ExecutePendingAsync(ctx, call, token)));

            if (pendingCalls.Count == 0 && FinalAnswer(loop.Messages) is { } answered)
            {
                status = RunStatus.Completed;
                output = answered;
                Log.Write(depth, ConsoleColor.DarkGray, "retomada", $"{agent.Id} ja tinha a resposta final gravada; concluindo sem chamar o modelo");
            }
            else while (true)
            {
                token.ThrowIfCancellationRequested();
                if (loop.Steps >= agent.MaxSteps)
                {
                    status = "max_steps";
                    output = $"Limite de {agent.MaxSteps} passos atingido antes de concluir.";
                    break;
                }
                if (ctx.Budget.Exhausted)
                {
                    status = "budget_exceeded";
                    output = $"Budget de tokens esgotado: usados {ctx.Budget.Used:N0} de {ctx.Budget.Limit:N0} (o limite e verificado entre chamadas ao modelo).";
                    await _events.AddAsync(ctx, "budget_exceeded", new JsonObject { ["limit"] = ctx.Budget.Limit, ["used"] = ctx.Budget.Used, ["remaining"] = ctx.Budget.Remaining });
                    Log.Write(depth, ConsoleColor.Red, "budget", output);
                    break;
                }

                loop.Steps++;
                Compact(loop.Messages, model);
                var specs = ctx.ActiveTools.Values
                    .OrderBy(t => t.Name)
                    .Select(t => new ToolSpec(t.Name, t.Description, JsonNode.Parse(t.ParametersJson) ?? new JsonObject()))
                    .ToList();

                var resp = await client.CompleteAsync(model, new LlmRequest(BuildSystem(agent, ctx, loop.Chat), loop.Messages, specs, agent.Temperature, stream), token);
                if (ctx.Observer != null && !model.SupportsStreaming && !string.IsNullOrEmpty(resp.Content))
                    ctx.Observer.OnTextDelta(ctx, resp.Content);

                loop.TokensIn += resp.InputTokens;
                loop.TokensOut += resp.OutputTokens;
                var stepCost = (resp.InputTokens * model.CostInPerMTok + resp.OutputTokens * model.CostOutPerMTok) / 1_000_000m;
                loop.Cost += stepCost;
                ctx.Budget.Consume(resp.InputTokens + resp.OutputTokens);

                var callsJson = new JsonArray();
                foreach (var c in resp.ToolCalls)
                    callsJson.Add(new JsonObject { ["name"] = c.Name, ["args"] = Text.Trunc(c.ArgumentsJson, 600) });
                await _events.AddAsync(ctx, "llm_call", new JsonObject
                {
                    ["step"] = loop.Steps,
                    ["input_tokens"] = resp.InputTokens,
                    ["output_tokens"] = resp.OutputTokens,
                    ["latency_ms"] = resp.LatencyMs,
                    ["stop_reason"] = resp.StopReason,
                    ["cost_usd"] = stepCost,
                    ["budget_remaining"] = ctx.Budget.Remaining,
                    ["tools_offered"] = specs.Count,
                    ["content"] = Text.Trunc(resp.Content, 2000),
                    ["tool_calls"] = callsJson
                });
                await _db.ExecAsync(
                    "update runs set steps = @s, tokens_in = @ti, tokens_out = @to, cost_usd = @c where id = @id and owner = @owner",
                    ("s", loop.Steps), ("ti", loop.TokensIn), ("to", loop.TokensOut), ("c", loop.Cost), ("id", ctx.RunId), ("owner", InstanceId));

                var decision = resp.ToolCalls.Count == 0 ? "resposta final" : "chama " + string.Join(", ", resp.ToolCalls.Select(c => c.Name));
                Log.Write(depth, ConsoleColor.Cyan, "modelo", $"{agent.Id} passo {loop.Steps}: {resp.InputTokens}->{resp.OutputTokens} tokens, {resp.LatencyMs}ms, {decision}");
                if (!string.IsNullOrWhiteSpace(resp.Content) && resp.ToolCalls.Count > 0)
                    Log.Write(depth, ConsoleColor.DarkCyan, "pensa", resp.Content);

                await loop.AppendAsync(ChatMessage.Assistant(resp.Content, resp.ToolCalls.Count > 0 ? resp.ToolCalls : null));
                if (!string.IsNullOrEmpty(resp.Content))
                    await _events.AddAsync(ctx, "assistant_message", new JsonObject { ["content"] = resp.Content });

                if (resp.ToolCalls.Count == 0)
                {
                    status = RunStatus.Completed;
                    output = resp.Content ?? "";
                    break;
                }

                var toolsBefore = ctx.ActiveTools.Count;
                foreach (var call in resp.ToolCalls)
                    await loop.AppendAsync(ChatMessage.Tool(call.Id, call.Name, await _executor.ExecuteAsync(ctx, call, token)));
                if (ctx.ActiveTools.Count != toolsBefore) await SaveActiveToolsAsync(ctx);
            }
        }
        catch (RunSuspendedException suspended)
        {
            suspension = suspended;
            status = RunStatus.ForSuspension(suspended);
            output = suspended.Message;
        }
        catch (LeaseLostException)
        {
            leaseLost = true;
        }
        catch (OperationCanceledException) when (lease.Lost)
        {
            leaseLost = true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            status = RunStatus.Cancelled;
            output = "Cancelado pelo usuario.";
            foreach (var orphan in UnansweredToolCalls(loop.Messages))
                await loop.AppendAsync(ChatMessage.Tool(orphan.Id, orphan.Name, "CANCELADO: o usuario interrompeu antes desta ferramenta terminar."));
            await _events.AddAsync(ctx, "cancelled", new JsonObject { ["step"] = loop.Steps });
            Log.Write(depth, ConsoleColor.Yellow, "cancelado", $"{agent.Id} run {Text.Short(ctx.RunId)}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not InjectedFaultException)
        {
            status = RunStatus.Failed;
            output = $"Erro: {ex.Message}";
            await _events.AddAsync(ctx, "error", new JsonObject { ["type"] = ex.GetType().Name, ["message"] = ex.Message });
            Log.Write(depth, ConsoleColor.Red, "erro", $"{agent.Id}: {ex.Message}");
        }

        var outcome = new RunOutcome(status, output, loop.Steps, loop.TokensIn, loop.TokensOut, loop.Cost,
            ctx.ActiveTools.Keys.ToArray(), suspension);
        if (leaseLost || !await _lifecycle.RecordOutcomeAsync(ctx, outcome))
        {
            Log.Write(depth, ConsoleColor.Yellow, "lease", $"{agent.Id} run {Text.Short(ctx.RunId)}: posse perdida para outra instancia; nada gravado");
            return new RunResult(ctx.RunId, ctx.RootRunId, LeaseLostStatus, "Outra instancia assumiu o run.", loop.TokensIn, loop.TokensOut, loop.Cost, loop.Steps);
        }

        if (suspension != null) await _events.AddAsync(ctx, status, SuspensionEvent(suspension, loop.Steps));
        else
            await _events.AddAsync(ctx, "run_finished", new JsonObject
            {
                ["status"] = status,
                ["steps"] = loop.Steps,
                ["tokens_in"] = loop.TokensIn,
                ["tokens_out"] = loop.TokensOut,
                ["cost_usd"] = loop.Cost,
                ["output"] = Text.Trunc(output, 4000)
            });
        Log.Write(depth, status == RunStatus.Completed ? ConsoleColor.Green : ConsoleColor.Red, "fim",
            $"{agent.Id} run {Text.Short(ctx.RunId)}: {status} | {loop.Steps} passos | {loop.TokensIn + loop.TokensOut:N0} tokens | US$ {loop.Cost:0.0000}");

        return new RunResult(ctx.RunId, ctx.RootRunId, status, output, loop.TokensIn, loop.TokensOut, loop.Cost, loop.Steps)
        {
            ActiveTools = ctx.ActiveTools.Keys.ToArray(),
            WaitingFor = (suspension as HumanInputRequiredException)?.RequestId,
            WaitingOnRun = (suspension as ChildSuspendedException)?.ChildRunId
        };
    }

    /// <summary>
    /// Ferramentas carregadas agora (inclusive as descobertas no meio do run): se a instancia cair, quem recuperar
    /// o run precisa delas para reexecutar as chamadas pendentes.
    /// </summary>
    private Task SaveActiveToolsAsync(RunContext ctx) =>
        _db.ExecAsync("update runs set active_tools = @t where id = @id and owner = @owner",
            ("t", ctx.ActiveTools.Keys.OrderBy(k => k).ToArray()), ("id", ctx.RunId), ("owner", ctx.Owner));

    private static JsonObject SuspensionEvent(RunSuspendedException suspension, int step)
    {
        var data = new JsonObject { ["tool_call_id"] = suspension.ToolCallId, ["step"] = step };
        if (suspension is HumanInputRequiredException human) data["request_id"] = human.RequestId.ToString();
        if (suspension is ChildSuspendedException child)
        {
            data["child_run_id"] = child.ChildRunId.ToString();
            data["child_agent"] = child.ChildAgent;
        }
        return data;
    }

    /// <summary>
    /// O run ja tinha gravado a resposta final antes de parar (ex.: caiu entre gravar a resposta e concluir):
    /// a ultima mensagem e do assistente, sem tool calls. Concluir com ela evita chamar o modelo de novo.
    /// </summary>
    public static string? FinalAnswer(IReadOnlyList<ChatMessage> messages) =>
        messages.Count > 0 && messages[^1] is { Role: "assistant", ToolCalls: null or { Count: 0 } } last ? last.Content ?? "" : null;

    /// <summary>Ferramentas iniciais do agente mais as descobertas em turnos anteriores que ainda estao no escopo dele.</summary>
    private static IEnumerable<string> ToolsToLoad(AgentCfg agent, ChatTurn? chat) =>
        chat == null
            ? agent.InitialTools
            : agent.InitialTools.Concat(chat.ActiveTools.Where(t => Glob.Match(agent.AllowedTools, t)));

    private static LlmStream? StreamFor(RunContext ctx) =>
        ctx.Observer is { } observer
            ? new LlmStream(delta => observer.OnTextDelta(ctx, delta), delta => observer.OnReasoningDelta(ctx, delta))
            : null;

    /// <summary>Tool calls da ultima mensagem do assistente que ficaram sem resultado (o provedor rejeita historico assim).</summary>
    public static IEnumerable<ToolCall> UnansweredToolCalls(IReadOnlyList<ChatMessage> messages)
    {
        var lastAssistant = -1;
        for (var i = messages.Count - 1; i >= 0; i--)
            if (messages[i].Role == "assistant") { lastAssistant = i; break; }
        if (lastAssistant < 0 || messages[lastAssistant].ToolCalls is not { Count: > 0 } calls) return Array.Empty<ToolCall>();
        var answered = messages.Skip(lastAssistant + 1).Where(m => m.Role == "tool").Select(m => m.ToolCallId).ToHashSet();
        return calls.Where(c => !answered.Contains(c.Id)).ToList();
    }

    /// <summary>
    /// Executa uma unica ferramenta fora de um agente (no de workflow), com a mesma politica e auditoria. O run e a
    /// chamada tem ids estaveis por tentativa do no: na retomada, um resultado ja gravado e devolvido e uma chamada
    /// interrompida segue <see cref="ToolExecutor.ExecutePendingAsync"/> (com efeito colateral, nao repete sozinha).
    /// Cancelada (prazo ou cancelamento do workflow), o run termina cancelled e o resultado volta como falha.
    /// </summary>
    public async Task<ToolNodeRun> RunToolAsync(string toolName, JsonObject args, Guid workflowRunId, string workflowNodeId,
                                                int attempt, IHumanGate human, WorkflowToolRules? rules, CancellationToken ct)
    {
        var runId = StableId($"{workflowRunId}/{workflowNodeId}/{attempt}");
        var call = new ToolCall($"wf_{workflowNodeId}_{attempt}", toolName, args.ToJsonString());
        var agentId = "workflow:" + toolName;
        var input = $"{toolName}({args.ToJsonString(Text.RelaxedJson)})";
        var tools = await _registry.LoadAsync(new[] { toolName });
        if (!tools.ContainsKey(toolName)) return new ToolNodeRun(false, $"ERRO: ferramenta '{toolName}' nao existe.", runId, false, false);

        var existing = await _db.QueryOneAsync(
            "select status, output, (select coalesce(max(seq), 0) from run_events where run_id = @id) as last_seq from runs where id = @id",
            ("id", runId));
        if (existing != null && existing.S("status") != RunStatus.Running)
            return new ToolNodeRun(existing.S("status") == RunStatus.Completed, existing.S("output"), runId,
                await _events.ToolCallStartedAsync(runId, call.Id), tools[toolName].SideEffect);

        var ctx = new RunContext
        {
            RunId = runId,
            RootRunId = runId,
            WorkflowRunId = workflowRunId,
            Agent = new AgentCfg(agentId, "no de ferramenta de workflow", "", "", new[] { toolName }, new[] { toolName },
                Array.Empty<string>(), 1, 0, null, 1),
            Model = new ModelCfg("none", new ProviderCfg("none", "openai_compat", "", null, "none", "{}"), "", false, false,
                "max_tokens", 0, 0, 0, 0),
            Depth = 0,
            Budget = new Budget(0),
            Human = human,
            ToolRules = rules,
            SeqStart = existing?.I("last_seq") ?? 0
        };
        ctx.ActiveTools[toolName] = tools[toolName];

        if (existing == null)
        {
            await _db.ExecAsync(
                @"insert into runs (id, root_run_id, workflow_run_id, workflow_node_id, agent_id, input, depth) values (@id, @id, @wf, @n, @a, @i, 0)",
                ("id", runId), ("wf", workflowRunId), ("n", workflowNodeId), ("a", agentId), ("i", input));
            await _events.AddAsync(ctx, "run_started", new JsonObject { ["agent"] = agentId, ["depth"] = 0, ["input"] = input });
        }
        string output;
        var cancelled = false;
        try
        {
            if (existing == null) output = await _executor.ExecuteAsync(ctx, call, ct);
            else if (await RecordedToolOutputAsync(runId) is { } recorded) output = recorded;
            else
            {
                await _events.AddAsync(ctx, "run_resumed", new JsonObject { ["by"] = InstanceId });
                output = await _executor.ExecutePendingAsync(ctx, call, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            cancelled = true;
            output = "CANCELADO: a ferramenta foi interrompida (prazo do no ou cancelamento do workflow).";
            await _events.AddAsync(ctx, "cancelled", new JsonObject { ["tool"] = toolName });
        }

        var ok = !cancelled && IsToolSuccess(output);
        var status = ok ? RunStatus.Completed : cancelled ? RunStatus.Cancelled : RunStatus.Failed;
        await _db.ExecAsync("update runs set status = @s, output = @o, steps = 1, finished_at = now() where id = @id",
            ("s", status), ("o", output), ("id", runId));
        await _events.AddAsync(ctx, "run_finished", new JsonObject { ["status"] = status, ["output"] = Text.Trunc(output, 4000) });
        return new ToolNodeRun(ok, output, runId, await _events.ToolCallStartedAsync(runId, call.Id), tools[toolName].SideEffect);
    }

    /// <summary>Resultado da ferramenta ja gravado (queda entre o tool_result e o fim do run).</summary>
    private async Task<string?> RecordedToolOutputAsync(Guid runId) =>
        (string?)await _db.ScalarAsync(
            "select data->>'output' from run_events where run_id = @id and type = 'tool_result' order by seq desc limit 1", ("id", runId));

    private static bool IsToolSuccess(string output) =>
        !output.StartsWith("ERRO", StringComparison.Ordinal) && !output.StartsWith("BLOQUEADO", StringComparison.Ordinal) &&
        !output.StartsWith("NEGADO", StringComparison.Ordinal);

    /// <summary>Guid deterministico (MD5 do texto): o mesmo no e tentativa sempre gera o mesmo id de run.</summary>
    private static Guid StableId(string key) => new(System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes(key)));

    private static string BuildSystem(AgentCfg agent, RunContext ctx, ChatTurn? chat)
    {
        var sb = new StringBuilder(agent.SystemPrompt);
        if (!string.IsNullOrWhiteSpace(chat?.Summary))
            sb.AppendLine().AppendLine().AppendLine("## Resumo da conversa ate aqui (turnos antigos compactados)").AppendLine(chat.Summary);
        if (!string.IsNullOrWhiteSpace(chat?.Memories))
            sb.AppendLine().AppendLine().AppendLine("## Memorias de longo prazo possivelmente relevantes").AppendLine(chat.Memories);
        sb.AppendLine().AppendLine();
        sb.AppendLine("## Contexto do runtime");
        sb.AppendLine($"- Data atual: {DateTime.Now:yyyy-MM-dd}.");
        sb.AppendLine($"- Seu id: {agent.Id}. Profundidade de delegacao: {ctx.Depth}/{MaxDepth}.");
        sb.AppendLine("- Arquivos usam caminhos relativos ao workspace (ex.: 'relatorio.md').");
        sb.AppendLine($"- Ferramentas carregadas agora: {string.Join(", ", ctx.ActiveTools.Keys.OrderBy(k => k))}.");
        sb.AppendLine("- Se precisar de uma capacidade que nao esta carregada, chame search_tools com palavras-chave.");
        sb.AppendLine($"- Budget restante: {ctx.Budget.Remaining:N0} tokens. Seja eficiente.");
        sb.AppendLine("- Quando terminar, responda sem chamar ferramentas, com o resultado final.");
        return sb.ToString();
    }

    /// <summary>Compactacao simples: se o historico passar de 60% da janela, encurta resultados antigos de ferramentas.</summary>
    private static void Compact(List<ChatMessage> messages, ModelCfg model)
    {
        var budgetChars = (long)(model.ContextWindow * 0.6 * 4);
        long total = messages.Sum(m => (long)(m.Content?.Length ?? 0) + (m.ToolCalls?.Sum(c => c.ArgumentsJson.Length) ?? 0));
        if (total <= budgetChars) return;
        for (var i = 0; i < messages.Count - 4 && total > budgetChars; i++)
        {
            var m = messages[i];
            if (m.Role != "tool" || m.Content == null || m.Content.Length <= 500) continue;
            total -= m.Content.Length - 500;
            m.Content = m.Content.Substring(0, 500) + " ... [resultado antigo compactado]";
        }
    }
}
