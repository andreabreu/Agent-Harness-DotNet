using AgentPoc.Core;
using AgentPoc.Harness;
using AgentPoc.Human;

namespace AgentPoc.Chat;

/// <summary>
/// Chat interativo no terminal (REPL). Acompanha a conversa pelo feed de eventos (o turno pode ser retomado pelo
/// despachante desta ou de outra instancia), pergunta no terminal quando um run pede uma pessoa e so grava a
/// decisao: quem retoma e o despachante. Ctrl+C cancela a cadeia do turno.
/// </summary>
public sealed class ChatCli
{
    private const int BackgroundWorkers = 2;
    private const int DispatcherConcurrency = 2;
    private const int ConversationsListed = 15;
    private const int HistoryMessagesShown = 20;
    private static readonly TimeSpan FeedPollInterval = TimeSpan.FromMilliseconds(200);

    private readonly App _app;
    private readonly ConsoleChatRenderer _renderer = new();
    private volatile bool _following;
    private Guid _conversationId;
    private string _agentId;

    public ChatCli(App app, string agentId)
    {
        _app = app;
        _agentId = agentId;
    }

    /// <summary>Cancela o turno em andamento. Retorna false se nao havia turno (o Ctrl+C deve encerrar o programa).</summary>
    public bool CancelTurn()
    {
        if (!_following) return false;
        _ = _app.Chat.CancelAsync(_conversationId);
        return true;
    }

