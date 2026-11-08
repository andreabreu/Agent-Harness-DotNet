using System.Text.Json.Nodes;
using AgentPoc.Llm;
using Xunit;

namespace AgentPoc.Tests;

public class OpenAiStreamAccumulatorTests
{
    private static JsonNode Chunk(string json) => JsonNode.Parse(json)!;

    [Fact]
    public void Text_deltas_are_concatenated_and_returned_one_by_one()
    {
        var acc = new OpenAiStreamAccumulator();

        var first = acc.Add(Chunk("""{"choices":[{"index":0,"delta":{"role":"assistant","content":"Ola, "}}]}"""));
        var second = acc.Add(Chunk("""{"choices":[{"index":0,"delta":{"content":"mundo"},"finish_reason":"stop"}]}"""));
        var response = acc.Build(10);

        Assert.Equal("Ola, ", first.Text);
        Assert.Equal("mundo", second.Text);
        Assert.Equal("Ola, mundo", response.Content);
        Assert.Equal("stop", response.StopReason);
        Assert.Empty(response.ToolCalls);
    }

    [Fact]
    public void Tool_call_arguments_split_across_chunks_are_rebuilt_per_index()
    {
        var acc = new OpenAiStreamAccumulator();

        acc.Add(Chunk("""{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_a","function":{"name":"web_search","arguments":""}}]}}]}"""));
        acc.Add(Chunk("""{"choices":[{"delta":{"tool_calls":[{"index":1,"id":"call_b","function":{"name":"think","arguments":"{\"thought\":"}}]}}]}"""));
        acc.Add(Chunk("""{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"query\":"}}]}}]}"""));
        acc.Add(Chunk("""{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"\"temporal\"}"}}]}}]}"""));
        acc.Add(Chunk("""{"choices":[{"delta":{"tool_calls":[{"index":1,"function":{"arguments":"\"ok\"}"}}]}}]}"""));
        acc.Add(Chunk("""{"choices":[{"delta":{},"finish_reason":"tool_calls"}]}"""));
        var response = acc.Build(10);

        Assert.Collection(response.ToolCalls,
            c => Assert.Equal(("call_a", "web_search", """{"query":"temporal"}"""), (c.Id, c.Name, c.ArgumentsJson)),
            c => Assert.Equal(("call_b", "think", """{"thought":"ok"}"""), (c.Id, c.Name, c.ArgumentsJson)));
        Assert.Equal("tool_calls", response.StopReason);
    }

    [Fact]
    public void Usage_chunk_with_empty_choices_sets_token_counts()
    {
        var acc = new OpenAiStreamAccumulator();

        acc.Add(Chunk("""{"choices":[],"prompt_filter_results":[]}"""));
        acc.Add(Chunk("""{"choices":[],"usage":{"prompt_tokens":120,"completion_tokens":30}}"""));
        var response = acc.Build(10);

        Assert.Equal(120, response.InputTokens);
        Assert.Equal(30, response.OutputTokens);
        Assert.Null(response.Content);
    }

    [Fact]
    public void Reasoning_content_from_local_models_is_reported_separately()
    {
        var acc = new OpenAiStreamAccumulator();

        var delta = acc.Add(Chunk("""{"choices":[{"delta":{"reasoning_content":"pensando..."}}]}"""));

        Assert.Equal("pensando...", delta.Reasoning);
        Assert.Null(delta.Text);
        Assert.Null(acc.Build(1).Content);
    }
}

public class AnthropicStreamAccumulatorTests
{
    private static JsonNode Evt(string json) => JsonNode.Parse(json)!;

    [Fact]
    public void Text_and_tool_use_blocks_are_rebuilt_with_usage_and_stop_reason()
    {
        var acc = new AnthropicStreamAccumulator();

        acc.Add(Evt("""{"type":"message_start","message":{"usage":{"input_tokens":50,"output_tokens":1}}}"""));
        acc.Add(Evt("""{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}"""));
        var text = acc.Add(Evt("""{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Vou pesquisar."}}"""));
        acc.Add(Evt("""{"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"toolu_1","name":"web_search","input":{}}}"""));
        acc.Add(Evt("""{"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{\"query\":"}}"""));
        acc.Add(Evt("""{"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"\"dbos\"}"}}"""));
        acc.Add(Evt("""{"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":42}}"""));
        var response = acc.Build(10);

        Assert.Equal("Vou pesquisar.", text.Text);
        Assert.Equal("Vou pesquisar.", response.Content);
        var call = Assert.Single(response.ToolCalls);
        Assert.Equal(("toolu_1", "web_search", """{"query":"dbos"}"""), (call.Id, call.Name, call.ArgumentsJson));
        Assert.Equal((50, 42, "tool_use"), (response.InputTokens, response.OutputTokens, response.StopReason));
    }

    [Fact]
    public void Tool_use_without_input_deltas_gets_empty_object_arguments()
    {
        var acc = new AnthropicStreamAccumulator();

        acc.Add(Evt("""{"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"toolu_2","name":"list_agents","input":{}}}"""));

        Assert.Equal("{}", Assert.Single(acc.Build(1).ToolCalls).ArgumentsJson);
    }

    [Fact]
    public void Error_event_raises_llm_exception()
    {
        var acc = new AnthropicStreamAccumulator();

        Assert.Throws<LlmException>(() => acc.Add(Evt("""{"type":"error","error":{"type":"overloaded_error"}}""")));
    }
}
