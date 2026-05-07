using System.Text;
using System.Text.Json;
using Assistant.Sdk;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Gmail.v1;
using Google.Apis.Gmail.v1.Data;
using Google.Apis.Services;
using GmailMessage = Google.Apis.Gmail.v1.Data.Message;

namespace Gmail;

public class GmailDataSourceInstance : IDataSourceInstance
{
    public string Id => Config.Id;
    public string Name => Config.Name;
    public string Type => "gmail";
    public DataSourceConfig Config { get; private set; }

    private GmailService? _gmail;
    private IInformationStore? _store;
    private ISyncReporter? _sync;
    private string? _connectedEmail;
    private CancellationTokenSource? _cts;
    private Task? _pollTask;
    private string _lastHistoryId = "";
    private readonly string _stateDir;

    public GmailDataSourceInstance(DataSourceConfig config)
    {
        Config = config;
        _stateDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".assistant", "datasources", config.Id);
        Directory.CreateDirectory(_stateDir);
    }

    public void UpdateConfig(DataSourceConfig config) => Config = config;

    public async Task StartAsync(INotificationBus bus, IServiceProvider services, CancellationToken ct)
    {
        if (!Config.Config.TryGetValue("refreshToken", out var refreshToken) || string.IsNullOrEmpty(refreshToken))
            return;

        var creds = LoadAppCredentials();
        if (creds == null) return;

        _store = services.GetService(typeof(IInformationStore)) as IInformationStore;
        _sync = services.GetService(typeof(ISyncReporter)) as ISyncReporter;

        try
        {
            _gmail = BuildService(creds.Value.ClientId, creds.Value.ClientSecret, refreshToken);
            var profile = await _gmail.Users.GetProfile("me").ExecuteAsync(ct);
            _connectedEmail = profile.EmailAddress;
            LoadState();

            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _pollTask = PollLoopAsync(bus, _cts.Token);
        }
        catch (Exception ex)
        {
            _connectedEmail = null;
            _gmail = null;
            Console.Error.WriteLine($"[Gmail:{Name}] Start failed: {ex.Message}");
        }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_cts != null)
        {
            await _cts.CancelAsync();
            if (_pollTask != null)
                try { await _pollTask; } catch { }
        }
        _gmail?.Dispose();
        _gmail = null;
    }

    public Task ResetStateAsync()
    {
        _lastHistoryId = "";
        var path = Path.Combine(_stateDir, "state.json");
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    public Task<DataSourceStatus> GetStatusAsync()
    {
        if (LoadAppCredentials() == null)
            return Task.FromResult(new DataSourceStatus(false, "~/.assistant/google-credentials.json not found"));

        if (!Config.Config.TryGetValue("refreshToken", out var refreshToken) || string.IsNullOrEmpty(refreshToken))
            return Task.FromResult(new DataSourceStatus(false, "Authorization required",
                AuthUrl: $"/api/gmail-authorize/{Config.Id}"));

        if (_gmail == null)
            return Task.FromResult(new DataSourceStatus(false, "Token expired — re-authorization required",
                AuthUrl: $"/api/gmail-authorize/{Config.Id}"));

        return Task.FromResult(new DataSourceStatus(true, _connectedEmail));
    }

    public async Task<List<GmailMessage>> GetRecentMessagesAsync(string? labelId = null, int maxResults = 20)
    {
        if (_gmail == null) return [];
        labelId ??= Config.Config.TryGetValue("labelFilter", out var lf) && !string.IsNullOrEmpty(lf) ? lf : "INBOX";
        var req = _gmail.Users.Messages.List("me");
        req.LabelIds = labelId;
        req.MaxResults = maxResults;
        var list = await req.ExecuteAsync();
        if (list.Messages == null) return [];
        var results = new List<GmailMessage>();
        foreach (var msg in list.Messages)
            results.Add(await _gmail.Users.Messages.Get("me", msg.Id).ExecuteAsync());
        return results;
    }

    public async Task<List<GmailMessage>> SearchMessagesAsync(string query, int maxResults = 20)
    {
        if (_gmail == null) return [];
        var req = _gmail.Users.Messages.List("me");
        req.Q = query;
        req.MaxResults = maxResults;
        var list = await req.ExecuteAsync();
        if (list.Messages == null) return [];
        var results = new List<GmailMessage>();
        foreach (var msg in list.Messages)
            results.Add(await _gmail.Users.Messages.Get("me", msg.Id).ExecuteAsync());
        return results;
    }

    public async Task<GmailMessage?> GetMessageAsync(string messageId)
    {
        if (_gmail == null) return null;
        return await _gmail.Users.Messages.Get("me", messageId).ExecuteAsync();
    }

    private async Task PollLoopAsync(INotificationBus bus, CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(15), ct);

        var interval = TimeSpan.FromSeconds(
            Config.Config.TryGetValue("pollIntervalSeconds", out var s) && int.TryParse(s, out var sec) ? sec : 60);

        while (!ct.IsCancellationRequested)
        {
            try { await PollAsync(bus, ct); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Console.Error.WriteLine($"[Gmail:{Name}] Poll error: {ex.Message}"); }

            await Task.Delay(interval, ct);
        }
    }

    private async Task PollAsync(INotificationBus bus, CancellationToken ct)
    {
        if (_gmail == null) return;

        _sync?.Begin(Id, $"{Name} · Gmail", "gmail");

        try
        {
            var labelId = Config.Config.TryGetValue("labelFilter", out var lf) && !string.IsNullOrEmpty(lf) ? lf : "INBOX";
            var notifyOn = Config.Config.TryGetValue("notifyOn", out var no) ? no : "all";

            if (!string.IsNullOrEmpty(_lastHistoryId) && long.TryParse(_lastHistoryId, out var startId))
            {
                try
                {
                    _sync?.Progress(Id, "Checking history for new messages", 0);
                    var histReq = _gmail.Users.History.List("me");
                    histReq.StartHistoryId = (ulong)startId;
                    histReq.LabelId = labelId;
                    histReq.HistoryTypes = UsersResource.HistoryResource.ListRequest.HistoryTypesEnum.MessageAdded;
                    var hist = await histReq.ExecuteAsync(ct);

                    var newCount = 0;
                    if (hist.History != null)
                    {
                        foreach (var entry in hist.History)
                        {
                            if (entry.MessagesAdded == null) continue;
                            foreach (var added in entry.MessagesAdded)
                            {
                                var msg = await _gmail.Users.Messages.Get("me", added.Message.Id).ExecuteAsync(ct);
                                if (notifyOn == "unread" && !msg.LabelIds.Contains("UNREAD")) continue;
                                await WriteToStoreAsync(msg);
                                PublishNotification(bus, msg);
                                newCount++;
                                _sync?.Progress(Id, $"Stored {newCount} new email{(newCount == 1 ? "" : "s")}", newCount);
                            }
                        }
                    }

                    if (hist.HistoryId.HasValue)
                    {
                        _lastHistoryId = hist.HistoryId.Value.ToString();
                        SaveState();
                    }

                    if (newCount > 0)
                    {
                        bus.Publish(new AssistantNotification(
                            PluginId: Id,
                            Title: $"Gmail — {Name}",
                            Body: $"{newCount} new email{(newCount == 1 ? "" : "s")}",
                            Timestamp: DateTimeOffset.UtcNow,
                            Category: "sync-summary"
                        ));
                    }

                    _sync?.Complete(Id, newCount);
                    return;
                }
                catch
                {
                    // History ID expired — do a catchup fetch instead of silently skipping
                    _lastHistoryId = "";
                }
            }

            // First poll or history expired: fetch last 7 days and write to store, then anchor historyId
            await CatchUpAsync(labelId, notifyOn, bus, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _sync?.Fail(Id, ex.Message);
            throw;
        }
    }

    private async Task CatchUpAsync(string labelId, string notifyOn, INotificationBus bus, CancellationToken ct)
    {
        if (_gmail == null) return;

        _sync?.Progress(Id, "Catching up — fetching last 7 days", 0);

        // Fetch up to 50 recent messages to backfill the store
        var req = _gmail.Users.Messages.List("me");
        req.LabelIds = labelId;
        req.Q = $"after:{DateTime.UtcNow.AddDays(-7):yyyy/MM/dd}";
        req.MaxResults = 50;
        var list = await req.ExecuteAsync(ct);

        var count = 0;
        if (list.Messages != null)
        {
            foreach (var stub in list.Messages)
            {
                var msg = await _gmail.Users.Messages.Get("me", stub.Id).ExecuteAsync(ct);
                if (notifyOn == "unread" && !msg.LabelIds.Contains("UNREAD")) continue;
                await WriteToStoreAsync(msg);
                count++;
                // Don't notify on catchup — these are historical
            }
        }

        var profile = await _gmail.Users.GetProfile("me").ExecuteAsync(ct);
        _lastHistoryId = profile.HistoryId?.ToString() ?? "";
        SaveState();
        _sync?.Complete(Id, count);
    }

    private async Task WriteToStoreAsync(GmailMessage msg)
    {
        if (_store == null) return;

        var date = ParseMessageDate(msg);
        var filename = $"{date:yyyy-MM-dd}.md";

        var from = msg.GetFrom();
        var subject = msg.GetSubject();
        var body = msg.GetPlainTextBody();
        var time = date.ToString("HH:mm");

        var entry = new StringBuilder();
        entry.AppendLine($"## {time} · {from}");
        entry.AppendLine($"**Subject:** {subject}");
        entry.AppendLine($"**ID:** {msg.Id}");
        entry.AppendLine();
        entry.AppendLine(string.IsNullOrWhiteSpace(body) ? msg.GetSnippet() : body);
        entry.AppendLine();
        entry.AppendLine("---");
        entry.AppendLine();

        var content = entry.ToString();
        await _store.AppendDataAsync(Name, "Gmail", filename, content);
    }

    private static DateTime ParseMessageDate(GmailMessage msg)
    {
        if (msg.InternalDate.HasValue)
            return DateTimeOffset.FromUnixTimeMilliseconds(msg.InternalDate.Value).LocalDateTime;
        var dateHeader = msg.GetDate();
        if (DateTimeOffset.TryParse(dateHeader, out var parsed))
            return parsed.LocalDateTime;
        return DateTime.Now;
    }

    private void PublishNotification(INotificationBus bus, GmailMessage msg)
    {
        bus.Publish(new AssistantNotification(
            PluginId: "gmail",
            Title: $"Gmail — {Name}",
            Body: $"From: {msg.GetFrom()}\n{msg.GetSubject()}",
            Timestamp: DateTimeOffset.UtcNow,
            ActionUrl: $"https://mail.google.com/mail/u/0/#inbox/{msg.Id}"
        ));
    }

    private void LoadState()
    {
        var path = Path.Combine(_stateDir, "state.json");
        if (!File.Exists(path)) return;
        try
        {
            var state = JsonSerializer.Deserialize<GmailState>(File.ReadAllText(path));
            _lastHistoryId = state?.LastHistoryId ?? "";
        }
        catch { }
    }

    private void SaveState()
    {
        var path = Path.Combine(_stateDir, "state.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new GmailState(_lastHistoryId)));
    }

    private static readonly string CredentialsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".assistant", "google-credentials.json");

    private static (string ClientId, string ClientSecret)? LoadAppCredentials()
    {
        if (!File.Exists(CredentialsPath)) return null;
        try
        {
            var doc = JsonDocument.Parse(File.ReadAllText(CredentialsPath)).RootElement;
            var clientId = doc.GetProperty("clientId").GetString() ?? "";
            var clientSecret = doc.GetProperty("clientSecret").GetString() ?? "";
            return string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret)
                ? null : (clientId, clientSecret);
        }
        catch { return null; }
    }

    private static GmailService BuildService(string clientId, string clientSecret, string refreshToken)
    {
        var flow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
        {
            ClientSecrets = new ClientSecrets { ClientId = clientId, ClientSecret = clientSecret },
            Scopes = [GmailService.Scope.GmailReadonly],
        });
        var token = new Google.Apis.Auth.OAuth2.Responses.TokenResponse
        {
            RefreshToken = refreshToken,
            TokenType = "Bearer",
        };
        var credential = new UserCredential(flow, "user", token);
        return new GmailService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "Assistant",
        });
    }

    public ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _gmail?.Dispose();
        return ValueTask.CompletedTask;
    }

    private record GmailState(string LastHistoryId);
}

