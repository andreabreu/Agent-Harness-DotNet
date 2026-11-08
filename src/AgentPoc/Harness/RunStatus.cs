using AgentPoc.Core;

namespace AgentPoc.Harness;

/// <summary>
/// Estados de um run e as transicoes da suspensao duravel:
/// running -> waiting_human (pediu uma pessoa) | waiting_child (um subagente suspendeu)
/// waiting_* -> ready (decisao gravada, prazo vencido ou filho terminou) -> running (despachante retomou)
/// waiting_* | ready -> cancelled (cancelamento da cadeia).
/// </summary>
public static class RunStatus
{
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string BudgetExceeded = "budget_exceeded";
    public const string MaxSteps = "max_steps";
    public const string Cancelled = "cancelled";
    public const string WaitingHuman = "waiting_human";
    public const string WaitingChild = "waiting_child";
    public const string Ready = "ready";

    /// <summary>Parados esperando algo externo, sem processo preso; cancelaveis direto no banco.</summary>
    public static readonly string[] Suspended = { WaitingHuman, WaitingChild, Ready };

    public static bool IsTerminal(string status) =>
        status is Completed or Failed or BudgetExceeded or MaxSteps or Cancelled;

    public static bool IsSuspended(string status) => Suspended.Contains(status);

    public static string ForSuspension(RunSuspendedException suspension) =>
        suspension is ChildSuspendedException ? WaitingChild : WaitingHuman;

    /// <summary>O pai deve receber o resultado agora: filho terminou e o pai esta parado exatamente nele.</summary>
    public static bool ShouldDeliverToParent(string childStatus, Guid childId, string parentStatus, Guid? parentWaitingOn) =>
        IsTerminal(childStatus) && parentStatus == WaitingChild && parentWaitingOn == childId;
}

/// <summary>Como o resultado de um subagente aparece para o pai, como resultado da ferramenta delegate_task.</summary>
public static class DelegationResult
{
    private const int CompletedOutputChars = 6000;
    private const int FailedOutputChars = 2000;

    public static string Format(string agentId, string status, int steps, string output) =>
        status == RunStatus.Completed
            ? $"[resultado de {agentId} | status={status} | passos={steps}]\n{Text.Trunc(output, CompletedOutputChars)}"
            : $"ERRO: o subagente {agentId} terminou como {status} (passos={steps}): {Text.Trunc(output, FailedOutputChars)}";

    public static bool IsSuccess(string formatted) => !formatted.StartsWith("ERRO", StringComparison.Ordinal);
}
