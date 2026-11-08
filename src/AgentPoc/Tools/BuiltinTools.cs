using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using AgentPoc.Core;
using AgentPoc.Harness;
using EventLog = AgentPoc.Harness.EventLog;

namespace AgentPoc.Tools;

/// <summary>Ferramentas nativas (kind='builtin'). Metadados e politica ficam no banco; aqui so a implementacao.</summary>
public sealed class BuiltinTools
{
    private readonly Db _db;
    private readonly ToolRegistry _registry;
    private readonly ConfigRepo _config;
    private readonly EventLog _events;
    private readonly MemoryStore _memory;

    /// <summary>Atribuido depois da construcao (dependencia circular runner -> tools -> runner).</summary>
    public AgentRunner? Runner { get; set; }

    public BuiltinTools(Db db, ToolRegistry registry, ConfigRepo config, EventLog events, MemoryStore memory)
    {
        _db = db;
        _registry = registry;
        _config = config;
        _events = events;
        _memory = memory;
    }

    private static string Arg(JsonObject a, string key) => HttpTool.ArgString(a[key]).Trim();

    private static int ArgInt(JsonObject a, string key, int fallback) =>
        int.TryParse(HttpTool.ArgString(a[key]), out var v) ? v : fallback;

    public Task<string> InvokeAsync(string name, JsonObject args, RunContext ctx, string callId, CancellationToken ct) => name switch
    {
        "search_tools" => SearchTools(args, ctx),
        "ask_human" => AskHuman(args, ctx, callId, ct),
        "think" => Think(args, ctx),
        "wait_seconds" => WaitSeconds(args, ct),
        "update_plan" => UpdatePlan(args, ctx),
        "web_search" => WebSearchTool(args, ct),
        "list_agents" => ListAgents(ctx),
        "delegate_task" => Delegate(args, ctx, callId, ct),
        "create_task" => CreateTask(args, ctx),
        "check_task" => CheckTask(args),
        "memory_save" => MemorySave(args, ctx),
        "memory_recall" => MemoryRecall(args, ctx),
        "kb_search" => KbSearch(args),
        "kb_add" => KbAdd(args),
        "http_get" => HttpGet(args, ct),
        "read_file" => ReadFile(args),
        "write_file" => WriteFile(args),
        "list_files" => ListFiles(args),
        "run_shell" => RunShell(args, ct),
        "delete_file" => DeleteFile(args),
        _ => Task.FromResult($"ERRO: ferramenta builtin '{name}' sem implementacao.")
    };

    // ---------------- descoberta ----------------

    private async Task<string> SearchTools(JsonObject args, RunContext ctx)
    {
        var query = Arg(args, "query");
        var found = await _registry.SearchAsync(query, ctx.Agent.AllowedTools, ctx.ActiveTools.Keys.ToHashSet(), 5);
        foreach (var t in found) ctx.ActiveTools[t.Name] = t;

        var names = new JsonArray();
        foreach (var t in found) names.Add(t.Name);
        await _events.AddAsync(ctx, "tools_loaded", new JsonObject { ["query"] = query, ["tools"] = names });
        if (found.Count > 0)
            Log.Write(ctx.Depth, ConsoleColor.Blue, "descoberta", $"{ctx.Agent.Id} carregou: {string.Join(", ", found.Select(t => t.Name))}");

        if (found.Count == 0)
            return $"Nenhuma ferramenta nova encontrada para '{query}' dentro do seu escopo. " +
                   $"Ferramentas ja carregadas: {string.Join(", ", ctx.ActiveTools.Keys.OrderBy(k => k))}.";
        var sb = new StringBuilder("Ferramentas carregadas e disponiveis a partir de agora:\n");
        foreach (var t in found) sb.AppendLine($"- {t.Name} (politica: {t.Policy}): {t.Description}");
        return sb.ToString();
    }

    // ---------------- raciocinio e plano ----------------

    private async Task<string> Think(JsonObject args, RunContext ctx)
    {
        var thought = Arg(args, "thought");
        if (thought.Length == 0) return "ERRO: thought vazio.";
        await _events.AddAsync(ctx, "thinking", new JsonObject { ["thought"] = Text.Trunc(thought, 4000) });
        return "Raciocinio registrado. Continue.";
    }

