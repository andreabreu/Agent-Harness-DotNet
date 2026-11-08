using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using AgentPoc.Core;

namespace AgentPoc.Llm;

public sealed record ToolCall(string Id, string Name, string ArgumentsJson);

/// <summary>Mensagem neutra de provedor. Cada cliente converte para o formato da sua API.</summary>
public sealed class ChatMessage
{
    public string Role { get; init; } = "user";           // user | assistant | tool
    public string? Content { get; set; }
    public List<ToolCall>? ToolCalls { get; init; }
    public string? ToolCallId { get; init; }
    public string? ToolName { get; init; }

    public static ChatMessage User(string content) => new() { Role = "user", Content = content };
    public static ChatMessage Assistant(string? content, List<ToolCall>? calls) => new() { Role = "assistant", Content = content, ToolCalls = calls };
    public static ChatMessage Tool(string callId, string name, string content) => new() { Role = "tool", ToolCallId = callId, ToolName = name, Content = content };
}

public sealed record ToolSpec(string Name, string Description, JsonNode Parameters);

/// <summary>Callbacks chamados a cada pedaco de texto quando a resposta vem em streaming.</summary>
public sealed record LlmStream(Action<string> OnText, Action<string>? OnReasoning = null);

public sealed record LlmRequest(string System, IReadOnlyList<ChatMessage> Messages, IReadOnlyList<ToolSpec> Tools, float? Temperature,
                                LlmStream? Stream = null);

public sealed record LlmResponse(string? Content, List<ToolCall> ToolCalls, int InputTokens, int OutputTokens, string StopReason, long LatencyMs);

public interface ILlmClient
{
    Task<LlmResponse> CompleteAsync(ModelCfg model, LlmRequest request, CancellationToken ct);
}

public sealed class LlmException : Exception
{
    public LlmException(string message) : base(message) { }
}

public static class LlmFactory
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(240) };
    private static readonly OpenAiCompatClient OpenAi = new(Http);
    private static readonly AnthropicClient Anthropic = new(Http);

    public static ILlmClient For(ModelCfg model) => model.Provider.Kind switch
    {
        "anthropic" => Anthropic,
        "openai_compat" => OpenAi,
        _ => throw new LlmException($"Tipo de provedor desconhecido: {model.Provider.Kind}")
    };
}

internal static class ProviderHttp
{
    public static void ApplyAuth(HttpRequestMessage msg, ProviderCfg p)
    {
        var key = string.IsNullOrEmpty(p.ApiKeyEnv) ? "" : Env.Get(p.ApiKeyEnv);
        if (p.AuthHeader != "none" && !string.IsNullOrEmpty(p.ApiKeyEnv) && string.IsNullOrEmpty(key))
            throw new LlmException($"Variavel de ambiente {p.ApiKeyEnv} nao definida (provedor {p.Id}).");

        switch (p.AuthHeader)
        {
            case "bearer":
                if (key.Length > 0) msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                break;
            case "api-key":
                msg.Headers.TryAddWithoutValidation("api-key", key);
                break;
            case "x-api-key":
                msg.Headers.TryAddWithoutValidation("x-api-key", key);
                break;
        }

        if (!string.IsNullOrWhiteSpace(p.ExtraHeadersJson) && JsonNode.Parse(p.ExtraHeadersJson) is JsonObject extra)
            foreach (var kv in extra)
                msg.Headers.TryAddWithoutValidation(kv.Key, Env.Expand(kv.Value?.ToString() ?? ""));
    }

    /// <summary>Envia com retentativa para 429/5xx e falhas de rede (backoff exponencial, respeita Retry-After).</summary>
    public static async Task<HttpResponseMessage> SendWithRetryAsync(HttpClient http, Func<HttpRequestMessage> factory, CancellationToken ct,
                                                                     bool streaming = false, int maxAttempts = 4)
    {
        var completion = streaming ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var req = factory();
                var resp = await http.SendAsync(req, completion, ct);
                var code = (int)resp.StatusCode;
                if ((code == 429 || code >= 500) && attempt < maxAttempts)
                {
                    var delay = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
                    if (delay > TimeSpan.FromSeconds(30)) delay = TimeSpan.FromSeconds(30);
                    Log.Warn($"HTTP {code} do provedor; tentativa {attempt + 1}/{maxAttempts} em {delay.TotalSeconds:0}s");
                    resp.Dispose();
                    await Task.Delay(delay, ct);
                    continue;
                }
                return resp;
            }
            catch (HttpRequestException ex) when (attempt < maxAttempts)
            {
                Log.Warn($"Falha de rede ({ex.Message}); tentativa {attempt + 1}/{maxAttempts}");
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
            }
        }
    }

    /// <summary>Le um corpo Server-Sent Events e entrega o JSON de cada linha 'data:' (ignora '[DONE]' e linhas invalidas).</summary>
    public static async Task ReadSseAsync(HttpResponseMessage resp, Action<JsonNode> onData, CancellationToken ct)
    {
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var payload = line.Substring(5).Trim();
            if (payload.Length == 0 || payload == "[DONE]") continue;
            JsonNode? node;
            try { node = JsonNode.Parse(payload); }
            catch (System.Text.Json.JsonException) { continue; }
            if (node != null) onData(node);
        }
    }

    /// <summary>Estimativa grosseira (4 caracteres por token) para provedores que nao informam uso no streaming.</summary>
    public static int EstimateTokens(int chars) => chars / 4 + 1;

    public static JsonObject ParseArgs(string json)
    {
        try
        {
            return JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json) as JsonObject ?? new JsonObject();
        }
        catch
        {
            return new JsonObject();
        }
    }

    public static string? Str(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public static int Int(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<int>(out var i) ? i : 0;
}
