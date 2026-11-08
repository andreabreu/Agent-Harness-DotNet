using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using AgentPoc.Chat;
using AgentPoc.Core;
using AgentPoc.Harness;
using AgentPoc.Human;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentPoc.Web;

public sealed record NewConversationRequest(string? Agent);
public sealed record SendMessageRequest(string Text);
public sealed record DecisionRequest(bool? Approved, string? Reason, JsonObject? Args, string? Answer);

/// <summary>
/// API do chat (REST + Server-Sent Events) e a pagina web. Sem autenticacao: escuta so em localhost por padrao.
/// </summary>
public sealed class ChatServer
{
    private const int BackgroundWorkers = 2;
    private const int ConversationsListed = 50;
    private const string DefaultAgent = "assistant";

    private readonly App _app;
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _activeTurns = new();

    public ChatServer(App app) => _app = app;

    public async Task RunAsync(int port, CancellationToken ct)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
            WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot")
        });
        builder.WebHost.UseUrls($"http://localhost:{port}");
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        var web = builder.Build();
        web.UseDefaultFiles();
        web.UseStaticFiles();
        Map(web);

        var stopping = web.Lifetime.ApplicationStopping;
        var workers = Task.Run(() => _app.Worker.RunAsync(BackgroundWorkers, untilEmpty: false, stopping));
        Log.Ok($"Chat web em http://localhost:{port} (Ctrl+C encerra)");
        await web.RunAsync(ct);
        try { await workers; } catch (OperationCanceledException) { /* encerrando */ }
    }

    private void Map(WebApplication web)
    {
        web.MapGet("/api/agents", async () =>
            (await _app.Config.ListAgentsAsync()).Select(a => new { id = a.Id, description = a.Description, model = a.ModelId }));

        web.MapGet("/api/conversations", async () =>
            (await _app.Chat.Store.ListAsync(ConversationsListed)).Select(c => new
            {
                id = c.Id, agent = c.AgentId, title = c.Title, updated_at = c.UpdatedAt, busy = _app.Chat.IsBusy(c.Id)
            }));

        web.MapPost("/api/conversations", async (NewConversationRequest req) =>
        {
            try
            {
                return Results.Ok(new { id = await _app.Chat.StartAsync(req.Agent ?? DefaultAgent) });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        web.MapGet("/api/conversations/{id:guid}", GetConversationAsync);

        web.MapPost("/api/conversations/{id:guid}/messages", (Guid id, SendMessageRequest req, HttpContext http, IHostApplicationLifetime lifetime) =>
            string.IsNullOrWhiteSpace(req.Text)
                ? Results.BadRequest(new { error = "mensagem vazia" }).ExecuteAsync(http)
                : StreamTurnAsync(id, http, lifetime, (hooks, ct) => _app.Chat.SendAsync(id, req.Text.Trim(), hooks, ct)));

        web.MapPost("/api/conversations/{id:guid}/resume", (Guid id, HttpContext http, IHostApplicationLifetime lifetime) =>
            StreamTurnAsync(id, http, lifetime, (hooks, ct) => _app.Chat.ResumeAsync(id, hooks, ct)));

        web.MapPost("/api/conversations/{id:guid}/cancel", (Guid id) =>
        {
            if (!_activeTurns.TryGetValue(id, out var turn)) return Results.NotFound(new { error = "nenhum turno em andamento" });
            turn.Cancel();
            return Results.Ok();
        });

        web.MapPost("/api/approvals/{id:guid}", DecideAsync);
    }

    /// <summary>
    /// Registra a decisao. Se o agente raiz estava suspenso esperando por ela, a resposta e o stream SSE
    /// da continuacao do turno; se era um subagente esperando em memoria, ele segue no stream que ja estava aberto.
    /// </summary>
    private async Task DecideAsync(Guid id, DecisionRequest req, HttpContext http, IHostApplicationLifetime lifetime)
    {
        DecisionOutcome outcome;
        try
        {
            outcome = await _app.Chat.DecideAsync(id, new HumanDecision(req.Approved, req.Reason, req.Args, req.Answer));
        }
        catch (KeyNotFoundException ex)
        {
            await Results.NotFound(new { error = ex.Message }).ExecuteAsync(http);
            return;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            await Results.Conflict(new { error = ex.Message }).ExecuteAsync(http);
            return;
        }

        if (!outcome.NeedsResume || outcome.Request.ConversationId is not { } conversationId)
        {
            await Results.Ok(new { resumed = false }).ExecuteAsync(http);
            return;
        }
        await StreamTurnAsync(conversationId, http, lifetime, (hooks, ct) => _app.Chat.ResumeAsync(conversationId, hooks, ct));
    }

    private async Task<IResult> GetConversationAsync(Guid id)
    {
        var conv = await _app.Chat.Store.GetAsync(id);
        if (conv == null) return Results.NotFound();
        var messages = await _app.Chat.Store.LoadMessagesAsync(id);
        var usage = await _app.Db.QueryOneAsync(
            @"select coalesce(sum(tokens_in + tokens_out), 0) as tokens, coalesce(sum(cost_usd), 0) as cost
                from runs where conversation_id = @c", ("c", id));
        var pending = await _app.Chat.Human.PendingAsync(id);
        var waitingRun = await _app.Chat.Store.WaitingRunAsync(id);
        return Results.Ok(new
        {
            id = conv.Id,
            agent = conv.AgentId,
            title = conv.Title,
            summary = conv.Summary,
            summarized_upto = conv.SummarizedUpto,
            active_tools = conv.ActiveTools,
            busy = _app.Chat.IsBusy(id),
            waiting = waitingRun == null ? null : new { run_id = waitingRun, ready_to_resume = pending.All(p => p.RunId != waitingRun) },
            tokens = usage?.L("tokens") ?? 0,
            cost_usd = usage?.D("cost") ?? 0,
            pending_approvals = pending.Select(p => new
            {
                approval_id = p.Id, kind = p.Kind, tool = p.Tool, args = p.Args, policy_reason = p.PolicyReason,
                question = p.Question, options = p.Options, durable = p.RunId == waitingRun, created_at = p.CreatedAt
            }),
            messages = messages.Select(m => new
            {
                seq = m.Seq,
                role = m.Message.Role,
                content = m.Message.Content,
                tool_calls = m.Message.ToolCalls?.Select(c => new { id = c.Id, name = c.Name, arguments = c.ArgumentsJson }),
                tool_call_id = m.Message.ToolCallId,
                tool_name = m.Message.ToolName,
                created_at = m.CreatedAt
            })
        });
    }

    /// <summary>
    /// Roda um turno (novo ou retomado) e transmite os eventos em SSE. O turno continua no servidor se o
    /// navegador desconectar (as mensagens sao persistidas); so o botao parar (ou o desligamento) cancela.
    /// </summary>
    private async Task StreamTurnAsync(Guid id, HttpContext http, IHostApplicationLifetime lifetime,
                                       Func<RunHooks, CancellationToken, Task<RunResult>> turnFn)
    {
        if (_app.Chat.IsBusy(id))
        {
            await Results.Conflict(new { error = "a conversa ja tem um turno em andamento" }).ExecuteAsync(http);
            return;
        }

        using var turn = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
        _activeTurns[id] = turn;
        var observer = new SseRunObserver();
        var run = RunTurnAsync(observer, () => turnFn(new RunHooks(observer, _app.HumanGate), turn.Token));

        http.Response.Headers.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        http.Response.Headers["X-Accel-Buffering"] = "no";
        try
        {
            await foreach (var evt in observer.Reader.ReadAllAsync(http.RequestAborted))
            {
                await http.Response.WriteAsync($"event: {evt.Type}\ndata: {evt.Data.ToJsonString(Text.RelaxedJson)}\n\n", http.RequestAborted);
                await http.Response.Body.FlushAsync(http.RequestAborted);
            }
        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
        {
            Log.Info($"Navegador desconectou da conversa {Text.Short(id)}; o turno continua no servidor.");
        }
        finally
        {
            await run;
            _activeTurns.TryRemove(id, out _);
        }
    }

    private static async Task RunTurnAsync(SseRunObserver observer, Func<Task<RunResult>> turn)
    {
        await Task.Yield();
        try
        {
            var r = await turn();
            observer.Complete(new ServerEvent("done", new JsonObject
            {
                ["run_id"] = r.RunId.ToString(),
                ["status"] = r.Status,
                ["output"] = r.Output,
                ["steps"] = r.Steps,
                ["tokens"] = r.TokensIn + r.TokensOut,
                ["cost_usd"] = r.CostUsd,
                ["waiting_for"] = r.WaitingFor?.ToString()
            }));
        }
        catch (Exception ex)
        {
            observer.Complete(new ServerEvent("failed", new JsonObject { ["error"] = ex.Message }));
        }
    }
}
