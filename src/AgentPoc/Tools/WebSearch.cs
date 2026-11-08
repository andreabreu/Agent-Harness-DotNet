using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AgentPoc.Core;

namespace AgentPoc.Tools;

public sealed record SearchResult(string Title, string Url, string Snippet);

/// <summary>
/// Busca na web com o primeiro provedor configurado: SEARXNG_URL (self-hosted, ideal on-prem),
/// BRAVE_API_KEY (Brave Search API) ou, sem configuracao, DuckDuckGo Lite (sem chave; bom para desenvolvimento).
/// </summary>
public static class WebSearch
{
    private const string BraveEndpoint = "https://api.search.brave.com/res/v1/web/search";
    private const string DuckDuckGoLiteEndpoint = "https://lite.duckduckgo.com/lite/";
    private const string BrowserUserAgent = "Mozilla/5.0 (Macintosh; Intel Mac OS X 14_0) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Safari/605.1.15";

    private static readonly Regex DuckDuckGoLink = new(@"<a[^>]*href=""(?<url>[^""]+)""[^>]*class='result-link'[^>]*>(?<title>[\s\S]*?)</a>", RegexOptions.Compiled);
    private static readonly Regex DuckDuckGoSnippet = new(@"<td class='result-snippet'>(?<snippet>[\s\S]*?)</td>", RegexOptions.Compiled);

    public static string ProviderName =>
        Env.Get("SEARXNG_URL").Length > 0 ? "searxng" : Env.Get("BRAVE_API_KEY").Length > 0 ? "brave" : "duckduckgo";

    public static Task<List<SearchResult>> SearchAsync(string query, int limit, CancellationToken ct) => ProviderName switch
    {
        "searxng" => SearxngAsync(query, limit, ct),
        "brave" => BraveAsync(query, limit, ct),
        _ => DuckDuckGoAsync(query, limit, ct)
    };

    public static string Format(string query, IReadOnlyList<SearchResult> results)
    {
        if (results.Count == 0) return $"Nenhum resultado para '{query}' (provedor {ProviderName}).";
        var sb = new StringBuilder($"Resultados para '{query}' (provedor {ProviderName}):\n");
        for (var i = 0; i < results.Count; i++)
            sb.AppendLine($"{i + 1}. {results[i].Title}\n   {results[i].Url}\n   {results[i].Snippet}");
        return sb.ToString();
    }

    private static async Task<List<SearchResult>> SearxngAsync(string query, int limit, CancellationToken ct)
    {
        var url = $"{Env.Get("SEARXNG_URL").TrimEnd('/')}/search?format=json&q={Uri.EscapeDataString(query)}";
        var root = JsonNode.Parse(await GetStringAsync(new HttpRequestMessage(HttpMethod.Get, url), ct));
        return (root?["results"] as JsonArray ?? new JsonArray())
            .Select(r => new SearchResult(Str(r?["title"]), Str(r?["url"]), Str(r?["content"])))
            .Where(r => r.Url.Length > 0)
            .Take(limit)
            .ToList();
    }

    private static async Task<List<SearchResult>> BraveAsync(string query, int limit, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, $"{BraveEndpoint}?count={limit}&q={Uri.EscapeDataString(query)}");
        req.Headers.TryAddWithoutValidation("X-Subscription-Token", Env.Get("BRAVE_API_KEY"));
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        var root = JsonNode.Parse(await GetStringAsync(req, ct));
        return (root?["web"]?["results"] as JsonArray ?? new JsonArray())
            .Select(r => new SearchResult(Str(r?["title"]), Str(r?["url"]), HttpTool.HtmlToText(Str(r?["description"]))))
            .Take(limit)
            .ToList();
    }

    private static async Task<List<SearchResult>> DuckDuckGoAsync(string query, int limit, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, DuckDuckGoLiteEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["q"] = query })
        };
        req.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
        return ParseDuckDuckGoLite(await GetStringAsync(req, ct), limit);
    }

    /// <summary>Extrai os resultados do HTML do DuckDuckGo Lite (links e trechos aparecem na mesma ordem).</summary>
    public static List<SearchResult> ParseDuckDuckGoLite(string html, int limit)
    {
        var links = DuckDuckGoLink.Matches(html);
        var snippets = DuckDuckGoSnippet.Matches(html);
        return links
            .Select((m, i) => new SearchResult(
                HttpTool.HtmlToText(m.Groups["title"].Value),
                WebUtility.HtmlDecode(m.Groups["url"].Value),
                i < snippets.Count ? HttpTool.HtmlToText(snippets[i].Groups["snippet"].Value) : ""))
            .Where(r => r.Url.StartsWith("http", StringComparison.Ordinal))
            .Take(limit)
            .ToList();
    }

    private static async Task<string> GetStringAsync(HttpRequestMessage req, CancellationToken ct)
    {
        using (req)
        using (var resp = await HttpTool.Http.SendAsync(req, ct))
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                throw new HttpRequestException($"busca ({ProviderName}) respondeu HTTP {(int)resp.StatusCode}: {Text.Trunc(body, 300)}");
            return body;
        }
    }

    private static string Str(JsonNode? node) => HttpTool.ArgString(node).Trim();
}
