using AgentPoc.Core;
using AgentPoc.Harness;

namespace AgentPoc.Chat;

/// <summary>Chat interativo no terminal (REPL) com streaming, aprovacao e cancelamento por Ctrl+C.</summary>
public sealed class ChatCli
{
    private const int BackgroundWorkers = 2;
    private const int ConversationsListed = 15;
    private const int HistoryMessagesShown = 20;

    private readonly App _app;
    private readonly ConsoleChatRenderer _renderer = new();
    private CancellationTokenSource? _turn;
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
        var turn = _turn;
        if (turn == null || turn.IsCancellationRequested) return false;
        turn.Cancel();
        return true;
    }

    public async Task RunAsync(string? conversation, bool startWorkers, CancellationToken ct)
    {
        using var workersCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var workers = startWorkers ? Task.Run(() => _app.Worker.RunAsync(BackgroundWorkers, untilEmpty: false, workersCts.Token)) : Task.CompletedTask;

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
            workersCts.Cancel();
            try { await workers; } catch (OperationCanceledException) { /* encerrando */ }
        }
    }

    private async Task SendAsync(string text, CancellationToken ct)
    {
        using var turn = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _turn = turn;
        _renderer.BeginTurn();
        try
        {
            var result = await _app.Chat.SendAsync(_conversationId, text, new RunHooks(_renderer, ModeHumanGate.Instance), turn.Token);
            _renderer.EndTurn();
            if (_renderer.TextDeltas == 0 || result.Status != "completed") Log.Raw(result.Output);
            Log.Inline($"[{result.Status} | {result.Steps} passos | {result.TokensIn + result.TokensOut:N0} tokens | US$ {result.CostUsd:0.0000}]\n", ConsoleColor.DarkGray);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _renderer.EndTurn();
            Log.Error(ex.Message);
        }
        finally
        {
            _turn = null;
        }
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
