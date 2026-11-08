using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AgentPoc.Workflows;

/// <summary>Referencia de template sem valor (campo inexistente na saida de um no concluido).</summary>
public sealed class WorkflowValueException : Exception
{
    public WorkflowValueException(string message) : base(message) { }
}

/// <summary>
/// Valores de uma execucao: a entrada e a saida de cada no. Resolve caminhos ("no.campo.sub[0]", "input.campo")
/// e renderiza templates com {{caminho}}. Saidas sao texto; campos so existem quando o texto e JSON.
/// </summary>
public sealed class WorkflowValues
{
    public const string InputRoot = "input";

    public static readonly Regex Reference =
        new(@"\{\{\s*([A-Za-z0-9_]+(?:\.[A-Za-z0-9_]+|\[\d+\])*)\s*\}\}", RegexOptions.Compiled);

    private static readonly Regex Segment = new(@"\.([A-Za-z0-9_]+)|\[(\d+)\]", RegexOptions.Compiled);

    private readonly Dictionary<string, string> _texts = new(StringComparer.Ordinal);
    private readonly HashSet<string> _skipped = new(StringComparer.Ordinal);

    public WorkflowValues(string input) => _texts[InputRoot] = input;

    public void SetOutput(string node, string output) => _texts[node] = output;

    public void MarkSkipped(string node) => _skipped.Add(node);

    public string? OutputOf(string node) => _texts.GetValueOrDefault(node);

    /// <summary>Raiz de um caminho ("classificar" em "classificar.itens[0]").</summary>
    public static string RootOf(string path)
    {
        var end = path.IndexOfAny(new[] { '.', '[' });
        return end < 0 ? path : path[..end];
    }

    /// <summary>
    /// Valor do caminho. Sem campos, a raiz vale o JSON da saida (ou o texto, se nao for JSON). Falso quando o no
    /// nao executou, foi pulado, a saida nao e JSON ou o campo nao existe.
    /// </summary>
    public bool TryResolve(string path, out JsonNode? value)
    {
        value = null;
        var root = RootOf(path);
        if (!_texts.TryGetValue(root, out var text)) return false;
        var current = ParseJson(text);
        var rest = path[root.Length..];
        if (rest.Length == 0)
        {
            value = current?.DeepClone() ?? JsonValue.Create(text);
            return true;
        }
        foreach (Match m in Segment.Matches(rest))
        {
            current = m.Groups[1].Success
                ? (current as JsonObject)?[m.Groups[1].Value]
                : current is JsonArray list && int.Parse(m.Groups[2].Value) is var i && i < list.Count ? list[i] : null;
            if (current == null) return false;
        }
        if (current == null) return false;
        value = current.DeepClone();
        return true;
    }

    /// <summary>Troca cada {{caminho}} pelo valor como texto. Raiz sem campos vale o texto original da saida.</summary>
    public string RenderText(string template) =>
        Reference.Replace(template, m => AsText(m.Groups[1].Value));

    /// <summary>
    /// Renderiza um JSON com templates nas strings. Uma string que e exatamente uma referencia vira o proprio valor,
    /// preservando o tipo (numero, booleano, lista, objeto).
    /// </summary>
    public JsonNode? RenderJson(JsonNode? template) => template switch
    {
        JsonObject o => new JsonObject(o.Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, RenderJson(kv.Value)))),
        JsonArray a => new JsonArray(a.Select(RenderJson).ToArray()),
        JsonValue v when v.TryGetValue<string>(out var s) => RenderString(s),
        _ => template?.DeepClone()
    };

    private JsonNode? RenderString(string text)
    {
        var whole = Reference.Match(text);
        if (!whole.Success || whole.Index != 0 || whole.Length != text.Length) return JsonValue.Create(RenderText(text));
        var path = whole.Groups[1].Value;
        if (TryResolve(path, out var value)) return value;
        return Absent(path) ? null : throw Missing(path);
    }

    private string AsText(string path)
    {
        var root = RootOf(path);
        if (root.Length == path.Length && _texts.TryGetValue(root, out var whole)) return whole;
        if (TryResolve(path, out var value))
            return value is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : value?.ToJsonString() ?? "";
        return Absent(path) ? "" : throw Missing(path);
    }

    /// <summary>Caminho em no pulado conta como ausente, sem erro (comum depois de um join all_active).</summary>
    private bool Absent(string path) => _skipped.Contains(RootOf(path));

    private static WorkflowValueException Missing(string path) =>
        new($"referencia '{{{{{path}}}}}' sem valor: o campo nao existe na saida ou o no ainda nao executou");

    /// <summary>JSON da saida, ou nulo se for texto. Sem cache: cada leitura tem a propria arvore (nos paralelos leem juntos).</summary>
    private static JsonNode? ParseJson(string text)
    {
        var trimmed = text.TrimStart();
        if (!trimmed.StartsWith('{') && !trimmed.StartsWith('[')) return null;
        try { return JsonNode.Parse(text); }
        catch (JsonException) { return null; }
    }
}
