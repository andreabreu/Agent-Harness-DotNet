using System.Text.Json.Nodes;
using AgentPoc.Workflows;
using Xunit;

namespace AgentPoc.Tests;

public class NodeRetryPolicyTests
{
    private static NodeRetryPolicy Policy(string json) => NodeRetryPolicy.From(JsonNode.Parse(json));

    [Fact]
    public void Without_retry_the_node_runs_once()
    {
        var policy = NodeRetryPolicy.From(null);

        Assert.Equal(1, policy.MaxAttempts);
        Assert.False(policy.ShouldRetry(1, NodeFailure.Error, effectStarted: false, sideEffect: false));
    }

    [Fact]
    public void Retries_until_max_attempts()
    {
        var policy = Policy("""{"max_attempts": 3}""");

        Assert.True(policy.ShouldRetry(1, NodeFailure.Error, false, false));
        Assert.True(policy.ShouldRetry(2, NodeFailure.Timeout, false, false));
        Assert.False(policy.ShouldRetry(3, NodeFailure.Error, false, false));
    }

    [Fact]
    public void Only_retries_the_listed_failures()
    {
        var policy = Policy("""{"max_attempts": 3, "on": ["timeout"]}""");

        Assert.True(policy.ShouldRetry(1, NodeFailure.Timeout, false, false));
        Assert.False(policy.ShouldRetry(1, NodeFailure.Error, false, false));
    }

    [Fact]
    public void Side_effect_tool_only_retries_when_the_effect_never_started()
    {
        var policy = Policy("""{"max_attempts": 3}""");

        Assert.True(policy.ShouldRetry(1, NodeFailure.Error, effectStarted: false, sideEffect: true));
        Assert.False(policy.ShouldRetry(1, NodeFailure.Error, effectStarted: true, sideEffect: true));
        Assert.True(policy.ShouldRetry(1, NodeFailure.Error, effectStarted: true, sideEffect: false));
    }

    [Fact]
    public void Wait_grows_by_the_factor()
    {
        var defaults = Policy("""{"max_attempts": 4}""");
        var custom = Policy("""{"max_attempts": 3, "backoff_seconds": 0.2, "factor": 2}""");

        Assert.Equal(new[] { 1.0, 2.0, 4.0 }, new[] { 1, 2, 3 }.Select(a => defaults.Wait(a).TotalSeconds));
        Assert.Equal(new[] { 0.2, 0.4 }, new[] { 1, 2 }.Select(a => Math.Round(custom.Wait(a).TotalSeconds, 3)));
    }
}
