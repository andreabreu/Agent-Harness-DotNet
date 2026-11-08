using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentPoc.Core;
using AgentPoc.Llm;
using AgentPoc.Tools;
using EventLog = AgentPoc.Harness.EventLog;

namespace AgentPoc.Harness;

/// <summary>Valida, aplica politica (auto/approval/deny) e executa uma chamada de ferramenta.</summary>
public sealed class ToolExecutor
{
    private readonly BuiltinTools _builtins;
    private readonly McpManager _mcp;
    private readonly EventLog _events;

    public ToolExecutor(BuiltinTools builtins, McpManager mcp, EventLog events)
    {
        _builtins = builtins;
        _mcp = mcp;
        _events = events;
    }

    public async Task<string> ExecuteAsync(RunContext ctx, ToolCall call, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        JsonObject args;
        try
        {
            args = JsonNode.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson) as JsonObject ?? new JsonObject();
        }
        catch (JsonException ex)
        {
            return await Finish(ctx, call.Name, false, sw, $"ERRO: argumentos JSON invalidos ({ex.Message}). Corrija e tente novamente.");
        }

        await _events.AddAsync(ctx, "tool_call", new JsonObject { ["name"] = call.Name, ["args"] = args.DeepClone() });
        Log.Write(ctx.Depth, ConsoleColor.Yellow, "tool", $"{ctx.Agent.Id} -> {call.Name}({Text.Trunc(args.ToJsonString(Text.RelaxedJson), 160)})");

        if (!ctx.ActiveTools.TryGetValue(call.Name, out var def))
            return await Finish(ctx, call.Name, false, sw, $"ERRO: a ferramenta '{call.Name}' nao esta carregada. Use search_tools para encontra-la.");

        var policy = ToolPolicy.Resolve(def.Policy, def.ConfigJson, args);
        if (policy.Decision == PolicyDecision.Deny)
            return await BlockAsync(ctx, def, sw, policy.Reason);

        if (policy.Decision == PolicyDecision.Approval)
        {
            var decision = await ctx.Human.ApproveAsync(ctx, call.Id, def.Name, args, policy.Reason, ct);
            var policyEvent = new JsonObject
            {
                ["tool"] = def.Name,
                ["decision"] = decision.Approved ? "approved" : "denied",
                ["reason"] = decision.Reason,
                ["policy_reason"] = policy.Reason
            };
            if (decision.EditedArgs != null)
            {
                policyEvent["edited"] = true;
                policyEvent["original_args"] = args.DeepClone();
                policyEvent["args"] = decision.EditedArgs.DeepClone();
            }
            await _events.AddAsync(ctx, "policy", policyEvent);
            Log.Write(ctx.Depth, decision.Approved ? ConsoleColor.Green : ConsoleColor.Red, "politica", $"{def.Name}: {decision.Reason}");
            if (!decision.Approved)
                return await Finish(ctx, call.Name, false, sw, $"NEGADO: a execucao de '{def.Name}' nao foi aprovada ({decision.Reason}). Nao tente contornar; leve o motivo em conta e informe ao solicitante.");

            if (decision.EditedArgs != null)
            {
                args = decision.EditedArgs;
                if (ToolPolicy.Resolve(def.Policy, def.ConfigJson, args) is { Decision: PolicyDecision.Deny } recheck)
                    return await BlockAsync(ctx, def, sw, $"argumentos editados violam {recheck.Reason}");
            }
        }

        string output;
        try
        {
            output = def.Kind switch
            {
                "builtin" => await _builtins.InvokeAsync(def.Name, args, ctx, call.Id, ct),
                "http" => await HttpTool.InvokeAsync(def, args, ct),
                "mcp" => await _mcp.CallAsync(def, args, ct),
                _ => $"ERRO: tipo de ferramenta desconhecido '{def.Kind}'."
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (HumanInputRequiredException)
        {
            throw;
        }
        catch (Exception ex)
        {
            output = $"ERRO: {ex.GetType().Name}: {ex.Message}";
        }

        var ok = !output.StartsWith("ERRO", StringComparison.Ordinal);
        return await Finish(ctx, call.Name, ok, sw, output);
    }

    private async Task<string> BlockAsync(RunContext ctx, ToolDef def, Stopwatch sw, string reason)
    {
        await _events.AddAsync(ctx, "policy", new JsonObject { ["tool"] = def.Name, ["decision"] = "blocked", ["reason"] = reason });
        Log.Write(ctx.Depth, ConsoleColor.Red, "politica", $"{def.Name} BLOQUEADA ({reason})");
        return await Finish(ctx, def.Name, false, sw, $"BLOQUEADO: '{def.Name}' nao pode executar ({reason}). Nao tente novamente nem contorne; informe ao solicitante.");
    }

    private async Task<string> Finish(RunContext ctx, string name, bool ok, Stopwatch sw, string output)
    {
        output = Text.Trunc(output, 8000);
        await _events.AddAsync(ctx, "tool_result", new JsonObject
        {
            ["name"] = name,
            ["ok"] = ok,
            ["ms"] = sw.ElapsedMilliseconds,
            ["output"] = Text.Trunc(output, 2000)
        });
        Log.Write(ctx.Depth, ok ? ConsoleColor.DarkGreen : ConsoleColor.DarkRed, ok ? "resultado" : "falha", $"{name} ({sw.ElapsedMilliseconds}ms): {Text.Trunc(output, 140)}");
        return output;
    }
}
