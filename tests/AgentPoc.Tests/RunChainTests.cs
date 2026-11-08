using AgentPoc.Harness;
using Xunit;

namespace AgentPoc.Tests;

public class RunStatusTransitionTests
{
    [Fact]
    public void Human_request_suspends_as_waiting_human_and_child_suspension_as_waiting_child()
    {
        var human = new HumanInputRequiredException(Guid.NewGuid(), "call_1", "aprovacao de run_shell");
        var child = new ChildSuspendedException(Guid.NewGuid(), "researcher", "call_2");

        Assert.Equal(RunStatus.WaitingHuman, RunStatus.ForSuspension(human));
        Assert.Equal(RunStatus.WaitingChild, RunStatus.ForSuspension(child));
        Assert.Equal("call_2", child.ToolCallId);
    }

    [Theory]
    [InlineData(RunStatus.Completed, true)]
    [InlineData(RunStatus.Failed, true)]
    [InlineData(RunStatus.Cancelled, true)]
    [InlineData(RunStatus.BudgetExceeded, true)]
    [InlineData(RunStatus.MaxSteps, true)]
    [InlineData(RunStatus.Running, false)]
    [InlineData(RunStatus.WaitingHuman, false)]
    [InlineData(RunStatus.WaitingChild, false)]
    [InlineData(RunStatus.Ready, false)]
    public void Terminal_statuses_are_exactly_the_ones_that_end_a_run(string status, bool terminal)
    {
        Assert.Equal(terminal, RunStatus.IsTerminal(status));
    }

    [Theory]
    [InlineData(RunStatus.WaitingHuman)]
    [InlineData(RunStatus.WaitingChild)]
    [InlineData(RunStatus.Ready)]
    public void Suspended_statuses_can_be_cancelled_directly_in_the_database(string status)
    {
        Assert.True(RunStatus.IsSuspended(status));
        Assert.False(RunStatus.IsTerminal(status));
    }

    [Fact]
    public void Parent_receives_the_result_only_when_the_child_finished_and_the_parent_waits_exactly_on_it()
    {
        var child = Guid.NewGuid();

        Assert.True(RunStatus.ShouldDeliverToParent(RunStatus.Completed, child, RunStatus.WaitingChild, child));
        Assert.True(RunStatus.ShouldDeliverToParent(RunStatus.Failed, child, RunStatus.WaitingChild, child));
    }

    [Theory]
    [InlineData(RunStatus.WaitingHuman, RunStatus.WaitingChild, true)]
    [InlineData(RunStatus.Running, RunStatus.WaitingChild, true)]
    [InlineData(RunStatus.Completed, RunStatus.Running, true)]
    [InlineData(RunStatus.Completed, RunStatus.Cancelled, true)]
    [InlineData(RunStatus.Completed, RunStatus.WaitingChild, false)]
    public void Parent_is_not_woken_while_the_child_is_unfinished_or_the_parent_is_elsewhere(string childStatus, string parentStatus, bool sameChild)
    {
        var child = Guid.NewGuid();
        var waitingOn = sameChild ? child : Guid.NewGuid();

        Assert.False(RunStatus.ShouldDeliverToParent(childStatus, child, parentStatus, waitingOn));
    }
}

public class DelegationResultTests
{
    [Fact]
    public void Completed_child_output_becomes_a_successful_tool_result_for_the_parent()
    {
        var result = DelegationResult.Format("researcher", RunStatus.Completed, 2, "A pessoa escolheu: producao");

        Assert.Equal("[resultado de researcher | status=completed | passos=2]\nA pessoa escolheu: producao", result);
        Assert.True(DelegationResult.IsSuccess(result));
    }

    [Theory]
    [InlineData(RunStatus.Failed)]
    [InlineData(RunStatus.Cancelled)]
    [InlineData(RunStatus.BudgetExceeded)]
    public void Unfinished_child_becomes_a_tool_error_for_the_parent(string status)
    {
        var result = DelegationResult.Format("coder", status, 3, "Erro: timeout");

        Assert.StartsWith($"ERRO: o subagente coder terminou como {status}", result);
        Assert.Contains("Erro: timeout", result);
        Assert.False(DelegationResult.IsSuccess(result));
    }

    [Fact]
    public void Long_child_output_is_truncated_for_the_parent()
    {
        var result = DelegationResult.Format("writer", RunStatus.Completed, 1, new string('x', 10_000));

        Assert.True(result.Length < 7_000);
    }
}
