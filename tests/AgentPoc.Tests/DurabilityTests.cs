using AgentPoc.Harness;
using AgentPoc.Llm;
using Xunit;

namespace AgentPoc.Tests;

public class ParentDeliveryTests
{
    private static readonly Guid Child = Guid.NewGuid();

    [Fact]
    public void Finished_child_delivers_its_output_as_the_tool_result_of_the_pending_delegate_call()
    {
        var plan = ParentDelivery.Plan(Child, "researcher", RunStatus.Completed, 2, "ambiente: producao",
            RunStatus.WaitingChild, Child, "call_delegate");

        Assert.NotNull(plan);
        Assert.Equal("call_delegate", plan!.ToolCallId);
        Assert.Equal(("tool", "call_delegate", "delegate_task"), (plan.ToolMessage.Role, plan.ToolMessage.ToolCallId, plan.ToolMessage.ToolName));
        Assert.Contains("ambiente: producao", plan.ToolMessage.Content);
        Assert.True(plan.Success);
    }

    [Theory]
    [InlineData(RunStatus.Failed)]
    [InlineData(RunStatus.Cancelled)]
    public void Failed_or_cancelled_child_reaches_the_parent_as_a_tool_error(string status)
    {
        var plan = ParentDelivery.Plan(Child, "coder", status, 3, "Erro: timeout", RunStatus.WaitingChild, Child, "call_x");

        Assert.False(plan!.Success);
        Assert.StartsWith("ERRO", plan.ToolMessage.Content);
    }

    [Fact]
    public void Nothing_is_delivered_when_the_parent_already_moved_on_or_waits_on_another_child()
    {
        Assert.Null(ParentDelivery.Plan(Child, "x", RunStatus.Completed, 1, "", RunStatus.Ready, null, "call"));
        Assert.Null(ParentDelivery.Plan(Child, "x", RunStatus.Completed, 1, "", RunStatus.WaitingChild, Guid.NewGuid(), "call"));
        Assert.Null(ParentDelivery.Plan(Child, "x", RunStatus.Running, 1, "", RunStatus.WaitingChild, Child, "call"));
        Assert.Null(ParentDelivery.Plan(Child, "x", RunStatus.Completed, 1, "", RunStatus.WaitingChild, Child, null));
    }
}

public class RunLeaseTests
{
    private static readonly DateTime Now = new(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Defaults_are_a_60s_lease_renewed_every_third_of_it()
    {
        var options = RunLeaseOptions.From(null, null);

        Assert.Equal(TimeSpan.FromSeconds(60), options.Lease);
        Assert.Equal(TimeSpan.FromSeconds(20), options.Heartbeat);
        Assert.Equal(TimeSpan.FromSeconds(20), options.Sweep);
    }

    [Fact]
    public void Heartbeat_can_be_configured_but_never_reaches_the_lease()
    {
        Assert.Equal(TimeSpan.FromSeconds(2), RunLeaseOptions.From("6", "2").Heartbeat);
        Assert.Equal(TimeSpan.FromSeconds(2), RunLeaseOptions.From("6", "10").Heartbeat);
        Assert.Equal(TimeSpan.FromSeconds(60), RunLeaseOptions.From("abc", null).Lease);
    }

    [Fact]
    public void A_running_run_is_orphan_only_after_its_lease_expires()
    {
        Assert.True(RunLeaseOptions.IsOrphan(RunStatus.Running, Now.AddSeconds(-1), Now));
        Assert.False(RunLeaseOptions.IsOrphan(RunStatus.Running, Now.AddSeconds(30), Now));
        Assert.False(RunLeaseOptions.IsOrphan(RunStatus.Running, null, Now));
    }

    [Theory]
    [InlineData(RunStatus.WaitingHuman)]
    [InlineData(RunStatus.Ready)]
    [InlineData(RunStatus.Completed)]
    public void Only_running_runs_can_be_orphans(string status)
    {
        Assert.False(RunLeaseOptions.IsOrphan(status, Now.AddMinutes(-5), Now));
    }
}

public class ToolRecoveryTests
{
    [Fact]
    public void Interrupted_call_without_side_effect_is_repeated_and_with_side_effect_asks_a_person()
    {
        Assert.Equal(RecoveryAction.Repeat, ToolRecovery.ForInterrupted(sideEffect: false));
        Assert.Equal(RecoveryAction.AskHuman, ToolRecovery.ForInterrupted(sideEffect: true));
    }

    [Theory]
    [InlineData("repetir", RecoveryAction.Repeat)]
    [InlineData("  Repetir ", RecoveryAction.Repeat)]
    [InlineData("pular", RecoveryAction.Skip)]
    [InlineData("", RecoveryAction.Skip)]
    [InlineData("SEM RESPOSTA: a pergunta expired", RecoveryAction.Skip)]
    public void Answer_decides_repeat_or_skip_and_no_answer_is_the_safe_skip(string answer, RecoveryAction expected)
    {
        Assert.Equal(expected, ToolRecovery.FromAnswer(answer).Action);
    }

    [Fact]
    public void Any_other_answer_is_the_result_informed_by_the_person()
    {
        var decision = ToolRecovery.FromAnswer("o arquivo ja foi gerado, 3 linhas");

        Assert.Equal(RecoveryAction.UseInformedResult, decision.Action);
        Assert.Equal("o arquivo ja foi gerado, 3 linhas", decision.InformedResult);
    }

    [Fact]
    public void Recovery_question_has_its_own_id_so_it_never_reuses_the_approval_of_the_call()
    {
        Assert.Equal("call_7#retomada", ToolRecovery.QuestionId("call_7"));
    }

    [Fact]
    public void Run_that_had_saved_its_final_answer_completes_without_calling_the_model_again()
    {
        var finished = new List<ChatMessage> { ChatMessage.User("oi"), ChatMessage.Assistant("pronto", null) };
        var midway = new List<ChatMessage> { ChatMessage.User("oi"), ChatMessage.Assistant(null, new List<ToolCall> { new("c", "think", "{}") }) };

        Assert.Equal("pronto", AgentRunner.FinalAnswer(finished));
        Assert.Null(AgentRunner.FinalAnswer(midway));
        Assert.Null(AgentRunner.FinalAnswer(new List<ChatMessage> { ChatMessage.User("oi") }));
    }
}
