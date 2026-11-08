namespace AgentPoc.Harness;

public enum RecoveryAction { Repeat, AskHuman, Skip, UseInformedResult }

public sealed record RecoveryDecision(RecoveryAction Action, string? InformedResult = null);

/// <summary>
/// Idempotencia na retomada. Uma tool call que comecou (tool_call_started) e nao gravou resultado pode ou nao ter
/// executado antes da queda: sem efeito colateral, repete; com efeito, nao repete sozinha e pergunta a uma pessoa
/// se deve repetir, pular ou usar um resultado informado por ela.
/// </summary>
public static class ToolRecovery
{
    public const string QuestionSuffix = "#retomada";
    public const string RepeatOption = "repetir";
    public const string SkipOption = "pular";
    public static readonly string[] Options = { RepeatOption, SkipOption };

    public static RecoveryAction ForInterrupted(bool sideEffect) => sideEffect ? RecoveryAction.AskHuman : RecoveryAction.Repeat;

    /// <summary>Id do pedido humano da retomada (diferente do id da aprovacao da propria chamada).</summary>
    public static string QuestionId(string toolCallId) => toolCallId + QuestionSuffix;

    public static string Question(string tool, string argsJson) =>
        $"A chamada {tool}({argsJson}) pode ter executado antes da queda do processo. Repetir, pular ou informar o resultado?";

    /// <summary>Resposta da pessoa: "repetir", "pular" ou qualquer outro texto como o resultado da chamada. Sem resposta, pula.</summary>
    public static RecoveryDecision FromAnswer(string answer)
    {
        var text = answer.Trim();
        if (text.StartsWith("SEM RESPOSTA", StringComparison.Ordinal) || text.Length == 0) return new RecoveryDecision(RecoveryAction.Skip);
        if (text.Equals(RepeatOption, StringComparison.OrdinalIgnoreCase)) return new RecoveryDecision(RecoveryAction.Repeat);
        if (text.Equals(SkipOption, StringComparison.OrdinalIgnoreCase)) return new RecoveryDecision(RecoveryAction.Skip);
        return new RecoveryDecision(RecoveryAction.UseInformedResult, text);
    }

    public static string SkippedResult(string tool) =>
        $"PULADO: a chamada {tool} foi interrompida por uma queda e a pessoa decidiu nao repetir. Pode ou nao ter executado; siga sem ela.";

    public static string InformedResult(string tool, string result) =>
        $"Resultado de {tool} informado pela pessoa apos a queda: {result}";
}