    private static async Task<string> AskHuman(JsonObject args, RunContext ctx, string callId, CancellationToken ct)
    {
        var question = Arg(args, "question");
        if (question.Length == 0) return "ERRO: informe question.";
        var options = args["options"] is JsonArray arr
            ? arr.Select(o => HttpTool.ArgString(o).Trim()).Where(o => o.Length > 0).ToList()
            : new List<string>();
        var answer = await ctx.Human.AskAsync(ctx, callId, question, options, ct);
        return answer.StartsWith("SEM RESPOSTA", StringComparison.Ordinal) ? answer : $"Resposta da pessoa: {answer}";
    }

    private const int MaxWaitSeconds = 60;

    private static async Task<string> WaitSeconds(JsonObject args, CancellationToken ct)
    {
        var seconds = Math.Clamp(ArgInt(args, "seconds", 1), 1, MaxWaitSeconds);
        await Task.Delay(TimeSpan.FromSeconds(seconds), ct);
        return $"Esperei {seconds} segundos.";
    }

    private async Task<string> UpdatePlan(JsonObject args, RunContext ctx)
    {
        if (args["steps"] is not JsonArray steps || steps.Count == 0) return "ERRO: informe steps com ao menos um passo.";
        var plan = new JsonArray();
        foreach (var s in steps)
        {
            var step = HttpTool.ArgString(s?["step"]).Trim();
            var status = HttpTool.ArgString(s?["status"]).Trim();
            if (step.Length == 0) continue;
            plan.Add(new JsonObject { ["step"] = step, ["status"] = status is "in_progress" or "done" ? status : "pending" });
        }
        await _events.AddAsync(ctx, "plan_updated", new JsonObject { ["steps"] = plan });
        var done = plan.Count(p => HttpTool.ArgString(p?["status"]) == "done");
        return $"Plano atualizado: {done}/{plan.Count} passos concluidos.";
    }

    // ---------------- delegacao ----------------

    private async Task<string> ListAgents(RunContext ctx)
    {
        var agents = (await _config.ListAgentsAsync())
            .Where(a => a.Id != ctx.Agent.Id && Glob.Match(ctx.Agent.CanDelegateTo, a.Id))
            .ToList();
        if (agents.Count == 0) return "Voce nao pode delegar para nenhum agente.";
        return string.Join("\n", agents.Select(a => $"- {a.Id}: {a.Description}"));
    }

    private async Task<string?> ValidateTarget(string agentId, RunContext ctx)
    {
        if (string.IsNullOrEmpty(agentId)) return "ERRO: informe o id do agente.";
        if (agentId == ctx.Agent.Id) return "ERRO: um agente nao pode delegar para si mesmo.";
        if (!Glob.Match(ctx.Agent.CanDelegateTo, agentId)) return $"ERRO: '{ctx.Agent.Id}' nao tem permissao para delegar a '{agentId}'.";
        if (await _config.GetAgentAsync(agentId) == null) return $"ERRO: agente '{agentId}' nao existe. Use list_agents.";
        return null;
    }

    private async Task<string> Delegate(JsonObject args, RunContext ctx, string callId, CancellationToken ct)
    {
        var agentId = Arg(args, "agent");
        var task = Arg(args, "task");
        if (await ValidateTarget(agentId, ctx) is { } err) return err;
        if (ctx.Depth + 1 > AgentRunner.MaxDepth) return $"ERRO: profundidade maxima de delegacao ({AgentRunner.MaxDepth}) atingida. Faca a tarefa voce mesmo.";
        if (Runner == null) return "ERRO: runner nao configurado.";

        if (await ExistingChildAsync(ctx, callId) is { } existing) return await AdoptChildAsync(ctx, existing, callId);

        await _events.AddAsync(ctx, "delegation_start", new JsonObject { ["to_agent"] = agentId, ["task"] = Text.Trunc(task, 1500) });
        var child = await Runner.RunAsync(agentId, task, parent: ctx, parentToolCallId: callId, ct: ct);
        if (child.Status == AgentRunner.LeaseLostStatus) throw new LeaseLostException(ctx.RunId);
        if (RunStatus.IsSuspended(child.Status))
            throw new ChildSuspendedException(child.RunId, agentId, callId);

        await _events.AddAsync(ctx, "delegation_end", new JsonObject
        {
            ["to_agent"] = agentId,
            ["child_run_id"] = child.RunId.ToString(),
            ["status"] = child.Status,
            ["tokens"] = child.TokensIn + child.TokensOut
        });
        return DelegationResult.Format(agentId, child.Status, child.Steps, child.Output);
    }

