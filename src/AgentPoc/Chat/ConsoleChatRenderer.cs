using System.Text.Json.Nodes;
using AgentPoc.Core;
using AgentPoc.Harness;

namespace AgentPoc.Chat;

/// <summary>Mostra no terminal, ao vivo, o que o agente e seus subagentes estao fazendo.</summary>
public sealed class ConsoleChatRenderer : IRunObserver
{
    private const int ArgsPreviewChars = 110;
    private const int ResultPreviewChars = 130;
    private static readonly HashSet<string> ToolsRenderedByTheirOwnEvent = new() { "think", "update_plan", "delegate_task" };

    private readonly object _gate = new();
    private bool _midLine;
    private bool _answering;

    public int TextDeltas { get; private set; }

    public void BeginTurn()
    {
        lock (_gate)
        {
            _midLine = false;
            _answering = false;
            TextDeltas = 0;
        }
    }

    public void EndTurn()
    {
        lock (_gate) BreakLine();
    }

    public void OnTextDelta(RunContext ctx, string delta)
    {
        if (ctx.Depth > 0) return;
        lock (_gate)
        {
            TextDeltas++;
            if (!_answering)
            {
                BreakLine();
                Log.Inline("assistente> ", ConsoleColor.Green);
                _answering = true;
            }
            Log.Inline(delta);
            _midLine = !delta.EndsWith('\n');
        }
    }

    public void OnReasoningDelta(RunContext ctx, string delta)
    {
        if (ctx.Depth > 0) return;
        lock (_gate)
        {
            Log.Inline(delta, ConsoleColor.DarkGray);
            _midLine = true;
        }
    }

    public void OnEvent(RunContext ctx, string type, JsonObject data)
    {
        var line = Describe(ctx, type, data);
        if (line == null) return;
        lock (_gate)
        {
            BreakLine();
            _answering = false;
            Log.Inline(new string(' ', 2 + ctx.Depth * 4));
            Log.Inline(line.Value.Text + "\n", line.Value.Color);
        }
    }

    private (string Text, ConsoleColor Color)? Describe(RunContext ctx, string type, JsonObject data)
    {
        string S(string key) => data[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : data[key]?.ToJsonString() ?? "";
        var who = ctx.Depth > 0 ? $"[{ctx.Agent.Id}] " : "";
        switch (type)
        {
            case "tool_call":
            {
                var name = S("name");
                if (ToolsRenderedByTheirOwnEvent.Contains(name)) return null;
                var args = data["args"]?.ToJsonString(Text.RelaxedJson) ?? "{}";
                return ($"{who}> {name}({OneLine(args, ArgsPreviewChars)})", ConsoleColor.DarkYellow);
            }
            case "tool_result":
            {
                var name = S("name");
                if (ToolsRenderedByTheirOwnEvent.Contains(name)) return null;
                var ok = data["ok"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
                return ($"{who}  {(ok ? "ok" : "falhou")} {S("ms")}ms: {OneLine(S("output"), ResultPreviewChars)}", ok ? ConsoleColor.DarkGreen : ConsoleColor.DarkRed);
            }
            case "tools_loaded":
                return data["tools"] is JsonArray { Count: > 0 } tools
                    ? ($"{who}descobriu ferramentas: {string.Join(", ", tools)}", ConsoleColor.Blue)
                    : ($"{who}search_tools(\"{S("query")}\"): nada novo", ConsoleColor.DarkGray);
            case "thinking":
                return ($"{who}pensando: {OneLine(S("thought"), 300)}", ConsoleColor.DarkGray);
            case "plan_updated":
                return (PlanText(who, data["steps"] as JsonArray, 2 + ctx.Depth * 4), ConsoleColor.Cyan);
            case "delegation_start":
                return ($"{who}delegando para {S("to_agent")}: {OneLine(S("task"), 160)}", ConsoleColor.Magenta);
            case "delegation_end":
                return ($"{who}{S("to_agent")} terminou: {S("status")} ({S("tokens")} tokens)", ConsoleColor.Magenta);
            case "task_created":
                return ($"{who}tarefa em segundo plano para {S("agent")}: {S("task_id")}", ConsoleColor.DarkYellow);
            case "policy":
                var decision = S("decision");
                return ($"{who}politica {S("tool")}: {decision} ({S("reason")})", decision == "approved" ? ConsoleColor.Green : ConsoleColor.Red);
            case "budget_exceeded":
                return ($"{who}budget de tokens esgotado (usados {S("used")} de {S("limit")})", ConsoleColor.Red);
            case "error":
                return ($"{who}erro: {S("message")}", ConsoleColor.Red);
            case "cancelled":
                return ($"{who}cancelado", ConsoleColor.Yellow);
            default:
                return null;
        }
    }

    private static string PlanText(string who, JsonArray? steps, int indent)
    {
        if (steps == null || steps.Count == 0) return $"{who}plano vazio";
        var pad = new string(' ', indent);
        var lines = steps.Select(s =>
        {
            var status = s?["status"]?.GetValue<string>() ?? "pending";
            var mark = status switch { "done" => "[x]", "in_progress" => "[~]", _ => "[ ]" };
            return $"{pad}  {mark} {s?["step"]?.GetValue<string>()}";
        });
        return $"{who}plano:\n" + string.Join("\n", lines);
    }

    private static string OneLine(string s, int max) => Text.Trunc(s.Replace("\r", "").Replace("\n", " "), max);

    private void BreakLine()
    {
        if (!_midLine) return;
        Log.Inline("\n");
        _midLine = false;
    }
}
