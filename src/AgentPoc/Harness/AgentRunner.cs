using System.Text;
using System.Text.Json.Nodes;
using AgentPoc.Core;
using AgentPoc.Llm;
using AgentPoc.Tools;

namespace AgentPoc.Harness;

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

    public AgentRunner(Db db, ConfigRepo config, ToolRegistry registry, ToolExecutor executor, EventLog events)
    {
        _db = db;
        _config = config;
        _registry = registry;
        _executor = executor;
        _events = events;
    }

    private static JsonArray ToArray(IEnumerable<string> items)
    {
        var arr = new JsonArray();
        foreach (var i in items) arr.Add(i);
        return arr;
    }

    /// <param name="chat">Continuacao de conversa: historico, ferramentas ja descobertas e persistencia de cada mensagem nova.</param>
    /// <param name="hooks">Observer ao vivo e quem responde pelo humano; quando nulos, herda do pai ou usa o padrao da CLI.</param>
    public async Task<RunResult> RunAsync(string agentId, string input, RunContext? parent = null, Guid? workflowRunId = null,
                                          long? budgetOverride = null, ChatTurn? chat = null, RunHooks? hooks = null,
                                          CancellationToken ct = default)
    {
        var agent = await _config.GetAgentAsync(agentId)
                    ?? throw new InvalidOperationException($"Agente '{agentId}' nao existe ou esta desabilitado.");
        var model = await _config.GetModelAsync(agent.ModelId);
        var depth = parent == null ? 0 : parent.Depth + 1;
        var limit = budgetOverride ?? agent.MaxTokens;
        var budget = parent == null ? new Budget(limit) : parent.Budget.Child(limit);
        var runId = Guid.NewGuid();
        var rootId = parent?.RootRunId ?? runId;
        var wfRun = workflowRunId ?? parent?.WorkflowRunId;
        var conversationId = chat?.ConversationId ?? parent?.ConversationId;

        await _db.ExecAsync(
            @"insert into runs (id, parent_run_id, root_run_id, workflow_run_id, conversation_id, agent_id, agent_version, model_id, input, depth)
              values (@id, @p, @root, @wf, @conv, @a, @v, @m, @i, @d)",
            ("id", runId), ("p", parent?.RunId), ("root", rootId), ("wf", wfRun), ("conv", conversationId), ("a", agent.Id),
            ("v", agent.Version), ("m", model.Id), ("i", input), ("d", depth));

        var ctx = new RunContext
        {
            RunId = runId,
            ParentRunId = parent?.RunId,
            RootRunId = rootId,
            WorkflowRunId = wfRun,
            ConversationId = conversationId,
            Agent = agent,
            Model = model,
            Depth = depth,
            Budget = budget,
            Observer = hooks?.Observer ?? parent?.Observer,
            Human = hooks?.Human ?? parent?.Human ?? ModeHumanGate.Instance
        };
        foreach (var t in (await _registry.LoadAsync(ToolsToLoad(agent, chat))).Values) ctx.ActiveTools[t.Name] = t;

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
        Log.Write(depth, ConsoleColor.Magenta, "agente", $"{agent.Id} v{agent.Version} iniciou run {Text.Short(runId)} | modelo {model.Id} ({model.ResolvedName}) | budget {budget.Remaining:N0} tokens");
        Log.Write(depth, ConsoleColor.Gray, "entrada", input);

        var loop = new LoopState(ctx, chat);
        await loop.AppendAsync(ChatMessage.User(input));
        return await RunLoopAsync(loop, Array.Empty<ToolCall>(), ct);
    }

    /// <summary>
    /// Retoma um run suspenso em waiting_human (mesmo run, mesmos contadores). Primeiro conclui as tool calls que
    /// ficaram sem resultado, agora com a decisao humana gravada, e depois segue o loop normal do agente.
    /// </summary>
    public async Task<RunResult> ResumeAsync(Guid runId, ChatTurn chat, RunHooks hooks, CancellationToken ct)
    {
        var run = await _db.QueryOneAsync("select * from runs where id = @id", ("id", runId))
                  ?? throw new InvalidOperationException($"Run {runId} nao encontrado.");
        if (run.S("status") != "waiting_human")
            throw new InvalidOperationException($"Run {Text.Short(runId)} nao esta aguardando decisao humana (status {run.S("status")}).");
        var agent = await _config.GetAgentAsync(run.S("agent_id"))
                    ?? throw new InvalidOperationException($"Agente '{run.S("agent_id")}' nao existe ou esta desabilitado.");
        var model = await _config.GetModelAsync(agent.ModelId);
        var lastSeq = Convert.ToInt32(await _db.ScalarAsync("select coalesce(max(seq), 0) from run_events where run_id = @id", ("id", runId)));

        var budget = new Budget(agent.MaxTokens);
        budget.Consume(run.L("tokens_in") + run.L("tokens_out"));
        var ctx = new RunContext
        {
            RunId = runId,
            RootRunId = run.G("root_run_id"),
            ConversationId = chat.ConversationId,
            Agent = agent,
            Model = model,
            Depth = 0,
            Budget = budget,
            Observer = hooks.Observer,
            Human = hooks.Human ?? ModeHumanGate.Instance,
            SeqStart = lastSeq
        };
        foreach (var t in (await _registry.LoadAsync(ToolsToLoad(agent, chat))).Values) ctx.ActiveTools[t.Name] = t;

        await _db.ExecAsync("update runs set status = 'running' where id = @id", ("id", runId));
        await _events.AddAsync(ctx, "run_resumed", new JsonObject { ["after_steps"] = run.I("steps") });
        Log.Write(0, ConsoleColor.Magenta, "agente", $"{agent.Id} retomou run {Text.Short(runId)} apos decisao humana");

        var loop = new LoopState(ctx, chat)
        {
            Steps = run.I("steps"),
            TokensIn = run.L("tokens_in"),
            TokensOut = run.L("tokens_out"),
            Cost = run.D("cost_usd")
        };
        return await RunLoopAsync(loop, UnansweredToolCalls(loop.Messages).ToList(), ct);
    }

    /// <summary>Estado mutavel de um run em execucao (mensagens e contadores).</summary>
    private sealed class LoopState
    {
        private readonly ChatTurn? _chat;

        public LoopState(RunContext ctx, ChatTurn? chat)
        {
            Ctx = ctx;
            _chat = chat;
            Messages = new List<ChatMessage>(chat?.History ?? Array.Empty<ChatMessage>());
        }

        public RunContext Ctx { get; }
        public ChatTurn? Chat => _chat;
        public List<ChatMessage> Messages { get; }
        public int Steps { get; set; }
        public long TokensIn { get; set; }
        public long TokensOut { get; set; }
        public decimal Cost { get; set; }

        public async Task AppendAsync(ChatMessage message)
        {
            Messages.Add(message);
            if (_chat != null) await _chat.OnMessage(Ctx.RunId, message);
        }
    }

    private async Task<RunResult> RunLoopAsync(LoopState loop, IReadOnlyList<ToolCall> pendingCalls, CancellationToken ct)
    {
        var ctx = loop.Ctx;
        var agent = ctx.Agent;
        var model = ctx.Model;
        var depth = ctx.Depth;
        var status = "running";
        var output = "";
        Guid? waitingFor = null;
        var client = LlmFactory.For(model);
        var stream = StreamFor(ctx);

        try
        {
            foreach (var call in pendingCalls)
                await loop.AppendAsync(ChatMessage.Tool(call.Id, call.Name, await _executor.ExecuteAsync(ctx, call, ct)));

            while (true)
            {
                ct.ThrowIfCancellationRequested();
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

                var resp = await client.CompleteAsync(model, new LlmRequest(BuildSystem(agent, ctx, loop.Chat), loop.Messages, specs, agent.Temperature, stream), ct);
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
                    "update runs set steps = @s, tokens_in = @ti, tokens_out = @to, cost_usd = @c where id = @id",
                    ("s", loop.Steps), ("ti", loop.TokensIn), ("to", loop.TokensOut), ("c", loop.Cost), ("id", ctx.RunId));

                var decision = resp.ToolCalls.Count == 0 ? "resposta final" : "chama " + string.Join(", ", resp.ToolCalls.Select(c => c.Name));
                Log.Write(depth, ConsoleColor.Cyan, "modelo", $"{agent.Id} passo {loop.Steps}: {resp.InputTokens}->{resp.OutputTokens} tokens, {resp.LatencyMs}ms, {decision}");
                if (!string.IsNullOrWhiteSpace(resp.Content) && resp.ToolCalls.Count > 0)
                    Log.Write(depth, ConsoleColor.DarkCyan, "pensa", resp.Content);

                await loop.AppendAsync(ChatMessage.Assistant(resp.Content, resp.ToolCalls.Count > 0 ? resp.ToolCalls : null));

                if (resp.ToolCalls.Count == 0)
                {
                    status = "completed";
                    output = resp.Content ?? "";
                    break;
                }

                foreach (var call in resp.ToolCalls)
                    await loop.AppendAsync(ChatMessage.Tool(call.Id, call.Name, await _executor.ExecuteAsync(ctx, call, ct)));
            }
        }
        catch (HumanInputRequiredException waiting)
        {
            status = "waiting_human";
            output = waiting.Message;
            waitingFor = waiting.RequestId;
            await _events.AddAsync(ctx, "waiting_human", new JsonObject { ["request_id"] = waiting.RequestId.ToString(), ["step"] = loop.Steps });
            Log.Write(depth, ConsoleColor.Yellow, "aguardando", $"{agent.Id} run {Text.Short(ctx.RunId)}: {waiting.Message}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            status = "cancelled";
            output = "Cancelado pelo usuario.";
            foreach (var orphan in UnansweredToolCalls(loop.Messages))
                await loop.AppendAsync(ChatMessage.Tool(orphan.Id, orphan.Name, "CANCELADO: o usuario interrompeu antes desta ferramenta terminar."));
            await _events.AddAsync(ctx, "cancelled", new JsonObject { ["step"] = loop.Steps });
            Log.Write(depth, ConsoleColor.Yellow, "cancelado", $"{agent.Id} run {Text.Short(ctx.RunId)}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            status = "failed";
            output = $"Erro: {ex.Message}";
            await _events.AddAsync(ctx, "error", new JsonObject { ["type"] = ex.GetType().Name, ["message"] = ex.Message });
            Log.Write(depth, ConsoleColor.Red, "erro", $"{agent.Id}: {ex.Message}");
        }

        await _db.ExecAsync(
            @"update runs set status = @st, output = @o, steps = @s, tokens_in = @ti, tokens_out = @to, cost_usd = @c,
                     finished_at = case when @st = 'waiting_human' then null else now() end
               where id = @id",
            ("st", status), ("o", output), ("s", loop.Steps), ("ti", loop.TokensIn), ("to", loop.TokensOut), ("c", loop.Cost), ("id", ctx.RunId));
        if (status != "waiting_human")
            await _events.AddAsync(ctx, "run_finished", new JsonObject
            {
                ["status"] = status,
                ["steps"] = loop.Steps,
                ["tokens_in"] = loop.TokensIn,
                ["tokens_out"] = loop.TokensOut,
                ["cost_usd"] = loop.Cost,
                ["output"] = Text.Trunc(output, 4000)
            });
        Log.Write(depth, status == "completed" ? ConsoleColor.Green : ConsoleColor.Red, "fim",
            $"{agent.Id} run {Text.Short(ctx.RunId)}: {status} | {loop.Steps} passos | {loop.TokensIn + loop.TokensOut:N0} tokens | US$ {loop.Cost:0.0000}");

        return new RunResult(ctx.RunId, ctx.RootRunId, status, output, loop.TokensIn, loop.TokensOut, loop.Cost, loop.Steps)
        {
            ActiveTools = ctx.ActiveTools.Keys.ToArray(),
            WaitingFor = waitingFor
        };
    }

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

    /// <summary>Executa uma unica ferramenta fora de um agente (no de workflow), com a mesma politica e auditoria.</summary>
    public async Task<(bool Ok, string Output, Guid RunId)> RunToolAsync(string toolName, JsonObject args, Guid? workflowRunId, CancellationToken ct)
    {
        var runId = Guid.NewGuid();
        var agentId = "workflow:" + toolName;
        var input = $"{toolName}({args.ToJsonString(Text.RelaxedJson)})";
        var tools = await _registry.LoadAsync(new[] { toolName });
        if (!tools.ContainsKey(toolName)) return (false, $"ERRO: ferramenta '{toolName}' nao existe.", runId);

        await _db.ExecAsync(
            @"insert into runs (id, root_run_id, workflow_run_id, agent_id, input, depth) values (@id, @id, @wf, @a, @i, 0)",
            ("id", runId), ("wf", workflowRunId), ("a", agentId), ("i", input));
        var pseudoAgent = new AgentCfg(agentId, "no de ferramenta de workflow", "", "", new[] { toolName }, new[] { toolName },
            Array.Empty<string>(), 1, 0, null, 1);
        var dummyModel = new ModelCfg("none", new ProviderCfg("none", "openai_compat", "", null, "none", "{}"), "", false, false,
            "max_tokens", 0, 0, 0, 0);
        var ctx = new RunContext
        {
            RunId = runId,
            RootRunId = runId,
            WorkflowRunId = workflowRunId,
            Agent = pseudoAgent,
            Model = dummyModel,
            Depth = 0,
            Budget = new Budget(0)
        };
        ctx.ActiveTools[toolName] = tools[toolName];
        await _events.AddAsync(ctx, "run_started", new JsonObject { ["agent"] = agentId, ["depth"] = 0, ["input"] = input });

        var output = await _executor.ExecuteAsync(ctx, new ToolCall("wf_" + Guid.NewGuid().ToString("N").Substring(0, 8), toolName, args.ToJsonString()), ct);
        var ok = !output.StartsWith("ERRO", StringComparison.Ordinal) && !output.StartsWith("BLOQUEADO", StringComparison.Ordinal) &&
                 !output.StartsWith("NEGADO", StringComparison.Ordinal);
        var status = ok ? "completed" : "failed";
        await _db.ExecAsync("update runs set status = @s, output = @o, steps = 1, finished_at = now() where id = @id",
            ("s", status), ("o", output), ("id", runId));
        await _events.AddAsync(ctx, "run_finished", new JsonObject { ["status"] = status, ["output"] = Text.Trunc(output, 4000) });
        return (ok, output, runId);
    }

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
