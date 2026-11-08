using System.Text.Json.Nodes;
using AgentPoc.Core;
using AgentPoc.Llm;

namespace AgentPoc.Harness;

/// <summary>
/// Budget de tokens hierarquico: o filho nunca gasta mais que o saldo do pai,
/// e todo consumo do filho e descontado do pai.
/// </summary>
public sealed class Budget
{
    private readonly Budget? _parent;
    private long _used;

    public Budget(long limit, Budget? parent = null)
    {
        Limit = Math.Max(0, limit);
        _parent = parent;
    }

    public long Limit { get; }
    public long Used => Interlocked.Read(ref _used);

    public long Remaining
    {
        get
        {
            var own = Limit - Used;
            return _parent == null ? own : Math.Min(own, _parent.Remaining);
        }
    }

    public bool Exhausted => Remaining <= 0;

    public void Consume(long tokens)
    {
        Interlocked.Add(ref _used, tokens);
        _parent?.Consume(tokens);
    }

    public Budget Child(long childLimit) => new(Math.Min(childLimit, Math.Max(0, Remaining)), this);
}

/// <summary>Estado de um run de agente em execucao.</summary>
public sealed class RunContext
{
    public required Guid RunId { get; init; }
    public Guid? ParentRunId { get; init; }
    public required Guid RootRunId { get; init; }
    public Guid? WorkflowRunId { get; init; }
    public required AgentCfg Agent { get; init; }
    public required ModelCfg Model { get; init; }
    public required int Depth { get; init; }
    public required Budget Budget { get; init; }
    public Guid? ConversationId { get; init; }

    /// <summary>Quem acompanha o run ao vivo (chat, API). Herdado pelos subagentes.</summary>
    public IRunObserver? Observer { get; init; }

    /// <summary>Quem responde pelo humano: aprovacoes e perguntas (ask_human). Herdado pelos subagentes.</summary>
    public IHumanGate Human { get; init; } = ModeHumanGate.Instance;

    /// <summary>Ferramentas que o modelo enxerga agora. Cresce via search_tools.</summary>
    public Dictionary<string, ToolDef> ActiveTools { get; } = new(StringComparer.Ordinal);

    /// <summary>Ultimo seq de evento ja gravado (diferente de zero quando o run e retomado).</summary>
    public int SeqStart { init => _seq = value; }

    private int _seq;
    public int NextSeq() => Interlocked.Increment(ref _seq);
}

/// <summary>Grava eventos do run (fonte da verdade para auditoria e evidencia).</summary>
public sealed class EventLog
{
    private readonly Db _db;
    public EventLog(Db db) => _db = db;

    public async Task AddAsync(RunContext ctx, string type, JsonObject data)
    {
        await _db.ExecAsync(
            "insert into run_events (run_id, seq, type, data) values (@r, @s, @t, @d)",
            ("r", ctx.RunId), ("s", ctx.NextSeq()), ("t", type), ("d", data));
        ctx.Observer?.OnEvent(ctx, type, data);
    }
}

/// <summary>Recebe ao vivo os eventos de um run e dos seus subagentes (os mesmos gravados em run_events) e o texto em streaming.</summary>
public interface IRunObserver
{
    void OnEvent(RunContext ctx, string type, JsonObject data);
    void OnTextDelta(RunContext ctx, string delta);
    void OnReasoningDelta(RunContext ctx, string delta);
}

/// <summary>Decisao humana sobre uma ferramenta; o humano pode corrigir os argumentos antes de aprovar.</summary>
public sealed record ApprovalDecision(bool Approved, string Reason, JsonObject? EditedArgs = null);

/// <summary>Pedido ao humano: aprovar uma tool call ou responder uma pergunta do agente.</summary>
public interface IHumanGate
{
    Task<ApprovalDecision> ApproveAsync(RunContext ctx, string toolCallId, string tool, JsonObject args, string policyReason, CancellationToken ct);
    Task<string> AskAsync(RunContext ctx, string toolCallId, string question, IReadOnlyList<string> options, CancellationToken ct);
}

/// <summary>
/// O run deve parar e esperar uma pessoa sem segurar processo. O harness grava o estado (waiting_human)
/// e o run e retomado depois, a partir do historico persistido, quando a decisao chegar.
/// </summary>
public sealed class HumanInputRequiredException : Exception
{
    public Guid RequestId { get; }
    public HumanInputRequiredException(Guid requestId, string what) : base($"Aguardando decisao humana: {what}") => RequestId = requestId;
}