public static class GmailMessageExtensions
{
    public static string GetSubject(this GmailMessage msg) =>
        msg.Payload?.Headers?.FirstOrDefault(h =>
            string.Equals(h.Name, "Subject", StringComparison.OrdinalIgnoreCase))?.Value ?? "(no subject)";

    public static string GetFrom(this GmailMessage msg) =>
        msg.Payload?.Headers?.FirstOrDefault(h =>
            string.Equals(h.Name, "From", StringComparison.OrdinalIgnoreCase))?.Value ?? "Unknown";

    public static string GetDate(this GmailMessage msg) =>
        msg.Payload?.Headers?.FirstOrDefault(h =>
            string.Equals(h.Name, "Date", StringComparison.OrdinalIgnoreCase))?.Value ?? "";

    public static string GetSnippet(this GmailMessage msg) => msg.Snippet ?? "";

    public static string GetPlainTextBody(this GmailMessage msg)
    {
        if (msg.Payload == null) return "";
        return ExtractPlainText(msg.Payload) ?? "";
    }

    private static string? ExtractPlainText(Google.Apis.Gmail.v1.Data.MessagePart part)
    {
        if (part.MimeType == "text/plain" && part.Body?.Data != null)
            return DecodeBase64Url(part.Body.Data);

        if (part.Parts != null)
        {
            // Prefer text/plain in multipart; fall back to first result
            foreach (var p in part.Parts)
            {
                if (p.MimeType == "text/plain")
                {
                    var text = ExtractPlainText(p);
                    if (text != null) return text;
                }
            }
            foreach (var p in part.Parts)
            {
                var text = ExtractPlainText(p);
                if (text != null) return text;
            }
        }

        return null;
    }

    private static string DecodeBase64Url(string data)
    {
        var s = data.Replace('-', '+').Replace('_', '/');
        s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
        return Encoding.UTF8.GetString(Convert.FromBase64String(s));
    }
}
