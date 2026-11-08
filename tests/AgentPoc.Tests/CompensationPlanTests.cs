using AgentPoc.Workflows;
using Xunit;

namespace AgentPoc.Tests;

public class CompensationPlanTests
{
    private static readonly DateTime T0 = new(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Compensates_completed_nodes_in_reverse_completion_order()
    {
        var order = CompensationPlan.Order(new[]
        {
            new CompensationCandidate("a", T0, HasCompensation: true, Compensated: false),
            new CompensationCandidate("c", T0.AddSeconds(2), HasCompensation: true, Compensated: false),
            new CompensationCandidate("b", T0.AddSeconds(1), HasCompensation: true, Compensated: false)
        });

        Assert.Equal(new[] { "c", "b", "a" }, order);
    }

    [Fact]
    public void Skips_nodes_without_compensation_and_the_already_compensated()
    {
        var order = CompensationPlan.Order(new[]
        {
            new CompensationCandidate("a", T0, HasCompensation: true, Compensated: false),
            new CompensationCandidate("b", T0.AddSeconds(1), HasCompensation: false, Compensated: false),
            new CompensationCandidate("c", T0.AddSeconds(2), HasCompensation: true, Compensated: true)
        });

        Assert.Equal(new[] { "a" }, order);
    }
}
