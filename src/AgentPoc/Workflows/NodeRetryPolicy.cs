using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentPoc.Workflows;

/// <summary>Por que uma tentativa de no falhou (valores de retry.on).</summary>
public static class NodeFailure
{
    public const string Error = "error";
    public const string Timeout = "timeout";

    public static readonly string[] All = { Error, Timeout };
}

/// <summary>
/// Novas tentativas de um no (campo retry): ate max_attempts, so para as falhas listadas em on, com espera
/// backoff * factor^(tentativa-1). Uma ferramenta com efeito colateral so repete se o efeito nao chegou a comecar.
/// </summary>
public sealed record NodeRetryPolicy(int MaxAttempts, double BackoffSeconds, double Factor, IReadOnlyList<string> On)
{
    public const int MaxAllowedAttempts = 10;
    private const double DefaultBackoffSeconds = 1;
    private const double DefaultFactor = 2;

    public static readonly NodeRetryPolicy Default = new(1, DefaultBackoffSeconds, DefaultFactor, NodeFailure.All);

    /// <summary>Le o campo retry ja validado por <see cref="WorkflowDefinition"/>; ausente, uma tentativa.</summary>
    public static NodeRetryPolicy From(JsonNode? retry) => retry is not JsonObject o
        ? Default
        : new NodeRetryPolicy(
            o["max_attempts"]?.GetValue<int>() ?? Default.MaxAttempts,
            o["backoff_seconds"]?.GetValue<double>() ?? DefaultBackoffSeconds,
            o["factor"]?.GetValue<double>() ?? DefaultFactor,
            o["on"] is JsonArray on ? on.Select(v => v!.GetValue<string>()).ToArray() : NodeFailure.All);

    /// <param name="attempt">Tentativa que acabou de falhar (1 = a primeira).</param>
    public bool ShouldRetry(int attempt, string reason, bool effectStarted, bool sideEffect) =>
        attempt < MaxAttempts && On.Contains(reason) && !(sideEffect && effectStarted);

    /// <summary>Espera antes da tentativa seguinte a <paramref name="attempt"/>.</summary>
    public TimeSpan Wait(int attempt) => TimeSpan.FromSeconds(BackoffSeconds * Math.Pow(Factor, attempt - 1));

    /// <summary>Erros do campo retry no formato "no.retry.campo: problema".</summary>
    public static IEnumerable<string> Validate(JsonNode? retry, string at)
    {
        if (retry == null) yield break;
        if (retry is not JsonObject o)
        {
            yield return $"{at}: deve ser um objeto";
            yield break;
        }
        if (o["max_attempts"] is { } max && !(TryInt(max, out var n) && n is >= 1 and <= MaxAllowedAttempts))
            yield return $"{at}.max_attempts: use um inteiro de 1 a {MaxAllowedAttempts}";
        if (o["backoff_seconds"] is { } backoff && !(TryNumber(backoff, out var b) && b >= 0))
            yield return $"{at}.backoff_seconds: use um numero maior ou igual a zero";
        if (o["factor"] is { } factor && !(TryNumber(factor, out var f) && f >= 1))
            yield return $"{at}.factor: use um numero maior ou igual a 1";
        if (o["on"] is { } on && !(on is JsonArray items && items.All(i => i is JsonValue v && v.GetValueKind() == JsonValueKind.String &&
                                                                       NodeFailure.All.Contains(v.GetValue<string>()))))
            yield return $"{at}.on: use {NodeFailure.Error} e/ou {NodeFailure.Timeout}";
    }

    internal static bool TryInt(JsonNode node, out int value)
    {
        value = 0;
        return node is JsonValue v && v.TryGetValue(out value);
    }

    private static bool TryNumber(JsonNode node, out double value)
    {
        value = 0;
        return node is JsonValue v && v.GetValueKind() == JsonValueKind.Number && v.TryGetValue(out value);
    }
}
