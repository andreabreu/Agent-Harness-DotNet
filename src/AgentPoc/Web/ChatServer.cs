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
/// A tela acompanha a conversa por um stream unico, montado a partir de run_events: mostra o turno andando
/// mesmo quando quem retoma e o despachante de outra instancia. O texto token a token chega so quando o run
/// executa neste processo; vindo de outra instancia, o texto aparece por mensagem completa.
/// </summary>
public sealed class ChatServer
{
    private const int BackgroundWorkers = 2;
    private const int DispatcherConcurrency = 2;
    private const int ConversationsListed = 50;
    private const string DefaultAgent = "assistant";
    private static readonly TimeSpan StreamPollInterval = TimeSpan.FromMilliseconds(250);

    private readonly App _app;

    public ChatServer(App app) => _app = app;

    /// <param name="startDispatcher">false para um no so de API/tela; a retomada fica com outras instancias.</param>
    public async Task RunAsync(int port, bool startDispatcher, CancellationToken ct)
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
        var dispatcher = startDispatcher
            ? Task.Run(() => _app.Dispatcher.RunAsync(DispatcherConcurrency, _app.Lease.Sweep, stopping))
            : Task.CompletedTask;
        Log.Ok($"Chat web em http://localhost:{port} (Ctrl+C encerra)");
        await web.RunAsync(ct);
        try { await Task.WhenAll(workers, dispatcher); } catch (OperationCanceledException) { /* encerrando */ }
    }

    private void Map(WebApplication web)
    {
        web.MapGet("/api/agents", async () =>
            (await _app.Config.ListAgentsAsync()).Select(a => new { id = a.Id, description = a.Description, model = a.ModelId }));

        web.MapGet("/api/conversations", async () =>
        {
            var list = new List<object>();
            foreach (var c in await _app.Chat.Store.ListAsync(ConversationsListed))
            {
                var root = await _app.Chat.Store.LatestRootRunAsync(c.Id);
                list.Add(new { id = c.Id, agent = c.AgentId, title = c.Title, updated_at = c.UpdatedAt, status = root?.Status });
            }
            return list;
        });

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
        web.MapGet("/api/conversations/{id:guid}/stream", StreamAsync);
        web.MapPost("/api/conversations/{id:guid}/messages", SendAsync);

        web.MapPost("/api/conversations/{id:guid}/cancel", async (Guid id) =>
            await _app.Chat.CancelAsync(id) ? Results.Ok() : Results.NotFound(new { error = "nenhum turno em andamento" }));

        web.MapPost("/api/approvals/{id:guid}", DecideAsync);
    }

    /// <summary>Comeca o turno em segundo plano neste processo e responde na hora; a tela acompanha pelo stream.</summary>
    private async Task<IResult> SendAsync(Guid id, SendMessageRequest req, IHostApplicationLifetime lifetime)
    {
        if (string.IsNullOrWhiteSpace(req.Text)) return Results.BadRequest(new { error = "mensagem vazia" });
        if (await _app.Chat.Store.GetAsync(id) == null) return Results.NotFound(new { error = "conversa nao encontrada" });
        if (await _app.Chat.IsBusyAsync(id)) return Results.Conflict(new { error = "a conversa ja tem um turno em andamento" });

        var hooks = new RunHooks(new LiveObserver(_app.Live), _app.HumanGate);
        _ = Task.Run(async () =>
        {
            try
            {
                await _app.Chat.SendAsync(id, req.Text.Trim(), hooks, lifetime.ApplicationStopping);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Error($"turno da conversa {Text.Short(id)}: {ex.Message}");
            }
        });
        return Results.Accepted();
    }

    /// <summary>Grava a decisao. A retomada e do despachante (desta ou de outra instancia), nunca desta requisicao.</summary>
    private async Task<IResult> DecideAsync(Guid id, DecisionRequest req)
    {
        try
        {
            var decided = await _app.Chat.DecideAsync(id, new HumanDecision(req.Approved, req.Reason, req.Args, req.Answer));
            return Results.Ok(new { status = decided.Status, reason = decided.Reason, answer = decided.Answer });
        }
        catch (KeyNotFoundException ex)
        {
            return Results.NotFound(new { error = ex.Message });
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return Results.Conflict(new { error = ex.Message });
        }
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
        var root = await _app.Chat.Store.LatestRootRunAsync(id);
        var agents = await _app.Db.QueryAsync("select id, agent_id, depth from runs where conversation_id = @c", ("c", id));
        var runInfo = agents.ToDictionary(r => r.G("id"), r => (Agent: r.S("agent_id"), Depth: r.I("depth")));
        return Results.Ok(new
        {
            id = conv.Id,
            agent = conv.AgentId,
            title = conv.Title,
            summary = conv.Summary,
            summarized_upto = conv.SummarizedUpto,
            active_tools = conv.ActiveTools,
            root_status = root?.Status,
            last_event_id = await _app.Feed.LastEventIdAsync(id),
            tokens = usage?.L("tokens") ?? 0,
            cost_usd = usage?.D("cost") ?? 0,
            pending_approvals = pending.Select(p => new
            {
                approval_id = p.Id, kind = p.Kind, tool = p.Tool, args = p.Args, policy_reason = p.PolicyReason,
                question = p.Question, options = p.Options, expires_at = p.ExpiresAt, created_at = p.CreatedAt,
                agent = runInfo.TryGetValue(p.RunId, out var info) ? info.Agent : null,
                depth = runInfo.TryGetValue(p.RunId, out var d) ? d.Depth : 0
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
    /// Stream da conversa: eventos gravados em run_events (com id, para retomar de onde parou ao reconectar)
    /// mais o texto ao vivo dos runs executados neste processo.
    /// </summary>
    private async Task StreamAsync(Guid id, HttpContext http)
    {
        var after = long.TryParse(http.Request.Headers["Last-Event-ID"].FirstOrDefault(), out var lastId) ? lastId
            : long.TryParse(http.Request.Query["after"], out var q) ? q : await _app.Feed.LastEventIdAsync(id);

        http.Response.Headers.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        http.Response.Headers["X-Accel-Buffering"] = "no";
        using var live = _app.Live.Subscribe(id);
        var ct = http.RequestAborted;
        try
        {
            await http.Response.WriteAsync(": conectado\n\n", ct);
            await http.Response.Body.FlushAsync(ct);
            while (!ct.IsCancellationRequested)
            {
                while (live.Reader.TryRead(out var delta))
                    await WriteAsync(http, null, delta.Type, new JsonObject { ["delta"] = delta.Text, ["agent"] = delta.Agent, ["depth"] = delta.Depth });
                foreach (var evt in await _app.Feed.AfterAsync(id, after))
                {
                    var data = (JsonObject)evt.Data.DeepClone();
                    data["agent"] = evt.Agent;
                    data["depth"] = evt.Depth;
                    data["run_id"] = evt.RunId.ToString();
                    await WriteAsync(http, evt.Id, evt.Type, data);
                    after = evt.Id;
                }
                await http.Response.Body.FlushAsync(ct);
                await Task.WhenAny(live.Reader.WaitToReadAsync(ct).AsTask(), Task.Delay(StreamPollInterval, ct));
            }
        }
        catch (OperationCanceledException) { /* navegador fechou */ }
    }

    private static Task WriteAsync(HttpContext http, long? id, string type, JsonObject data) =>
        http.Response.WriteAsync(
            $"{(id is { } eventId ? $"id: {eventId}\n" : "")}event: {type}\ndata: {data.ToJsonString(Text.RelaxedJson)}\n\n",
            http.RequestAborted);
}
