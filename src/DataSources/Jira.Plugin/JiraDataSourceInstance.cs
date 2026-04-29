using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Assistant.Sdk;

namespace Jira;

public class JiraDataSourceInstance : IDataSourceInstance
{
    public string Id => Config.Id;
    public string Name => Config.Name;
    public string Type => "jira";
    public DataSourceConfig Config { get; }

    private readonly string _baseUrl;
    private readonly string _email;
    private readonly string _apiToken;
    private readonly string _jql;
    private readonly int _pollSeconds;

    private IInformationStore? _store;
    private INotificationBus? _bus;
    private ISyncReporter? _sync;
    private CancellationTokenSource? _cts;
    private Task? _pollTask;

    private bool _connected;
    private string? _connectedEmail;
    private DateTime _lastSync = DateTime.MinValue;
    private string? _lastError;
    private HashSet<string> _seenKeys = [];

    private readonly string _stateDir;

    private static readonly HttpClient _http = new();

    private const string DefaultJql =
        "assignee = currentUser() AND resolution = Unresolved ORDER BY updated DESC";

    public JiraDataSourceInstance(DataSourceConfig config)
    {
        Config = config;
        _baseUrl = config.Config.GetValueOrDefault("baseUrl", "").TrimEnd('/');
        _email = config.Config.GetValueOrDefault("email", "");
        _apiToken = config.Config.GetValueOrDefault("apiToken", "");
        _jql = config.Config.GetValueOrDefault("jql") is { Length: > 0 } j ? j : DefaultJql;
        _pollSeconds = int.TryParse(config.Config.GetValueOrDefault("pollIntervalSeconds"), out var s) ? s : 300;
        _stateDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".assistant", "datasources", config.Id);
        Directory.CreateDirectory(_stateDir);
    }

    public async Task StartAsync(INotificationBus bus, IServiceProvider services, CancellationToken ct)
    {
        _bus = bus;
        _store = services.GetService(typeof(IInformationStore)) as IInformationStore;
        _sync = services.GetService(typeof(ISyncReporter)) as ISyncReporter;

        if (!IsConfigured()) return;

        try
        {
            using var req = BuildRequest(HttpMethod.Get, "/rest/api/3/myself");
            using var resp = await _http.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
                _connectedEmail = json.TryGetProperty("emailAddress", out var ea) ? ea.GetString() : null;
                _connected = true;
                _seenKeys = LoadSeenKeys();
            }
            else
            {
                Console.Error.WriteLine($"[Jira:{Name}] Auth failed: {resp.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Jira:{Name}] Start failed: {ex.Message}");
        }

        if (!_connected) return;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _pollTask = Task.Run(() => PollLoopAsync(_cts.Token), _cts.Token);
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
        if (!IsConfigured())
            return Task.FromResult(new DataSourceStatus(false, "Not configured"));
        if (!_connected)
            return Task.FromResult(new DataSourceStatus(false, "Not connected"));
        if (_lastError != null)
            return Task.FromResult(new DataSourceStatus(false, _lastError));
        return Task.FromResult(new DataSourceStatus(true,
            _lastSync == DateTime.MinValue ? "Waiting for first sync" : $"Last sync {_lastSync:HH:mm} UTC · {_connectedEmail}"));
    }

    public ValueTask DisposeAsync()
    {
        _cts?.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(15), ct);
        while (!ct.IsCancellationRequested)
        {
            try { await SyncAsync(ct); } catch when (!ct.IsCancellationRequested) { }
            await Task.Delay(TimeSpan.FromSeconds(_pollSeconds), ct).ConfigureAwait(false);
        }
    }

    private async Task SyncAsync(CancellationToken ct)
    {
        if (_store == null || _bus == null) return;

        _sync?.Begin(Id, $"{Name} · Jira", "jira");

        try
        {
        var issues = await FetchIssuesAsync(ct);
        _lastSync = DateTime.UtcNow;   // always mark sync time regardless of result

        if (issues == null)            // fetch failed — error already set in _lastError
        {
            _sync?.Fail(Id, _lastError ?? "Fetch failed");
            return;
        }

        if (issues.Count == 0)
        {
            _lastError = null;
            _sync?.Complete(Id, 0);
            return;                    // no matching issues — JQL returned empty
        }

        _lastError = null;
        _sync?.Progress(Id, $"Processing {issues.Count} issue{(issues.Count == 1 ? "" : "s")}", issues.Count);

        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var sb = new StringBuilder();
        sb.AppendLine($"# Jira Issues — {today}");
        sb.AppendLine();

        foreach (var issue in issues)
        {
            sb.AppendLine($"## [{issue.Key}] {issue.Summary}");

            var meta = new List<string>();
            if (!string.IsNullOrEmpty(issue.Status))    meta.Add($"**Status**: {issue.Status}");
            if (!string.IsNullOrEmpty(issue.Priority))  meta.Add($"**Priority**: {issue.Priority}");
            if (!string.IsNullOrEmpty(issue.IssueType)) meta.Add($"**Type**: {issue.IssueType}");
            if (!string.IsNullOrEmpty(issue.Project))   meta.Add($"**Project**: {issue.Project}");
            if (meta.Count > 0) sb.AppendLine(string.Join("  ", meta));

            if (!string.IsNullOrEmpty(issue.Assignee))
                sb.AppendLine($"**Assignee**: {issue.Assignee}");
            if (!string.IsNullOrEmpty(issue.Reporter))
                sb.AppendLine($"**Reporter**: {issue.Reporter}");
            if (issue.DueDate.HasValue)
                sb.AppendLine($"**Due**: {issue.DueDate.Value:yyyy-MM-dd}");

            if (!string.IsNullOrEmpty(issue.Description))
            {
                sb.AppendLine();
                sb.AppendLine(issue.Description);
            }

            if (!string.IsNullOrEmpty(issue.LastComment))
            {
                sb.AppendLine();
                var ago = issue.LastCommentUpdated != default
                    ? TimeAgo(issue.LastCommentUpdated)
                    : "";
                var author = issue.LastCommentAuthor ?? "Unknown";
                sb.AppendLine($"*Last comment ({author}{(ago.Length > 0 ? ", " + ago : "")}):* {issue.LastComment}");
            }

            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();
        }

        await _store.WriteDataAsync(Config.Name, "Jira", $"{today}.md", sb.ToString());

        // Detect and publish newly assigned tasks
        var currentKeys = issues.Select(i => i.Key).ToHashSet();
        if (_lastSync != DateTime.MinValue)   // skip on very first sync — just seed
        {
            foreach (var key in currentKeys.Except(_seenKeys))
            {
                var issue = issues.First(i => i.Key == key);
                _bus.Publish(new AssistantNotification(
                    PluginId: Config.Id,
                    Title: $"New task: {issue.Key}",
                    Body: issue.Summary,
                    Timestamp: DateTimeOffset.UtcNow,
                    Category: "new-assigned-task",
                    Payload: new NewTaskPayload(issue.Key, issue.Summary, issue.Description, Config.Name)));
            }
        }
        _seenKeys = currentKeys;
        SaveSeenKeys(_seenKeys);

        var newCount = issues.Count(i => i.Updated > _lastSync && _lastSync != DateTime.MinValue);
        if (newCount > 0)
        {
            _bus.Publish(new AssistantNotification(
                PluginId: "jira",
                Title: $"Jira — {Config.Name}",
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

    private async Task<List<JiraIssue>?> FetchIssuesAsync(CancellationToken ct)
    {
        try
        {
            var fields = "summary,status,priority,issuetype,assignee,reporter,duedate,updated,project,comment,description";
            var url = $"/rest/api/3/search/jql?jql={Uri.EscapeDataString(_jql)}&fields={fields}&maxResults=50";

            using var req = BuildRequest(HttpMethod.Get, url);
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                var snippet = body.Length > 120 ? body[..120] : body;
                _lastError = $"API error {(int)resp.StatusCode}: {snippet}";
                Console.Error.WriteLine($"[Jira:{Name}] Search failed: {resp.StatusCode} — {body}");
                return null;
            }

            var json = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            if (!json.TryGetProperty("issues", out var issuesEl)) return null;

            var issues = new List<JiraIssue>();
            foreach (var el in issuesEl.EnumerateArray())
            {
                var key = el.GetProperty("key").GetString() ?? "";
                var f = el.GetProperty("fields");

                var summary   = f.TryGetProperty("summary", out var sm) ? sm.GetString() ?? "" : "";
                var status    = GetNestedString(f, "status", "name");
                var priority  = f.TryGetProperty("priority", out var pr) && pr.ValueKind != JsonValueKind.Null
                                    ? GetNestedString(f, "priority", "name") : null;
                var issueType = GetNestedString(f, "issuetype", "name");
                var project   = GetNestedString(f, "project", "name");
                var assignee  = f.TryGetProperty("assignee", out var asgn) && asgn.ValueKind != JsonValueKind.Null
                                    ? GetNestedString(f, "assignee", "displayName") : null;
                var reporter  = f.TryGetProperty("reporter", out var rep) && rep.ValueKind != JsonValueKind.Null
                                    ? GetNestedString(f, "reporter", "displayName") : null;

                DateTime? dueDate = null;
                if (f.TryGetProperty("duedate", out var dd) && dd.ValueKind != JsonValueKind.Null
                    && DateTime.TryParse(dd.GetString(), out var ddt))
                    dueDate = ddt;

                var updated = DateTime.MinValue;
                if (f.TryGetProperty("updated", out var upd))
                    DateTime.TryParse(upd.GetString(), out updated);

                string? description = null;
                if (f.TryGetProperty("description", out var desc) && desc.ValueKind != JsonValueKind.Null)
                    description = ExtractAdfText(desc);

                string? lastComment = null, lastCommentAuthor = null;
                DateTime lastCommentUpdated = default;
                if (f.TryGetProperty("comment", out var commentBlock) &&
                    commentBlock.TryGetProperty("comments", out var comments) &&
                    comments.GetArrayLength() > 0)
                {
                    var last = comments[comments.GetArrayLength() - 1];
                    lastCommentAuthor = last.TryGetProperty("author", out var auth) && auth.TryGetProperty("displayName", out var dn)
                        ? dn.GetString() : null;
                    if (last.TryGetProperty("updated", out var cu))
                        DateTime.TryParse(cu.GetString(), out lastCommentUpdated);
                    if (last.TryGetProperty("body", out var body))
                        lastComment = ExtractAdfText(body);
                }

                issues.Add(new JiraIssue(key, summary, status, priority, issueType, project, assignee, reporter,
                    dueDate, updated, description, lastComment, lastCommentAuthor, lastCommentUpdated));
            }

            return issues;
        }
        catch (Exception ex)
        {
            _lastError = $"Fetch error: {ex.Message}";
            Console.Error.WriteLine($"[Jira:{Name}] Fetch failed: {ex.Message}");
            return null;
        }
    }

    private static string? GetNestedString(JsonElement parent, string prop, string child)
    {
        if (parent.TryGetProperty(prop, out var el) && el.ValueKind != JsonValueKind.Null
            && el.TryGetProperty(child, out var val))
            return val.GetString();
        return null;
    }

    private static string ExtractAdfText(JsonElement node)
    {
        var sb = new StringBuilder();
        AppendAdfText(node, sb);
        var text = sb.ToString().Trim();
        return text.Length > 300 ? text[..300] + "…" : text;
    }

    private static void AppendAdfText(JsonElement node, StringBuilder sb)
    {
        if (node.TryGetProperty("text", out var text))
        {
            sb.Append(text.GetString());
            return;
        }
        if (node.TryGetProperty("content", out var content))
        {
            foreach (var child in content.EnumerateArray())
                AppendAdfText(child, sb);
            if (node.TryGetProperty("type", out var t) &&
                t.GetString() is "paragraph" or "heading" or "listItem")
                sb.Append(' ');
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

    private HttpRequestMessage BuildRequest(HttpMethod method, string path)
    {
        var req = new HttpRequestMessage(method, _baseUrl + path);
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_email}:{_apiToken}"));
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return req;
    }

    private bool IsConfigured() =>
        !string.IsNullOrWhiteSpace(_baseUrl) &&
        !string.IsNullOrWhiteSpace(_email) &&
        !string.IsNullOrWhiteSpace(_apiToken);

    private static string TimeAgo(DateTime dt)
    {
        var diff = DateTime.UtcNow - dt.ToUniversalTime();
        if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes}m ago";
        if (diff.TotalHours < 24) return $"{(int)diff.TotalHours}h ago";
        return $"{(int)diff.TotalDays}d ago";
    }

    private record JiraIssue(
        string Key,
        string Summary,
        string? Status,
        string? Priority,
        string? IssueType,
        string? Project,
        string? Assignee,
        string? Reporter,
        DateTime? DueDate,
        DateTime Updated,
        string? Description,
        string? LastComment,
        string? LastCommentAuthor,
        DateTime LastCommentUpdated);
}
