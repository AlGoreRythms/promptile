using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Promptile.Sdk;

namespace Linear;

public class LinearDataSourceInstance : IDataSourceInstance
{
    public string Id => Config.Id;
    public string Name => Config.Name;
    public string Type => "linear";
    public DataSourceConfig Config { get; }

    private readonly string _apiKey;
    private readonly string? _teamId;
    private readonly int _pollSeconds;

    private IInformationStore? _store;
    private INotificationBus? _bus;
    private ISyncReporter? _sync;
    private CancellationTokenSource? _cts;
    private Task? _pollTask;

    private DateTime _lastSync = DateTime.MinValue;
    private HashSet<string> _seenKeys = [];

    private readonly string _stateDir;

    private static readonly HttpClient _http = new();
    private const string GraphQlUrl = "https://api.linear.app/graphql";

    public LinearDataSourceInstance(DataSourceConfig config)
    {
        Config = config;
        _apiKey = config.Config.GetValueOrDefault("apiKey", "");
        _teamId = config.Config.GetValueOrDefault("teamId") is { Length: > 0 } t ? t : null;
        _pollSeconds = int.TryParse(config.Config.GetValueOrDefault("pollIntervalSeconds"), out var s) ? s : 300;
        _stateDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".promptile", "datasources", config.Id);
        Directory.CreateDirectory(_stateDir);
    }

    public Task StartAsync(INotificationBus bus, IServiceProvider services, CancellationToken ct)
    {
        _bus = bus;
        _store = services.GetService(typeof(IInformationStore)) as IInformationStore;
        _sync = services.GetService(typeof(ISyncReporter)) as ISyncReporter;
        _seenKeys = LoadSeenKeys();

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _pollTask = Task.Run(() => PollLoopAsync(_cts.Token), _cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _cts?.Cancel();
        if (_pollTask != null)
        {
            try { await _pollTask.WaitAsync(TimeSpan.FromSeconds(5), ct); } catch { }
        }
    }

    public Task ResetStateAsync()
    {
        _lastSync = DateTime.MinValue;
        _seenKeys = [];
        var path = Path.Combine(_stateDir, "seen-keys.json");
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    public Task<DataSourceStatus> GetStatusAsync()
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
            return Task.FromResult(new DataSourceStatus(false, "No API key configured"));
        return Task.FromResult(new DataSourceStatus(true,
            _lastSync == DateTime.MinValue ? "Waiting for first sync" : $"Last sync: {_lastSync:HH:mm} UTC"));
    }

    public ValueTask DisposeAsync()
    {
        _cts?.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await SyncAsync(ct); } catch when (!ct.IsCancellationRequested) { }
            await Task.Delay(TimeSpan.FromSeconds(_pollSeconds), ct).ConfigureAwait(false);
        }
    }

    private async Task SyncAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_apiKey) || _store == null || _bus == null) return;

        _sync?.Begin(Id, $"{Name} · Linear", "linear");

        try
        {
        _sync?.Progress(Id, "Fetching assigned issues", 0);
        var issues = await FetchAssignedIssuesAsync(ct);
        if (issues == null || issues.Count == 0)
        {
            _sync?.Complete(Id, 0);
            return;
        }
        _sync?.Progress(Id, $"Processing {issues.Count} issue{(issues.Count == 1 ? "" : "s")}", issues.Count);

        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var sb = new StringBuilder();
        sb.AppendLine($"# Linear Issues — {today}");
        sb.AppendLine();

        foreach (var issue in issues.OrderBy(i => i.Priority).ThenBy(i => i.UpdatedAt))
        {
            sb.AppendLine($"## [{issue.Identifier}] {issue.Title}");
            sb.AppendLine($"**Status**: {issue.State}  **Priority**: {PriorityLabel(issue.Priority)}");
            if (!string.IsNullOrEmpty(issue.Team))
                sb.AppendLine($"**Team**: {issue.Team}");
            if (issue.DueDate.HasValue)
                sb.AppendLine($"**Due**: {issue.DueDate.Value:yyyy-MM-dd}");
            if (!string.IsNullOrEmpty(issue.Description))
            {
                sb.AppendLine();
                sb.AppendLine(issue.Description.Length > 400
                    ? issue.Description[..400] + "…"
                    : issue.Description);
            }
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();
        }

        await _store.WriteDataAsync(Config.Name, "Linear", $"{today}.md", sb.ToString());

        // Detect newly assigned tasks
        var currentKeys = issues.Select(i => i.Identifier).ToHashSet();
        if (_lastSync != DateTime.MinValue)
        {
            foreach (var key in currentKeys.Except(_seenKeys))
            {
                var issue = issues.First(i => i.Identifier == key);
                _bus.Publish(new AssistantNotification(
                    PluginId: Config.Id,
                    Title: $"New task: {issue.Identifier}",
                    Body: issue.Title,
                    Timestamp: DateTimeOffset.UtcNow,
                    Category: "new-assigned-task",
                    Payload: new NewTaskPayload(issue.Identifier, issue.Title, issue.Description, Config.Name)));
            }
        }
        _seenKeys = currentKeys;
        SaveSeenKeys(_seenKeys);

        var newCount = issues.Count(i => i.UpdatedAt > _lastSync);
        if (newCount > 0)
        {
            _bus.Publish(new AssistantNotification(
                PluginId: "linear",
                Title: $"Linear — {Config.Name}",
                Body: $"{newCount} issue{(newCount == 1 ? "" : "s")} updated",
                Timestamp: DateTimeOffset.UtcNow,
                Category: "sync-summary"));
        }

        _lastSync = DateTime.UtcNow;
        _sync?.Complete(Id, issues.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _sync?.Fail(Id, ex.Message);
            throw;
        }
    }

    private HashSet<string> LoadSeenKeys()
    {
        var path = Path.Combine(_stateDir, "seen-keys.json");
        if (!File.Exists(path)) return [];
        try { return JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(path)) ?? []; }
        catch { return []; }
    }

    private void SaveSeenKeys(HashSet<string> keys)
    {
        try { File.WriteAllText(Path.Combine(_stateDir, "seen-keys.json"), JsonSerializer.Serialize(keys)); }
        catch { }
    }

    private async Task<List<LinearIssue>?> FetchAssignedIssuesAsync(CancellationToken ct)
    {
        var teamFilter = string.IsNullOrWhiteSpace(_teamId)
            ? ""
            : $", filter: {{ team: {{ key: {{ eq: \"{_teamId}\" }} }} }}";

        var query = $$"""
            {
              viewer {
                assignedIssues(first: 50{{teamFilter}}) {
                  nodes {
                    identifier
                    title
                    description
                    priority
                    updatedAt
                    dueDate
                    state { name }
                    team { name }
                  }
                }
              }
            }
            """;

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, GraphQlUrl);
            req.Headers.Authorization = new AuthenticationHeaderValue(_apiKey);
            req.Content = new StringContent(
                JsonSerializer.Serialize(new { query }),
                Encoding.UTF8, "application/json");

            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;

            var json = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            var nodes = json
                .GetProperty("data")
                .GetProperty("viewer")
                .GetProperty("assignedIssues")
                .GetProperty("nodes");

            var issues = new List<LinearIssue>();
            foreach (var node in nodes.EnumerateArray())
            {
                issues.Add(new LinearIssue(
                    Identifier: node.GetProperty("identifier").GetString() ?? "",
                    Title: node.GetProperty("title").GetString() ?? "",
                    Description: node.TryGetProperty("description", out var d) && d.ValueKind != JsonValueKind.Null
                        ? d.GetString() : null,
                    Priority: node.TryGetProperty("priority", out var p) ? p.GetInt32() : 0,
                    State: node.GetProperty("state").GetProperty("name").GetString() ?? "",
                    Team: node.TryGetProperty("team", out var t) && t.ValueKind != JsonValueKind.Null
                        ? t.GetProperty("name").GetString() : null,
                    UpdatedAt: node.TryGetProperty("updatedAt", out var u)
                        ? DateTime.TryParse(u.GetString(), out var dt) ? dt : DateTime.MinValue
                        : DateTime.MinValue,
                    DueDate: node.TryGetProperty("dueDate", out var dd) && dd.ValueKind != JsonValueKind.Null
                        ? DateTime.TryParse(dd.GetString(), out var ddt) ? ddt : (DateTime?)null
                        : null
                ));
            }
            return issues;
        }
        catch { return null; }
    }

    private static string PriorityLabel(int priority) => priority switch
    {
        0 => "No priority",
        1 => "Urgent",
        2 => "High",
        3 => "Medium",
        4 => "Low",
        _ => "Unknown",
    };

    private record LinearIssue(
        string Identifier,
        string Title,
        string? Description,
        int Priority,
        string State,
        string? Team,
        DateTime UpdatedAt,
        DateTime? DueDate);
}
