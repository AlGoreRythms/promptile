using System.Collections.Concurrent;
using System.Text.Json;
using Assistant.Sdk;

namespace Assistant.Host.Services;

public static class AsanaOAuthHelper
{
    private const string RedirectUri = "http://localhost:5309/api/asana-callback";
    private const string AuthBaseUrl = "https://app.asana.com/-/oauth_authorize";
    private const string TokenUrl = "https://app.asana.com/-/oauth_token";

    private static readonly ConcurrentDictionary<string, string> _pendingStates = new();
    private static readonly HttpClient _http = new();

    public static async Task<IResult> BeginAuthorizeAsync(string id, DataSourcesService sources)
    {
        var all = await sources.LoadAsync();
        var cfg = all.FirstOrDefault(c => c.Id == id && c.Type == "asana");
        if (cfg == null)
            return Results.NotFound("Data source not found");

        var clientId = cfg.Config.GetValueOrDefault("clientId", "");
        if (string.IsNullOrEmpty(clientId))
            return Results.Content(MissingCredentialsHtml(), "text/html; charset=utf-8");

        var state = Guid.NewGuid().ToString("N");
        _pendingStates[state] = id;

        var authUrl = $"{AuthBaseUrl}?response_type=code" +
                      $"&client_id={Uri.EscapeDataString(clientId)}" +
                      $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
                      $"&scope={Uri.EscapeDataString("openid tasks:read workspaces:read")}" +
                      $"&state={state}";
        return Results.Redirect(authUrl);
    }

    public static async Task<IResult> HandleCallbackAsync(HttpContext ctx, DataSourcesService sources, DataSourceManager manager)
    {
        var code = ctx.Request.Query["code"].ToString();
        var state = ctx.Request.Query["state"].ToString();
        var error = ctx.Request.Query["error"].ToString();

        if (!string.IsNullOrEmpty(error))
            return Results.Content(
                $"<html><body><h2>Authorization failed: {error}</h2><p><a href='/DataSources'>Back to Data Sources</a></p></body></html>",
                "text/html; charset=utf-8");

        if (!_pendingStates.TryRemove(state, out var datasourceId))
            return Results.BadRequest("Invalid or expired state parameter");

        var all = await sources.LoadAsync();
        var cfg = all.FirstOrDefault(c => c.Id == datasourceId);
        if (cfg == null)
            return Results.NotFound("Data source no longer exists");

        var clientId = cfg.Config.GetValueOrDefault("clientId", "");
        var clientSecret = cfg.Config.GetValueOrDefault("clientSecret", "");

        try
        {
            var token = await ExchangeCodeAsync(clientId, clientSecret, code);
            var refreshToken = token.GetProperty("refresh_token").GetString() ?? "";

            var newConfig = new Dictionary<string, string>(cfg.Config) { ["refreshToken"] = refreshToken };
            await sources.UpsertAsync(cfg with { Config = newConfig });
            await manager.ReloadAsync();

            return Results.Content("""
                <html><body>
                <h2>✅ Asana connected!</h2>
                <p>Redirecting back...</p>
                <script>setTimeout(function(){ window.location='/DataSources'; }, 1200);</script>
                </body></html>
                """, "text/html; charset=utf-8");
        }
        catch (Exception ex)
        {
            return Results.Content(
                $"<html><body><h2>Authorization error</h2><p>{ex.Message}</p><p><a href='/DataSources'>Back to Data Sources</a></p></body></html>",
                "text/html; charset=utf-8");
        }
    }

    public static async Task<(string AccessToken, DateTimeOffset ExpiresAt)> RefreshAccessTokenAsync(
        string clientId, string clientSecret, string refreshToken)
    {
        var resp = await ExchangeRefreshAsync(clientId, clientSecret, refreshToken);
        var accessToken = resp.GetProperty("access_token").GetString()
            ?? throw new Exception("No access_token in response");
        var expiresIn = resp.TryGetProperty("expires_in", out var exp) ? exp.GetInt32() : 3600;
        return (accessToken, DateTimeOffset.UtcNow.AddSeconds(expiresIn - 60));
    }

    private static async Task<JsonElement> ExchangeCodeAsync(string clientId, string clientSecret, string code)
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["redirect_uri"] = RedirectUri,
            ["code"] = code,
        });
        var resp = await _http.PostAsync(TokenUrl, form);
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Token exchange failed ({(int)resp.StatusCode}): {body}");
        return JsonDocument.Parse(body).RootElement;
    }

    private static async Task<JsonElement> ExchangeRefreshAsync(string clientId, string clientSecret, string refreshToken)
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["refresh_token"] = refreshToken,
        });
        var resp = await _http.PostAsync(TokenUrl, form);
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Token refresh failed ({(int)resp.StatusCode}): {body}");
        return JsonDocument.Parse(body).RootElement;
    }

    private static string MissingCredentialsHtml() =>
        "<html><head><meta charset='utf-8'></head><body>" +
        "<h2>⚠️ Asana Client ID not configured</h2>" +
        "<p>Enter your Asana app's Client ID when creating the data source.</p>" +
        "<p><a href=\"/DataSources\">Back to Data Sources</a></p>" +
        "</body></html>";
}
