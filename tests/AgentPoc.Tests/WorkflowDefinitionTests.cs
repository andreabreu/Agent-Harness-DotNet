using System.Text.Json.Nodes;
using AgentPoc.Workflows;
using Xunit;

namespace AgentPoc.Tests;

public class WorkflowDefinitionTests
{
    private const string SeedReport = """
        {"nodes": {
          "contexto":   {"type": "tool",  "tool": "kb_search", "args": {"query": "{{input}}", "limit": 3}, "next": ["pesquisar"]},
          "pesquisar":  {"type": "agent", "agent": "researcher", "input": "Tema: {{input}}\n{{contexto}}", "next": ["resumir", "riscos"]},
          "resumir":    {"type": "agent", "agent": "writer", "input": "Resuma: {{pesquisar}}", "next": ["consolidar"]},
          "riscos":     {"type": "agent", "agent": "researcher", "input": "Riscos: {{pesquisar}}", "next": ["consolidar"]},
          "consolidar": {"type": "agent", "agent": "writer", "input": "{{resumir}} {{riscos}}", "next": []}
        }}
        """;

    private static WorkflowDefinition Parse(string json) => WorkflowDefinition.Parse(JsonNode.Parse(json)!.AsObject(), "teste");

    private static IReadOnlyList<string> Errors(string json) =>
        Assert.Throws<WorkflowDefinitionException>(() => Parse(json)).Errors;

    [Fact]
    public void Seed_report_workflow_is_valid_and_joins_default_to_all()
    {
        var definition = Parse(SeedReport);

        Assert.Equal(new[] { "contexto", "pesquisar", "resumir", "riscos", "consolidar" }, definition.Ordered.Select(n => n.Id));
        Assert.Equal(new[] { "resumir", "riscos" }, definition.PredecessorsOf("consolidar"));
        Assert.All(definition.Ordered, n => Assert.Equal(JoinMode.All, n.Join));
    }

    [Fact]
    public void All_errors_come_back_together_with_node_and_field()
    {
        var errors = Errors("""
            {"nodes": {
              "a": {"type": "agente", "next": []},
              "b": {"type": "tool", "next": ["zzz"]},
              "c": {"type": "agent", "agent": "writer", "input": "{{fantasma}}"}
            }}
            """);

        Assert.Contains("a.type: tipo 'agente' desconhecido (use agent, tool, condition, transform, end)", errors);
        Assert.Contains("b.tool: obrigatorio", errors);
        Assert.Contains("b.next: destino 'zzz' nao existe", errors);
        Assert.Contains("c.input: referencia '{{fantasma}}' a no que nao existe", errors);
    }

    [Fact]
    public void Missing_nodes_is_an_error()
    {
        Assert.Contains("nodes: obrigatorio", Errors("""{"tools": {}}"""));
    }

    [Fact]
    public void Exception_message_lists_every_error_with_the_workflow_id()
    {
        var error = Assert.Throws<WorkflowDefinitionException>(() => Parse("""{"nodes": {"a": {"type": "x"}, "b": {"type": "y"}}}"""));

        Assert.StartsWith("Definicao invalida do workflow 'teste':", error.Message);
        Assert.Equal(2, error.Errors.Count);
    }
}

public class WorkflowDefinitionNodeTypesTests
{
    private static IReadOnlyList<string> Errors(string json) =>
        Assert.Throws<WorkflowDefinitionException>(() => WorkflowDefinition.Parse(JsonNode.Parse(json)!.AsObject(), "teste")).Errors;

    private static WorkflowDefinition Parse(string json) => WorkflowDefinition.Parse(JsonNode.Parse(json)!.AsObject(), "teste");