    /// <summary>Filho ja criado por esta mesma chamada delegate_task (retomada depois de uma queda).</summary>
    private Task<Dictionary<string, object?>?> ExistingChildAsync(RunContext ctx, string callId) =>
        _db.QueryOneAsync(
            @"select id, agent_id, status, output, steps, tokens_in + tokens_out as tokens from runs
               where parent_run_id = @p and parent_tool_call_id = @c order by started_at desc limit 1",
            ("p", ctx.RunId), ("c", callId));

    /// <summary>Reaproveita o filho: terminado, devolve o resultado dele; ainda andando, o pai espera por ele.</summary>
    private async Task<string> AdoptChildAsync(RunContext ctx, Dictionary<string, object?> child, string callId)
    {
        var agentId = child.S("agent_id");
        if (!RunStatus.IsTerminal(child.S("status")))
            throw new ChildSuspendedException(child.G("id"), agentId, callId);
        await _events.AddAsync(ctx, "delegation_end", new JsonObject
        {
            ["to_agent"] = agentId,
            ["child_run_id"] = child.G("id").ToString(),
            ["status"] = child.S("status"),
            ["tokens"] = child.L("tokens"),
            ["adopted"] = true
        });
        return DelegationResult.Format(agentId, child.S("status"), child.I("steps"), child.S("output"));
    }

    private async Task<string> CreateTask(JsonObject args, RunContext ctx)
    {
        var agentId = Arg(args, "agent");
        var task = Arg(args, "task");
        if (await ValidateTarget(agentId, ctx) is { } err) return err;
        var id = (Guid)(await _db.ScalarAsync(
            "insert into tasks (agent_id, input, priority, created_by_run, workflow_run_id) values (@a, @i, @p, @r, @wf) returning id",
            ("a", agentId), ("i", task), ("p", ArgInt(args, "priority", 0)), ("r", ctx.RunId), ("wf", ctx.WorkflowRunId)))!;
        await _events.AddAsync(ctx, "task_created", new JsonObject { ["task_id"] = id.ToString(), ["agent"] = agentId, ["task"] = Text.Trunc(task, 800) });
        Log.Write(ctx.Depth, ConsoleColor.DarkYellow, "fila", $"tarefa {Text.Short(id)} criada para {agentId}");
        return $"Tarefa criada: {id} (agente {agentId}, status queued).";
    }

    private async Task<string> CheckTask(JsonObject args)
    {
        if (!Guid.TryParse(Arg(args, "task_id"), out var id)) return "ERRO: task_id invalido.";
        var r = await _db.QueryOneAsync("select status, attempts, result, error from tasks where id = @id", ("id", id));
        if (r == null) return "ERRO: tarefa nao encontrada.";
        return $"status={r.S("status")} tentativas={r.I("attempts")}\nresultado: {Text.Trunc(r.SN("result") ?? "(ainda sem resultado)", 3000)}\nerro: {r.SN("error") ?? "-"}";
    }

    // ---------------- memoria e conhecimento ----------------

    private async Task<string> MemorySave(JsonObject args, RunContext ctx)
    {
        var content = Arg(args, "content");
        if (content.Length == 0) return "ERRO: content vazio.";
        await _memory.SaveAsync(ctx.Agent.Id, content, ctx.RunId);
        return "Memoria salva.";
    }

    private async Task<string> MemoryRecall(JsonObject args, RunContext ctx)
    {
        var query = Arg(args, "query");
        if (TextSearch.ToTsQuery(query) == null) return "Consulta vazia.";
        var items = await _memory.RecallAsync(ctx.Agent.Id, query);
        return items.Count == 0 ? "Nada encontrado na memoria." : MemoryStore.Format(items);
    }

    private async Task<string> KbSearch(JsonObject args)
    {
        var tsq = TextSearch.ToTsQuery(Arg(args, "query"));
        if (tsq == null) return "Consulta vazia.";
        var limit = Math.Clamp(ArgInt(args, "limit", 3), 1, 10);
        var rows = await _db.QueryAsync(
            @"select id, title, content from kb_documents
               where search @@ to_tsquery('simple', @q)
               order by ts_rank(search, to_tsquery('simple', @q)) desc limit @l",
            ("q", tsq), ("l", limit));
        if (rows.Count == 0) return "Nenhum documento encontrado na base de conhecimento.";
        return string.Join("\n\n", rows.Select(r => $"[doc {r.L("id")}] {r.S("title")}\n{r.S("content")}"));
    }

