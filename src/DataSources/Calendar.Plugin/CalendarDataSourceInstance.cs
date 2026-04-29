using System.Text;
using System.Text.Json;
using Assistant.Sdk;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Calendar.v3;
using Google.Apis.Calendar.v3.Data;
using Google.Apis.Services;

namespace Calendar;

public class CalendarDataSourceInstance : IDataSourceInstance
{
    public string Id => Config.Id;
    public string Name => Config.Name;
    public string Type => "google-calendar";
    public DataSourceConfig Config { get; private set; }

    private CalendarService? _calendar;
    private IInformationStore? _store;
    private ISyncReporter? _sync;
    private CancellationTokenSource? _cts;
    private Task? _pollTask;
    private string? _connectedEmail;
    private readonly string _stateDir;

    private static readonly string GoogleCredentialsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".assistant", "google-credentials.json");

    private static readonly string[] Scopes = ["https://www.googleapis.com/auth/calendar.readonly"];

    public CalendarDataSourceInstance(DataSourceConfig config)
    {
        Config = config;
        _stateDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".assistant", "datasources", config.Id);
        Directory.CreateDirectory(_stateDir);
    }

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
            _calendar = BuildService(creds.Value.ClientId, creds.Value.ClientSecret, refreshToken);
            // Quick connectivity test
            var cal = await _calendar.Calendars.Get("primary").ExecuteAsync(ct);
            _connectedEmail = cal.Summary ?? "Connected";

            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _pollTask = PollLoopAsync(bus, _cts.Token);
        }
        catch (Exception ex)
        {
            _connectedEmail = null;
            _calendar = null;
            Console.Error.WriteLine($"[Calendar:{Name}] Start failed: {ex.Message}");
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
        _calendar?.Dispose();
        _calendar = null;
    }

    public Task ResetStateAsync() => Task.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _calendar?.Dispose();
        return ValueTask.CompletedTask;
    }

    public Task<DataSourceStatus> GetStatusAsync()
    {
        if (LoadAppCredentials() == null)
            return Task.FromResult(new DataSourceStatus(false, "~/.assistant/google-credentials.json not found"));

        if (!Config.Config.TryGetValue("refreshToken", out var refreshToken) || string.IsNullOrEmpty(refreshToken))
            return Task.FromResult(new DataSourceStatus(false, "Authorization required",
                AuthUrl: $"/api/calendar-authorize/{Config.Id}"));

        if (_calendar == null)
            return Task.FromResult(new DataSourceStatus(false, "Not connected"));

        return Task.FromResult(new DataSourceStatus(true, _connectedEmail));
    }

    private async Task PollLoopAsync(INotificationBus bus, CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(10), ct);

        var interval = TimeSpan.FromSeconds(
            Config.Config.TryGetValue("pollIntervalSeconds", out var s) && int.TryParse(s, out var sec) ? sec : 300);

        while (!ct.IsCancellationRequested)
        {
            try { await PollAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Console.Error.WriteLine($"[Calendar:{Name}] Poll error: {ex.Message}"); }

            await Task.Delay(interval, ct);
        }
    }

    private async Task PollAsync(CancellationToken ct)
    {
        if (_calendar == null || _store == null) return;

        _sync?.Begin(Id, $"{Name} · Calendar", "google-calendar");

        try
        {
            var calendarId = Config.Config.TryGetValue("calendarId", out var cid) && !string.IsNullOrWhiteSpace(cid)
                ? cid : "primary";
            var daysAhead = Config.Config.TryGetValue("daysAhead", out var da) && int.TryParse(da, out var d) ? d : 14;

            var timeMin = DateTime.UtcNow.Date.AddDays(-1);
            var timeMax = DateTime.UtcNow.Date.AddDays(daysAhead + 1);

            _sync?.Progress(Id, $"Fetching events for next {daysAhead} days", 0);

            var request = _calendar.Events.List(calendarId);
            request.TimeMinDateTimeOffset = timeMin;
            request.TimeMaxDateTimeOffset = timeMax;
            request.SingleEvents = true;
            request.OrderBy = EventsResource.ListRequest.OrderByEnum.StartTime;
            request.MaxResults = 250;

            var events = await request.ExecuteAsync(ct);
            if (events.Items == null || events.Items.Count == 0)
            {
                _sync?.Complete(Id, 0);
                return;
            }

            // Group events by local date
            var byDate = new Dictionary<DateOnly, List<Event>>();
            foreach (var ev in events.Items)
            {
                var dt = ev.Start?.DateTimeDateTimeOffset?.LocalDateTime
                      ?? (ev.Start?.Date != null ? DateTime.Parse(ev.Start.Date) : (DateTime?)null);
                if (dt == null) continue;
                var date = DateOnly.FromDateTime(dt.Value);
                if (!byDate.TryGetValue(date, out var list))
                    byDate[date] = list = [];
                list.Add(ev);
            }

            var totalEvents = 0;
            foreach (var (date, dayEvents) in byDate)
            {
                _sync?.Progress(Id, $"Writing {date:MMM d}", totalEvents);

                var filename = $"{date:yyyy-MM-dd}.md";
                var sb = new StringBuilder();
                sb.AppendLine($"# Calendar — {date:dddd, MMMM d, yyyy}");
                sb.AppendLine();

                foreach (var ev in dayEvents)
                {
                    var startDt = ev.Start?.DateTimeDateTimeOffset?.LocalDateTime;
                    var endDt   = ev.End?.DateTimeDateTimeOffset?.LocalDateTime;
                    var allDay  = startDt == null;

                    var timeLabel = allDay ? "All day" : $"{startDt:HH:mm}–{endDt:HH:mm}";
                    var title     = ev.Summary ?? "(no title)";
                    sb.AppendLine($"## {timeLabel} · {title}");

                    if (!string.IsNullOrWhiteSpace(ev.Location))
                        sb.AppendLine($"**Location**: {ev.Location}");

                    if (ev.Attendees?.Count > 0)
                    {
                        var names = ev.Attendees
                            .Select(a => a.DisplayName ?? a.Email ?? "")
                            .Where(n => n.Length > 0)
                            .Take(10);
                        sb.AppendLine($"**Attendees**: {string.Join(", ", names)}");
                    }

                    if (!string.IsNullOrWhiteSpace(ev.Description))
                    {
                        var desc = ev.Description.Length > 300
                            ? ev.Description[..300] + "…"
                            : ev.Description;
                        sb.AppendLine();
                        sb.AppendLine(desc.Trim());
                    }

                    sb.AppendLine();
                    sb.AppendLine("---");
                    sb.AppendLine();
                }

                var content = sb.ToString();
                // Overwrite the day's file (calendar events change, so we replace rather than append)
                await _store.WriteDataAsync(Name, "Calendar", filename, content);
                totalEvents += dayEvents.Count;
            }

            _sync?.Complete(Id, totalEvents);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _sync?.Fail(Id, ex.Message);
            throw;
        }
    }

    private CalendarService BuildService(string clientId, string clientSecret, string refreshToken)
    {
        var flow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
        {
            ClientSecrets = new ClientSecrets { ClientId = clientId, ClientSecret = clientSecret },
            Scopes = Scopes,
        });
        var token = new Google.Apis.Auth.OAuth2.Responses.TokenResponse { RefreshToken = refreshToken };
        var credential = new UserCredential(flow, "user", token);
        return new CalendarService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "Assistant",
        });
    }

    private static (string ClientId, string ClientSecret)? LoadAppCredentials()
    {
        if (!File.Exists(GoogleCredentialsPath)) return null;
        try
        {
            var doc = JsonDocument.Parse(File.ReadAllText(GoogleCredentialsPath)).RootElement;
            var clientId = doc.GetProperty("clientId").GetString() ?? "";
            var clientSecret = doc.GetProperty("clientSecret").GetString() ?? "";
            if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret)) return null;
            return (clientId, clientSecret);
        }
        catch { return null; }
    }
}
