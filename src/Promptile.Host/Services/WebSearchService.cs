using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Promptile.Sdk;

namespace Promptile.Host.Services;

/// <summary>
/// Web search using DuckDuckGo HTML search (no API key needed) and HTTP fetch with HTML-to-text.
/// </summary>
public partial class WebSearchService : IWebSearch
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(15),
        DefaultRequestHeaders =
        {
            { "User-Agent", "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36" }
        }
    };

    public async Task<List<WebSearchResult>> SearchAsync(string query, int maxResults = 5, CancellationToken ct = default)
    {
        var results = new List<WebSearchResult>();

        try
        {
            // Use DuckDuckGo HTML search (POST required — GET returns homepage)
            var content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("q", query) });
            var response = await Http.PostAsync("https://html.duckduckgo.com/html/", content, ct);
            var html = await response.Content.ReadAsStringAsync(ct);

            // Parse results from HTML
            var resultMatches = ResultPattern().Matches(html);
            foreach (Match match in resultMatches)
            {
                if (results.Count >= maxResults) break;

                var resultUrl = WebUtility.HtmlDecode(match.Groups[1].Value);
                var title = StripHtml(WebUtility.HtmlDecode(match.Groups[2].Value));
                var snippet = "";

                // Try to find snippet
                var snippetMatch = SnippetPattern().Match(match.Value);
                if (snippetMatch.Success)
                    snippet = StripHtml(WebUtility.HtmlDecode(snippetMatch.Groups[1].Value));

                if (!string.IsNullOrEmpty(title) && resultUrl.StartsWith("http"))
                    results.Add(new WebSearchResult(title.Trim(), resultUrl, snippet.Trim()));
            }

            // Fallback: simpler parsing if regex didn't match
            if (results.Count == 0)
            {
                var linkMatches = SimpleLinkPattern().Matches(html);
                foreach (Match m in linkMatches)
                {
                    if (results.Count >= maxResults) break;
                    var linkUrl = WebUtility.HtmlDecode(m.Groups[1].Value);
                    var linkText = StripHtml(WebUtility.HtmlDecode(m.Groups[2].Value));
                    if (linkUrl.StartsWith("http") && !linkUrl.Contains("duckduckgo") && linkText.Length > 5)
                        results.Add(new WebSearchResult(linkText.Trim(), linkUrl, ""));
                }
            }
        }
        catch (Exception ex)
        {
            results.Add(new WebSearchResult("Search error", "", ex.Message));
        }

        return results;
    }

    public async Task<string> FetchAsync(string url, CancellationToken ct = default)
    {
        try
        {
            var html = await Http.GetStringAsync(url, ct);
            return HtmlToText(html);
        }
        catch (Exception ex)
        {
            return $"Error fetching {url}: {ex.Message}";
        }
    }

    private static string HtmlToText(string html)
    {
        // Remove script and style blocks
        html = Regex.Replace(html, @"<script[^>]*>[\s\S]*?</script>", "", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<style[^>]*>[\s\S]*?</style>", "", RegexOptions.IgnoreCase);
        // Remove HTML tags
        html = Regex.Replace(html, @"<[^>]+>", " ");
        // Decode entities
        html = WebUtility.HtmlDecode(html);
        // Collapse whitespace
        html = Regex.Replace(html, @"\s+", " ").Trim();
        // Limit length
        if (html.Length > 8000) html = html[..8000] + "...";
        return html;
    }

    private static string StripHtml(string html)
    {
        return Regex.Replace(html, @"<[^>]+>", "").Trim();
    }

    [GeneratedRegex(@"<a[^>]*class=""[^""]*result__a[^""]*""[^>]*href=""(https?://[^""]+)""[^>]*>(.*?)</a>", RegexOptions.Singleline)]
    private static partial Regex ResultPattern();

    [GeneratedRegex(@"class=""[^""]*result__snippet[^""]*""[^>]*>(.*?)</", RegexOptions.Singleline)]
    private static partial Regex SnippetPattern();

    [GeneratedRegex(@"<a[^>]+href=""(https?://[^""]+)""[^>]*>(.*?)</a>", RegexOptions.Singleline)]
    private static partial Regex SimpleLinkPattern();
}
