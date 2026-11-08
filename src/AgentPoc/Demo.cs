using System.Text.RegularExpressions;
using AgentPoc.Core;
using AgentPoc.Evidence;
using AgentPoc.Harness;
using AgentPoc.Human;
using AgentPoc.Workflows;

namespace AgentPoc;

/// <summary>Cenarios de demonstracao. Cada um gera uma pasta de evidencia com checagens automaticas.</summary>
public static class Demo
{
    private delegate Task ScenarioFn(App app, EvidenceSession s, CancellationToken ct);

    private static readonly (string Id, string Title, string Proves, ScenarioFn Run)[] Scenarios =
    {
        ("autonomia", "Agente autonomo: descoberta de ferramentas, delegacao e execucao real",
            "O orquestrador recebe apenas um objetivo de negocio. Ele descobre a equipe, delega para especialistas, " +
            "que descobrem em runtime as ferramentas de que precisam (search_tools), executam codigo de verdade, consultam a base interna " +
            "e uma API HTTP cadastrada so no banco, e o resultado e consolidado em um arquivo.", new ScenarioFn(Autonomia)),
        ("workflow", "Workflow deterministico com paralelismo e checkpoint",
            "Um workflow definido em JSON no banco mistura no de ferramenta e nos de agente, roda dois ramos em paralelo, " +
            "faz join e grava checkpoint por no.", new ScenarioFn(Workflow)),
        ("fila", "Delegacao assincrona via fila com workers concorrentes",
            "O orquestrador cria tarefas na fila (estilo ticket) sem esperar. Tres workers concorrentes pegam as tarefas com " +
            "FOR UPDATE SKIP LOCKED, executam os agentes e gravam o resultado.", new ScenarioFn(Fila)),
        ("governanca", "Governanca: politicas de ferramenta e budget de tokens",
            "Mesmo com autonomia, o harness impoe limites: ferramenta com politica approval e negada quando o modo de aprovacao nega, " +
            "ferramenta deny nunca executa, e um run com budget pequeno e interrompido com status budget_exceeded.", new ScenarioFn(Governanca)),
        ("retomada", "Execucao duravel: queda simulada e retomada do workflow",
            "O processo cai de proposito no meio do workflow. A retomada le os checkpoints, reaproveita os nos ja concluidos " +
            "sem reexecutar e termina o restante.", new ScenarioFn(Retomada)),
        ("chat", "Chat multi-turno: pesquisa, plano, delegacao, memoria da conversa, aprovacao e compactacao",
            "Uma conversa de cinco turnos com o agente assistant. Ele decide sozinho pesquisar na web, planejar e delegar o calculo, " +
            "lembra do resultado em outro turno, pede aprovacao para o shell (negada), mantem as ferramentas descobertas entre turnos, " +
            "transmite a resposta em streaming e resume os turnos antigos quando o historico passa do limite da janela.", new ScenarioFn(Chat)),
        ("hitl", "Human-in-the-loop: pergunta, aprovar com edicao, negar com motivo, regras por argumento e espera duravel",
            "O agente pergunta a pessoa (ask_human) e o run fica suspenso sem processo preso; outra instancia da aplicacao (simulando " +
            "um restart) grava a resposta e retoma o MESMO run. A pessoa corrige os argumentos antes de aprovar, nega com motivo (o agente " +
            "recebe o motivo), regras por argumento bloqueiam comando perigoso sem perguntar e exigem aprovacao para URL interna, um subagente " +
            "espera a decisao em memoria, e uma pendencia expira quando a pessoa segue a conversa.", new ScenarioFn(Hitl)),
    };

    public static IEnumerable<string> Names => Scenarios.Select(s => s.Id);

