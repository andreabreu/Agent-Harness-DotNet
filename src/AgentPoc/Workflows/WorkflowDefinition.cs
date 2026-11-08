using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentPoc.Workflows;

public enum JoinMode { All, AllActive, Any }

/// <summary>Definicao rejeitada: todos os erros juntos, um por linha, no formato "no.campo: problema".</summary>
public sealed class WorkflowDefinitionException : InvalidOperationException
{
    public IReadOnlyList<string> Errors { get; }

    public WorkflowDefinitionException(string workflowId, IReadOnlyList<string> errors)
        : base($"Definicao invalida do workflow '{workflowId}':\n{string.Join("\n", errors)}") => Errors = errors;
}

public sealed record ConditionCase(JsonNode When, IReadOnlyList<string> Next);

/// <summary>No do workflow. Em um desvio, Next sao todos os destinos possiveis (casos e default).</summary>
public sealed record WorkflowNode(string Id, string Type, IReadOnlyList<string> Next, JoinMode Join, JsonObject Raw)
{
    public IReadOnlyList<ConditionCase> Cases { get; init; } = Array.Empty<ConditionCase>();
    public IReadOnlyList<string> Default { get; init; } = Array.Empty<string>();

    public bool IsCondition => Type == WorkflowNodeType.Condition;

    public string Text(string key) => Raw[key] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : "";
}

public static class WorkflowNodeType
{
    public const string Agent = "agent";
    public const string Tool = "tool";
    public const string Condition = "condition";
    public const string Transform = "transform";
    public const string End = "end";

    public static readonly string[] All = { Agent, Tool, Condition, Transform, End };
}

/// <summary>Definicao de workflow validada: nos tipados, arestas, entrada e saida declaradas.</summary>
public sealed class WorkflowDefinition
{
    public const int DefaultOutputRetries = 2;
    private const int MaxOutputRetries = 5;
    private static readonly Dictionary<string, JoinMode> Joins = new()
    {
        ["all"] = JoinMode.All, ["all_active"] = JoinMode.AllActive, ["any"] = JoinMode.Any
    };

    private readonly Dictionary<string, List<string>> _predecessors;

    public IReadOnlyList<WorkflowNode> Ordered { get; }
    public IReadOnlyDictionary<string, WorkflowNode> Nodes { get; }
    public JsonNode? InputSchema { get; }
    public JsonNode? Output { get; }

    private WorkflowDefinition(List<WorkflowNode> nodes, JsonNode? inputSchema, JsonNode? output)
    {
        Ordered = nodes;
        Nodes = nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        InputSchema = inputSchema;
        Output = output;
        _predecessors = nodes.ToDictionary(n => n.Id, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var node in nodes)
            foreach (var target in node.Next.Distinct())
                _predecessors[target].Add(node.Id);
    }

    public IReadOnlyList<string> PredecessorsOf(string node) => _predecessors[node];

    public static WorkflowDefinition Parse(JsonObject definition, string workflowId)
    {
        var errors = new List<string>();
        if (definition["nodes"] is not JsonObject rawNodes)
            throw new WorkflowDefinitionException(workflowId, new[] { "nodes: obrigatorio" });

        var ids = rawNodes.Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal);
        var nodes = rawNodes.Select(kv => ParseNode(kv.Key, kv.Value, ids, errors)).OfType<WorkflowNode>().ToList();

        var inputSchema = definition["input_schema"];
        if (inputSchema != null) errors.AddRange(JsonSchemaLite.CheckSupported(inputSchema, "input_schema"));
        var output = definition["output"];
        CheckReferences(output, "output", ids, errors);

