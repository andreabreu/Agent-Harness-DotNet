using AgentPoc.Chat;
using AgentPoc.Harness;
using AgentPoc.Llm;
using Xunit;

namespace AgentPoc.Tests;

public class ChatHistoryTests
{
    private static ChatMessage AssistantCalling(params (string Id, string Name)[] calls) =>
        ChatMessage.Assistant(null, calls.Select(c => new ToolCall(c.Id, c.Name, "{}")).ToList());

    [Fact]
    public void Repair_keeps_a_consistent_history_untouched()
    {
        var history = new List<ChatMessage>
        {
            ChatMessage.User("oi"),
            AssistantCalling(("c1", "web_search")),
            ChatMessage.Tool("c1", "web_search", "resultados"),
            ChatMessage.Assistant("resposta", null)
        };

        var repaired = ChatHistory.Repair(history);

        Assert.Equal(history, repaired);
    }

    [Fact]
    public void Repair_answers_tool_calls_left_without_result_when_the_process_died()
    {
        var history = new List<ChatMessage>
        {
            ChatMessage.User("calcule"),
            AssistantCalling(("c1", "write_file"), ("c2", "run_shell")),
            ChatMessage.Tool("c1", "write_file", "salvo"),
            ChatMessage.User("e agora?")
        };

        var repaired = ChatHistory.Repair(history);

        Assert.Equal(new[] { "user", "assistant", "tool", "tool", "user" }, repaired.Select(m => m.Role));
        Assert.Equal("c2", repaired[3].ToolCallId);
        Assert.Equal(ChatHistory.InterruptedToolResult, repaired[3].Content);
    }

    [Fact]
    public void Repair_closes_orphan_calls_at_the_end_of_the_history()
    {
        var repaired = ChatHistory.Repair(new List<ChatMessage> { ChatMessage.User("x"), AssistantCalling(("c9", "http_get")) });

        Assert.Equal("c9", repaired.Last().ToolCallId);
    }

    [Fact]
    public void Repair_drops_tool_results_without_a_matching_call()
    {
        var repaired = ChatHistory.Repair(new List<ChatMessage> { ChatMessage.User("x"), ChatMessage.Tool("perdido", "http_get", "...") });

        Assert.Single(repaired);
    }

    [Fact]
    public void Summary_cutoff_keeps_the_last_turns_and_never_splits_a_tool_call()
    {
        var messages = new List<(int, string)>
        {
            (1, "user"), (2, "assistant"), (3, "tool"), (4, "assistant"),
            (5, "user"), (6, "assistant"),
            (7, "user"), (8, "assistant"), (9, "tool"), (10, "assistant")
        };

        Assert.Equal(4, ChatHistory.FindSummaryCutoff(messages, keepTurns: 2));
    }

    [Fact]
    public void Summary_cutoff_is_null_when_there_are_not_enough_turns()
    {
        var messages = new List<(int, string)> { (1, "user"), (2, "assistant"), (3, "user"), (4, "assistant") };

        Assert.Null(ChatHistory.FindSummaryCutoff(messages, keepTurns: 2));
    }

    [Fact]
    public void Unanswered_tool_calls_lists_only_calls_of_the_last_assistant_message_without_result()
    {
        var messages = new List<ChatMessage>
        {
            ChatMessage.User("x"),
            AssistantCalling(("a", "think"), ("b", "web_search"), ("c", "http_get")),
            ChatMessage.Tool("a", "think", "ok")
        };

        var orphans = AgentRunner.UnansweredToolCalls(messages).Select(c => c.Id);

        Assert.Equal(new[] { "b", "c" }, orphans);
    }
}
