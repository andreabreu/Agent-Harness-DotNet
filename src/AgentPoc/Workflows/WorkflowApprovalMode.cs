using AgentPoc.Harness;

namespace AgentPoc.Workflows;

/// <summary>
/// Modo de aprovacao de uma execucao de workflow. Sem --approval nem APPROVAL_MODE, nega: nao ha quem responda.
/// </summary>
public sealed record WorkflowApprovalMode(string Mode, bool Explicit)
{
    private static readonly string[] AcceptedModes = { "deny", "auto", "ask" };

    public static readonly WorkflowApprovalMode Default = new("deny", false);

    /// <summary>A flag vence a variavel de ambiente; valores fora de deny/auto/ask sao rejeitados.</summary>
    public static WorkflowApprovalMode From(string? flag, string? env)
    {
        var chosen = !string.IsNullOrWhiteSpace(flag) ? flag : env;
        if (string.IsNullOrWhiteSpace(chosen)) return Default;
        var mode = chosen.Trim().ToLowerInvariant();
        if (!AcceptedModes.Contains(mode))
            throw new ArgumentException($"Modo de aprovacao invalido '{chosen}'. Use um de: {string.Join(", ", AcceptedModes)}.");
        return new WorkflowApprovalMode(mode, true);
    }

    public IHumanGate Gate() => ModeHumanGate.For(Mode, Explicit);

    public string Describe() => $"{Mode} ({(Explicit ? "explicito" : "padrao de workflow")})";
}
