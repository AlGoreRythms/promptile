namespace Promptile.Sdk;

public record WebSearchResult(string Title, string Url, string Snippet);

/// <summary>
/// Provides web search and page fetching for plugins that need to research online.
/// </summary>
public interface IWebSearch
{
    /// <summary>
    /// Search the web for a query. Returns title, URL, and snippet for each result.
    /// </summary>
    Task<List<WebSearchResult>> SearchAsync(string query, int maxResults = 5, CancellationToken ct = default);

    /// <summary>
    /// Fetch the text content of a URL (HTML stripped to readable text).
    /// </summary>
    Task<string> FetchAsync(string url, CancellationToken ct = default);
}
