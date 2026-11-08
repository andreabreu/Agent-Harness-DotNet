using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using AgentPoc.Core;
using AgentPoc.Harness;

namespace AgentPoc.Chat;

/// <summary>Evento gravado de um run da conversa (de qualquer instancia que o tenha executado).</summary>
public sealed record FeedEvent(long Id, Guid RunId, string Type, JsonObject Data, string Agent, int Depth);

/// <summary>Pedaco de texto em streaming, so do processo que esta executando o run (nao e gravado).</summary>
public sealed record LiveDelta(string Type, string Text, string Agent, int Depth);

/// <summary>Eventos dos runs de uma conversa, lidos de run_events (fonte da verdade, igual para todas as instancias).</summary>
public sealed class ConversationFeed
{
    private const int MaxEventsPerRead = 500;
    private readonly Db _db;
    public ConversationFeed(Db db) => _db = db;

    public async Task<long> LastEventIdAsync(Guid conversationId) =>
        Convert.ToInt64(await _db.ScalarAsync(
            "select coalesce(max(e.id), 0) from run_events e join runs r on r.id = e.run_id where r.conversation_id = @c", ("c", conversationId)));

    public async Task<List<FeedEvent>> AfterAsync(Guid conversationId, long afterId) =>
        (await _db.QueryAsync(
            @"select e.id, e.run_id, e.type, e.data::text as data, r.agent_id, r.depth
                from run_events e join runs r on r.id = e.run_id
               where r.conversation_id = @c and e.id > @after
               order by e.id limit @l", ("c", conversationId), ("after", afterId), ("l", MaxEventsPerRead)))
        .Select(r => new FeedEvent(r.L("id"), r.G("run_id"), r.S("type"),
            JsonNode.Parse(r.S("data")) as JsonObject ?? new JsonObject(), r.S("agent_id"), r.I("depth")))
        .ToList();
}

/// <summary>Distribui o texto em streaming dos runs executados neste processo para quem acompanha a conversa.</summary>
public sealed class LiveHub
{
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Channel<LiveDelta>, byte>> _subscribers = new();

    public Subscription Subscribe(Guid conversationId)
    {
        var channel = Channel.CreateUnbounded<LiveDelta>(new UnboundedChannelOptions { SingleReader = true });
        _subscribers.GetOrAdd(conversationId, _ => new()).TryAdd(channel, 0);
        return new Subscription(channel.Reader, () =>
        {
            if (_subscribers.TryGetValue(conversationId, out var set)) set.TryRemove(channel, out _);
            channel.Writer.TryComplete();
        });
    }

    public void Publish(Guid conversationId, LiveDelta delta)
    {
        if (!_subscribers.TryGetValue(conversationId, out var set)) return;
        foreach (var channel in set.Keys) channel.Writer.TryWrite(delta);
    }

    public sealed class Subscription : IDisposable
    {
        private readonly Action _dispose;

        public Subscription(ChannelReader<LiveDelta> reader, Action dispose)
        {
            Reader = reader;
            _dispose = dispose;
        }

        public ChannelReader<LiveDelta> Reader { get; }
        public void Dispose() => _dispose();
    }
}

/// <summary>Observer dos runs de chat: so publica o texto ao vivo; os eventos chegam pelo banco.</summary>
public sealed class LiveObserver : IRunObserver
{
    private readonly LiveHub _hub;
    public LiveObserver(LiveHub hub) => _hub = hub;

    public void OnEvent(RunContext ctx, string type, JsonObject data) { }

    public void OnTextDelta(RunContext ctx, string delta) => Publish(ctx, "text", delta);

    public void OnReasoningDelta(RunContext ctx, string delta) => Publish(ctx, "reasoning", delta);

    private void Publish(RunContext ctx, string type, string delta)
    {
        if (ctx.ConversationId is { } conversationId) _hub.Publish(conversationId, new LiveDelta(type, delta, ctx.Agent.Id, ctx.Depth));
    }
}