    public async Task RunAsync(string? conversation, bool startWorkers, CancellationToken ct)
    {
        using var backgroundCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var workers = startWorkers ? Task.Run(() => _app.Worker.RunAsync(BackgroundWorkers, untilEmpty: false, backgroundCts.Token)) : Task.CompletedTask;
        var dispatcher = Task.Run(() => _app.Dispatcher.RunAsync(DispatcherConcurrency, _app.Lease.Sweep, backgroundCts.Token));

        if (conversation != null) await OpenAsync(conversation);
        else await NewAsync(_agentId);
        PrintHelp();

        try
        {
            while (!ct.IsCancellationRequested)
            {
                Log.Inline("\nvoce> ", ConsoleColor.Cyan);
                var line = Console.ReadLine();
                if (line == null) break;
                line = line.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith('/'))
                {
                    if (!await HandleCommandAsync(line)) break;
                    continue;
                }
                await SendAsync(line, ct);
            }
        }
        finally
        {
            backgroundCts.Cancel();
            try { await Task.WhenAll(workers, dispatcher); } catch (OperationCanceledException) { /* encerrando */ }
        }
    }

    private async Task SendAsync(string text, CancellationToken ct)
    {
        var cursor = await _app.Feed.LastEventIdAsync(_conversationId);
        using var live = _app.Live.Subscribe(_conversationId);
        _renderer.BeginTurn();
        var turn = Task.Run(() => _app.Chat.SendAsync(_conversationId, text, new RunHooks(new LiveObserver(_app.Live), _app.HumanGate), ct), ct);
        _following = true;
        try
        {
            await FollowTurnAsync(cursor, live, turn, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _renderer.EndTurn();
            Log.Error(ex.Message);
        }
        finally
        {
            _following = false;
        }
    }

    /// <summary>Mostra o turno ate o agente raiz terminar, pedindo as decisoes humanas quando aparecem.</summary>
    private async Task FollowTurnAsync(long cursor, LiveHub.Subscription live, Task<RunResult> turn, CancellationToken ct)
    {
        while (true)
        {
            while (live.Reader.TryRead(out var delta)) _renderer.Live(delta);
            foreach (var e in await _app.Feed.AfterAsync(_conversationId, cursor))
            {
                cursor = e.Id;
                _renderer.Render(e);
                if (e.Type == "waiting_human") await AskDecisionsAsync();
                if (e.Type == "run_finished" && e.Depth == 0)
                {
                    _renderer.EndTurn();
                    PrintFooter(e);
                    return;
                }
            }
            if (turn.IsFaulted) throw turn.Exception!.GetBaseException();
            await Task.WhenAny(live.Reader.WaitToReadAsync(ct).AsTask(), Task.Delay(FeedPollInterval, ct));
        }
    }

    /// <summary>Pergunta no terminal cada pedido pendente da conversa e grava a decisao (sem retomar nada aqui).</summary>
    private async Task AskDecisionsAsync()
    {
        foreach (var request in await _app.Chat.Human.PendingAsync(_conversationId))
        {
            _renderer.EndTurn();
            var decision = request.Kind == "question" ? AskAnswer(request) : AskApproval(request);
            try
            {
                var decided = await _app.Chat.DecideAsync(request.Id, decision);
                Log.Inline($"  decisao gravada: {decided.Status}; o despachante retoma o run.\n", ConsoleColor.DarkGray);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                Log.Warn(ex.Message);
            }
        }
    }

    private static HumanDecision AskAnswer(HumanRequest request)
    {
        Log.Raw($">>> PERGUNTA: {request.Question}");
        if (request.Options.Length > 0) Log.Raw($">>> Opcoes: {string.Join(" | ", request.Options)}");
        Console.Write(">>> Resposta: ");
        return new HumanDecision(null, null, null, (Console.ReadLine() ?? "").Trim());
    }

    private static HumanDecision AskApproval(HumanRequest request)
    {
        Log.Raw($">>> APROVACAO ({request.PolicyReason}): {request.Tool}({Text.Trunc(request.Args.ToJsonString(Text.RelaxedJson), 300)})");
        Console.Write(">>> Aprovar? [s]im / [n]ao / [e]ditar argumentos: ");
        var answer = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
        if (answer is "s" or "sim" or "y" or "yes") return new HumanDecision(true, null, null, null);
        if (answer is "e" or "editar")
        {
            Console.Write(">>> Novos argumentos (JSON): ");
            try
            {
                if (System.Text.Json.Nodes.JsonNode.Parse(Console.ReadLine() ?? "") is System.Text.Json.Nodes.JsonObject edited)
                    return new HumanDecision(true, null, edited, null);
            }
            catch (System.Text.Json.JsonException) { /* cai na negacao abaixo */ }
            return new HumanDecision(false, "JSON editado invalido", null, null);
        }
        Console.Write(">>> Motivo da negacao (opcional): ");
        return new HumanDecision(false, (Console.ReadLine() ?? "").Trim(), null, null);
    }

    private static void PrintFooter(FeedEvent finished)
    {
        string S(string key) => finished.Data[key]?.ToString() ?? "";
        var tokens = (finished.Data["tokens_in"]?.GetValue<long>() ?? 0) + (finished.Data["tokens_out"]?.GetValue<long>() ?? 0);
        if (S("status") != RunStatus.Completed) Log.Raw(S("output"));
        var cost = finished.Data["cost_usd"]?.GetValue<decimal>() ?? 0;
        Log.Inline($"[{S("status")} | {S("steps")} passos | {tokens:N0} tokens | US$ {cost:0.0000}]\n", ConsoleColor.DarkGray);
    }

    private async Task<bool> HandleCommandAsync(string line)
    {
        var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var arg = parts.Length > 1 ? parts[1] : null;
        switch (parts[0])
        {
            case "/sair":
            case "/exit":
                return false;
            case "/nova":
                await NewAsync(arg ?? _agentId);
                break;
            case "/conversas":
                await ListAsync();
                break;
            case "/abrir":
                if (arg == null) Log.Warn("Uso: /abrir <id ou prefixo>");
                else await OpenAsync(arg);
                break;
            case "/historico":
                await PrintHistoryAsync();
                break;
            case "/agentes":
                foreach (var a in await _app.Config.ListAgentsAsync()) Log.Raw($"  {a.Id,-13} {a.Description}");
                break;
            default:
                PrintHelp();
                break;
        }
        return true;
    }

    private async Task NewAsync(string agentId)
    {
        _conversationId = await _app.Chat.StartAsync(agentId);
        _agentId = agentId;
        Log.Ok($"Nova conversa {Text.Short(_conversationId)} com o agente '{agentId}'.");
    }

    private async Task OpenAsync(string idOrPrefix)
    {
        var conv = await _app.Chat.Store.FindAsync(idOrPrefix);
        if (conv == null)
        {
            Log.Warn($"Conversa '{idOrPrefix}' nao encontrada.");
            if (_conversationId == Guid.Empty) await NewAsync(_agentId);
            return;
        }
        _conversationId = conv.Id;
        _agentId = conv.AgentId;
        Log.Ok($"Conversa {Text.Short(conv.Id)} ({conv.AgentId}): {conv.Title ?? "(sem titulo)"}");
        await PrintHistoryAsync();
    }

    private async Task ListAsync()
    {
        foreach (var c in await _app.Chat.Store.ListAsync(ConversationsListed))
            Log.Raw($"  {Text.Short(c.Id)}  {c.UpdatedAt.ToLocalTime():dd/MM HH:mm}  {c.AgentId,-10} {c.Title ?? "(sem titulo)"}");
    }

    private async Task PrintHistoryAsync()
    {
        var messages = (await _app.Chat.Store.LoadMessagesAsync(_conversationId))
            .Where(m => m.Message.Role != "tool" && !string.IsNullOrWhiteSpace(m.Message.Content))
            .TakeLast(HistoryMessagesShown);
        foreach (var m in messages)
        {
            var user = m.Message.Role == "user";
            Log.Inline(user ? "voce> " : "assistente> ", user ? ConsoleColor.Cyan : ConsoleColor.Green);
            Log.Raw(m.Message.Content!);
        }
    }

    private static void PrintHelp() =>
        Log.Inline("Comandos: /nova [agente]  /conversas  /abrir <id>  /historico  /agentes  /sair   (Ctrl+C cancela o turno)\n", ConsoleColor.DarkGray);
}
