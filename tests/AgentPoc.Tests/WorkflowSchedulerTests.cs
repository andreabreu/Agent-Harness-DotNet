using System.Text.Json.Nodes;
using AgentPoc.Workflows;
using Xunit;

namespace AgentPoc.Tests;

public class WorkflowSchedulerTests
{
    private static WorkflowScheduler Scheduler(string nodesJson) =>
        new(WorkflowDefinition.Parse(JsonNode.Parse($$"""{"nodes": {{nodesJson}}}""")!.AsObject(), "teste"));

    private static string Tool(string next, string join = "all") =>
        $$"""{"type": "tool", "tool": "kb_search", "join": "{{join}}", "next": [{{next}}]}""";

    private const string Diamond = """
        {"a": {"type": "tool", "tool": "kb_search", "next": ["b", "c"]},
         "b": {"type": "tool", "tool": "kb_search", "next": ["d"]},
         "c": {"type": "tool", "tool": "kb_search", "next": ["d"]},
         "d": {"type": "tool", "tool": "kb_search", "next": []}}
        """;

    [Fact]
    public void Linear_graph_releases_one_node_at_a_time()
    {
        var s = Scheduler($$"""{"a": {{Tool("\"b\"")}}, "b": {{Tool("")}}}""");

        Assert.Equal(new[] { "a" }, s.Next().Ready);
        Assert.Empty(s.Next().Ready);
        s.Complete("a");
        Assert.Equal(new[] { "b" }, s.Next().Ready);
        s.Complete("b");
        Assert.False(s.HasPending);
    }

    [Fact]
    public void Parallel_branches_leave_in_the_same_batch_and_the_join_waits_for_both()
    {
        var s = Scheduler(Diamond);
        s.Next();
        s.Complete("a");

        Assert.Equal(new[] { "b", "c" }, s.Next().Ready);
        s.Complete("b");
        Assert.Empty(s.Next().Ready);
        s.Complete("c");
        Assert.Equal(new[] { "d" }, s.Next().Ready);
    }

    [Fact]
    public void Skipped_predecessor_skips_an_all_join_and_skips_propagate()
    {
        var s = Scheduler($$"""
            {"r": {"type": "condition", "cases": [{"when": {"path": "input.x", "exists": true}, "next": ["b"]}], "default": ["c"]},
             "b": {{Tool("\"d\"")}}, "c": {{Tool("\"e\"")}}, "d": {{Tool("")}}, "e": {{Tool("")}}}
            """);
        s.Next();
        s.Complete("r", chosenNext: new[] { "b" });

        var step = s.Next();

        Assert.Equal(new[] { "b" }, step.Ready);
        Assert.Equal(new[] { "c", "e" }, step.Skipped);
        Assert.Equal(NodeState.Skipped, s.StateOf("e"));
    }

    [Fact]
    public void Condition_without_chosen_destination_skips_all_of_them()
    {
        var s = Scheduler($$"""
            {"r": {"type": "condition", "cases": [{"when": {"path": "input.x", "exists": true}, "next": ["b"]}]},
             "b": {{Tool("")}}}
            """);
        s.Next();
        s.Complete("r", chosenNext: Array.Empty<string>());

        Assert.Equal(new[] { "b" }, s.Next().Skipped);
        Assert.False(s.HasPending);
    }

    [Fact]
    public void Nothing_ready_with_pending_and_nothing_running_means_no_progress()
    {
        var s = Scheduler(Diamond);
        s.Next();
        s.Fail("a");

        Assert.Empty(s.Next().Ready);
        Assert.True(s.HasPending);
        Assert.False(s.HasRunning);
    }