    private async Task<string> KbAdd(JsonObject args)
    {
        var title = Arg(args, "title");
        var content = Arg(args, "content");
        if (title.Length == 0 || content.Length == 0) return "ERRO: title e content sao obrigatorios.";
        var id = await _db.ScalarAsync("insert into kb_documents (title, content, source) values (@t, @c, 'agent') returning id", ("t", title), ("c", content));
        return $"Documento {id} adicionado.";
    }

    // ---------------- web ----------------

    private static async Task<string> WebSearchTool(JsonObject args, CancellationToken ct)
    {
        var query = Arg(args, "query");
        if (query.Length == 0) return "ERRO: query vazia.";
        var results = await WebSearch.SearchAsync(query, Math.Clamp(ArgInt(args, "limit", 5), 1, 10), ct);
        return WebSearch.Format(query, results);
    }

    private static async Task<string> HttpGet(JsonObject args, CancellationToken ct)
    {
        var url = Arg(args, "url");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            return "ERRO: url invalida (use http/https).";
        using var resp = await HttpTool.Http.GetAsync(uri, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        var mediaType = resp.Content.Headers.ContentType?.MediaType ?? "";
        var text = mediaType.Contains("html") ? HttpTool.HtmlToText(body) : body;
        var prefix = resp.IsSuccessStatusCode ? "" : "ERRO: ";
        return $"{prefix}HTTP {(int)resp.StatusCode} {uri}\n{Text.Trunc(text, 6000)}";
    }

    // ---------------- arquivos e shell (confinados ao workspace) ----------------

    private static string SafePath(string relative)
    {
        var root = Path.GetFullPath(Env.Workspace);
        var full = Path.GetFullPath(Path.Combine(root, string.IsNullOrWhiteSpace(relative) ? "." : relative));
        if (full != root && !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("caminho fora do workspace nao e permitido.");
        return full;
    }

    private static async Task<string> ReadFile(JsonObject args)
    {
        var path = SafePath(Arg(args, "path"));
        if (!File.Exists(path)) return "ERRO: arquivo nao encontrado.";
        return Text.Trunc(await File.ReadAllTextAsync(path), 8000);
    }

    private static async Task<string> WriteFile(JsonObject args)
    {
        var rel = Arg(args, "path");
        if (rel.Length == 0) return "ERRO: path obrigatorio.";
        var path = SafePath(rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var content = HttpTool.ArgString(args["content"]);
        await File.WriteAllTextAsync(path, content);
        return $"Arquivo salvo: {Path.GetRelativePath(Env.Workspace, path)} ({content.Length} caracteres).";
    }

    private static Task<string> ListFiles(JsonObject args)
    {
        var dir = SafePath(Arg(args, "path"));
        if (!Directory.Exists(dir)) return Task.FromResult("ERRO: diretorio nao encontrado.");
        var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
            .Select(f => $"{Path.GetRelativePath(Env.Workspace, f)} ({new FileInfo(f).Length} bytes)")
            .OrderBy(f => f)
            .Take(200)
            .ToList();
        return Task.FromResult(files.Count == 0 ? "(workspace vazio)" : string.Join("\n", files));
    }

    private static Task<string> DeleteFile(JsonObject args)
    {
        var path = SafePath(Arg(args, "path"));
        if (!File.Exists(path)) return Task.FromResult("ERRO: arquivo nao encontrado.");
        File.Delete(path);
        return Task.FromResult("Arquivo apagado.");
    }

    private static async Task<string> RunShell(JsonObject args, CancellationToken ct)
    {
        var command = Arg(args, "command");
        if (command.Length == 0) return "ERRO: command obrigatorio.";

        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var psi = new ProcessStartInfo(isWindows ? "cmd.exe" : "/bin/bash")
        {
            WorkingDirectory = Env.Workspace,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        psi.ArgumentList.Add(isWindows ? "/c" : "-c");
        psi.ArgumentList.Add(command);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("falha ao iniciar o shell.");
        var stdout = proc.StandardOutput.ReadToEndAsync();
        var stderr = proc.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await proc.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
            return "ERRO: timeout de 30s excedido; processo encerrado.";
        }
        var output = await stdout;
        var error = await stderr;
        var prefix = proc.ExitCode == 0 ? "" : "ERRO: ";
        return $"{prefix}exit_code={proc.ExitCode}\n--- stdout ---\n{Text.Trunc(output, 4000)}\n--- stderr ---\n{Text.Trunc(error, 2000)}";
    }
}
