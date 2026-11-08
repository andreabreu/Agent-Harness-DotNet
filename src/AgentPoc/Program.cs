using System.Text.Json.Nodes;
using AgentPoc;
using AgentPoc.Chat;
using AgentPoc.Core;
using AgentPoc.Evidence;
using AgentPoc.Harness;
using AgentPoc.Llm;

Env.Init();
Console.OutputEncoding = System.Text.Encoding.UTF8;

var valueOptions = new HashSet<string> { "approval", "budget", "crash-after", "resume", "concurrency", "agent", "conversation", "port" };
string? Opt(string name)
{
    for (var i = 0; i < args.Length; i++)
    {
        if (args[i] == "--" + name && i + 1 < args.Length) return args[i + 1];
        if (args[i].StartsWith("--" + name + "=")) return args[i].Substring(name.Length + 3);
    }
    return null;
}
bool Flag(string name) => args.Contains("--" + name);
var positional = new List<string>();
for (var i = 0; i < args.Length; i++)
{
    if (args[i].StartsWith("--"))
    {
        var key = args[i].Substring(2);
        if (valueOptions.Contains(key)) i++;
        continue;
    }
    positional.Add(args[i]);
}

var command = positional.Count > 0 ? positional[0] : "help";
var connection = Env.Get("AGENTS_PG", "Host=localhost;Port=5433;Username=agents;Password=agents;Database=agents");
Approval.Mode = Opt("approval") ?? Env.Get("APPROVAL_MODE", command is "task" or "chat" ? "ask" : "auto");

using var cts = new CancellationTokenSource();
Func<bool>? cancelTurn = null;
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    if (cancelTurn?.Invoke() == true)
    {
        Log.Warn("Turno cancelado (Ctrl+C de novo encerra o chat).");
        return;
    }
    Log.Warn("Cancelando (Ctrl+C)...");
    cts.Cancel();
};

await using var app = new App(connection);

