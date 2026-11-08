using System.Text.Json.Nodes;
using AgentPoc.Chat;
using AgentPoc.Harness;
using AgentPoc.Llm;
using Xunit;

namespace AgentPoc.Tests;

public class ToolPolicyTests
{
    private const string ShellRules = """{"deny_when":{"command":"(^|[;&|\\s])(sudo|rm\\s+-[a-z]*[rf])"}}""";
    private const string HttpRules = """{"approval_when":{"url":"^https?://(localhost|127\\.|10\\.)"}}""";

    private static JsonObject Args(string key, string value) => new() { [key] = value };

    [Theory]
    [InlineData("rm -rf ./*")]
    [InlineData("ls && sudo reboot")]
    [InlineData("echo ok; rm -f arquivo")]
    public void Deny_rule_blocks_dangerous_commands_even_when_the_tool_needs_approval(string command)
    {
        var result = ToolPolicy.Resolve("approval", ShellRules, Args("command", command));

        Assert.Equal(PolicyDecision.Deny, result.Decision);
        Assert.Contains("deny_when", result.Reason);
    }

    [Fact]
    public void Tool_policy_applies_when_no_rule_matches()
    {
        Assert.Equal(PolicyDecision.Approval, ToolPolicy.Resolve("approval", ShellRules, Args("command", "python3 custo.py")).Decision);
    }

    [Theory]
    [InlineData("http://localhost:8080/admin")]
    [InlineData("https://127.0.0.1/x")]
    [InlineData("http://10.0.0.5/metadata")]
    public void Approval_rule_escalates_an_auto_tool_for_internal_urls(string url)
    {
        var result = ToolPolicy.Resolve("auto", HttpRules, Args("url", url));

        Assert.Equal(PolicyDecision.Approval, result.Decision);
        Assert.Contains("approval_when", result.Reason);
    }

    [Fact]
    public void Public_url_keeps_the_auto_policy()
    {
        Assert.Equal(PolicyDecision.Auto, ToolPolicy.Resolve("auto", HttpRules, Args("url", "https://temporal.io")).Decision);
    }

    [Fact]
    public void Deny_policy_wins_over_everything()
    {
        Assert.Equal(PolicyDecision.Deny, ToolPolicy.Resolve("deny", "{}", new JsonObject()).Decision);
    }

    [Fact]
    public void Missing_argument_or_invalid_config_does_not_trigger_rules()
    {
        Assert.Equal(PolicyDecision.Auto, ToolPolicy.Resolve("auto", HttpRules, new JsonObject()).Decision);
        Assert.Equal(PolicyDecision.Auto, ToolPolicy.Resolve("auto", "{nao e json", Args("url", "http://localhost")).Decision);
    }
}

public class ResumeHistoryTests
{
    [Fact]
    public void Repair_keeps_the_trailing_pending_calls_for_resume_but_closes_older_orphans()
    {
        var history = new List<ChatMessage>
        {
            ChatMessage.User("primeiro"),
            ChatMessage.Assistant(null, new List<ToolCall> { new("velho", "http_get", "{}") }),
            ChatMessage.User("segundo"),
            ChatMessage.Assistant(null, new List<ToolCall> { new("a", "think", "{}"), new("b", "run_shell", "{}") }),
            ChatMessage.Tool("a", "think", "ok")
        };

        var repaired = ChatHistory.Repair(history, keepTrailingPending: true);

        Assert.Contains(repaired, m => m.ToolCallId == "velho" && m.Content == ChatHistory.InterruptedToolResult);
        Assert.DoesNotContain(repaired, m => m.ToolCallId == "b");
        Assert.Equal(new[] { "b" }, AgentRunner.UnansweredToolCalls(repaired).Select(c => c.Id));
    }
}