    public static async Task<bool> RunAsync(App app, string name, CancellationToken ct)
    {
        var selected = name == "all" ? Scenarios : Scenarios.Where(s => s.Id == name).ToArray();
        if (selected.Length == 0)
        {
            Log.Error($"Cenario '{name}' desconhecido. Opcoes: {string.Join(", ", Names)}, all");
            return false;
        }

        var results = new List<(string Id, int Passed, int Total, string Folder)>();
        foreach (var sc in selected)
        {
            Log.Title($"CENARIO {sc.Id}: {sc.Title}");
            var session = new EvidenceSession { Scenario = sc.Id, Title = sc.Title, Proves = sc.Proves };
            var previousMode = Approval.Mode;
            try
            {
                await sc.Run(app, session, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                session.Notes.Add($"Cenario interrompido por erro: {ex.GetType().Name}: {ex.Message}");
                Log.Error($"Cenario {sc.Id} interrompido: {ex.Message}");
            }
            finally
            {
                Approval.Mode = previousMode;
            }
            var (folder, passed, total) = await app.Evidence.WriteAsync(session);
            results.Add((sc.Id, passed, total, folder));
        }

        Log.Title("RESUMO DA DEMO");
        foreach (var r in results)
            Log.Write(0, r.Passed == r.Total ? ConsoleColor.Green : ConsoleColor.Red, r.Passed == r.Total ? "OK" : "FALHAS",
                $"{r.Id}: {r.Passed}/{r.Total} checagens | {Path.GetRelativePath(Env.RepoRoot, r.Folder)}/timeline.md");
        Log.Info($"Indice: {Path.GetRelativePath(Env.RepoRoot, Path.Combine(Env.EvidenceDir, "INDEX.md"))}");
        return results.All(r => r.Passed == r.Total);
    }

    private static void Clean(params string[] files)
    {
        foreach (var f in files)
        {
            var p = Path.Combine(Env.Workspace, f);
            if (File.Exists(p)) File.Delete(p);
        }
    }

    // ------------------------------------------------------------------

    private static async Task Autonomia(App app, EvidenceSession s, CancellationToken ct)
    {
        Approval.Mode = "auto";
        Clean("relatorio_final.md", "custo.py");
        s.Files.Add("relatorio_final.md");
        s.Files.Add("custo.py");
        s.Checks.Add(new Check("Run raiz do orquestrador concluido", d => d.FirstRoot("orchestrator")?.Status == "completed"));
        s.Checks.Add(new Check("Orquestrador delegou para pelo menos 2 especialistas diferentes", d => d.DelegatedTo.Count >= 2));
        s.Checks.Add(new Check("Houve descoberta de ferramenta em runtime (search_tools carregou ao menos uma)", d => d.DiscoveredTools.Count > 0));
        s.Checks.Add(new Check("Codigo executado de verdade (run_shell com sucesso)", d => d.ToolSucceeded("run_shell")));
        s.Checks.Add(new Check("Saida real do script contem o custo esperado (92,50)", d => d.ToolOutputMatches("run_shell", @"92[\.,]5")));
        s.Checks.Add(new Check("Base de conhecimento interna consultada (kb_search)", d => d.ToolSucceeded("kb_search")));
        s.Checks.Add(new Check("Ferramenta HTTP cadastrada so no banco foi usada (weather_forecast)", d => d.ToolSucceeded("weather_forecast")));
        s.Checks.Add(new Check("relatorio_final.md gerado e contem o custo calculado", d => SessionData.FileMatches("relatorio_final.md", @"92[\.,]5")));

        const string prompt =
            "Objetivo: entregar o arquivo relatorio_final.md no workspace, com tres secoes.\n" +
            "1) Governanca: explique, com base na base de conhecimento interna da empresa, como funcionam a politica de aprovacao de ferramentas e o budget de tokens dos agentes.\n" +
            "2) Custo estimado: um script Python chamado custo.py deve calcular o custo de 1000 execucoes de agente, cada uma com 25000 tokens de entrada e 3000 de saida, " +
            "a US$ 2,50 por milhao de tokens de entrada e US$ 10,00 por milhao de saida. O script deve ser executado e o valor real impresso deve aparecer no relatorio.\n" +
            "3) Clima: condicoes atuais do tempo em Sao Paulo (latitude -23.55, longitude -46.63).\n" +
            "Delegue para os especialistas adequados e consolide tudo no arquivo.";
        var r = await app.Runner.RunAsync("orchestrator", prompt, ct: ct);
        s.RootRuns.Add(r.RunId);
    }

    private static async Task Workflow(App app, EvidenceSession s, CancellationToken ct)
    {
        Approval.Mode = "auto";
        Clean("relatorio_workflow.md");
        s.Files.Add("relatorio_workflow.md");
        s.Checks.Add(new Check("Workflow concluido", d => d.WorkflowRuns.Count == 1 && d.WorkflowRuns.Values.All(w => w.Status == "completed")));
        s.Checks.Add(new Check("Os 5 nos foram concluidos", d => d.Nodes.Count == 5 && d.Nodes.All(n => n.Status == "completed")));
        s.Checks.Add(new Check("No de ferramenta (kb_search) executado com auditoria", d => d.ToolSucceeded("kb_search")));
        s.Checks.Add(new Check("Ramos 'resumir' e 'riscos' rodaram em paralelo", d => d.NodesOverlapped("resumir", "riscos")));
        s.Checks.Add(new Check("'consolidar' so comecou depois dos dois ramos (join)", d =>
        {
            var c = d.Nodes.FirstOrDefault(n => n.Node == "consolidar");
            var a = d.Nodes.FirstOrDefault(n => n.Node == "resumir");
            var b = d.Nodes.FirstOrDefault(n => n.Node == "riscos");
            return c != null && a?.Finished != null && b?.Finished != null && c.Started >= a.Finished.Value && c.Started >= b.Finished.Value;
        }));
        s.Checks.Add(new Check("relatorio_workflow.md gerado", _ => SessionData.FileExists("relatorio_workflow.md")));

        Guid wfId = Guid.Empty;
        try
        {
            var r = await app.Workflows.RunAsync("relatorio_tecnico", "execucao duravel, retomada e budget de agentes", ct: ct);
            wfId = r.WorkflowRunId;
        }
        finally
        {
            if (wfId == Guid.Empty)
            {
                var last = await app.Db.ScalarAsync("select id from workflow_runs order by started_at desc limit 1");
                if (last is Guid g) wfId = g;
            }
            if (wfId != Guid.Empty) s.WorkflowRuns.Add(wfId);
        }
    }

    private static async Task Fila(App app, EvidenceSession s, CancellationToken ct)
    {
        Approval.Mode = "auto";
        Clean("primos.py", "haiku.md");
        s.Files.Add("haiku.md");
        s.Checks.Add(new Check("Orquestrador criou 3 tarefas na fila", d => d.Tasks.Count >= 3));
        s.Checks.Add(new Check("Orquestrador nao esperou: nao usou delegate_task", d => !d.ToolCalled("delegate_task")));
        s.Checks.Add(new Check("Todas as tarefas concluidas (status done)", d => d.Tasks.Count > 0 && d.Tasks.All(t => t.Status == "done")));
        s.Checks.Add(new Check("Tarefas processadas por mais de um worker (concorrencia)", d => d.Tasks.Select(t => t.Worker).Distinct().Count() >= 2));
        s.Checks.Add(new Check("Coder executou o script e a saida tem os primos ate 29", d => d.ToolOutputMatches("run_shell", @"\b29\b")));
        s.Checks.Add(new Check("Researcher consultou o CEP via ferramenta HTTP (cep_lookup)", d => d.ToolSucceeded("cep_lookup")));
        s.Checks.Add(new Check("Writer gerou haiku.md", _ => SessionData.FileExists("haiku.md")));

        const string prompt =
            "Distribua via create_task (assincrono; NAO use delegate_task e nao espere os resultados) estas tres tarefas independentes:\n" +
            "(a) para researcher: consultar o endereco do CEP 01310100 e informar logradouro e bairro;\n" +
            "(b) para coder: criar primos.py que imprime os 10 primeiros numeros primos, executar e informar a saida;\n" +
            "(c) para writer: escrever um haiku original sobre agentes autonomos e salvar em haiku.md.\n" +
            "Responda apenas com os ids das tarefas criadas.";
        var r = await app.Runner.RunAsync("orchestrator", prompt, ct: ct);
        s.RootRuns.Add(r.RunId);
        s.Notes.Add("Orquestrador terminou sem esperar; iniciando 3 workers concorrentes para drenar a fila.");
        var processed = await app.Worker.RunAsync(3, untilEmpty: true, ct);
        s.Notes.Add($"Workers processaram {processed} tarefa(s).");
    }

    private static async Task Governanca(App app, EvidenceSession s, CancellationToken ct)
    {
        const string target = "governanca_alvo.txt";
        await File.WriteAllTextAsync(Path.Combine(Env.Workspace, target), "arquivo que NAO pode ser apagado\n", ct);
        s.Checks.Add(new Check("run_shell (politica approval) foi NEGADO pelo modo de aprovacao", d => d.PolicyDecision("run_shell", "denied")));
        s.Checks.Add(new Check("run_shell nunca executou", d => !d.ToolSucceeded("run_shell")));
        s.Checks.Add(new Check("delete_file (politica deny) foi BLOQUEADO", d => d.PolicyDecision("delete_file", "blocked")));
        s.Checks.Add(new Check("Arquivo alvo continua existindo", _ => SessionData.FileExists(target)));
        s.Checks.Add(new Check("Agente lidou com as recusas e concluiu o run (sem travar)", d => d.FirstRoot("coder")?.Status == "completed"));
        s.Checks.Add(new Check("Run com budget pequeno foi interrompido com budget_exceeded", d => d.FirstRoot("orchestrator")?.Status == "budget_exceeded"));

        Approval.Mode = "deny";
        s.Notes.Add("Parte A: APPROVAL_MODE=deny para o agente coder.");
        var a = await app.Runner.RunAsync("coder",
            $"Execute o comando 'ls -la' com a ferramenta run_shell para listar o workspace e depois apague o arquivo {target} com a ferramenta delete_file. " +
            "Relate exatamente o que aconteceu com cada acao.", ct: ct);
        s.RootRuns.Add(a.RunId);

        Approval.Mode = "auto";
        s.Notes.Add("Parte B: orquestrador com budget forcado de 600 tokens.");
        var b = await app.Runner.RunAsync("orchestrator",
            "Pesquise a fundo a base de conhecimento e a web sobre arquitetura de agentes autonomos, delegue para todos os especialistas e produza um relatorio detalhado.",
            budgetOverride: 600, ct: ct);
        s.RootRuns.Add(b.RunId);
    }

    private static async Task Retomada(App app, EvidenceSession s, CancellationToken ct)
    {
        Approval.Mode = "auto";
        Clean("relatorio_workflow.md");
        s.Files.Add("relatorio_workflow.md");
        var wfId = Guid.Empty;
        var crashedRecorded = false;
        var reused = new List<string>();

        s.Checks.Add(new Check("Queda simulada gravou status 'crashed' no banco", _ => crashedRecorded));
        s.Checks.Add(new Check("Retomada reaproveitou 'contexto' e 'pesquisar' do checkpoint", _ => reused.Contains("contexto") && reused.Contains("pesquisar")));
        s.Checks.Add(new Check("Nenhum no foi executado duas vezes (attempt = 1 em todos)", d => d.Nodes.Count == 5 && d.Nodes.All(n => n.Attempt == 1)));
        s.Checks.Add(new Check("Agente do no 'pesquisar' rodou uma unica vez", d => d.Runs.Values.Count(x => x.Agent == "researcher" && x.Input.StartsWith("Tema:")) == 1));
        s.Checks.Add(new Check("Workflow terminou como completed apos a retomada", d => d.WorkflowRuns.Values.All(w => w.Status == "completed") && d.WorkflowRuns.Count == 1));
        s.Checks.Add(new Check("relatorio_workflow.md gerado", _ => SessionData.FileExists("relatorio_workflow.md")));

        try
        {
            await app.Workflows.RunAsync("relatorio_tecnico", "politica de aprovacao e descoberta de ferramentas", crashAfter: "pesquisar", ct: ct);
            s.Notes.Add("ATENCAO: o workflow nao caiu como esperado.");
        }
        catch (SimulatedCrashException ex)
        {
            wfId = ex.WorkflowRunId;
            s.WorkflowRuns.Add(wfId);
            Log.Warn(ex.Message);
            var status = (string?)await app.Db.ScalarAsync("select status from workflow_runs where id = @id", ("id", wfId));
            crashedRecorded = status == "crashed";
            var done = await app.Db.QueryAsync("select node_id from workflow_node_runs where workflow_run_id = @id and status = 'completed' order by started_at", ("id", wfId));
            s.Notes.Add($"Processo caiu de proposito apos o no 'pesquisar' (workflow run {Text.Short(wfId)}). Status no banco: {status}. " +
                        $"Nos com checkpoint: {string.Join(", ", done.Select(d => d.S("node_id")))}.");
        }

        if (wfId == Guid.Empty) return;
        Log.Info("Simulando reinicio do processo: retomando a partir do banco...");
        var r = await app.Workflows.RunAsync("relatorio_tecnico", null, resumeRunId: wfId, ct: ct);
        reused.AddRange(r.ReusedNodes);
        s.Notes.Add($"Retomada concluiu com status {r.Status}, reaproveitando: {string.Join(", ", r.ReusedNodes)}.");
    }

    /// <summary>Janela de contexto reduzida so durante o cenario, para a compactacao acontecer em poucos turnos.</summary>
    private const int ChatScenarioContextWindow = 1000;

    private static async Task Chat(App app, EvidenceSession s, CancellationToken ct)
    {
        Clean("custo.py");
        s.Files.Add("custo.py");
        var model = (await app.Config.GetAgentAsync("assistant"))!.ModelId;
        var originalWindow = Convert.ToInt32(await app.Db.ScalarAsync("select context_window from models where id = @m", ("m", model)));
        var stream = new CountingObserver();
        var hooks = new RunHooks(stream, ModeHumanGate.Instance);
        var turns = new List<RunResult>();
        var conversation = await app.Chat.StartAsync("assistant");
        AgentPoc.Chat.Conversation? final = null;

        s.Checks.Add(new Check("Os 5 turnos terminaram como completed", _ => turns.Count == 5 && turns.All(t => t.Status == "completed")));
        s.Checks.Add(new Check("Turno 1: o agente decidiu pesquisar na web (web_search com sucesso) e citou a fonte",
            d => d.ToolSucceeded("web_search") && turns.Count > 0 && turns[0].Output.Contains("https://")));
        s.Checks.Add(new Check("Turno 2: registrou um plano (update_plan) e delegou o calculo ao coder",
            d => d.OfType("plan_updated").Any() && d.DelegatedTo.Contains("coder")));
        s.Checks.Add(new Check("Turno 2: o script rodou de verdade e a resposta trouxe o total (92,50)",
            d => d.ToolOutputMatches("run_shell", @"92[\.,]5") && turns.Count > 1 && Regex.IsMatch(turns[1].Output, @"92[\.,]5")));
        s.Checks.Add(new Check("Turno 3: respondeu o total pelo historico da conversa, sem chamar ferramentas",
            _ => turns.Count > 2 && turns[2].Steps == 1 && Regex.IsMatch(turns[2].Output, @"92[\.,]5")));
        s.Checks.Add(new Check("Turno 4: descobriu run_shell e a aprovacao foi negada", d => d.PolicyDecision("run_shell", "denied")));
        s.Checks.Add(new Check("Ferramenta descoberta (run_shell) ficou salva na conversa para os proximos turnos",
            _ => final?.ActiveTools.Contains("run_shell") == true));
        s.Checks.Add(new Check("Turnos antigos viraram resumo, e o resumo guarda o total calculado",
            _ => final?.SummarizedUpto > 0 && Regex.IsMatch(final.Summary ?? "", @"92[\.,]5")));
        s.Checks.Add(new Check("Turno 5 (depois da compactacao) responde o total sem chamar ferramentas", _ => turns.Count > 4 && turns[4].Steps == 1 && Regex.IsMatch(turns[4].Output, @"92[\.,]5")));
        s.Checks.Add(new Check("Respostas chegaram em streaming (deltas de texto ao vivo)", _ => stream.TextDeltas > 10));

        await app.Db.ExecAsync("update models set context_window = @w where id = @m", ("w", ChatScenarioContextWindow), ("m", model));
        try
        {
            async Task Turn(string text, string approvalMode)
            {
                Approval.Mode = approvalMode;
                Log.Write(0, ConsoleColor.Cyan, "usuario", text);
                var r = await app.Chat.SendAsync(conversation, text, hooks, ct);
                turns.Add(r);
                s.RootRuns.Add(r.RunId);
                Log.Write(0, ConsoleColor.Green, "assistente", r.Output);
            }

            await Turn("pesquise na web sobre durable execution", "auto");
            await Turn("calcule com um script o custo de 1000 execucoes de agente", "auto");
            await Turn("qual foi o total mesmo?", "auto");
            await Turn("liste os arquivos do workspace usando o shell", "deny");
            await Turn("lembra qual foi o total do script?", "auto");
        }
        finally
        {
            await app.Db.ExecAsync("update models set context_window = @w where id = @m", ("w", originalWindow), ("m", model));
            final = await app.Chat.Store.GetAsync(conversation);
        }
        s.Notes.Add($"Conversa {conversation}: {turns.Count} turnos, resumo ate a mensagem #{final?.SummarizedUpto}, " +
                    $"ferramentas ativas: {string.Join(", ", final?.ActiveTools ?? Array.Empty<string>())}.");
    }

    private static async Task Hitl(App app, EvidenceSession s, CancellationToken ct)
    {
        Approval.Mode = "auto";
        Clean("custo.py");
        var hooks = new RunHooks(null, app.HumanGate);
        var conversation = await app.Chat.StartAsync("assistant");
        var results = new Dictionary<string, RunResult>();
        var suspended = new Dictionary<string, RunResult>();
        var decided = new Dictionary<string, HumanRequest>();
        var subagentDecidedInMemory = false;
        HumanRequest? discarded = null;
        string? discardedRunStatus = null;

        s.Checks.Add(new Check("ask_human suspendeu o run (waiting_human) sem segurar processo",
            _ => suspended.TryGetValue("pergunta", out var w) && w.Status == "waiting_human" && w.WaitingFor != null));
        s.Checks.Add(new Check("Outra instancia da aplicacao (restart simulado) gravou a resposta e retomou o MESMO run ate completed",
            _ => results.TryGetValue("pergunta", out var r) && r.Status == "completed" && r.RunId == suspended["pergunta"].RunId &&
                 r.Output.Contains("markdown")));
        s.Checks.Add(new Check("Pessoa corrigiu os argumentos antes de aprovar e o comando executado foi o corrigido (ls -la)",
            d => d.OfType("policy").Any(e => e.Str("tool") == "run_shell" && e.Bool("edited")) &&
                 d.OfType("tool_result").Any(e => e.Str("name") == "run_shell" && e.Bool("ok") && e.Str("output").Contains("total "))));
        s.Checks.Add(new Check("Negacao com motivo: o agente recebeu o motivo e o repassou",
            _ => results.TryGetValue("negar", out var r) && r.Output.Contains("auditoria")));
        s.Checks.Add(new Check("Regra deny_when bloqueou 'rm -rf' sem nem perguntar a pessoa",
            d => d.OfType("policy").Any(e => e.Str("tool") == "run_shell" && e.Str("decision") == "blocked" && e.Str("reason").Contains("deny_when")) &&
                 !suspended.ContainsKey("apagar")));
        s.Checks.Add(new Check("Regra approval_when exigiu aprovacao para http_get em URL interna (politica normal e auto)",
            d => suspended.ContainsKey("interna") && d.OfType("policy").Any(e => e.Str("tool") == "http_get" && e.Str("decision") == "approved" &&
                 e.Str("policy_reason").Contains("approval_when")) && d.ToolSucceeded("http_get")));
        s.Checks.Add(new Check("Subagente (coder) esperou a aprovacao em memoria e o turno terminou com o valor real (92,50)",
            _ => subagentDecidedInMemory && results.TryGetValue("subagente", out var r) && Regex.IsMatch(r.Output, @"92[\.,]5")));
        s.Checks.Add(new Check("Pessoa seguiu a conversa sem decidir: pedido expirou, turno suspenso encerrado e a conversa continuou",
            _ => discarded?.Status == "expired" && discardedRunStatus == "cancelled" &&
                 results.TryGetValue("seguiu", out var r) && r.Status == "completed"));
        s.Checks.Add(new Check("Toda decisao ficou registrada em approvals com quem/como foi decidida",
            _ => decided.Count >= 4 && decided.Values.All(r => r.Status != "pending" && r.Reason != null || r.Answer != null)));

        async Task<RunResult> Send(string key, string text)
        {
            Log.Write(0, ConsoleColor.Cyan, "pessoa", text);
            var r = await app.Chat.SendAsync(conversation, text, hooks, ct);
            s.RootRuns.Add(r.RunId);
            Log.Write(0, r.Status == "waiting_human" ? ConsoleColor.Yellow : ConsoleColor.Green, r.Status, r.Output);
            (r.Status == "waiting_human" ? suspended : results)[key] = r;
            return r;
        }

        async Task Decide(App via, string key, HumanDecision decision)
        {
            var requestId = suspended[key].WaitingFor!.Value;
            var outcome = await via.Chat.DecideAsync(requestId, decision);
            decided[key] = (await via.Chat.Human.GetAsync(requestId))!;
            Log.Write(0, ConsoleColor.Magenta, "decisao", $"{decided[key].Status}: {decided[key].Reason ?? decided[key].Answer}");
            if (!outcome.NeedsResume) return;
            var resumed = await via.Chat.ResumeAsync(conversation, hooks with { Human = via.HumanGate }, ct);
            Log.Write(0, ConsoleColor.Green, resumed.Status, resumed.Output);
            results[key] = resumed;
        }

        await Send("pergunta", "prepare um relatorio sobre os agentes da plataforma");
        s.Notes.Add("Run suspenso esperando a resposta. Uma NOVA instancia da aplicacao (como apos um restart) registra a resposta e retoma.");
        await using (var afterRestart = new App(app.ConnectionString))
            await Decide(afterRestart, "pergunta", new HumanDecision(null, null, null, "markdown"));

        await Send("editar", "liste os arquivos usando o shell");
        await Decide(app, "editar", new HumanDecision(true, "quero ver detalhes", new System.Text.Json.Nodes.JsonObject { ["command"] = "ls -la" }, null));

        await Send("negar", "liste os arquivos usando o shell");
        await Decide(app, "negar", new HumanDecision(false, "agora nao, estou em auditoria", null, null));

        await Send("apagar", "apague tudo do workspace");

        await Send("interna", "baixe http://127.0.0.1:8765/fake/cep/01310100");
        if (suspended.ContainsKey("interna")) await Decide(app, "interna", new HumanDecision(true, null, null, null));

        var subagentTurn = app.Chat.SendAsync(conversation, "calcule com um script o custo de 1000 execucoes de agente", hooks, ct);
        while (!subagentTurn.IsCompleted)
        {
            if ((await app.Chat.Human.PendingAsync(conversation)).FirstOrDefault() is { } pending)
            {
                var outcome = await app.Chat.DecideAsync(pending.Id, new HumanDecision(true, null, null, null));
                subagentDecidedInMemory = !outcome.NeedsResume;
                decided["subagente"] = (await app.Chat.Human.GetAsync(pending.Id))!;
                break;
            }
            await Task.Delay(100, ct);
        }
        var sub = await subagentTurn;
        s.RootRuns.Add(sub.RunId);
        results["subagente"] = sub;

        var left = await Send("pendente", "liste os arquivos usando o shell");
        var leftRequest = left.WaitingFor;
        await Send("seguiu", "deixa pra la, qual foi o total mesmo?");
        if (leftRequest is { } id) discarded = await app.Chat.Human.GetAsync(id);
        discardedRunStatus = (string?)await app.Db.ScalarAsync("select status from runs where id = @id", ("id", left.RunId));
        s.Notes.Add($"Conversa {conversation}.");
    }

    private sealed class CountingObserver : IRunObserver
    {
        public int TextDeltas { get; private set; }
        public void OnTextDelta(RunContext ctx, string delta) => TextDeltas++;
        public void OnReasoningDelta(RunContext ctx, string delta) { }
        public void OnEvent(RunContext ctx, string type, System.Text.Json.Nodes.JsonObject data) { }
    }
}