    [Fact]
    public void Restored_state_releases_the_same_nodes_as_the_original_run()
    {
        const string graph = """
            {"r": {"type": "condition", "cases": [{"when": {"path": "input.x", "exists": true}, "next": ["b"]}], "default": ["c"]},
             "b": {"type": "tool", "tool": "kb_search", "next": []},
             "c": {"type": "tool", "tool": "kb_search", "next": []}}
            """;
        var original = Scheduler(graph);
        original.Next();
        original.Complete("r", chosenNext: new[] { "c" });
        var restored = Scheduler(graph);
        restored.Restore("r", NodeState.Completed, new[] { "c" });

        var a = original.Next();
        var b = restored.Next();

        Assert.Equal(a.Ready, b.Ready);
        Assert.Equal(a.Skipped, b.Skipped);
    }

    [Fact]
    public void All_active_join_runs_after_the_chosen_branch_and_skips_when_every_branch_was_skipped()
    {
        const string graph = """
            {"r": {"type": "condition", "cases": [{"when": {"path": "input.x", "exists": true}, "next": ["b"]}], "default": ["c"]},
             "b": {"type": "tool", "tool": "kb_search", "next": ["j"]},
             "c": {"type": "tool", "tool": "kb_search", "next": ["j"]},
             "j": {"type": "tool", "tool": "kb_search", "join": "all_active", "next": []}}
            """;
        var s = Scheduler(graph);
        s.Next();
        s.Complete("r", chosenNext: new[] { "b" });
        Assert.Equal(new[] { "b" }, s.Next().Ready);
        s.Complete("b");
        Assert.Equal(new[] { "j" }, s.Next().Ready);

        var none = Scheduler(graph);
        none.Next();
        none.Complete("r", chosenNext: Array.Empty<string>());
        Assert.Equal(new[] { "b", "c", "j" }, none.Next().Skipped);
    }

    [Fact]
    public void Any_join_runs_once_when_the_first_predecessor_completes_even_with_another_still_running()
    {
        var s = Scheduler(Diamond.Replace("\"d\": {\"type\": \"tool\", \"tool\": \"kb_search\", \"next\": []}",
                                          "\"d\": {\"type\": \"tool\", \"tool\": \"kb_search\", \"join\": \"any\", \"next\": []}"));
        s.Next();
        s.Complete("a");
        s.Next();
        s.Complete("b");

        Assert.Equal(new[] { "d" }, s.Next().Ready);
        s.Complete("c");
        Assert.Empty(s.Next().Ready);
    }

    [Fact]
    public void Any_join_with_every_predecessor_skipped_is_skipped()
    {
        var s = Scheduler("""
            {"r": {"type": "condition", "cases": [{"when": {"path": "input.x", "exists": true}, "next": ["b", "c"]}]},
             "b": {"type": "tool", "tool": "kb_search", "next": ["j"]},
             "c": {"type": "tool", "tool": "kb_search", "next": ["j"]},
             "j": {"type": "tool", "tool": "kb_search", "join": "any", "next": []}}
            """);
        s.Next();
        s.Complete("r", chosenNext: Array.Empty<string>());

        Assert.Contains("j", s.Next().Skipped);
    }

    [Fact]
    public void Finish_skips_every_pending_node_and_releases_nothing_else()
    {
        var s = Scheduler(Diamond);
        s.Next();
        s.Complete("a");

        Assert.Equal(new[] { "b", "c", "d" }, s.Finish());
        Assert.Empty(s.Next().Ready);
        Assert.False(s.HasPending);
    }

    [Fact]
    public void Limit_releases_at_most_that_many_running_nodes_in_definition_order()
    {
        var s = Scheduler($$"""{"a": {{Tool("")}}, "b": {{Tool("")}}, "c": {{Tool("")}}, "d": {{Tool("")}}}""");

        Assert.Equal(new[] { "a", "b" }, s.Next(limit: 2).Ready);
        Assert.Empty(s.Next(limit: 2).Ready);
        s.Complete("a");
        Assert.Equal(new[] { "c" }, s.Next(limit: 2).Ready);
        s.Complete("b");
        s.Complete("c");
        Assert.Equal(new[] { "d" }, s.Next(limit: 2).Ready);
    }
}
