using System.Text.Json.Nodes;
using AgentPoc.Human;
using Xunit;

namespace AgentPoc.Tests;

public class DurableHumanGateTests
{
    private static JsonObject Args(string command) => new() { ["command"] = command };

    private static HumanRequest Approval(string status, DateTime? consumedAt = null, string kind = "approval") => new(
        Guid.NewGuid(), Guid.NewGuid(), null, kind, "call_0", "run_shell", Args("deploy prod"), "politica approval",
        null, Array.Empty<string>(), status, null, null, null, DateTime.UtcNow, null, consumedAt);

    [Fact]
    public void Decision_not_yet_used_is_reused_for_the_same_tool_and_args()
    {
        var request = Approval("approved");

        Assert.Same(request, DurableHumanGate.Reusable(request, "run_shell", Args("deploy prod")));
    }

    [Fact]
    public void Approval_already_used_by_one_execution_never_authorizes_another_identical_call()
    {
        var used = Approval("approved", consumedAt: DateTime.UtcNow);

        Assert.Null(DurableHumanGate.Reusable(used, "run_shell", Args("deploy prod")));
    }

    [Fact]
    public void Decision_does_not_carry_over_to_other_tool_other_args_or_a_question()
    {
        Assert.Null(DurableHumanGate.Reusable(Approval("approved"), "http_get", Args("deploy prod")));
        Assert.Null(DurableHumanGate.Reusable(Approval("approved"), "run_shell", Args("ls")));
        Assert.Null(DurableHumanGate.Reusable(Approval("answered", kind: "question"), "run_shell", Args("deploy prod")));
        Assert.Null(DurableHumanGate.Reusable(null, "run_shell", Args("deploy prod")));
    }
}
