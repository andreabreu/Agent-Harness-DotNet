using AgentPoc.Harness;

namespace AgentPoc.Workflows;

public enum NodeResumeAction { RunFromScratch, UseResult, TakeOverAndResume, Wait }

/// <summary>
/// O que fazer, na retomada, com um no que ficou running: depende do run que ele iniciou. Sem run, executa do zero;
/// run terminado (queda entre o fim do agente e o checkpoint), usa o resultado; run orfao ou pronto, assume e
/// continua pelo historico; run com dono vivo, espera.
/// </summary>
public static class NodeResumeDecision
{
    public static NodeResumeAction For(string? runStatus, DateTime? leaseUntil, DateTime now) => runStatus switch
    {
        null => NodeResumeAction.RunFromScratch,
        _ when RunStatus.IsTerminal(runStatus) => NodeResumeAction.UseResult,
        RunStatus.Ready => NodeResumeAction.TakeOverAndResume,
        _ when RunLeaseOptions.IsOrphan(runStatus, leaseUntil, now) => NodeResumeAction.TakeOverAndResume,
        _ => NodeResumeAction.Wait
    };
}
