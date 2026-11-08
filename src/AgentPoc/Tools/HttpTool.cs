using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AgentPoc.Core;

namespace AgentPoc.Tools;

/// <summary>
/// Executor generico para ferramentas kind='http' definidas so no banco:
/// config = {"method":"GET","url":"https://api/x/{param}","headers":{"k":"${ENV}"}}.
/// </summary>
public static class HttpTool
{
    public static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AgentPoc", "1.0"));
        return c;
    }

    private static readonly Regex Placeholder = new(@"\{([A-Za-z_][A-Za-z0-9_]*)\}", RegexOptions.Compiled);

    public static string ArgString(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : node?.ToJsonString() ?? "";

    public static async Task<string> InvokeAsync(ToolDef def, JsonObject args, CancellationToken ct)
    {
        var cfg = JsonNode.Parse(def.ConfigJson) as JsonObject ?? new JsonObject();
        var method = (ArgString(cfg["method"]) is { Length: > 0 } m ? m : "GET").ToUpperInvariant();
        var template = ArgString(cfg["url"]);
        var missing = new List<string>();
        var url = Placeholder.Replace(template, match =>
        {
            var key = match.Groups[1].Value;
            if (!args.ContainsKey(key)) { missing.Add(key); return ""; }
            return Uri.EscapeDataString(ArgString(args[key]));
        });
        if (missing.Count > 0) return $"ERRO: parametros obrigatorios ausentes: {string.Join(", ", missing)}";

        using var req = new HttpRequestMessage(new HttpMethod(method), url);
        if (cfg["headers"] is JsonObject headers)
            foreach (var kv in headers)
                req.Headers.TryAddWithoutValidation(kv.Key, Env.Expand(ArgString(kv.Value)));
        if (method != "GET" && method != "DELETE")
            req.Content = new StringContent(args.ToJsonString(), Encoding.UTF8, "application/json");

        using var resp = await Http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        var prefix = resp.IsSuccessStatusCode ? "" : "ERRO: ";
        return $"{prefix}HTTP {(int)resp.StatusCode} {method} {url}\n{Text.Trunc(body, 6000)}";
    }

    /// <summary>Converte HTML em texto legivel (remove script/style/tags, compacta espacos).</summary>
    public static string HtmlToText(string html)
    {
        var s = Regex.Replace(html, @"<(script|style|noscript|svg)[^>]*>[\s\S]*?</\1>", " ", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<br\s*/?>|</p>|</div>|</li>|</h[1-6]>", "\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, "<[^>]+>", " ");
        s = System.Net.WebUtility.HtmlDecode(s);
        s = Regex.Replace(s, @"[ \t\f\v]+", " ");
        s = Regex.Replace(s, @"\n\s*\n+", "\n");
        return s.Trim();
    }
}
