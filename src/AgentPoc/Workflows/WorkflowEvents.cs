using System.Text.Json.Nodes;
using AgentPoc.Core;

namespace AgentPoc.Workflows;

/// <summary>Eventos de uma execucao de workflow (workflow_events): recuperacao, tentativas, prazos, cancelamento e compensacao.</summary>
public static class WorkflowEvents
{
    public const string Recovered = "workflow_recovered";
    public const string DefinitionNotPinned = "definition_not_pinned";
    public const string NodeRetry = "node_retry";
    public const string NodeTimeout = "node_timeout";
    public const string NodeFailedOptional = "node_failed_optional";
    public const string Cancelled = "workflow_cancelled";
    public const string Expired = "workflow_expired";
    public const string CompensationStarted = "compensation_started";
    public const string NodeCompensated = "node_compensated";
    public const string CompensationFailed = "compensation_failed";

    public static Task AddAsync(IDbExecutor db, Guid workflowRunId, string type, string? nodeId, JsonObject data) =>
        db.ExecAsync("insert into workflow_events (workflow_run_id, type, node_id, data) values (@w, @t, @n, @d)",
            ("w", workflowRunId), ("t", type), ("n", nodeId), ("d", data));
}
