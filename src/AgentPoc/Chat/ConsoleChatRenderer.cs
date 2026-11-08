using System.Text.Json.Nodes;
using AgentPoc.Core;

namespace AgentPoc.Chat;

/// <summary>
/// Mostra no terminal o que o agente e seus subagentes estao fazendo: eventos gravados (de qualquer instancia)
/// e o texto ao vivo quando o run executa neste processo.
/// </summary>
public sealed class ConsoleChatRenderer
{
    private const int ArgsPreviewChars = 110;
    private const int ResultPreviewChars = 130;
    private static readonly HashSet<string> ToolsRenderedByTheirOwnEvent = new() { "think", "update_plan", "delegate_task", "ask_human" };

    private readonly object _gate = new();
    private bool _midLine;
    private bool _answering;
    private bool _streamedSinceLastMessage;

    public int TextDeltas { get; private set; }

    public void BeginTurn()
    {
        lock (_gate)
        {
            _midLine = false;
            _answering = false;
            _streamedSinceLastMessage = false;
            TextDeltas = 0;
        }
    }

    public void EndTurn()
    {
        lock (_gate) BreakLine();
    }

    public void Live(LiveDelta delta)
    {
        if (delta.Depth > 0) return;
        lock (_gate)
        {
            if (delta.Type == "reasoning")
            {
                Log.Inline(delta.Text, ConsoleColor.DarkGray);
                _midLine = true;
                return;
            }
            TextDeltas++;
            _streamedSinceLastMessage = true;
            WriteAnswer(delta.Text);
        }
    }

    public void Render(FeedEvent e)
    {
        if (e.Type == "assistant_message")
        {
            RenderMessage(e);
            return;
        }
        var line = Describe(e);
        if (line == null) return;
        lock (_gate)
        {
            BreakLine();
            _answering = false;
            Log.Inline(new string(' ', 2 + e.Depth * 4));
            Log.Inline(line.Value.Text + "\n", line.Value.Color);
        }
    }

    /// <summary>Texto completo de um passo: so imprime se nao veio ao vivo (run executado por outra instancia).</summary>
    private void RenderMessage(FeedEvent e)
    {
        if (e.Depth > 0) return;
        lock (_gate)
        {
            if (!_streamedSinceLastMessage) WriteAnswer(S(e.Data, "content"));
            _streamedSinceLastMessage = false;
        }
    }

    private void WriteAnswer(string text)
    {
        if (!_answering)
        {
            BreakLine();
            Log.Inline("assistente> ", ConsoleColor.Green);
            _answering = true;
        }
        Log.Inline(text);
        _midLine = !text.EndsWith('\n');
    }

    private static string S(JsonObject data, string key) =>
        data[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : data[key]?.ToJsonString() ?? "";

    private static (string Text, ConsoleColor Color)? Describe(FeedEvent e)
    {
        var data = e.Data;
        var who = e.Depth > 0 ? $"[{e.Agent}] " : "";
        switch (e.Type)
        {
            case "tool_call":
            {
                var name = S(data, "name");
                if (ToolsRenderedByTheirOwnEvent.Contains(name)) return null;
                var args = data["args"]?.ToJsonString(Text.RelaxedJson) ?? "{}";
                return ($"{who}> {name}({OneLine(args, ArgsPreviewChars)})", ConsoleColor.DarkYellow);
            }
            case "tool_result":
            {
                var name = S(data, "name");
                if (ToolsRenderedByTheirOwnEvent.Contains(name) && name != "delegate_task") return null;
                var ok = data["ok"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
                return ($"{who}  {(ok ? "ok" : "falhou")} {S(data, "ms")}ms: {OneLine(S(data, "output"), ResultPreviewChars)}", ok ? ConsoleColor.DarkGreen : ConsoleColor.DarkRed);
            }
            case "tools_loaded":
                return data["tools"] is JsonArray { Count: > 0 } tools
                    ? ($"{who}descobriu ferramentas: {string.Join(", ", tools)}", ConsoleColor.Blue)
                    : ($"{who}search_tools(\"{S(data, "query")}\"): nada novo", ConsoleColor.DarkGray);
            case "thinking":
                return ($"{who}pensando: {OneLine(S(data, "thought"), 300)}", ConsoleColor.DarkGray);
            case "plan_updated":
                return (PlanText(who, data["steps"] as JsonArray, 2 + e.Depth * 4), ConsoleColor.Cyan);
            case "delegation_start":
                return ($"{who}delegando para {S(data, "to_agent")}: {OneLine(S(data, "task"), 160)}", ConsoleColor.Magenta);
            case "delegation_end":
                return ($"{who}{S(data, "to_agent")} terminou: {S(data, "status")} ({S(data, "tokens")} tokens)", ConsoleColor.Magenta);
            case "task_created":
                return ($"{who}tarefa em segundo plano para {S(data, "agent")}: {S(data, "task_id")}", ConsoleColor.DarkYellow);
            case "policy":
                var decision = S(data, "decision");
                return ($"{who}politica {S(data, "tool")}: {decision} ({S(data, "reason")})", decision == "approved" ? ConsoleColor.Green : ConsoleColor.Red);
            case "waiting_human":
                return ($"{who}pausado esperando uma pessoa", ConsoleColor.Yellow);
            case "waiting_child":
                return ($"{who}pausado esperando o subagente {S(data, "child_agent")}", ConsoleColor.Yellow);
            case "run_resumed":
                return ($"{who}retomado por {S(data, "by")}", ConsoleColor.Magenta);
            case "budget_exceeded":
                return ($"{who}budget de tokens esgotado (usados {S(data, "used")} de {S(data, "limit")})", ConsoleColor.Red);
            case "error":
                return ($"{who}erro: {S(data, "message")}", ConsoleColor.Red);
            case "cancelled":
                return ($"{who}cancelado {S(data, "reason")}".TrimEnd(), ConsoleColor.Yellow);
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
