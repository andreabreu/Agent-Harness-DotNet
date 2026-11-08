using System.Text.Json.Nodes;
using AgentPoc.Workflows;
using Xunit;

namespace AgentPoc.Tests;

public class JsonSchemaLiteTests
{
    private static readonly JsonNode Classifier = JsonNode.Parse("""
        {"type": "object",
         "properties": {"categoria": {"type": "string", "enum": ["juridico", "financeiro", "suporte"]},
                        "urgente": {"type": "boolean"},
                        "itens": {"type": "array", "items": {"type": "integer"}}},
         "required": ["categoria", "urgente"]}
        """)!;

    private static List<string> Validate(string json) => JsonSchemaLite.Validate(Classifier, JsonNode.Parse(json));

    [Fact]
    public void Valid_classifier_output_has_no_errors()
    {
        Assert.Empty(Validate("""{"categoria": "juridico", "urgente": false, "itens": [1, 2]}"""));
    }

    [Fact]
    public void Errors_point_the_path_of_each_problem()
    {
        var errors = Validate("""{"categoria": "rh", "itens": [1, "dois", 1.5]}""");

        Assert.Equal(new[]
        {
            "$.urgente: campo obrigatorio ausente",
            "$.categoria: valor fora de enum (juridico, financeiro, suporte)",
            "$.itens[1]: esperado integer",
            "$.itens[2]: esperado integer"
        }, errors);
    }

    [Fact]
    public void Wrong_root_type_is_reported()
    {
        Assert.Equal(new[] { "$: esperado object" }, Validate("""["juridico"]"""));
    }

    [Fact]
    public void Keywords_outside_the_subset_are_rejected_with_their_location()
    {
        var errors = JsonSchemaLite.CheckSupported(JsonNode.Parse("""
            {"type": "object", "properties": {"score": {"type": "number", "minimum": 0}}, "additionalProperties": false}
            """), "classificar.output_schema");

        Assert.Equal(new[]
        {
            "classificar.output_schema: palavra-chave 'additionalProperties' nao suportada",
            "classificar.output_schema.properties.score: palavra-chave 'minimum' nao suportada"
        }, errors);
    }

    [Fact]
    public void Unknown_type_is_rejected()
    {
        Assert.Equal(new[] { "s: tipo 'texto' invalido" }, JsonSchemaLite.CheckSupported(JsonNode.Parse("""{"type": "texto"}"""), "s"));
    }

    [Theory]
    [InlineData("""{"categoria": "juridico"}""")]
    [InlineData("Segue a classificacao:\n```json\n{\"categoria\": \"juridico\"}\n```")]
    [InlineData("```\n{\"categoria\": \"juridico\"}\n```")]
    public void Json_is_extracted_from_plain_or_fenced_answers(string answer)
    {
        Assert.Equal("juridico", JsonSchemaLite.ExtractJson(answer)?["categoria"]?.GetValue<string>());
    }

    [Fact]
    public void Answer_without_json_extracts_nothing()
    {
        Assert.Null(JsonSchemaLite.ExtractJson("Acho que e juridico."));
    }
}
