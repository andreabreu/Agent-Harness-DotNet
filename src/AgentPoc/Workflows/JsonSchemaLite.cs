using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AgentPoc.Workflows;

/// <summary>
/// Subconjunto de JSON Schema para entrada e saida estruturada de workflows: type, properties, required, enum e
/// items. Outras palavras-chave sao rejeitadas na validacao da definicao.
/// </summary>
public static class JsonSchemaLite
{
    private static readonly string[] SupportedKeywords = { "type", "properties", "required", "enum", "items" };
    private static readonly string[] Types = { "object", "array", "string", "number", "integer", "boolean" };
    private static readonly Regex FencedJson = new(@"```(?:json)?\s*(.*?)```", RegexOptions.Singleline | RegexOptions.IgnoreCase);

    /// <summary>Palavras-chave e tipos fora do subconjunto, com a localizacao no schema.</summary>
    public static List<string> CheckSupported(JsonNode? schema, string at)
    {
        var errors = new List<string>();
        CheckSchema(schema, at, errors);
        return errors;
    }

    private static void CheckSchema(JsonNode? schema, string at, List<string> errors)
    {
        if (schema is not JsonObject s)
        {
            errors.Add($"{at}: schema deve ser um objeto");
            return;
        }
        foreach (var key in s.Select(kv => kv.Key).Where(k => !SupportedKeywords.Contains(k)))
            errors.Add($"{at}: palavra-chave '{key}' nao suportada");
        if (s["type"] is JsonNode type && !(type.GetValueKind() == JsonValueKind.String && Types.Contains(type.GetValue<string>())))
            errors.Add($"{at}: tipo '{type.ToJsonString().Trim('"')}' invalido");
        if (s["properties"] is JsonObject properties)
            foreach (var (name, child) in properties) CheckSchema(child, $"{at}.properties.{name}", errors);
        if (s["items"] is JsonNode items) CheckSchema(items, $"{at}.items", errors);
    }

    /// <summary>Erros de um valor contra o schema, com o caminho JSON ("$.categoria", "$.itens[1]").</summary>
    public static List<string> Validate(JsonNode schema, JsonNode? value)
    {
        var errors = new List<string>();
        ValidateNode(schema as JsonObject, value, "$", errors);
        return errors;
    }

    private static void ValidateNode(JsonObject? schema, JsonNode? value, string at, List<string> errors)
    {
        if (schema == null) return;
        if (schema["type"]?.GetValue<string>() is { } type && !HasType(value, type))
        {
            errors.Add($"{at}: esperado {type}");
            return;
        }
        if (schema["enum"] is JsonArray options && !options.Any(o => ConditionEvaluator.SameValue(o, value)))
            errors.Add($"{at}: valor fora de enum ({string.Join(", ", options.Select(o => o?.ToString()))})");
        if (value is JsonObject obj)
        {
            if (schema["required"] is JsonArray required)
                foreach (var name in required.Select(r => r?.GetValue<string>() ?? "").Where(n => !obj.ContainsKey(n)))
                    errors.Add($"{at}.{name}: campo obrigatorio ausente");
            if (schema["properties"] is JsonObject properties)
                foreach (var (name, child) in properties)
                    if (obj.TryGetPropertyValue(name, out var field)) ValidateNode(child as JsonObject, field, $"{at}.{name}", errors);
        }
        if (value is JsonArray list && schema["items"] is JsonObject itemSchema)
            for (var i = 0; i < list.Count; i++) ValidateNode(itemSchema, list[i], $"{at}[{i}]", errors);
    }

    private static bool HasType(JsonNode? value, string type)
    {
        var kind = value?.GetValueKind() ?? JsonValueKind.Null;
        return type switch
        {
            "object" => kind == JsonValueKind.Object,
            "array" => kind == JsonValueKind.Array,
            "string" => kind == JsonValueKind.String,
            "number" => kind == JsonValueKind.Number,
            "integer" => kind == JsonValueKind.Number && value!.GetValue<double>() % 1 == 0,
            "boolean" => kind is JsonValueKind.True or JsonValueKind.False,
            _ => false
        };
    }

    /// <summary>JSON da resposta de um modelo: o texto inteiro ou o primeiro bloco ```json; nulo se nao houver.</summary>
    public static JsonNode? ExtractJson(string answer)
    {
        var candidate = FencedJson.Match(answer) is { Success: true } fenced ? fenced.Groups[1].Value : answer;
        try
        {
            return JsonNode.Parse(candidate.Trim());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Instrucao anexada ao prompt de um no com output_schema.</summary>
    public static string DescribeForPrompt(JsonNode schema) =>
        "Responda APENAS com um JSON valido, sem texto antes ou depois, que siga este JSON Schema:\n" +
        schema.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
}
