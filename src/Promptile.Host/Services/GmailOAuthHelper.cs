using System.Collections.Concurrent;
using System.Text.Json;
using Promptile.Sdk;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Gmail.v1;

namespace Promptile.Host.Services;

public static class GmailOAuthHelper
{
    private const string RedirectUri = "http://localhost:5309/api/gmail-callback";
    private static readonly ConcurrentDictionary<string, string> _pendingStates = new();

    public static IResult BeginAuthorize(string id, DataSourceManager manager)
    {
        var instances = manager.GetInstances("gmail");
        var instance = instances.FirstOrDefault(i => i.Id == id);
        if (instance == null)
            return Results.NotFound("Data source not found");

        if (!instance.Config.Config.TryGetValue("clientId", out var clientId) || string.IsNullOrEmpty(clientId) ||
            !instance.Config.Config.TryGetValue("clientSecret", out var clientSecret) || string.IsNullOrEmpty(clientSecret))
            return Results.Content(MissingCredentialsHtml(), "text/html; charset=utf-8");

        var state = Guid.NewGuid().ToString("N");
        _pendingStates[state] = id;

        var flow = BuildFlow(clientId, clientSecret);
        var authUrl = flow.CreateAuthorizationCodeRequest(RedirectUri).Build();

        var uriBuilder = new UriBuilder(authUrl);
        var query = System.Web.HttpUtility.ParseQueryString(uriBuilder.Query);
        query["state"] = state;
        query["prompt"] = "consent";
        query["access_type"] = "offline";
        uriBuilder.Query = query.ToString();

        return Results.Redirect(uriBuilder.ToString());
    }

    public static async Task<IResult> HandleCallbackAsync(HttpContext ctx, DataSourcesService sources, DataSourceManager manager)
    {
        var code = ctx.Request.Query["code"].ToString();
        var state = ctx.Request.Query["state"].ToString();
        var error = ctx.Request.Query["error"].ToString();

        if (!string.IsNullOrEmpty(error))
            return Results.Content($"<html><body><h2>Authorization failed: {error}</h2><p><a href='/DataSources'>Back to Data Sources</a></p></body></html>", "text/html; charset=utf-8");

        if (!_pendingStates.TryRemove(state, out var datasourceId))
            return Results.BadRequest("Invalid or expired state parameter");

        var all = await sources.LoadAsync();
        var cfg = all.FirstOrDefault(c => c.Id == datasourceId);
        if (cfg == null)
            return Results.NotFound("Data source no longer exists");

        if (!cfg.Config.TryGetValue("clientId", out var clientId) || string.IsNullOrEmpty(clientId) ||
            !cfg.Config.TryGetValue("clientSecret", out var clientSecret) || string.IsNullOrEmpty(clientSecret))
            return Results.Content(MissingCredentialsHtml(), "text/html; charset=utf-8");

        try
        {
            var flow = BuildFlow(clientId, clientSecret);
            var tokenResponse = await flow.ExchangeCodeForTokenAsync("user", code, RedirectUri, CancellationToken.None);

            var newConfig = new Dictionary<string, string>(cfg.Config)
            {
                ["refreshToken"] = tokenResponse.RefreshToken ?? ""
            };
            await sources.UpsertAsync(cfg with { Config = newConfig });
            await manager.ReloadAsync();

            return Results.Content("""
                <html><body>
                <h2>✅ Gmail connected!</h2>
                <p>Redirecting back...</p>
                <script>setTimeout(function(){ window.location='/DataSources'; }, 1200);</script>
                </body></html>
                """, "text/html; charset=utf-8");
        }
        catch (Exception ex)
        {
            return Results.Content($"<html><body><h2>Authorization error</h2><p>{ex.Message}</p><p><a href='/DataSources'>Back to Data Sources</a></p></body></html>", "text/html; charset=utf-8");
        }
    }

    private static GoogleAuthorizationCodeFlow BuildFlow(string clientId, string clientSecret) =>
        new(new GoogleAuthorizationCodeFlow.Initializer
        {
            ClientSecrets = new ClientSecrets { ClientId = clientId, ClientSecret = clientSecret },
            Scopes = [GmailService.Scope.GmailReadonly],
        });

    private static string MissingCredentialsHtml() =>
        "<html><head><meta charset='utf-8'></head><body>" +
        "<h2>⚠️ Google credentials not configured</h2>" +
        "<p>Edit this data source and enter your Client ID and Client Secret.</p>" +
        "<p>Get credentials at <a href=\"https://console.cloud.google.com\">Google Cloud Console</a> → " +
        "APIs &amp; Services → Credentials → Create OAuth 2.0 Client ID (Desktop app).</p>" +
        "<p><a href=\"/DataSources\">Back to Data Sources</a></p>" +
        "</body></html>";
}
