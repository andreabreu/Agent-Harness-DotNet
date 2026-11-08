using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AgentPoc.Tools;

namespace AgentPoc.Harness;

public enum PolicyDecision { Auto, Approval, Deny }

public sealed record PolicyResult(PolicyDecision Decision, string Reason);

/// <summary>
/// Politica efetiva de uma chamada: a politica da ferramenta refinada por regras sobre os argumentos,
/// definidas em tools.config:
///   "deny_when":     {"arg": "regex"}  bloqueia sem perguntar (ex.: rm -rf, sudo)
///   "approval_when": {"arg": "regex"}  exige aprovacao mesmo com politica auto (ex.: url interna)
/// </summary>
public static class ToolPolicy
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(200);

    public static PolicyResult Resolve(string policy, string configJson, JsonObject args)
    {
        if (policy == "deny") return new PolicyResult(PolicyDecision.Deny, "politica deny no registro de ferramentas");
        var config = ParseConfig(configJson);

        if (FirstMatch(config?["deny_when"], args) is { } denied)
            return new PolicyResult(PolicyDecision.Deny, $"regra deny_when: '{denied}'");
        if (policy == "approval")
            return new PolicyResult(PolicyDecision.Approval, "politica approval no registro de ferramentas");
        if (FirstMatch(config?["approval_when"], args) is { } escalated)
            return new PolicyResult(PolicyDecision.Approval, $"regra approval_when: '{escalated}'");
        return new PolicyResult(PolicyDecision.Auto, "politica auto");
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
