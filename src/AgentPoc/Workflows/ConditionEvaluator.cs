using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentPoc.Workflows;

/// <summary>
/// Condicoes de desvio como arvore JSON: comparacao {"path": "no.campo", "&lt;op&gt;": valor} ou combinacao
/// {"all": [...]}, {"any": [...]}, {"not": {...}}. Tipos estritos, sem coercao; caminho ausente da falso.
/// </summary>
public static class ConditionEvaluator
{
    private const string PathKey = "path";
    private static readonly string[] Combinators = { "all", "any", "not" };
    private static readonly string[] Operators = { "eq", "ne", "gt", "gte", "lt", "lte", "contains", "in", "exists" };

    public static bool Evaluate(JsonNode? when, WorkflowValues values)
    {
        if (when is not JsonObject condition) return false;
        if (condition["all"] is JsonArray all) return all.All(c => Evaluate(c, values));
        if (condition["any"] is JsonArray any) return any.Any(c => Evaluate(c, values));
        if (condition.ContainsKey("not")) return !Evaluate(condition["not"], values);

        var path = condition[PathKey]?.GetValue<string>() ?? "";
        var (op, expected) = condition.First(kv => kv.Key != PathKey);
        var found = values.TryResolve(path, out var actual);
        if (op == "exists") return found == (expected?.GetValueKind() == JsonValueKind.True);
        return found && Compare(op, actual, expected);
    }

    private static bool Compare(string op, JsonNode? actual, JsonNode? expected) => op switch
    {
        "eq" => SameValue(actual, expected),
        "ne" => !SameValue(actual, expected),
        "gt" => Numbers(actual, expected, (a, b) => a > b),
        "gte" => Numbers(actual, expected, (a, b) => a >= b),
        "lt" => Numbers(actual, expected, (a, b) => a < b),
        "lte" => Numbers(actual, expected, (a, b) => a <= b),
        "contains" => Contains(actual, expected),
        "in" => expected is JsonArray options && options.Any(o => SameValue(actual, o)),
        _ => false
    };

    /// <summary>Igualdade estrita: numeros pelo valor (7 == 7.0), demais pelo JSON; 1 e "1" sao diferentes.</summary>
    public static bool SameValue(JsonNode? a, JsonNode? b) =>
        TryNumber(a, out var x) && TryNumber(b, out var y) ? x == y : JsonNode.DeepEquals(a, b);

    private static bool Numbers(JsonNode? a, JsonNode? b, Func<double, double, bool> compare) =>
        TryNumber(a, out var x) && TryNumber(b, out var y) && compare(x, y);

    private static bool Contains(JsonNode? actual, JsonNode? expected) => actual switch
    {
        JsonArray items => items.Any(i => SameValue(i, expected)),
        JsonValue text when text.GetValueKind() == JsonValueKind.String && expected?.GetValueKind() == JsonValueKind.String =>
            text.GetValue<string>().Contains(expected.GetValue<string>(), StringComparison.Ordinal),
        _ => false
    };

    private static bool TryNumber(JsonNode? node, out double value)
    {
        value = 0;
        return node is JsonValue v && v.GetValueKind() == JsonValueKind.Number && v.TryGetValue(out value);
    }

    /// <summary>Erros de forma da condicao, com a localizacao (ex.: "r.cases[0].when.all[1]: ...").</summary>
    public static List<string> Validate(JsonNode? when, string at)
    {
        var errors = new List<string>();
        Check(when, at, errors);
        return errors;
    }

    private static void Check(JsonNode? when, string at, List<string> errors)
    {
        if (when is not JsonObject condition)
        {
            errors.Add($"{at}: condicao deve ser um objeto");
            return;
        }
        if (Combinators.FirstOrDefault(condition.ContainsKey) is { } combinator)
        {
            if (combinator == "not") Check(condition["not"], $"{at}.not", errors);
            else if (condition[combinator] is JsonArray items)
                for (var i = 0; i < items.Count; i++) Check(items[i], $"{at}.{combinator}[{i}]", errors);
            else errors.Add($"{at}: '{combinator}' exige uma lista");
            return;
        }

        var operators = condition.Where(kv => kv.Key != PathKey).Select(kv => kv.Key).ToList();
        if (condition[PathKey] is not JsonValue path || path.GetValueKind() != JsonValueKind.String) errors.Add($"{at}: 'path' obrigatorio");
        else if (operators.Count != 1) errors.Add($"{at}: use um unico operador por comparacao");
        else if (!Operators.Contains(operators[0])) errors.Add($"{at}: operador desconhecido '{operators[0]}'");
        else if (operators[0] == "in" && condition["in"] is not JsonArray) errors.Add($"{at}: 'in' exige uma lista");
        else if (operators[0] == "exists" && condition["exists"]?.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False))
            errors.Add($"{at}: 'exists' exige true ou false");
    }

    /// <summary>Todos os caminhos usados pela condicao (para validar referencias a nos).</summary>
    public static IEnumerable<string> Paths(JsonNode? when)
    {
        if (when is not JsonObject condition) yield break;
        foreach (var combinator in Combinators.Where(condition.ContainsKey))
        {
            var children = condition[combinator] is JsonArray items ? items : new JsonArray(condition[combinator]?.DeepClone());
            foreach (var path in children.SelectMany(Paths)) yield return path;
        }
        if (condition[PathKey] is JsonValue p && p.GetValueKind() == JsonValueKind.String) yield return p.GetValue<string>();
    }
}
