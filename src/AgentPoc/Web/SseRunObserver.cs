using System.Text.Json.Nodes;
using System.Threading.Channels;
using AgentPoc.Harness;

namespace AgentPoc.Web;

public sealed record ServerEvent(string Type, JsonObject Data);

/// <summary>Converte os eventos do harness em eventos para o navegador, enfileirados para o stream SSE.</summary>
public sealed class SseRunObserver : IRunObserver
{
    private static readonly HashSet<string> ForwardedEvents = new()
    {
        "tool_call", "tool_result", "tools_loaded", "thinking", "plan_updated", "delegation_start", "delegation_end",
        "task_created", "policy", "approval_request", "budget_exceeded", "error", "cancelled", "llm_call", "waiting_human", "run_resumed"
    };

    private readonly Channel<ServerEvent> _channel = Channel.CreateUnbounded<ServerEvent>(new UnboundedChannelOptions { SingleReader = true });

    public ChannelReader<ServerEvent> Reader => _channel.Reader;

    public void OnTextDelta(RunContext ctx, string delta)
    {
        if (ctx.Depth == 0) Write("text", ctx, new JsonObject { ["delta"] = delta });
    }

    public void OnReasoningDelta(RunContext ctx, string delta)
    {
        if (ctx.Depth == 0) Write("reasoning", ctx, new JsonObject { ["delta"] = delta });
    }

    public void OnEvent(RunContext ctx, string type, JsonObject data)
    {
        if (ForwardedEvents.Contains(type)) Write(type, ctx, (JsonObject)data.DeepClone());
    }

    public void Complete(ServerEvent final)
    {
        _channel.Writer.TryWrite(final);
        _channel.Writer.TryComplete();
    }

    private void Write(string type, RunContext ctx, JsonObject data)
    {
        data["agent"] = ctx.Agent.Id;
        data["depth"] = ctx.Depth;
        data["run_id"] = ctx.RunId.ToString();
        _channel.Writer.TryWrite(new ServerEvent(type, data));
    }
}
