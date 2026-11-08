namespace AgentPoc.Workflows;

public sealed record CompensationCandidate(string Node, DateTime FinishedAt, bool HasCompensation, bool Compensated);

/// <summary>
/// Quem compensar quando o workflow falha: os nos concluidos que declaram compensate e ainda nao foram compensados,
/// do ultimo a concluir para o primeiro (desfaz na ordem inversa do que foi feito).
/// </summary>
public static class CompensationPlan
{
    public static IReadOnlyList<string> Order(IEnumerable<CompensationCandidate> completed) =>
        completed.Where(c => c.HasCompensation && !c.Compensated)
            .OrderByDescending(c => c.FinishedAt)
            .Select(c => c.Node)
            .ToList();
}
