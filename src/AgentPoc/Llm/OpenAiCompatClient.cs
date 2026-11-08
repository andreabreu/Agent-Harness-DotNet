using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using AgentPoc.Core;

namespace AgentPoc.Llm;

/// <summary>
/// Cliente para APIs compativeis com OpenAI Chat Completions:
/// Azure Foundry / Azure OpenAI (endpoint .../openai/v1), Foundry Local, Ollama, vLLM, OpenAI.
/// </summary>
public sealed class OpenAiCompatClient : ILlmClient
{
    private readonly HttpClient _http;
    public OpenAiCompatClient(HttpClient http) => _http = http;

    public async Task<LlmResponse> CompleteAsync(ModelCfg model, LlmRequest req, CancellationToken ct)
    {
        var baseUrl = Env.Expand(model.Provider.BaseUrl).TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new LlmException($"Provedor {model.Provider.Id}: base_url vazio ('{model.Provider.BaseUrl}'). Verifique o .env.");
        var url = baseUrl.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase) ? baseUrl : baseUrl + "/chat/completions";

        var messages = new JsonArray();
        if (!string.IsNullOrEmpty(req.System))
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = req.System });

        foreach (var m in req.Messages)
        {
            if (m.Role == "tool")
            {
                messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = m.ToolCallId, ["content"] = m.Content ?? "" });
            }
            else if (m.Role == "assistant")
            {
                var o = new JsonObject { ["role"] = "assistant", ["content"] = m.Content };
                if (m.ToolCalls is { Count: > 0 })
                {
                    var calls = new JsonArray();
                    foreach (var tc in m.ToolCalls)
                    {
                        calls.Add(new JsonObject
                        {
                            ["id"] = tc.Id,
                            ["type"] = "function",
                            ["function"] = new JsonObject { ["name"] = tc.Name, ["arguments"] = tc.ArgumentsJson }
                        });
                    }
                    o["tool_calls"] = calls;
                }
                messages.Add(o);
            }
            else
            {
                messages.Add(new JsonObject { ["role"] = "user", ["content"] = m.Content ?? "" });
            }
        }

        var body = new JsonObject { ["model"] = model.ResolvedName, ["messages"] = messages };
        body[model.MaxTokensParam] = model.MaxOutputTokens;
        if (model.SupportsTemperature && req.Temperature.HasValue) body["temperature"] = req.Temperature.Value;
        if (model.SupportsTools && req.Tools.Count > 0)
        {
            var tools = new JsonArray();
            foreach (var t in req.Tools)
            {
                tools.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = t.Name,
                        ["description"] = t.Description,
                        ["parameters"] = t.Parameters.DeepClone()
                    }
                });
            }
            body["tools"] = tools;
        }

        if (!string.IsNullOrEmpty(model.ReasoningEffort)) body["reasoning_effort"] = model.ReasoningEffort;

        var streaming = req.Stream != null && model.SupportsStreaming;
        if (streaming)
        {
            body["stream"] = true;
            body["stream_options"] = new JsonObject { ["include_usage"] = true };
        }

        var json = body.ToJsonString();
        var sw = Stopwatch.StartNew();
        using var resp = await ProviderHttp.SendWithRetryAsync(_http, () =>
        {
            var msg = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            ProviderHttp.ApplyAuth(msg, model.Provider);
            return msg;
        }, ct, streaming);
        if (!resp.IsSuccessStatusCode)
            throw new LlmException($"{model.Provider.Id} respondeu HTTP {(int)resp.StatusCode}: {Text.Trunc(await resp.Content.ReadAsStringAsync(ct), 800)}");

        return streaming
            ? await ReadStreamAsync(resp, req.Stream!, json.Length, sw, ct)
            : Parse(await resp.Content.ReadAsStringAsync(ct), sw);
    }

    private static async Task<LlmResponse> ReadStreamAsync(HttpResponseMessage resp, LlmStream stream, int requestChars, Stopwatch sw, CancellationToken ct)
    {
        var acc = new OpenAiStreamAccumulator();
        await ProviderHttp.ReadSseAsync(resp, chunk =>
        {
            var delta = acc.Add(chunk);
            if (delta.Text != null) stream.OnText(delta.Text);
            if (delta.Reasoning != null) stream.OnReasoning?.Invoke(delta.Reasoning);
        }, ct);
        sw.Stop();
        var result = acc.Build(sw.ElapsedMilliseconds);
        if (result.InputTokens > 0) return result;
        return result with
        {
            InputTokens = ProviderHttp.EstimateTokens(requestChars),
            OutputTokens = ProviderHttp.EstimateTokens(acc.ContentChars)
        };
    }

    private static LlmResponse Parse(string text, Stopwatch sw)
    {
        sw.Stop();
        var root = JsonNode.Parse(text) ?? throw new LlmException("Resposta vazia do provedor.");
        var choice = root["choices"]?[0];
        var message = choice?["message"];
        var content = ProviderHttp.Str(message?["content"]);

        var toolCalls = new List<ToolCall>();
        if (message?["tool_calls"] is JsonArray arr)
        {
            foreach (var tc in arr)
            {
                var fn = tc?["function"];
                var name = ProviderHttp.Str(fn?["name"]) ?? "";
                var argsNode = fn?["arguments"];
                var args = ProviderHttp.Str(argsNode) ?? argsNode?.ToJsonString() ?? "{}";
                var id = ProviderHttp.Str(tc?["id"]) ?? ("call_" + Guid.NewGuid().ToString("N").Substring(0, 12));
                toolCalls.Add(new ToolCall(id, name, string.IsNullOrWhiteSpace(args) ? "{}" : args));
            }
        }

        var usage = root["usage"];
        return new LlmResponse(
            content,
            toolCalls,
            ProviderHttp.Int(usage?["prompt_tokens"]),
            ProviderHttp.Int(usage?["completion_tokens"]),
            ProviderHttp.Str(choice?["finish_reason"]) ?? "",
            sw.ElapsedMilliseconds);
    }
}