        if (errors.Count > 0) throw new WorkflowDefinitionException(workflowId, errors);
        return new WorkflowDefinition(nodes, inputSchema, output);
    }

    private static WorkflowNode? ParseNode(string id, JsonNode? raw, HashSet<string> ids, List<string> errors)
    {
        if (raw is not JsonObject node)
        {
            errors.Add($"{id}: no deve ser um objeto");
            return null;
        }
        var type = node["type"] is JsonValue t && t.GetValueKind() == JsonValueKind.String ? t.GetValue<string>() : "";
        if (!WorkflowNodeType.All.Contains(type))
        {
            errors.Add($"{id}.type: tipo '{type}' desconhecido (use {string.Join(", ", WorkflowNodeType.All)})");
            return null;
        }

        var join = JoinMode.All;
        if (node["join"] is JsonNode rawJoin)
        {
            var name = rawJoin.GetValueKind() == JsonValueKind.String ? rawJoin.GetValue<string>() : rawJoin.ToJsonString();
            if (!Joins.TryGetValue(name, out join)) errors.Add($"{id}.join: '{name}' invalido (use {string.Join(", ", Joins.Keys)})");
        }

        var next = Targets(node["next"], $"{id}.next", ids, errors);
        var parsed = type switch
        {
            WorkflowNodeType.Agent => Agent(id, node, ids, errors),
            WorkflowNodeType.Tool => Tool(id, node, ids, errors),
            WorkflowNodeType.Condition => Condition(id, node, ids, errors),
            WorkflowNodeType.Transform => Transform(id, node, ids, errors),
            _ => End(id, node, ids, errors)
        };
        return parsed with { Next = parsed.IsCondition ? parsed.Next : next, Join = join };
    }

    private static WorkflowNode Agent(string id, JsonObject node, HashSet<string> ids, List<string> errors)
    {
        Required(node, "agent", id, errors);
        Required(node, "input", id, errors);
        CheckReferences(node["input"], $"{id}.input", ids, errors);
        if (node["output_schema"] is JsonNode schema) errors.AddRange(JsonSchemaLite.CheckSupported(schema, $"{id}.output_schema"));
        if (node["output_retries"] is JsonNode retries &&
            !(retries is JsonValue v && v.TryGetValue<int>(out var n) && n is >= 0 and <= MaxOutputRetries))
            errors.Add($"{id}.output_retries: use um inteiro de 0 a {MaxOutputRetries}");
        return Node(id, WorkflowNodeType.Agent, node);
    }

    private static WorkflowNode Tool(string id, JsonObject node, HashSet<string> ids, List<string> errors)
    {
        Required(node, "tool", id, errors);
        CheckReferences(node["args"], $"{id}.args", ids, errors);
        return Node(id, WorkflowNodeType.Tool, node);
    }

    private static WorkflowNode Condition(string id, JsonObject node, HashSet<string> ids, List<string> errors)
    {
        if (node.ContainsKey("next")) errors.Add($"{id}.next: use cases/default em um no condition");
        var cases = new List<ConditionCase>();
        if (node["cases"] is not JsonArray rawCases) errors.Add($"{id}.cases: obrigatorio (lista de {{when, next}})");
        else
            for (var i = 0; i < rawCases.Count; i++)
            {
                var at = $"{id}.cases[{i}]";
                var when = rawCases[i]?["when"];
                errors.AddRange(ConditionEvaluator.Validate(when, $"{at}.when"));
                foreach (var path in ConditionEvaluator.Paths(when).Where(p => !KnownRoot(p, ids)))
                    errors.Add($"{at}.when: caminho '{path}' referencia no que nao existe");
                cases.Add(new ConditionCase(when?.DeepClone() ?? new JsonObject(), Targets(rawCases[i]?["next"], $"{at}.next", ids, errors)));
            }
        var fallback = Targets(node["default"], $"{id}.default", ids, errors);
        var destinations = cases.SelectMany(c => c.Next).Concat(fallback).Distinct().ToList();
        return Node(id, WorkflowNodeType.Condition, node) with { Next = destinations, Cases = cases, Default = fallback };
    }

    private static WorkflowNode Transform(string id, JsonObject node, HashSet<string> ids, List<string> errors)
    {
        if (!node.ContainsKey("output")) errors.Add($"{id}.output: obrigatorio");
        CheckReferences(node["output"], $"{id}.output", ids, errors);
        return Node(id, WorkflowNodeType.Transform, node);
    }

    private static WorkflowNode End(string id, JsonObject node, HashSet<string> ids, List<string> errors)
    {
        if (node["next"] is JsonArray { Count: > 0 }) errors.Add($"{id}.next: um no end nao tem destinos");
        if (TextOf(node, "status") is not (WorkflowRunStatus.Completed or WorkflowRunStatus.Failed))
            errors.Add($"{id}.status: use {WorkflowRunStatus.Completed} ou {WorkflowRunStatus.Failed}");
        CheckReferences(node["output"], $"{id}.output", ids, errors);
        return Node(id, WorkflowNodeType.End, node);
    }

    private static WorkflowNode Node(string id, string type, JsonObject raw) =>
        new(id, type, Array.Empty<string>(), JoinMode.All, raw);

    private static string TextOf(JsonObject node, string key) =>
        node[key] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : "";

    private static void Required(JsonObject node, string key, string id, List<string> errors)
    {
        if (TextOf(node, key).Length == 0) errors.Add($"{id}.{key}: obrigatorio");
    }

    private static List<string> Targets(JsonNode? list, string at, HashSet<string> ids, List<string> errors)
    {
        var targets = list is JsonArray items ? items.Select(i => i is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : "").ToList() : new List<string>();
        foreach (var target in targets.Where(t => !ids.Contains(t))) errors.Add($"{at}: destino '{target}' nao existe");
        return targets.Where(ids.Contains).ToList();
    }

    private static bool KnownRoot(string path, HashSet<string> ids)
    {
        var root = WorkflowValues.RootOf(path);
        return root == WorkflowValues.InputRoot || ids.Contains(root);
    }

    private static void CheckReferences(JsonNode? node, string at, HashSet<string> ids, List<string> errors)
    {
        foreach (var path in Strings(node).SelectMany(s => WorkflowValues.Reference.Matches(s).Select(m => m.Groups[1].Value)).Distinct())
            if (!KnownRoot(path, ids)) errors.Add($"{at}: referencia '{{{{{path}}}}}' a no que nao existe");
    }

    private static IEnumerable<string> Strings(JsonNode? node) => node switch
    {
        JsonObject o => o.SelectMany(kv => Strings(kv.Value)),
        JsonArray a => a.SelectMany(Strings),
        JsonValue v when v.GetValueKind() == JsonValueKind.String => new[] { v.GetValue<string>() },
        _ => Array.Empty<string>()
    };
}

public static class WorkflowRunStatus
{
    public const string Completed = "completed";
    public const string Failed = "failed";
}
