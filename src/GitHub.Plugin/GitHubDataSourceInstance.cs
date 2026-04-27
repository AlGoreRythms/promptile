using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Assistant.Sdk;

namespace GitHub;

public class GitHubDataSourceInstance : IDataSourceInstance
{
    public string Id => Config.Id;
    public string Name => Config.Name;
    public string Type => "github";
    public DataSourceConfig Config { get; }

    private readonly string _token;
    private readonly List<string> _repos;
    private readonly int _pollSeconds;

    private IInformationStore? _store;
    private INotificationBus? _bus;
    private ISyncReporter? _sync;
    private CancellationTokenSource? _cts;
    private Task? _pollTask;

    private DateTime _lastSync = DateTime.MinValue;
    private string? _cachedLogin;
    private HashSet<string> _seenKeys = [];

    private readonly string _stateDir;
    private static readonly HttpClient _http = new();
    private const string ApiBase = "https://api.github.com";

    public GitHubDataSourceInstance(DataSourceConfig config)
    {
        Config = config;
        _token = config.Config.GetValueOrDefault("token", "");
        var repoRaw = config.Config.GetValueOrDefault("repos", "");
        _repos = repoRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(r => r.Contains('/')).ToList();
        _pollSeconds = int.TryParse(config.Config.GetValueOrDefault("pollIntervalSeconds"), out var s) ? s : 300;
        _stateDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".assistant", "datasources", config.Id);
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
        if (string.IsNullOrWhiteSpace(_token))
            return Task.FromResult(new DataSourceStatus(false, "No token configured"));
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
        if (string.IsNullOrWhiteSpace(_token) || _store == null || _bus == null) return;

        _sync?.Begin(Id, $"{Name} · GitHub", "github");

        try
        {
        _sync?.Progress(Id, "Fetching assigned issues", 0);
        var issues = await FetchAssignedIssuesAsync(ct);
        _sync?.Progress(Id, "Fetching pull requests", issues.Count);
        var prs = await FetchAssignedPrsAsync(ct);

        if (issues.Count == 0 && prs.Count == 0)
        {
            _sync?.Complete(Id, 0);
            return;
        }
        _sync?.Progress(Id, $"{issues.Count} issue{(issues.Count == 1 ? "" : "s")}, {prs.Count} PR{(prs.Count == 1 ? "" : "s")}", issues.Count + prs.Count);

        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var sb = new StringBuilder();
        sb.AppendLine($"# GitHub — {today}");
        sb.AppendLine();

        if (prs.Count > 0)
        {
            sb.AppendLine("## Pull Requests");
            sb.AppendLine();
            foreach (var pr in prs.OrderByDescending(p => p.UpdatedAt))
            {
                sb.AppendLine($"### [{pr.Repo}#{pr.Number}] {pr.Title}");
                sb.AppendLine($"**State**: {pr.State}  **Draft**: {(pr.Draft ? "Yes" : "No")}");
                if (pr.Labels.Count > 0)
                    sb.AppendLine($"**Labels**: {string.Join(", ", pr.Labels)}");
                sb.AppendLine($"**Updated**: {pr.UpdatedAt:yyyy-MM-dd HH:mm} UTC");
                sb.AppendLine($"**URL**: {pr.HtmlUrl}");
                sb.AppendLine();
            }
        }

        if (issues.Count > 0)
        {
            sb.AppendLine("## Issues");
            sb.AppendLine();
            foreach (var issue in issues.OrderByDescending(i => i.UpdatedAt))
            {
                sb.AppendLine($"### [{issue.Repo}#{issue.Number}] {issue.Title}");
                sb.AppendLine($"**State**: {issue.State}");
                if (issue.Labels.Count > 0)
                    sb.AppendLine($"**Labels**: {string.Join(", ", issue.Labels)}");
                sb.AppendLine($"**Updated**: {issue.UpdatedAt:yyyy-MM-dd HH:mm} UTC");
                sb.AppendLine($"**URL**: {issue.HtmlUrl}");
                sb.AppendLine();
            }
        }

        await _store.WriteDataAsync(Config.Name, "GitHub", $"{today}.md", sb.ToString());

        // Detect newly assigned tasks
        var allItems = issues.Concat(prs).ToList();
        var currentKeys = allItems.Select(i => $"{i.Repo}#{i.Number}").ToHashSet();
        if (_lastSync != DateTime.MinValue)
        {
            foreach (var key in currentKeys.Except(_seenKeys))
            {
                var item = allItems.First(i => $"{i.Repo}#{i.Number}" == key);
                _bus.Publish(new AssistantNotification(
                    PluginId: Config.Id,
                    Title: $"New task: {key}",
                    Body: item.Title,
                    Timestamp: DateTimeOffset.UtcNow,
                    Category: "new-assigned-task",
                    Payload: new NewTaskPayload(key, item.Title, null, Config.Name)));
            }
        }
        _seenKeys = currentKeys;
        SaveSeenKeys(_seenKeys);

        var newCount = issues.Count(i => i.UpdatedAt > _lastSync) + prs.Count(p => p.UpdatedAt > _lastSync);
        if (newCount > 0)
        {
            _bus.Publish(new AssistantNotification(
                PluginId: "github",
                Title: $"GitHub — {Config.Name}",
                Body: $"{newCount} item{(newCount == 1 ? "" : "s")} updated",
                Timestamp: DateTimeOffset.UtcNow,
                Category: "sync-summary"));
        }

        _lastSync = DateTime.UtcNow;
        _sync?.Complete(Id, issues.Count + prs.Count);
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

    private HttpRequestMessage BuildRequest(string url)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        req.Headers.UserAgent.ParseAdd("AssistantApp/1.0");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        return req;
    }

