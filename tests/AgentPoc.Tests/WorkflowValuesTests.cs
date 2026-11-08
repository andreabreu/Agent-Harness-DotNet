using System.Text.Json.Nodes;
using AgentPoc.Workflows;
using Xunit;

namespace AgentPoc.Tests;

public class WorkflowValuesTests
{
    private static WorkflowValues Values()
    {
        var values = new WorkflowValues("""{"texto": "revisar contrato", "prioridade": 2}""");
        values.SetOutput("classificar", """{"categoria": "juridico", "urgente": false, "itens": ["a", "b"], "meta": {"score": 7}}""");
        values.SetOutput("resumo", "texto livre do resumo");
        values.MarkSkipped("financeiro");
        return values;
    }

    [Fact]
    public void Whole_input_and_whole_node_output_render_as_the_original_text()
    {
        var text = Values().RenderText("{{input}} | {{resumo}}");

        Assert.Equal("""{"texto": "revisar contrato", "prioridade": 2} | texto livre do resumo""", text);
    }

    [Theory]
    [InlineData("{{classificar.categoria}}", "juridico")]
    [InlineData("{{classificar.meta.score}}", "7")]
    [InlineData("{{classificar.itens[1]}}", "b")]
    [InlineData("{{input.texto}}", "revisar contrato")]
    [InlineData("{{ classificar.urgente }}", "false")]
    public void Fields_of_json_outputs_and_of_the_input_are_reachable_by_path(string template, string expected)
    {
        Assert.Equal(expected, Values().RenderText(template));
    }

    [Fact]
    public void Missing_field_of_a_completed_node_fails_naming_the_path()
    {
        var error = Assert.Throws<WorkflowValueException>(() => Values().RenderText("cor: {{classificar.cor}}"));

        Assert.Contains("classificar.cor", error.Message);
    }

    [Fact]
    public void Field_of_a_text_output_is_missing()
    {
        Assert.Throws<WorkflowValueException>(() => Values().RenderText("{{resumo.titulo}}"));
    }

    [Fact]
    public void Skipped_node_renders_empty_and_its_fields_count_as_absent_without_error()
    {
        var values = Values();

        Assert.Equal("[][]", values.RenderText("[{{financeiro}}][{{financeiro.total}}]"));
        Assert.False(values.TryResolve("financeiro.total", out _));
    }

    [Fact]
    public void A_string_that_is_exactly_one_reference_keeps_the_type_of_the_value()
    {
        var template = JsonNode.Parse("""{"urgente": "{{classificar.urgente}}", "score": "{{classificar.meta.score}}", "itens": "{{classificar.itens}}", "frase": "categoria {{classificar.categoria}}"}""");

        var rendered = Values().RenderJson(template)!.AsObject();

        Assert.Equal("""{"urgente":false,"score":7,"itens":["a","b"],"frase":"categoria juridico"}""", rendered.ToJsonString());
    }

    [Fact]
    public void Resolve_returns_the_json_value_or_reports_it_absent()
    {
        var values = Values();

        Assert.True(values.TryResolve("classificar.meta.score", out var score));
        Assert.Equal(7, score!.GetValue<int>());
        Assert.False(values.TryResolve("classificar.inexistente", out _));
        Assert.False(values.TryResolve("nunca_executou.campo", out _));
    }
}