try
{
    switch (command)
    {
        case "setup":
            await Setup(Flag("reset"));
            break;
        case "check":
            return await CheckModels() ? 0 : 1;
        case "agents":
            foreach (var a in await app.Config.ListAgentsAsync())
                Log.Raw($"{a.Id,-13} v{a.Version}  modelo={a.ModelId,-16} passos={a.MaxSteps,-3} budget={a.MaxTokens,-7:N0} iniciais=[{string.Join(",", a.InitialTools)}] escopo=[{string.Join(",", a.AllowedTools)}] delega=[{string.Join(",", a.CanDelegateTo)}]");
            break;
        case "tools":
            foreach (var t in await app.Registry.ListAsync())
                Log.Raw($"{t.Name,-18} {t.Kind,-8} {t.Policy,-9} {Text.Trunc(t.Description, 90)}");
            break;
        case "sync-mcp":
            Log.Ok($"{await app.Mcp.SyncAsync(cts.Token)} ferramentas MCP sincronizadas.");
            break;
        case "task":
        {
            if (positional.Count < 3) { Help(); return 1; }
            var agent = positional[1];
            var text = string.Join(" ", positional.Skip(2));
            long? budget = long.TryParse(Opt("budget"), out var b) ? b : null;
            var session = new EvidenceSession { Scenario = "task-" + agent, Title = $"Tarefa avulsa para '{agent}'", Proves = "Execucao avulsa via linha de comando." };
            session.Checks.Add(new Check("Run concluido", d => d.Runs.Values.Any(x => x.Parent == null && x.Status == "completed")));
            var r = await app.Runner.RunAsync(agent, text, budgetOverride: budget, ct: cts.Token);
            session.RootRuns.Add(r.RunId);
            Log.Title("RESPOSTA FINAL");
            Log.Raw(r.Output);
            await app.Evidence.WriteAsync(session);
            return r.Status == "completed" ? 0 : 1;
        }
        case "chat":
        {
            if (!await Preflight()) return 1;
            Log.Muted = !Flag("verbose");
            var chat = new ChatCli(app, Opt("agent") ?? "assistant");
            cancelTurn = chat.CancelTurn;
            await chat.RunAsync(Opt("conversation"), startWorkers: !Flag("no-worker"), cts.Token);
            break;
        }
        case "serve":
        {
            if (!await Preflight()) return 1;
            Log.Muted = !Flag("verbose");
            var port = int.TryParse(Opt("port"), out var p) ? p : 5080;
            await new AgentPoc.Web.ChatServer(app).RunAsync(port, cts.Token);
            break;
        }
        case "workflow":
        {
            Guid? resume = Guid.TryParse(Opt("resume"), out var g) ? g : null;
            if (resume == null && positional.Count < 3) { Help(); return 1; }
            var id = positional.Count > 1 ? positional[1] : "";
            var input = positional.Count > 2 ? string.Join(" ", positional.Skip(2)) : null;
            var session = new EvidenceSession { Scenario = "workflow-" + (id.Length > 0 ? id : "resume"), Title = $"Workflow {id}", Proves = "Execucao avulsa de workflow." };
            session.Checks.Add(new Check("Workflow concluido", d => d.WorkflowRuns.Values.All(w => w.Status == "completed")));
            try
            {
                var r = await app.Workflows.RunAsync(id, input, resume, Opt("crash-after"), cts.Token);
                session.WorkflowRuns.Add(r.WorkflowRunId);
                Log.Title("SAIDA DO WORKFLOW");
                Log.Raw(r.Output);
            }
            catch (AgentPoc.Workflows.SimulatedCrashException ex)
            {
                session.WorkflowRuns.Add(ex.WorkflowRunId);
                session.Notes.Add(ex.Message);
                Log.Warn($"{ex.Message} Para retomar: dotnet run -- workflow --resume {ex.WorkflowRunId}");
            }
            await app.Evidence.WriteAsync(session);
            break;
        }
        case "worker":
        {
            var n = int.TryParse(Opt("concurrency"), out var c) ? c : 2;
            var processed = await app.Worker.RunAsync(n, untilEmpty: Flag("once"), cts.Token);
            Log.Ok($"{processed} tarefa(s) processada(s).");
            break;
        }
        case "demo":
        {
            if (!await Preflight()) return 1;
            var ok = await Demo.RunAsync(app, positional.Count > 1 ? positional[1] : "all", cts.Token);
            return ok ? 0 : 2;
        }
        case "report":
        {
            if (positional.Count < 2 || !Guid.TryParse(positional[1], out var id)) { Help(); return 1; }
            var session = new EvidenceSession { Scenario = "report", Title = $"Relatorio de {Text.Short(id)}", Proves = "Relatorio gerado a partir do banco." };
            if (await app.Db.ScalarAsync("select 1 from workflow_runs where id = @id", ("id", id)) != null)
                session.WorkflowRuns.Add(id);
            else if (await app.Db.ScalarAsync("select root_run_id from runs where id = @id", ("id", id)) is Guid root)
                session.RootRuns.Add(root);
            else { Log.Error("Id nao encontrado em runs nem em workflow_runs."); return 1; }
            await app.Evidence.WriteAsync(session);
            break;
        }
        default:
            Help();
            break;
    }
}
catch (OperationCanceledException)
{
    Log.Warn("Operacao cancelada.");
    return 130;
}
catch (Npgsql.NpgsqlException ex)
{
    Log.Error($"Postgres: {ex.Message}");
    Log.Info("O banco esta no ar? Rode 'docker compose up -d' na raiz e depois 'dotnet run -- setup'.");
    return 1;
}
catch (Exception ex)
{
    Log.Error($"{ex.GetType().Name}: {ex.Message}");
    return 1;
}
return 0;

// ------------------------------------------------------------------

