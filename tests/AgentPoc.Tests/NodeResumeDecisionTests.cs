using AgentPoc.Workflows;
using Xunit;

namespace AgentPoc.Tests;

public class NodeResumeDecisionTests
{
    private static readonly DateTime Now = new(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Node_without_run_runs_from_scratch()
    {
        Assert.Equal(NodeResumeAction.RunFromScratch, NodeResumeDecision.For(null, null, Now));
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("failed")]
    [InlineData("cancelled")]
    [InlineData("budget_exceeded")]
    [InlineData("max_steps")]
    public void Finished_run_uses_its_result(string status)
    {
        Assert.Equal(NodeResumeAction.UseResult, NodeResumeDecision.For(status, null, Now));
    }

    [Fact]
    public void Running_run_with_expired_lease_is_taken_over()
    {
        Assert.Equal(NodeResumeAction.TakeOverAndResume, NodeResumeDecision.For("running", Now.AddSeconds(-1), Now));
    }

    [Fact]
    public void Ready_run_is_taken_over()
    {
        Assert.Equal(NodeResumeAction.TakeOverAndResume, NodeResumeDecision.For("ready", null, Now));
    }

    [Fact]
    public void Running_run_with_valid_lease_waits_for_its_owner()
    {
        Assert.Equal(NodeResumeAction.Wait, NodeResumeDecision.For("running", Now.AddSeconds(30), Now));
    }
}
