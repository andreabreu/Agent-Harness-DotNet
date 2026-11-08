namespace AgentPoc.Workflows;

public enum NodeState { Pending, Running, Completed, Failed, Skipped }

/// <summary>Resultado de uma rodada: nos liberados (ja marcados como running) e nos que passaram a ser pulados.</summary>
public sealed record SchedulerStep(IReadOnlyList<string> Ready, IReadOnlyList<string> Skipped);

/// <summary>
/// Decide quem roda e quem e pulado, sem banco nem execucao. Uma aresta A -> B e ativa quando A concluiu (e, se A e
/// desvio, B foi escolhido), inativa quando A foi pulado ou o desvio nao escolheu B, e pendente nos outros casos.
/// Joins: all (todas ativas), all_active (nenhuma pendente e ao menos uma ativa), any (a primeira ativa).
/// </summary>
public sealed class WorkflowScheduler
{
    private enum Edge { Pending, Active, Inactive }

    private readonly WorkflowDefinition _definition;
    private readonly Dictionary<string, NodeState> _states;
    private readonly Dictionary<string, IReadOnlyList<string>> _chosen = new(StringComparer.Ordinal);

    public WorkflowScheduler(WorkflowDefinition definition)
    {
        _definition = definition;
        _states = definition.Ordered.ToDictionary(n => n.Id, _ => NodeState.Pending, StringComparer.Ordinal);
    }

    public bool HasPending => _states.Values.Any(s => s == NodeState.Pending);
    public bool HasRunning => _states.Values.Any(s => s == NodeState.Running);

    public NodeState StateOf(string node) => _states[node];

    /// <summary>Estado vindo do checkpoint (retomada); um desvio concluido traz os destinos escolhidos.</summary>
    public void Restore(string node, NodeState state, IReadOnlyList<string>? chosenNext = null)
    {
        _states[node] = state;
        if (chosenNext != null) _chosen[node] = chosenNext;
    }

    public void Complete(string node, IReadOnlyList<string>? chosenNext = null) => Restore(node, NodeState.Completed, chosenNext);

    public void Fail(string node) => _states[node] = NodeState.Failed;

    /// <param name="limit">Maximo de nos running ao mesmo tempo (max_parallel); os demais prontos ficam para depois.</param>
    public SchedulerStep Next(int? limit = null)
    {
        var skipped = new List<string>();
        for (var changed = true; changed;)
        {
            var newlySkipped = Pending().Where(n => Decide(n) == NodeState.Skipped).ToList();
            foreach (var node in newlySkipped) _states[node] = NodeState.Skipped;
            skipped.AddRange(newlySkipped);
            changed = newlySkipped.Count > 0;
        }
        var slots = limit is { } max ? Math.Max(0, max - _states.Values.Count(st => st == NodeState.Running)) : int.MaxValue;
        var ready = Pending().Where(n => Decide(n) == NodeState.Running).Take(slots).ToList();
        foreach (var node in ready) _states[node] = NodeState.Running;
        return new SchedulerStep(ready, skipped);
    }

    /// <summary>Um no end executou: tudo o que ainda nao rodou e pulado.</summary>
    public IReadOnlyList<string> Finish()
    {
        var skipped = Pending().ToList();
        foreach (var node in skipped) _states[node] = NodeState.Skipped;
        return skipped;
    }

    private IEnumerable<string> Pending() =>
        _definition.Ordered.Select(n => n.Id).Where(id => _states[id] == NodeState.Pending);

    /// <summary>Running = pode rodar; Skipped = nunca vai rodar; Pending = esperar.</summary>
    private NodeState Decide(string node)
    {
        var edges = _definition.PredecessorsOf(node).Select(from => EdgeState(from, node)).ToList();
        if (edges.Count == 0) return NodeState.Running;
        return _definition.Nodes[node].Join switch
        {
            JoinMode.All when edges.Contains(Edge.Inactive) => NodeState.Skipped,
            JoinMode.All => edges.All(e => e == Edge.Active) ? NodeState.Running : NodeState.Pending,
            JoinMode.AllActive when edges.Contains(Edge.Pending) => NodeState.Pending,
            JoinMode.AllActive => edges.Contains(Edge.Active) ? NodeState.Running : NodeState.Skipped,
            _ when edges.Contains(Edge.Active) => NodeState.Running,
            _ => edges.All(e => e == Edge.Inactive) ? NodeState.Skipped : NodeState.Pending
        };
    }

    private Edge EdgeState(string from, string to) => _states[from] switch
    {
        NodeState.Skipped => Edge.Inactive,
        NodeState.Completed when !_definition.Nodes[from].IsCondition => Edge.Active,
        NodeState.Completed => _chosen.TryGetValue(from, out var chosen) && chosen.Contains(to) ? Edge.Active : Edge.Inactive,
        _ => Edge.Pending
    };
}
