using System.Text.Json.Nodes;
using AgentPoc.Workflows;
using Xunit;

namespace AgentPoc.Tests;

public class ConditionEvaluatorTests
{
    private static readonly WorkflowValues Values = CreateValues();

    private static WorkflowValues CreateValues()
    {
        var values = new WorkflowValues("""{"valor": 1500}""");
        values.SetOutput("classificar", """{"categoria": "juridico", "urgente": true, "score": 7, "tags": ["contrato", "prazo"], "resumo": "revisar clausula de multa"}""");
        return values;
    }

    private static bool Eval(string when) => ConditionEvaluator.Evaluate(JsonNode.Parse(when), Values);

    [Theory]
    [InlineData("""{"path": "classificar.categoria", "eq": "juridico"}""", true)]
    [InlineData("""{"path": "classificar.categoria", "eq": "suporte"}""", false)]
    [InlineData("""{"path": "classificar.categoria", "ne": "suporte"}""", true)]
    [InlineData("""{"path": "classificar.score", "gt": 6}""", true)]
    [InlineData("""{"path": "classificar.score", "gte": 7}""", true)]
    [InlineData("""{"path": "classificar.score", "lt": 7}""", false)]
    [InlineData("""{"path": "classificar.score", "lte": 7.0}""", true)]
    [InlineData("""{"path": "input.valor", "gt": 1000}""", true)]
    [InlineData("""{"path": "classificar.resumo", "contains": "multa"}""", true)]
    [InlineData("""{"path": "classificar.tags", "contains": "prazo"}""", true)]
    [InlineData("""{"path": "classificar.tags", "contains": "fatura"}""", false)]
    [InlineData("""{"path": "classificar.categoria", "in": ["juridico", "financeiro"]}""", true)]
    [InlineData("""{"path": "classificar.categoria", "in": ["suporte"]}""", false)]
    [InlineData("""{"path": "classificar.urgente", "eq": true}""", true)]
    [InlineData("""{"path": "classificar.score", "exists": true}""", true)]
    [InlineData("""{"path": "classificar.cor", "exists": false}""", true)]
    public void Each_comparison_operator_decides_on_the_resolved_value(string when, bool expected)
    {
        Assert.Equal(expected, Eval(when));
    }

    [Theory]
    [InlineData("""{"all": [{"path": "classificar.urgente", "eq": true}, {"path": "classificar.score", "gt": 5}]}""", true)]
    [InlineData("""{"all": [{"path": "classificar.urgente", "eq": true}, {"path": "classificar.score", "gt": 9}]}""", false)]
    [InlineData("""{"any": [{"path": "classificar.score", "gt": 9}, {"not": {"path": "classificar.categoria", "eq": "suporte"}}]}""", true)]
    [InlineData("""{"not": {"any": [{"path": "classificar.urgente", "eq": true}]}}""", false)]
    public void Combinations_nest(string when, bool expected)
    {
        Assert.Equal(expected, Eval(when));
    }

    [Theory]
    [InlineData("""{"path": "classificar.categoria", "gt": 1}""")]
    [InlineData("""{"path": "classificar.score", "eq": "7"}""")]
    [InlineData("""{"path": "classificar.cor", "eq": "azul"}""")]
    [InlineData("""{"path": "classificar.cor", "ne": "azul"}""")]
    [InlineData("""{"path": "classificar.cor", "exists": true}""")]
    public void Types_are_strict_and_a_missing_path_is_false(string when)
    {
        Assert.False(Eval(when));
    }

    [Fact]
    public void Validate_points_unknown_operator_two_operators_and_in_without_list()
    {
        var errors = ConditionEvaluator.Validate(JsonNode.Parse("""
            {"all": [{"path": "a.b", "maior": 1}, {"path": "a.b", "eq": 1, "ne": 2}, {"path": "a.b", "in": "x"}, {"eq": 1}]}
            """), "r.cases[0].when");

        Assert.Equal(new[]
        {
            "r.cases[0].when.all[0]: operador desconhecido 'maior'",
            "r.cases[0].when.all[1]: use um unico operador por comparacao",
            "r.cases[0].when.all[2]: 'in' exige uma lista",
            "r.cases[0].when.all[3]: 'path' obrigatorio"
        }, errors);
    }

    [Fact]
    public void Paths_lists_every_path_used_by_the_condition()
    {
        var paths = ConditionEvaluator.Paths(JsonNode.Parse("""{"any": [{"path": "a.x", "eq": 1}, {"not": {"path": "input.y", "exists": true}}]}"""));

        Assert.Equal(new[] { "a.x", "input.y" }, paths);
    }
}
