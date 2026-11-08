using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AgentPoc.Core;
using AgentPoc.Tools;

namespace AgentPoc.Harness;

public enum PolicyDecision { Auto, Approval, Deny }

public sealed record PolicyResult(PolicyDecision Decision, string Reason, string Source);

/// <summary>Origem de uma decisao de politica, gravada no evento policy (campo source).</summary>
public static class PolicySource
{
    public const string ToolPolicy = "tool_policy";
    public const string DenyWhen = "deny_when";
    public const string ApprovalWhen = "approval_when";
    public const string WorkflowBlock = "workflow_block";
    public const string WorkflowAllow = "workflow_allow";
    public const string Human = "human";
    public const string ModeDefault = "mode_default";
    public const string ModeExplicit = "mode_explicit";
}

/// <summary>
/// Ferramentas liberadas e bloqueadas por um workflow (bloco "tools" da definicao), com os curingas de allowed_tools.
/// </summary>
public sealed record WorkflowToolRules(string[] Allow, string[] Block)
{
    public static WorkflowToolRules? Parse(JsonObject definition) =>
        definition["tools"] is JsonObject tools ? new WorkflowToolRules(Patterns(tools["allow"]), Patterns(tools["block"])) : null;

    public bool Allows(string tool) => Glob.Match(Allow, tool);

    public bool Blocks(string tool) => Glob.Match(Block, tool);

    /// <summary>Padroes que nao casam com nenhuma ferramenta registrada (erro de digitacao seria uma falsa protecao).</summary>
    public string[] UnmatchedPatterns(IEnumerable<string> registeredTools)
    {
        var names = registeredTools.ToList();
        return Allow.Concat(Block).Distinct().Where(p => !names.Any(n => Glob.Match(new[] { p }, n))).ToArray();
    }

    private static string[] Patterns(JsonNode? list) =>
        list is JsonArray items ? items.Select(HttpTool.ArgString).Where(p => p.Length > 0).ToArray() : Array.Empty<string>();
}

/// <summary>
/// Politica efetiva de uma chamada: a politica da ferramenta refinada por regras sobre os argumentos,
/// definidas em tools.config, e pelas regras do workflow:
///   "deny_when":     {"arg": "regex"}  bloqueia sem perguntar (ex.: rm -rf, sudo)
///   "approval_when": {"arg": "regex"}  exige aprovacao mesmo com politica auto (ex.: url interna)
/// Bloqueios sempre vencem; "allow" do workflow so dispensa a politica approval da ferramenta.
/// </summary>
public static class ToolPolicy
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(200);

    public static PolicyResult Resolve(string policy, string configJson, JsonObject args, WorkflowToolRules? rules = null, string toolName = "")
    {
        if (policy == "deny") return new PolicyResult(PolicyDecision.Deny, "politica deny no registro de ferramentas", PolicySource.ToolPolicy);
        var config = ParseConfig(configJson);

        if (FirstMatch(config?["deny_when"], args) is { } denied)
            return new PolicyResult(PolicyDecision.Deny, $"regra deny_when: '{denied}'", PolicySource.DenyWhen);
        if (rules?.Blocks(toolName) == true)
            return new PolicyResult(PolicyDecision.Deny, "bloqueada pelas regras do workflow", PolicySource.WorkflowBlock);
        if (FirstMatch(config?["approval_when"], args) is { } escalated)
            return new PolicyResult(PolicyDecision.Approval, $"regra approval_when: '{escalated}'", PolicySource.ApprovalWhen);
        if (rules?.Allows(toolName) == true)
            return new PolicyResult(PolicyDecision.Auto, "liberada pelas regras do workflow", PolicySource.WorkflowAllow);
        if (policy == "approval")
            return new PolicyResult(PolicyDecision.Approval, "politica approval no registro de ferramentas", PolicySource.ToolPolicy);
        return new PolicyResult(PolicyDecision.Auto, "politica auto", PolicySource.ToolPolicy);
    }

    /// <summary>Descricao da primeira regra que casa ("arg ~ regex"), ou null.</summary>
    private static string? FirstMatch(JsonNode? rules, JsonObject args)
    {
        if (rules is not JsonObject byArg) return null;
        foreach (var (arg, pattern) in byArg)
        {
            var value = HttpTool.ArgString(args[arg]);
            var regex = HttpTool.ArgString(pattern);
            if (value.Length == 0 || regex.Length == 0) continue;
            try
            {
                if (Regex.IsMatch(value, regex, RegexOptions.IgnoreCase, RegexTimeout)) return $"{arg} ~ {regex}";
            }
            catch (RegexMatchTimeoutException)
            {
                return $"{arg} (regra demorou demais; tratada como correspondencia por seguranca)";
            }
        }
        return null;
    }

    private static JsonObject? ParseConfig(string json)
    {
        try { return JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json) as JsonObject; }
        catch (System.Text.Json.JsonException) { return null; }
    }
}
