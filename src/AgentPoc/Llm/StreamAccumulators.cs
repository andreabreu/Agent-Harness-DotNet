using System.Text;
using System.Text.Json.Nodes;

namespace AgentPoc.Llm;

/// <summary>Pedacos de texto extraidos de um evento de streaming.</summary>
public readonly record struct StreamDelta(string? Text, string? Reasoning);

internal sealed class ToolCallBuilder
{
    public string Id = "";
    public string Name = "";
    public readonly StringBuilder Arguments = new();

    public ToolCall Build() =>
        new(Id.Length > 0 ? Id : "call_" + Guid.NewGuid().ToString("N").Substring(0, 12), Name,
            Arguments.Length > 0 ? Arguments.ToString() : "{}");
}

/// <summary>Monta a resposta final a partir dos chunks de streaming do formato OpenAI Chat Completions.</summary>
public sealed class OpenAiStreamAccumulator
{
    private readonly StringBuilder _content = new();
    private readonly SortedDictionary<int, ToolCallBuilder> _calls = new();

    public int InputTokens { get; private set; }
    public int OutputTokens { get; private set; }
    public string FinishReason { get; private set; } = "";
    public int ContentChars => _content.Length + _calls.Values.Sum(c => c.Arguments.Length);

    public StreamDelta Add(JsonNode chunk)
    {
        if (chunk["usage"] is JsonObject usage)
        {
            InputTokens = ProviderHttp.Int(usage["prompt_tokens"]);
            OutputTokens = ProviderHttp.Int(usage["completion_tokens"]);
        }

        var choice = chunk["choices"] is JsonArray { Count: > 0 } choices ? choices[0] : null;
        if (choice == null) return default;
        if (ProviderHttp.Str(choice["finish_reason"]) is { Length: > 0 } finish) FinishReason = finish;

        var delta = choice["delta"];
        if (delta?["tool_calls"] is JsonArray calls)
            foreach (var tc in calls)
            {
                var index = ProviderHttp.Int(tc?["index"]);
                if (!_calls.TryGetValue(index, out var builder)) _calls[index] = builder = new ToolCallBuilder();
                if (ProviderHttp.Str(tc?["id"]) is { Length: > 0 } id) builder.Id = id;
                if (ProviderHttp.Str(tc?["function"]?["name"]) is { Length: > 0 } name) builder.Name = name;
                builder.Arguments.Append(ProviderHttp.Str(tc?["function"]?["arguments"]));
            }

        var text = ProviderHttp.Str(delta?["content"]);
        if (!string.IsNullOrEmpty(text)) _content.Append(text);
        var reasoning = ProviderHttp.Str(delta?["reasoning_content"]) ?? ProviderHttp.Str(delta?["reasoning"]);
        return new StreamDelta(string.IsNullOrEmpty(text) ? null : text, string.IsNullOrEmpty(reasoning) ? null : reasoning);
    }

    public LlmResponse Build(long latencyMs) =>
        new(_content.Length > 0 ? _content.ToString() : null, _calls.Values.Select(c => c.Build()).ToList(),
            InputTokens, OutputTokens, FinishReason, latencyMs);
}

/// <summary>Monta a resposta final a partir dos eventos de streaming da Messages API da Anthropic.</summary>
public sealed class AnthropicStreamAccumulator
{
    private readonly StringBuilder _content = new();
    private readonly SortedDictionary<int, ToolCallBuilder> _calls = new();

    public int InputTokens { get; private set; }
    public int OutputTokens { get; private set; }
    public string StopReason { get; private set; } = "";

    public StreamDelta Add(JsonNode evt)
    {
        switch (ProviderHttp.Str(evt["type"]))
        {
            case "message_start":
                InputTokens = ProviderHttp.Int(evt["message"]?["usage"]?["input_tokens"]);
                OutputTokens = ProviderHttp.Int(evt["message"]?["usage"]?["output_tokens"]);
                return default;
            case "content_block_start" when ProviderHttp.Str(evt["content_block"]?["type"]) == "tool_use":
                _calls[ProviderHttp.Int(evt["index"])] = new ToolCallBuilder
                {
                    Id = ProviderHttp.Str(evt["content_block"]?["id"]) ?? "",
                    Name = ProviderHttp.Str(evt["content_block"]?["name"]) ?? ""
                };
                return default;
            case "content_block_delta":
                return AddBlockDelta(ProviderHttp.Int(evt["index"]), evt["delta"]);
            case "message_delta":
                if (ProviderHttp.Str(evt["delta"]?["stop_reason"]) is { Length: > 0 } stop) StopReason = stop;
                if (evt["usage"]?["output_tokens"] is JsonNode output) OutputTokens = ProviderHttp.Int(output);
                return default;
            case "error":
                throw new LlmException($"Erro no streaming da Anthropic: {evt["error"]?.ToJsonString()}");
            default:
                return default;
        }
    }

    private StreamDelta AddBlockDelta(int index, JsonNode? delta)
    {
        switch (ProviderHttp.Str(delta?["type"]))
        {
            case "text_delta":
                var text = ProviderHttp.Str(delta?["text"]) ?? "";
                _content.Append(text);
                return new StreamDelta(text, null);
            case "thinking_delta":
                return new StreamDelta(null, ProviderHttp.Str(delta?["thinking"]));
            case "input_json_delta" when _calls.TryGetValue(index, out var builder):
                builder.Arguments.Append(ProviderHttp.Str(delta?["partial_json"]));
                return default;
            default:
                return default;
        }
    }

    public LlmResponse Build(long latencyMs) =>
        new(_content.Length > 0 ? _content.ToString() : null, _calls.Values.Select(c => c.Build()).ToList(),
            InputTokens, OutputTokens, StopReason, latencyMs);
}
