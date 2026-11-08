using System.Text.Json.Nodes;
using AgentPoc.Harness;
using Xunit;

namespace AgentPoc.Tests;

public class PolicySourceTests
{
    private const string ShellRules = """{"deny_when":{"command":"sudo"}}""";
    private const string HttpRules = """{"approval_when":{"url":"^http://127\\."}}""";

    private static JsonObject Args(string key, string value) => new() { [key] = value };

    [Theory]
    [InlineData("deny", "{}", "command", "ls", "tool_policy")]
    [InlineData("approval", ShellRules, "command", "sudo ls", "deny_when")]
    [InlineData("approval", ShellRules, "command", "ls", "tool_policy")]
    [InlineData("auto", HttpRules, "url", "http://127.0.0.1/x", "approval_when")]
    [InlineData("auto", HttpRules, "url", "https://example.com", "tool_policy")]
    public void Every_decision_records_where_it_came_from(string policy, string config, string arg, string value, string expectedSource)
    {
        var result = ToolPolicy.Resolve(policy, config, Args(arg, value));

        Assert.Equal(expectedSource, result.Source);
    }
}

public class WorkflowApprovalModeTests
{
    private static Task<ApprovalDecision> Approve(IHumanGate gate) =>
        gate.ApproveAsync(null!, "call_0", "run_shell", new JsonObject(), "politica approval", CancellationToken.None);

    [Fact]
    public async Task Without_a_mode_the_workflow_denies_and_says_it_was_the_default()
    {
        var decision = await Approve(ModeHumanGate.For("deny", explicitMode: false));

        Assert.False(decision.Approved);
        Assert.Equal("negado por padrao em workflow (sem modo de aprovacao informado)", decision.Reason);
        Assert.Equal(PolicySource.ModeDefault, decision.Source);
    }

    [Theory]
    [InlineData("auto", true)]
    [InlineData("deny", false)]
    public async Task Explicit_mode_decides_and_is_recorded_as_explicit(string mode, bool approved)
    {
        var decision = await Approve(ModeHumanGate.For(mode, explicitMode: true));

        Assert.Equal(approved, decision.Approved);
        Assert.Equal(PolicySource.ModeExplicit, decision.Source);
    }

    [Fact]
    public async Task Question_outside_ask_mode_gets_no_human_answer()
    {
        var answer = await ModeHumanGate.For("auto", explicitMode: true).AskAsync(null!, "call_0", "qual ambiente?", Array.Empty<string>(), CancellationToken.None);

        Assert.Equal(ModeHumanGate.NoHumanAnswer, answer);
    }

    [Theory]
    [InlineData("auto", null, "auto", true)]
    [InlineData(null, "ask", "ask", true)]
    [InlineData("deny", "auto", "deny", true)]
    [InlineData(null, null, "deny", false)]
    [InlineData("", "", "deny", false)]
    public void Mode_comes_from_flag_then_environment_then_safe_default(string? flag, string? env, string mode, bool isExplicit)
    {
        var resolved = Workflows.WorkflowApprovalMode.From(flag, env);

        Assert.Equal((mode, isExplicit), (resolved.Mode, resolved.Explicit));
    }

    [Fact]
    public void Unknown_mode_is_rejected_listing_the_accepted_values()
    {
        var error = Assert.Throws<ArgumentException>(() => Workflows.WorkflowApprovalMode.From("sim", null));

        Assert.Contains("deny, auto, ask", error.Message);
    }
}

public class WorkflowToolRulesTests
{
    private const string ShellRules = """{"deny_when":{"command":"sudo"}}""";
    private const string HttpRules = """{"approval_when":{"url":"^http://127\\."}}""";

    private static readonly WorkflowToolRules AllowShellAndHttp = new(new[] { "run_shell", "http_get", "delete_file" }, Array.Empty<string>());

    private static JsonObject Args(string key, string value) => new() { [key] = value };

