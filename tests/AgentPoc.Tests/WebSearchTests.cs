using AgentPoc.Tools;
using Xunit;

namespace AgentPoc.Tests;

public class WebSearchTests
{
    private const string DuckDuckGoLiteHtml = """
        <table>
          <tr><td>1.&nbsp;</td><td>
            <a rel="nofollow" href="https://temporal.io/blog/what-is-durable-execution" class='result-link'>The definitive guide to <b>Durable</b> Execution</a>
          </td></tr>
          <tr><td>&nbsp;</td>
            <td class='result-snippet'>
              Learn what <b>Durable</b> <b>Execution</b> is &amp; why it matters.
            </td></tr>
          <tr><td>2.&nbsp;</td><td>
            <a rel="nofollow" href="https://assets.temporal.io/durable-execution.pdf" class='result-link'><span class="result__type">PDF</span> Building Reliable Applications</a>
          </td></tr>
          <tr><td>&nbsp;</td><td class='result-snippet'>Second snippet.</td></tr>
        </table>
        """;

    [Fact]
    public void DuckDuckGo_lite_results_pair_titles_urls_and_snippets_in_order()
    {
        var results = WebSearch.ParseDuckDuckGoLite(DuckDuckGoLiteHtml, limit: 5);

        Assert.Collection(results,
            r =>
            {
                Assert.Equal("https://temporal.io/blog/what-is-durable-execution", r.Url);
                Assert.Equal("The definitive guide to Durable Execution", r.Title);
                Assert.Equal("Learn what Durable Execution is & why it matters.", r.Snippet);
            },
            r => Assert.Equal(("PDF Building Reliable Applications", "Second snippet."), (r.Title, r.Snippet)));
    }

    [Fact]
    public void DuckDuckGo_lite_results_respect_the_limit()
    {
        Assert.Single(WebSearch.ParseDuckDuckGoLite(DuckDuckGoLiteHtml, limit: 1));
    }

    [Fact]
    public void Format_lists_numbered_results_with_url_and_snippet()
    {
        var text = WebSearch.Format("dbos", new[] { new SearchResult("DBOS", "https://dbos.dev", "Durable workflows") });

        Assert.Contains("1. DBOS\n   https://dbos.dev\n   Durable workflows", text);
    }
}