    private async Task<List<GitHubItem>> FetchAssignedIssuesAsync(CancellationToken ct)
    {
        var items = new List<GitHubItem>();
        try
        {
            using var req = BuildRequest($"{ApiBase}/issues?filter=assigned&state=open&per_page=50");
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return items;
            var json = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            foreach (var node in json.EnumerateArray())
            {
                if (node.TryGetProperty("pull_request", out _)) continue;
                items.Add(ParseItem(node, isPr: false));
            }
        }
        catch { }
        return items;
    }

    private async Task<List<GitHubItem>> FetchAssignedPrsAsync(CancellationToken ct)
    {
        var items = new List<GitHubItem>();
        var login = await GetLoginAsync(ct);
        var repos = _repos.Count > 0 ? _repos : await GetUserReposAsync(ct);

        foreach (var repo in repos.Take(20))
        {
            try
            {
                using var req = BuildRequest($"{ApiBase}/repos/{repo}/pulls?state=open&per_page=50");
                using var resp = await _http.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode) continue;
                var json = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
                foreach (var node in json.EnumerateArray())
                {
                    var assignees = node.TryGetProperty("assignees", out var a)
                        ? a.EnumerateArray().Select(x => x.GetProperty("login").GetString()).ToList()
                        : new List<string?>();
                    var requestedReviewers = node.TryGetProperty("requested_reviewers", out var rr)
                        ? rr.EnumerateArray().Select(x => x.GetProperty("login").GetString()).ToList()
                        : new List<string?>();
                    var authorLogin = node.TryGetProperty("user", out var u) ? u.GetProperty("login").GetString() : null;

                    if (login != null &&
                        (assignees.Contains(login) || requestedReviewers.Contains(login) || authorLogin == login))
                        items.Add(ParseItem(node, isPr: true, repoOverride: repo));
                }
            }
            catch { }
        }
        return items;
    }

    private async Task<string?> GetLoginAsync(CancellationToken ct)
    {
        if (_cachedLogin != null) return _cachedLogin;
        try
        {
            using var req = BuildRequest($"{ApiBase}/user");
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            _cachedLogin = json.GetProperty("login").GetString();
        }
        catch { }
        return _cachedLogin;
    }

    private async Task<List<string>> GetUserReposAsync(CancellationToken ct)
    {
        var repos = new List<string>();
        try
        {
            using var req = BuildRequest($"{ApiBase}/user/repos?affiliation=owner,collaborator&per_page=50&sort=updated");
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return repos;
            var json = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            repos.AddRange(json.EnumerateArray()
                .Select(n => n.GetProperty("full_name").GetString())
                .Where(n => n != null).Select(n => n!));
        }
        catch { }
        return repos;
    }

    private static GitHubItem ParseItem(JsonElement node, bool isPr, string? repoOverride = null)
    {
        var repoUrl = node.TryGetProperty("repository_url", out var ru) ? ru.GetString() ?? "" : "";
        var repo = repoOverride ?? (repoUrl.Length > 0
            ? string.Join("/", repoUrl.Split('/').TakeLast(2))
            : "unknown/unknown");

        var labels = node.TryGetProperty("labels", out var l)
            ? l.EnumerateArray().Select(x => x.GetProperty("name").GetString() ?? "").ToList()
            : new List<string>();

        var updatedStr = node.TryGetProperty("updated_at", out var upd) ? upd.GetString() : null;
        var updated = updatedStr != null && DateTime.TryParse(updatedStr, out var dt) ? dt : DateTime.MinValue;

        return new GitHubItem(
            Repo: repo,
            Number: node.GetProperty("number").GetInt32(),
            Title: node.GetProperty("title").GetString() ?? "",
            State: node.GetProperty("state").GetString() ?? "",
            HtmlUrl: node.TryGetProperty("html_url", out var hu) ? hu.GetString() ?? "" : "",
            Labels: labels,
            UpdatedAt: updated,
            Draft: isPr && node.TryGetProperty("draft", out var d) && d.GetBoolean(),
            IsPr: isPr);
    }

    private record GitHubItem(
        string Repo,
        int Number,
        string Title,
        string State,
        string HtmlUrl,
        List<string> Labels,
        DateTime UpdatedAt,
        bool Draft,
        bool IsPr);
}