async Task Setup(bool reset)
{
    if (reset)
    {
        await app.Db.ExecAsync("drop schema if exists public cascade; create schema public;");
        Log.Warn("Schema recriado do zero (--reset).");
    }
    await app.Db.ExecAsync(await File.ReadAllTextAsync(Path.Combine(Env.SqlDir, "schema.sql")));
    await app.Db.ExecAsync(await File.ReadAllTextAsync(Path.Combine(Env.SqlDir, "seed.sql")));
    var agents = await app.Db.ScalarAsync("select count(*) from agents");
    var tools = await app.Db.ScalarAsync("select count(*) from tools");
    Log.Ok($"Banco pronto: {agents} agentes, {tools} ferramentas, workflows e base de conhecimento carregados.");
}

async Task<bool> Preflight()
{
    var problems = new List<string>();
    foreach (var modelId in (await app.Config.ListAgentsAsync()).Select(a => a.ModelId).Distinct())
        problems.AddRange(ConfigRepo.Problems(await app.Config.GetModelAsync(modelId)).Select(p => $"modelo {modelId}: falta {p}"));
    if (problems.Count == 0) return true;
    foreach (var p in problems) Log.Error(p);
    Log.Info("Preencha o arquivo .env na raiz (veja .env.example) ou troque o model_id dos agentes no banco.");
    return false;
}

async Task<bool> CheckModels()
{
    var all = true;
    foreach (var modelId in (await app.Config.ListAgentsAsync()).Select(a => a.ModelId).Distinct())
    {
        var model = await app.Config.GetModelAsync(modelId);
        var problems = ConfigRepo.Problems(model);
        if (problems.Count > 0)
        {
            all = false;
            Log.Error($"{modelId}: faltando {string.Join("; ", problems)}");
            continue;
        }
        try
        {
            var ping = new ToolSpec("ping", "Ferramenta de teste.", JsonNode.Parse("{\"type\":\"object\",\"properties\":{\"valor\":{\"type\":\"integer\"}},\"required\":[\"valor\"]}")!);
            var resp = await LlmFactory.For(model).CompleteAsync(model,
                new LlmRequest("Voce e um teste de integracao. Responda chamando a ferramenta ping.",
                    new[] { ChatMessage.User("Chame a ferramenta ping com valor 42.") }, new[] { ping }, null), cts.Token);
            var call = resp.ToolCalls.FirstOrDefault();
            if (call?.Name == "ping")
                Log.Ok($"{modelId} ({model.ResolvedName} via {model.Provider.Id}): tool calling OK, args={call.ArgumentsJson}, {resp.InputTokens}->{resp.OutputTokens} tokens, {resp.LatencyMs} ms");
            else
            {
                all = false;
                Log.Warn($"{modelId}: respondeu sem chamar a ferramenta (o modelo suporta tool calling?). Resposta: {Text.Trunc(resp.Content, 200)}");
            }
        }
        catch (Exception ex)
        {
            all = false;
            Log.Error($"{modelId}: {ex.Message}");
        }
    }
    return all;
}

void Help()
{
    Log.Raw("""
    AgentPoc - harness de agentes autonomos (.NET + Postgres + Azure Foundry/Anthropic/local)

    Uso: dotnet run -- <comando> [opcoes]

      setup [--reset]                   cria schema e carrega seed (agentes, tools, workflows, KB)
      check                             valida .env e testa tool calling em cada modelo usado
      agents | tools                    lista configuracao vinda do banco
      sync-mcp                          importa tools dos servidores MCP habilitados
      chat [--agent assistant] [--conversation <id>] [--verbose] [--no-worker]
                                        conversa com um agente (streaming, ferramentas, delegacao, aprovacao)
      serve [--port 5080] [--verbose]   chat web em http://localhost:5080 (API REST + SSE)
      task <agente> "<texto>"           roda um agente (aprovacao interativa por padrao)
           [--budget N] [--approval auto|deny|ask]
      workflow <id> "<entrada>"         roda um workflow  [--crash-after <no>]
      workflow --resume <workflow_run>  retoma um workflow interrompido
      worker [--concurrency N] [--once] processa a fila de tarefas
      demo [autonomia|workflow|fila|governanca|retomada|all]
                                        roda os cenarios e grava evidencias em ./evidence
      report <run_id|workflow_run_id>   gera evidencia de qualquer execucao gravada
    """);
}
