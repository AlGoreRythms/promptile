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
    private static readonly string CredentialsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".promptile", "google-credentials.json");

    private static readonly ConcurrentDictionary<string, string> _pendingStates = new();

    public static (string ClientId, string ClientSecret)? TryLoadCredentials()
    {
        if (!File.Exists(CredentialsPath)) return null;
        try
        {
            var json = File.ReadAllText(CredentialsPath);
            var doc = JsonDocument.Parse(json).RootElement;
            var clientId = doc.GetProperty("clientId").GetString() ?? "";
            var clientSecret = doc.GetProperty("clientSecret").GetString() ?? "";
            if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret)) return null;
            return (clientId, clientSecret);
        }
        catch { return null; }
    }

    public static IResult BeginAuthorize(string id, DataSourceManager manager)
    {
        var creds = TryLoadCredentials();
        if (creds == null)
            return Results.Content(MissingCredentialsHtml(), "text/html; charset=utf-8");

        var instances = manager.GetInstances("gmail");
        var instance = instances.FirstOrDefault(i => i.Id == id);
        if (instance == null)
            return Results.NotFound("Data source not found");

        var state = Guid.NewGuid().ToString("N");
        _pendingStates[state] = id;

        var flow = BuildFlow(creds.Value.ClientId, creds.Value.ClientSecret);
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

        var creds = TryLoadCredentials();
        if (creds == null)
            return Results.Content(MissingCredentialsHtml(), "text/html; charset=utf-8");

        var all = await sources.LoadAsync();
        var cfg = all.FirstOrDefault(c => c.Id == datasourceId);
        if (cfg == null)
            return Results.NotFound("Data source no longer exists");

        try
        {
            var flow = BuildFlow(creds.Value.ClientId, creds.Value.ClientSecret);
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
        "<h2>⚠️ Google credentials not found</h2>" +
        "<p>Create <code>~/.promptile/google-credentials.json</code> with your OAuth app credentials:</p>" +
        "<pre>{\n  \"clientId\": \"your-client-id.apps.googleusercontent.com\",\n  \"clientSecret\": \"your-client-secret\"\n}</pre>" +
        "<p>Get credentials at <a href=\"https://console.cloud.google.com\">Google Cloud Console</a> → " +
        "APIs &amp; Services → Credentials → Create OAuth 2.0 Client ID (Desktop app).</p>" +
        "<p><a href=\"/DataSources\">Back to Data Sources</a></p>" +
        "</body></html>";
}