    [Fact]
    public void Allow_never_overrides_a_global_deny_policy()
    {
        var result = ToolPolicy.Resolve("deny", "{}", Args("path", "x"), AllowShellAndHttp);

        Assert.Equal((PolicyDecision.Deny, PolicySource.ToolPolicy), (result.Decision, result.Source));
    }

    [Fact]
    public void Allow_never_overrides_a_deny_when_rule()
    {
        var result = ToolPolicy.Resolve("approval", ShellRules, Args("command", "sudo ls"), AllowShellAndHttp);

        Assert.Equal((PolicyDecision.Deny, PolicySource.DenyWhen), (result.Decision, result.Source));
    }

    [Fact]
    public void Block_stops_a_tool_the_global_policy_runs_automatically()
    {
        var result = ToolPolicy.Resolve("auto", "{}", Args("query", "x"), new WorkflowToolRules(Array.Empty<string>(), new[] { "kb_search" }), "kb_search");

        Assert.Equal((PolicyDecision.Deny, PolicySource.WorkflowBlock), (result.Decision, result.Source));
    }

    [Fact]
    public void Approval_when_still_asks_even_for_an_allowed_tool()
    {
        var result = ToolPolicy.Resolve("auto", HttpRules, Args("url", "http://127.0.0.1/admin"), AllowShellAndHttp, "http_get");

        Assert.Equal((PolicyDecision.Approval, PolicySource.ApprovalWhen), (result.Decision, result.Source));
    }

    [Fact]
    public void Allow_turns_an_approval_tool_into_automatic()
    {
        var result = ToolPolicy.Resolve("approval", ShellRules, Args("command", "ls"), AllowShellAndHttp, "run_shell");

        Assert.Equal((PolicyDecision.Auto, PolicySource.WorkflowAllow), (result.Decision, result.Source));
    }

    [Fact]
    public void Block_wins_when_the_tool_is_in_both_lists()
    {
        var both = new WorkflowToolRules(new[] { "run_shell" }, new[] { "run_shell" });

        var result = ToolPolicy.Resolve("approval", "{}", Args("command", "ls"), both, "run_shell");

        Assert.Equal((PolicyDecision.Deny, PolicySource.WorkflowBlock), (result.Decision, result.Source));
    }

    [Fact]
    public void Wildcard_matches_every_tool_of_an_mcp_server()
    {
        var rules = new WorkflowToolRules(new[] { "mcp_fs_*" }, Array.Empty<string>());

        var result = ToolPolicy.Resolve("approval", "{}", new JsonObject(), rules, "mcp_fs_read");

        Assert.Equal(PolicyDecision.Auto, result.Decision);
    }

    [Fact]
    public void Without_rules_the_decision_is_unchanged()
    {
        Assert.Equal(ToolPolicy.Resolve("approval", "{}", new JsonObject()), ToolPolicy.Resolve("approval", "{}", new JsonObject(), null, "run_shell"));
    }

    [Fact]
    public void Definition_without_tools_block_has_no_rules_and_missing_lists_are_empty()
    {
        Assert.Null(WorkflowToolRules.Parse(JsonNode.Parse("""{"nodes": {}}""")!.AsObject()));

        var onlyBlock = WorkflowToolRules.Parse(JsonNode.Parse("""{"tools": {"block": ["run_shell"]}, "nodes": {}}""")!.AsObject());

        Assert.Empty(onlyBlock!.Allow);
        Assert.Equal(new[] { "run_shell" }, onlyBlock.Block);
    }

    [Fact]
    public void Patterns_that_match_no_registered_tool_are_reported()
    {
        var rules = new WorkflowToolRules(new[] { "mcp_fs_*", "run_shel" }, new[] { "run_shell", "ferramenta_inexistente" });

        var unmatched = rules.UnmatchedPatterns(new[] { "run_shell", "mcp_fs_read", "kb_search" });

        Assert.Equal(new[] { "run_shel", "ferramenta_inexistente" }, unmatched);
    }
}