    [Fact]
    public void Condition_destinations_become_outgoing_edges()
    {
        var definition = Parse("""
            {"nodes": {
              "c": {"type": "agent", "agent": "researcher", "input": "x", "next": ["r"]},
              "r": {"type": "condition", "cases": [{"when": {"path": "c.categoria", "eq": "juridico"}, "next": ["j"]}], "default": ["s"]},
              "j": {"type": "tool", "tool": "kb_search"},
              "s": {"type": "tool", "tool": "kb_search"}
            }}
            """);

        Assert.Equal(new[] { "j", "s" }, definition.Nodes["r"].Next);
        Assert.Equal(new[] { "r" }, definition.PredecessorsOf("j"));
    }

    [Fact]
    public void Condition_rules_are_validated()
    {
        var errors = Errors("""
            {"nodes": {
              "r": {"type": "condition", "next": ["j"],
                    "cases": [{"when": {"path": "fantasma.x", "eq": 1}, "next": ["juridco"]}, {"when": {"path": "input.x", "maior": 1}, "next": ["j"]}],
                    "default": ["zz"]},
              "j": {"type": "tool", "tool": "kb_search"},
              "sem_casos": {"type": "condition"}
            }}
            """);

        Assert.Contains("r.next: use cases/default em um no condition", errors);
        Assert.Contains("r.cases[0].when: caminho 'fantasma.x' referencia no que nao existe", errors);
        Assert.Contains("r.cases[0].next: destino 'juridco' nao existe", errors);
        Assert.Contains("r.cases[1].when: operador desconhecido 'maior'", errors);
        Assert.Contains("r.default: destino 'zz' nao existe", errors);
        Assert.Contains("sem_casos.cases: obrigatorio (lista de {when, next})", errors);
    }

    [Fact]
    public void Output_schema_and_retries_are_validated()
    {
        var errors = Errors("""
            {"nodes": {"c": {"type": "agent", "agent": "researcher", "input": "x",
                             "output_schema": {"type": "object", "properties": {"n": {"type": "number", "minimum": 1}}},
                             "output_retries": 9}}}
            """);

        Assert.Contains("c.output_schema.properties.n: palavra-chave 'minimum' nao suportada", errors);
        Assert.Contains("c.output_retries: use um inteiro de 0 a 5", errors);
    }

    [Fact]
    public void Join_must_be_a_known_mode()
    {
        Assert.Contains("a.join: 'some' invalido (use all, all_active, any)",
            Errors("""{"nodes": {"a": {"type": "tool", "tool": "kb_search", "join": "some"}}}"""));
        Assert.Equal(JoinMode.AllActive, Parse("""{"nodes": {"a": {"type": "tool", "tool": "kb_search", "join": "all_active"}}}""").Nodes["a"].Join);
    }

    [Fact]
    public void Input_schema_output_transform_and_end_are_accepted()
    {
        var definition = Parse("""
            {"input_schema": {"type": "object", "properties": {"texto": {"type": "string"}}, "required": ["texto"]},
             "output": "{{t}}",
             "nodes": {
               "t": {"type": "transform", "output": {"texto": "{{input.texto}}"}, "next": ["fim"]},
               "fim": {"type": "end", "status": "failed", "output": "recusado: {{t.texto}}"}
             }}
            """);

        Assert.NotNull(definition.InputSchema);
        Assert.Equal("{{t}}", definition.Output!.GetValue<string>());
    }

    [Fact]
    public void Transform_end_and_top_level_fields_are_validated()
    {
        var errors = Errors("""
            {"input_schema": {"type": "object", "pattern": "x"},
             "output": "{{fantasma}}",
             "nodes": {
               "t": {"type": "transform"},
               "e": {"type": "end", "status": "talvez", "next": ["t"]}
             }}
            """);

        Assert.Contains("input_schema: palavra-chave 'pattern' nao suportada", errors);
        Assert.Contains("output: referencia '{{fantasma}}' a no que nao existe", errors);
        Assert.Contains("t.output: obrigatorio", errors);
        Assert.Contains("e.status: use completed ou failed", errors);
        Assert.Contains("e.next: um no end nao tem destinos", errors);
    }
}
