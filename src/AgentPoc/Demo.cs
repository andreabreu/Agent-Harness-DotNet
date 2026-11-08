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
            "suspende de forma duravel junto com o pai, e uma pendencia expira quando a pessoa segue a conversa.", new ScenarioFn(Hitl)),
        ("hitl_sub", "HITL em subagente: a suspensao sobe a cadeia e o despachante retoma de baixo para cima",
            "O orquestrador delega a um subagente que chama ask_human. O filho fica waiting_human e o pai waiting_child, sem processo " +
            "preso. Uma segunda instancia da aplicacao grava a resposta; o despachante retoma o filho, a saida dele vira o tool_result " +
            "do delegate_task no pai, e o pai termina com a resposta do filho.", new ScenarioFn(HitlSub)),
        ("hitl_concorrencia", "Despachante em duas instancias: cada run retomado uma unica vez",
            "Quatro conversas ficam suspensas em subagentes. Duas instancias da aplicacao rodam o despachante ao mesmo tempo; " +
            "FOR UPDATE SKIP LOCKED garante que cada run e retomado por uma so, sem eventos duplicados.", new ScenarioFn(HitlConcorrencia)),
        ("hitl_expira", "Pedido humano expirado: negado como expired e o agente replaneja",
            "O prazo do pedido vence sem resposta. A varredura do despachante nega o pedido como expired, retoma o subagente, " +
            "que replaneja com a opcao mais segura, e o pai termina normalmente.", new ScenarioFn(HitlExpira)),
        ("hitl_cancela", "Cancelar o agente raiz cancela a cadeia suspensa inteira",
            "Com o subagente esperando uma pessoa, o cancelamento do turno marca raiz e filho como cancelled, fecha o pedido " +
            "pendente e nada e retomado depois.", new ScenarioFn(HitlCancela)),
        ("durabilidade_entrega", "Entrega filho -> pai atomica, recuperacao por lease e reconciliacao",
            "Uma queda e injetada no meio da transacao que conclui o filho e acorda o pai: nada fica gravado pela metade. O lease " +
            "do filho vence, outra varredura o recupera (run_recovered), ele conclui com a resposta ja gravada (sem chamar o modelo " +
            "de novo) e o pai termina com um unico tool_result. Um pai deixado em waiting_child com o filho ja terminado (o estado " +
            "que a versao anterior podia produzir) e corrigido pela reconciliacao do despachante.", new ScenarioFn(DurabilidadeEntrega)),
    };

    private static readonly TimeSpan DemoDispatcherSweep = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(60);
    private const string SubagentQuestionPrompt = "Pergunte a pessoa, via especialista, qual ambiente usar no deploy.";

    /// <summary>Roda o cenario com o despachante desta instancia ativo (retoma runs prontos).</summary>
    private static async Task WithDispatcherAsync(App app, CancellationToken ct, Func<Task> body)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var dispatcher = Task.Run(() => app.Dispatcher.RunAsync(2, DemoDispatcherSweep, stop.Token));
        try
        {
            await body();
        }
        finally
        {
            stop.Cancel();
            try { await dispatcher; } catch (OperationCanceledException) { /* encerrado */ }
        }
    }

    /// <summary>Conversa com o orquestrador parada: subagente esperando uma pessoa (waiting_human) e pai em waiting_child.</summary>
    private static async Task<(Guid Conversation, RunResult Root, HumanRequest Request)> SuspendedSubagentAsync(App app, EvidenceSession s, CancellationToken ct)
    {
        var conversation = await app.Chat.StartAsync("orchestrator");
        var root = await app.Chat.SendAsync(conversation, SubagentQuestionPrompt, new RunHooks(null, app.HumanGate), ct);
        s.RootRuns.Add(root.RunId);
        var request = (await app.Chat.Human.PendingAsync(conversation)).Single();
        Log.Write(0, ConsoleColor.Yellow, root.Status, $"raiz {Text.Short(root.RunId)} parada; pedido {Text.Short(request.Id)}: {request.Question}");
        return (conversation, root, request);
    }

    private static async Task<int> CountEventsAsync(App app, Guid runId, string type) =>
        Convert.ToInt32(await app.Db.ScalarAsync("select count(*) from run_events where run_id = @r and type = @t", ("r", runId), ("t", type)));

    private static async Task<string> RunStatusAsync(App app, Guid runId) =>
        (string)(await app.Db.ScalarAsync("select status from runs where id = @id", ("id", runId)))!;

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

    private static Task Hitl(App app, EvidenceSession s, CancellationToken ct) =>
        WithDispatcherAsync(app, ct, () => HitlTurns(app, s, ct));

    private static async Task HitlTurns(App app, EvidenceSession s, CancellationToken ct)
    {
        Approval.Mode = "auto";
        Clean("custo.py");
        var hooks = new RunHooks(null, app.HumanGate);
        var conversation = await app.Chat.StartAsync("assistant");
        var results = new Dictionary<string, RunResult>();
        var suspended = new Dictionary<string, RunResult>();
        var decided = new Dictionary<string, HumanRequest>();
        var subagentSuspendedDurably = false;
        HumanRequest? discarded = null;
        string? discardedRunStatus = null;

        s.Checks.Add(new Check("ask_human suspendeu o run (waiting_human) sem segurar processo",
            _ => suspended.TryGetValue("pergunta", out var w) && w.Status == "waiting_human" && w.WaitingFor != null));
        s.Checks.Add(new Check("Outra instancia da aplicacao (restart simulado) gravou a resposta e o despachante retomou o MESMO run ate completed",
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
        s.Checks.Add(new Check("Subagente (coder) suspendeu de forma duravel com o pai (waiting_child) e a cadeia terminou com o valor real (92,50)",
            _ => subagentSuspendedDurably && results.TryGetValue("subagente", out var r) && Regex.IsMatch(r.Output, @"92[\.,]5")));
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
            var waiting = RunStatus.IsSuspended(r.Status);
            Log.Write(0, waiting ? ConsoleColor.Yellow : ConsoleColor.Green, r.Status, r.Output);
            (waiting ? suspended : results)[key] = r;
            return r;
        }

        async Task Decide(App via, string key, HumanDecision decision)
        {
            var request = (await via.Chat.Human.PendingAsync(conversation)).Single();
            decided[key] = await via.Chat.DecideAsync(request.Id, decision);
            Log.Write(0, ConsoleColor.Magenta, "decisao", $"{decided[key].Status}: {decided[key].Reason ?? decided[key].Answer}");
            var resumed = await app.Chain.WaitUntilSettledAsync(suspended[key].RunId, SettleTimeout, ct);
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

        var sub = await Send("subagente", "calcule com um script o custo de 1000 execucoes de agente");
        subagentSuspendedDurably = sub.Status == RunStatus.WaitingChild && sub.WaitingOnRun is { } coderRun &&
                                   await RunStatusAsync(app, coderRun) == RunStatus.WaitingHuman;
        await Decide(app, "subagente", new HumanDecision(true, null, null, null));

        var left = await Send("pendente", "liste os arquivos usando o shell");
        var leftRequest = left.WaitingFor;
        await Send("seguiu", "deixa pra la, qual foi o total mesmo?");
        if (leftRequest is { } id) discarded = await app.Chat.Human.GetAsync(id);
        discardedRunStatus = (string?)await app.Db.ScalarAsync("select status from runs where id = @id", ("id", left.RunId));
        s.Notes.Add($"Conversa {conversation}.");
    }

    private static Task HitlSub(App app, EvidenceSession s, CancellationToken ct) => WithDispatcherAsync(app, ct, async () =>
    {
        var (conversation, root, request) = await SuspendedSubagentAsync(app, s, ct);
        var childId = root.WaitingOnRun;
        var childStatus = childId is { } c ? await RunStatusAsync(app, c) : "";
        var parentRow = await app.Db.QueryOneAsync("select pending_tool_call_id from runs where id = @id", ("id", root.RunId));
        var delegateCallId = (await app.Chat.Store.LoadMessagesAsync(conversation))
            .SelectMany(m => m.Message.ToolCalls ?? new List<Llm.ToolCall>()).FirstOrDefault(t => t.Name == "delegate_task")?.Id;

        Log.Info("Segunda instancia da aplicacao grava a resposta; o despachante desta instancia retoma a cadeia.");
        await using (var other = new App(app.ConnectionString))
            await other.Chat.DecideAsync(request.Id, new HumanDecision(null, null, null, "producao"));
        var final = await app.Chain.WaitUntilSettledAsync(root.RunId, SettleTimeout, ct);
        Log.Write(0, ConsoleColor.Green, final.Status, final.Output);
        var delivered = (await app.Chat.Store.LoadMessagesAsync(conversation))
            .FirstOrDefault(m => m.Message.Role == "tool" && m.Message.ToolCallId == delegateCallId)?.Message.Content ?? "";
        var childFinal = childId is { } cid ? await RunStatusAsync(app, cid) : "";
        var childResumes = childId is { } c2 ? await CountEventsAsync(app, c2, "run_resumed") : 0;
        var parentResumes = await CountEventsAsync(app, root.RunId, "run_resumed");

        s.Checks.Add(new Check("Subagente pediu uma pessoa e ficou waiting_human", _ => childStatus == RunStatus.WaitingHuman && request.RunId == childId));
        s.Checks.Add(new Check("Pai suspendeu junto: waiting_child apontando para o filho", _ => root.Status == RunStatus.WaitingChild && root.WaitingOnRun == childId));
        s.Checks.Add(new Check("Pai guardou a chamada delegate_task que vai receber o resultado (pending_tool_call_id)",
            _ => delegateCallId != null && parentRow?.SN("pending_tool_call_id") == delegateCallId));
        s.Checks.Add(new Check("Saida do filho virou o tool_result do delegate_task no pai", _ => delivered.Contains("[resultado de researcher") && delivered.Contains("producao")));
        s.Checks.Add(new Check("Filho e pai foram retomados uma vez cada (mesmos runs, de baixo para cima)",
            _ => childResumes == 1 && parentResumes == 1 && childFinal == RunStatus.Completed));
        s.Checks.Add(new Check("Pai terminou com a resposta do filho", _ => final.Status == RunStatus.Completed && final.Output.Contains("producao")));
        s.Notes.Add($"Conversa {conversation}: raiz {root.RunId}, filho {childId}.");
    });

    private const int ConcurrentConversations = 4;

    private static async Task HitlConcorrencia(App app, EvidenceSession s, CancellationToken ct)
    {
        var chains = new List<(Guid Conversation, RunResult Root, HumanRequest Request)>();
        for (var i = 0; i < ConcurrentConversations; i++) chains.Add(await SuspendedSubagentAsync(app, s, ct));

        await using var instanceA = new App(app.ConnectionString);
        await using var instanceB = new App(app.ConnectionString);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var dispatchers = new[]
        {
            Task.Run(() => instanceA.Dispatcher.RunAsync(2, DemoDispatcherSweep, stop.Token)),
            Task.Run(() => instanceB.Dispatcher.RunAsync(2, DemoDispatcherSweep, stop.Token))
        };
        await Task.WhenAll(chains.Select(c => app.Chat.DecideAsync(c.Request.Id, new HumanDecision(null, null, null, "homologacao"))));
        var finals = new List<RunResult>();
        foreach (var c in chains) finals.Add(await app.Chain.WaitUntilSettledAsync(c.Root.RunId, SettleTimeout, ct));
        stop.Cancel();
        try { await Task.WhenAll(dispatchers); } catch (OperationCanceledException) { /* encerrado */ }

        var runIds = chains.SelectMany(c => new[] { c.Root.RunId, c.Root.WaitingOnRun!.Value }).ToArray();
        var resumes = await app.Db.QueryAsync(
            "select run_id, count(*) as n, min(data->>'by') as by_min, max(data->>'by') as by_max from run_events where run_id = any(@ids) and type = 'run_resumed' group by run_id",
            ("ids", runIds));
        var duplicatedSeq = Convert.ToInt32(await app.Db.ScalarAsync(
            "select count(*) from (select run_id, seq from run_events where run_id = any(@ids) group by run_id, seq having count(*) > 1) d", ("ids", runIds)));
        var deliveries = Convert.ToInt32(await app.Db.ScalarAsync(
            "select count(*) from conversation_messages where conversation_id = any(@c) and role = 'tool' and tool_name = 'delegate_task'",
            ("c", chains.Select(c => c.Conversation).ToArray())));
        var instances = resumes.SelectMany(r => new[] { r.S("by_min"), r.S("by_max") }).Distinct().ToList();

        s.Checks.Add(new Check($"As {ConcurrentConversations} cadeias terminaram como completed", _ => finals.All(f => f.Status == RunStatus.Completed)));
        s.Checks.Add(new Check("Cada run (pai e filho) foi retomado exatamente uma vez",
            _ => resumes.Count == runIds.Length && resumes.All(r => r.I("n") == 1)));
        s.Checks.Add(new Check("Nenhum evento duplicado (run_id, seq) nos run_events", _ => duplicatedSeq == 0));
        s.Checks.Add(new Check("Cada pai recebeu o resultado do filho uma unica vez", _ => deliveries == ConcurrentConversations));
        s.Notes.Add($"Instancias que retomaram runs: {string.Join(", ", instances)} (despachantes: {instanceA.Dispatcher.Name}, {instanceB.Dispatcher.Name}).");
    }

    private static Task HitlExpira(App app, EvidenceSession s, CancellationToken ct) => WithDispatcherAsync(app, ct, async () =>
    {
        var (conversation, root, request) = await SuspendedSubagentAsync(app, s, ct);
        s.Notes.Add("O prazo (expires_at, padrao 24 h) e antecipado para agora, simulando o tempo passando sem resposta.");
        await app.Db.ExecAsync("update approvals set expires_at = now() - interval '1 second' where id = @id", ("id", request.Id));
        while ((await app.Chat.Human.GetAsync(request.Id))?.Status == "pending") await Task.Delay(100, ct);
        var final = await app.Chain.WaitUntilSettledAsync(root.RunId, SettleTimeout, ct);
        var expired = await app.Chat.Human.GetAsync(request.Id);
        var childOutput = (string?)await app.Db.ScalarAsync("select output from runs where id = @id", ("id", request.RunId)) ?? "";
        Log.Write(0, ConsoleColor.Green, final.Status, final.Output);

        s.Checks.Add(new Check("Pedido vencido foi negado como expired pela varredura do despachante", _ => expired?.Status == "expired"));
        s.Checks.Add(new Check("Subagente foi retomado e replanejou sem a resposta", _ => childOutput.Contains("replanejei")));
        s.Checks.Add(new Check("Pai terminou normalmente com o replanejamento do filho",
            _ => final.Status == RunStatus.Completed && final.Output.Contains("replanejei")));
    });

    private static Task HitlCancela(App app, EvidenceSession s, CancellationToken ct) => WithDispatcherAsync(app, ct, async () =>
    {
        var (conversation, root, request) = await SuspendedSubagentAsync(app, s, ct);
        var cancelled = await app.Chat.CancelAsync(conversation);
        await Task.Delay(DemoDispatcherSweep * 2, ct);
        var rootStatus = await RunStatusAsync(app, root.RunId);
        var childStatus = await RunStatusAsync(app, request.RunId);
        var closed = await app.Chat.Human.GetAsync(request.Id);
        var resumed = await CountEventsAsync(app, root.RunId, "run_resumed") + await CountEventsAsync(app, request.RunId, "run_resumed");

        s.Checks.Add(new Check("Cancelamento aceito com a cadeia suspensa", _ => cancelled));
        s.Checks.Add(new Check("Raiz e subagente viraram cancelled", _ => rootStatus == RunStatus.Cancelled && childStatus == RunStatus.Cancelled));
        s.Checks.Add(new Check("Pedido pendente foi fechado (expired, motivo cancelamento)", _ => closed?.Status == "expired" && closed.Reason!.Contains("cancelado")));
        s.Checks.Add(new Check("Nada foi retomado depois do cancelamento", _ => resumed == 0));
    });

    /// <summary>Lease curto so neste cenario, para o run "caido" virar orfao em segundos.</summary>
    private static readonly RunLeaseOptions ShortLease = new(TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(500));

    private static async Task DurabilidadeEntrega(App app, EvidenceSession s, CancellationToken ct)
    {
        await using var instance = new App(app.ConnectionString, ShortLease);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var dispatcher = Task.Run(() => instance.Dispatcher.RunAsync(2, instance.Lease.Sweep, stop.Token));
        try
        {
            await InjectedFaultPartAsync(instance, s, ct);
            await ReconciliationPartAsync(instance, s, ct);
        }
        finally
        {
            Environment.SetEnvironmentVariable(FaultInjection.Variable, null);
            stop.Cancel();
            try { await dispatcher; } catch (OperationCanceledException) { /* encerrado */ }
        }
    }

    private static async Task<int> DelegateToolResultsAsync(App app, Guid conversation) =>
        Convert.ToInt32(await app.Db.ScalarAsync(
            "select count(*) from conversation_messages where conversation_id = @c and role = 'tool' and tool_name = 'delegate_task'", ("c", conversation)));

    private static async Task InjectedFaultPartAsync(App instance, EvidenceSession s, CancellationToken ct)
    {
        var (conversation, root, request) = await SuspendedSubagentAsync(instance, s, ct);
        var childId = request.RunId;
        Environment.SetEnvironmentVariable(FaultInjection.Variable, FaultInjection.AfterChildFinished);
        s.Notes.Add($"Parte 1: {FaultInjection.Variable}={FaultInjection.AfterChildFinished} ligado; a queda acontece dentro da transacao que conclui o filho.");
        await instance.Chat.DecideAsync(request.Id, new HumanDecision(null, null, null, "producao"));

        while (Environment.GetEnvironmentVariable(FaultInjection.Variable) != null) await Task.Delay(50, ct);
        var afterFault = await instance.Db.QueryOneAsync("select status, owner from runs where id = @id", ("id", childId));
        var parentAfterFault = await RunStatusAsync(instance, root.RunId);
        var deliveredAfterFault = await DelegateToolResultsAsync(instance, conversation);
        Log.Warn($"Queda injetada: filho continua {afterFault?.S("status")} (dono {afterFault?.S("owner")}), pai {parentAfterFault}, tool_results entregues {deliveredAfterFault}.");

        var final = await instance.Chain.WaitUntilSettledAsync(root.RunId, SettleTimeout, ct);
        Log.Write(0, ConsoleColor.Green, final.Status, final.Output);
        var recovered = await instance.Db.QueryOneAsync(
            "select data->>'previous_owner' as previous, data->>'recovered_by' as by from run_events where run_id = @id and type = 'run_recovered'", ("id", childId));
        var childLlmCalls = await CountEventsAsync(instance, childId, "llm_call");
        var delivered = await DelegateToolResultsAsync(instance, conversation);

        s.Checks.Add(new Check("Queda dentro da transacao nao gravou nada pela metade: filho ainda running e pai ainda waiting_child, sem tool_result",
            _ => afterFault?.S("status") == RunStatus.Running && parentAfterFault == RunStatus.WaitingChild && deliveredAfterFault == 0));
        s.Checks.Add(new Check("Lease do filho venceu e a varredura o recuperou (run_recovered com a instancia anterior)",
            _ => recovered != null && recovered.S("previous") == instance.InstanceId));
        s.Checks.Add(new Check("Filho concluiu com a resposta ja gravada, sem chamar o modelo de novo (2 chamadas: pergunta e resposta)",
            _ => childLlmCalls == 2));
        s.Checks.Add(new Check("Pai terminou com a resposta do filho", _ => final.Status == RunStatus.Completed && final.Output.Contains("producao")));
        s.Checks.Add(new Check("O tool_result do delegate_task foi gravado uma unica vez no pai", _ => delivered == 1));
    }

    private static async Task ReconciliationPartAsync(App instance, EvidenceSession s, CancellationToken ct)
    {
        var (conversation, root, request) = await SuspendedSubagentAsync(instance, s, ct);
        s.Notes.Add("Parte 2: estado da versao anterior forjado no banco (filho completed, pai preso em waiting_child sem tool_result).");
        await instance.Db.ExecAsync("update approvals set status = 'answered', answer = 'homologacao', decided_at = now() where id = @id", ("id", request.Id));
        await instance.Db.ExecAsync(
            "update runs set status = 'completed', output = 'A pessoa escolheu o ambiente: homologacao', finished_at = now() where id = @id",
            ("id", request.RunId));
        var final = await instance.Chain.WaitUntilSettledAsync(root.RunId, SettleTimeout, ct);
        var delivered = await DelegateToolResultsAsync(instance, conversation);
        Log.Write(0, ConsoleColor.Green, final.Status, final.Output);

        s.Checks.Add(new Check("Reconciliacao: pai preso com filho terminado recebeu o resultado e terminou",
            _ => final.Status == RunStatus.Completed && final.Output.Contains("homologacao")));
        s.Checks.Add(new Check("Reconciliacao gravou o tool_result uma unica vez", _ => delivered == 1));
    }

    private sealed class CountingObserver : IRunObserver
    {
        public int TextDeltas { get; private set; }
        public void OnTextDelta(RunContext ctx, string delta) => TextDeltas++;
        public void OnReasoningDelta(RunContext ctx, string delta) { }
        public void OnEvent(RunContext ctx, string type, System.Text.Json.Nodes.JsonObject data) { }
    }
}
