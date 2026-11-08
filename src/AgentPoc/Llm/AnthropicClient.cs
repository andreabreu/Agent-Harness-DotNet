using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using AgentPoc.Core;

namespace AgentPoc.Llm;

/// <summary>Cliente para a Messages API da Anthropic (tool use nativo).</summary>
public sealed class AnthropicClient : ILlmClient
{
    private readonly HttpClient _http;
    public AnthropicClient(HttpClient http) => _http = http;

    public async Task<LlmResponse> CompleteAsync(ModelCfg model, LlmRequest req, CancellationToken ct)
    {
        var url = Env.Expand(model.Provider.BaseUrl).TrimEnd('/') + "/v1/messages";

        var messages = new JsonArray();
        JsonArray? pendingResults = null;
        foreach (var m in req.Messages)
        {
            if (m.Role == "tool")
            {
                pendingResults ??= new JsonArray();
                pendingResults.Add(new JsonObject
                {
                    ["type"] = "tool_result",
                    ["tool_use_id"] = m.ToolCallId,
                    ["content"] = m.Content ?? ""
                });
                continue;
            }

            if (pendingResults != null)
            {
                messages.Add(new JsonObject { ["role"] = "user", ["content"] = pendingResults });
                pendingResults = null;
            }

            if (m.Role == "assistant")
            {
                var blocks = new JsonArray();
                if (!string.IsNullOrEmpty(m.Content))
                    blocks.Add(new JsonObject { ["type"] = "text", ["text"] = m.Content });
                if (m.ToolCalls != null)
                    foreach (var tc in m.ToolCalls)
                        blocks.Add(new JsonObject
                        {
                            ["type"] = "tool_use",
                            ["id"] = tc.Id,
                            ["name"] = tc.Name,
                            ["input"] = ProviderHttp.ParseArgs(tc.ArgumentsJson)
                        });
                if (blocks.Count == 0) blocks.Add(new JsonObject { ["type"] = "text", ["text"] = "(sem conteudo)" });
                messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = blocks });
            }
            else
            {
                messages.Add(new JsonObject { ["role"] = "user", ["content"] = m.Content ?? "" });
            }
        }
        if (pendingResults != null) messages.Add(new JsonObject { ["role"] = "user", ["content"] = pendingResults });

        var body = new JsonObject
        {
            ["model"] = model.ResolvedName,
            ["max_tokens"] = model.MaxOutputTokens,
            ["messages"] = messages
        };
        if (!string.IsNullOrEmpty(req.System)) body["system"] = req.System;
        if (model.SupportsTemperature && req.Temperature.HasValue) body["temperature"] = req.Temperature.Value;
        if (model.SupportsTools && req.Tools.Count > 0)
        {
            var tools = new JsonArray();
            foreach (var t in req.Tools)
                tools.Add(new JsonObject
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["input_schema"] = t.Parameters.DeepClone()
                });
            body["tools"] = tools;
        }

        var streaming = req.Stream != null && model.SupportsStreaming;
        if (streaming) body["stream"] = true;

        var json = body.ToJsonString();
        var sw = Stopwatch.StartNew();
        using var resp = await ProviderHttp.SendWithRetryAsync(_http, () =>
        {
            var msg = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            msg.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
            ProviderHttp.ApplyAuth(msg, model.Provider);
            return msg;
        }, ct, streaming);
        if (!resp.IsSuccessStatusCode)
            throw new LlmException($"{model.Provider.Id} respondeu HTTP {(int)resp.StatusCode}: {Text.Trunc(await resp.Content.ReadAsStringAsync(ct), 800)}");

        return streaming
            ? await ReadStreamAsync(resp, req.Stream!, sw, ct)
            : Parse(await resp.Content.ReadAsStringAsync(ct), sw);
    }

    private static async Task<LlmResponse> ReadStreamAsync(HttpResponseMessage resp, LlmStream stream, Stopwatch sw, CancellationToken ct)
    {
        var acc = new AnthropicStreamAccumulator();
        await ProviderHttp.ReadSseAsync(resp, evt =>
        {
            var delta = acc.Add(evt);
            if (delta.Text != null) stream.OnText(delta.Text);
            if (delta.Reasoning != null) stream.OnReasoning?.Invoke(delta.Reasoning);
        }, ct);
        sw.Stop();
        return acc.Build(sw.ElapsedMilliseconds);
    }

    private static LlmResponse Parse(string text, Stopwatch sw)
    {
        sw.Stop();
        var root = JsonNode.Parse(text) ?? throw new LlmException("Resposta vazia do provedor.");
        var sb = new StringBuilder();
        var calls = new List<ToolCall>();
        if (root["content"] is JsonArray blocksOut)
        {
            foreach (var b in blocksOut)
            {
                var type = ProviderHttp.Str(b?["type"]);
                if (type == "text") sb.Append(ProviderHttp.Str(b?["text"]));
                else if (type == "tool_use")
                    calls.Add(new ToolCall(
                        ProviderHttp.Str(b?["id"]) ?? Guid.NewGuid().ToString("N"),
                        ProviderHttp.Str(b?["name"]) ?? "",
                        b?["input"]?.ToJsonString() ?? "{}"));
            }
        }

        var usage = root["usage"];
        return new LlmResponse(
            sb.Length > 0 ? sb.ToString() : null,
            calls,
            ProviderHttp.Int(usage?["input_tokens"]),
            ProviderHttp.Int(usage?["output_tokens"]),
            ProviderHttp.Str(root["stop_reason"]) ?? "",
            sw.ElapsedMilliseconds);
    }
}