public static class Approval
{
    /// <summary>auto | deny | ask. Modo padrao da CLI, controlado por APPROVAL_MODE ou --approval.</summary>
    public static string Mode { get; set; } = "auto";
}

/// <summary>Gate da CLI: segue Approval.Mode (auto aprova, deny nega, ask pergunta no terminal).</summary>
public sealed class ModeHumanGate : IHumanGate
{
    public const string NoHumanAnswer =
        "SEM RESPOSTA: nao ha uma pessoa disponivel neste modo de execucao. Siga com a opcao mais segura e deixe a suposicao explicita no resultado.";

    public static readonly ModeHumanGate Instance = new();
    private static readonly SemaphoreSlim ConsoleGate = new(1, 1);

    public async Task<ApprovalDecision> ApproveAsync(RunContext ctx, string toolCallId, string tool, JsonObject args, string policyReason, CancellationToken ct)
    {
        switch (Approval.Mode)
        {
            case "deny":
                return new ApprovalDecision(false, "negado automaticamente (APPROVAL_MODE=deny)");
            case "ask":
                await ConsoleGate.WaitAsync(ct);
                try
                {
                    return AskConsoleApproval(ctx, tool, args, policyReason);
                }
                finally
                {
                    ConsoleGate.Release();
                }
            default:
                return new ApprovalDecision(true, "aprovado automaticamente (APPROVAL_MODE=auto)");
        }
    }

    public async Task<string> AskAsync(RunContext ctx, string toolCallId, string question, IReadOnlyList<string> options, CancellationToken ct)
    {
        if (Approval.Mode != "ask") return NoHumanAnswer;
        await ConsoleGate.WaitAsync(ct);
        try
        {
            Log.Raw("");
            Log.Raw($">>> PERGUNTA do agente '{ctx.Agent.Id}': {question}");
            if (options.Count > 0) Log.Raw($">>> Opcoes: {string.Join(" | ", options)}");
            Console.Write(">>> Resposta: ");
            var answer = (Console.ReadLine() ?? "").Trim();
            return answer.Length > 0 ? answer : NoHumanAnswer;
        }
        finally
        {
            ConsoleGate.Release();
        }
    }

    private static ApprovalDecision AskConsoleApproval(RunContext ctx, string tool, JsonObject args, string policyReason)
    {
        Log.Raw("");
        Log.Raw($">>> APROVACAO ({policyReason}): agente '{ctx.Agent.Id}' quer executar {tool}({Text.Trunc(args.ToJsonString(Text.RelaxedJson), 300)})");
        Console.Write(">>> Aprovar? [s]im / [n]ao / [e]ditar argumentos: ");
        var answer = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
        if (answer is "s" or "sim" or "y" or "yes") return new ApprovalDecision(true, "aprovado pelo operador");
        if (answer is "e" or "editar")
        {
            Console.Write(">>> Novos argumentos (JSON): ");
            if (JsonNode.Parse(Console.ReadLine() ?? "") is JsonObject edited)
                return new ApprovalDecision(true, "aprovado pelo operador com argumentos editados", edited);
            return new ApprovalDecision(false, "negado: JSON editado invalido");
        }
        Console.Write(">>> Motivo da negacao (opcional): ");
        var reason = (Console.ReadLine() ?? "").Trim();
        return new ApprovalDecision(false, reason.Length > 0 ? $"negado pelo operador: {reason}" : "negado pelo operador");
    }
}

/// <summary>Como acompanhar e governar um run (herdado pelos subagentes quando nulo).</summary>
public sealed record RunHooks(IRunObserver? Observer = null, IHumanGate? Human = null);

/// <summary>Continuacao de uma conversa: historico ja persistido e o que o harness deve restaurar.</summary>
public sealed record ChatTurn(
    Guid ConversationId,
    IReadOnlyList<ChatMessage> History,
    IReadOnlyCollection<string> ActiveTools,
    string? Summary,
    string? Memories,
    Func<Guid, ChatMessage, Task> OnMessage);

public sealed record RunResult(Guid RunId, Guid RootRunId, string Status, string Output, long TokensIn, long TokensOut, decimal CostUsd, int Steps)
{
    public IReadOnlyCollection<string> ActiveTools { get; init; } = Array.Empty<string>();

    /// <summary>Pedido humano que suspendeu o run (status waiting_human).</summary>
    public Guid? WaitingFor { get; init; }
}
