using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Assistant.Sdk;

namespace Asana;

public class AsanaDataSourceInstance : IDataSourceInstance
{
    public string Id => Config.Id;
    public string Name => Config.Name;
    public string Type => "asana";
    public DataSourceConfig Config { get; }

    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly string _refreshToken;
    private readonly string? _configuredWorkspaceGid;
    private readonly int _pollSeconds;

    private string _accessToken = "";
    private DateTimeOffset _tokenExpiresAt = DateTimeOffset.MinValue;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    private IInformationStore? _store;
    private INotificationBus? _bus;
    private ISyncReporter? _sync;
    private CancellationTokenSource? _cts;
    private Task? _pollTask;

    private bool _connected;
    private string? _workspaceGid;
    private string? _workspaceName;
    private DateTime _lastSync = DateTime.MinValue;
    private string? _lastError;
    private HashSet<string> _seenGids = [];

    private readonly string _stateDir;

    private static readonly HttpClient _http = new()
    {
        BaseAddress = new Uri("https://app.asana.com/api/1.0/"),
    };

    private const string TaskFields =
        "gid,name,notes,due_on,projects.name,assignee.name,created_at,modified_at,permalink_url,completed";

    public AsanaDataSourceInstance(DataSourceConfig config)
    {
        Config = config;
        _clientId = config.Config.GetValueOrDefault("clientId", "");
        _clientSecret = config.Config.GetValueOrDefault("clientSecret", "");
        _refreshToken = config.Config.GetValueOrDefault("refreshToken", "");
        _configuredWorkspaceGid = config.Config.GetValueOrDefault("workspaceGid") is { Length: > 0 } g ? g : null;
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
            if (_configuredWorkspaceGid != null)
            {
                _workspaceGid = _configuredWorkspaceGid;
                _workspaceName = _configuredWorkspaceGid;
            }
            else
            {
                var ws = await GetAsync<JsonElement>("workspaces?opt_fields=gid,name&limit=1", ct);
                if (ws.TryGetProperty("data", out var arr) && arr.GetArrayLength() > 0)
                {
                    var first = arr[0];
                    _workspaceGid = first.TryGetProperty("gid", out var gid) ? gid.GetString() : null;
                    _workspaceName = first.TryGetProperty("name", out var wn) ? wn.GetString() : _workspaceGid;
                }
            }

            if (_workspaceGid == null)
            {
                _lastError = "No workspace found.";
                return;
            }

            _connected = true;
            _seenGids = LoadSeenGids();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Asana:{Name}] Start failed: {ex.Message}");
            _lastError = ex.Message;
            return;
        }

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
        _seenGids = [];
        var path = Path.Combine(_stateDir, "seen-gids.json");
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    public Task<DataSourceStatus> GetStatusAsync()
    {
        if (!IsConfigured())
            return Task.FromResult(new DataSourceStatus(false, "Not configured"));
        if (!_connected)
            return Task.FromResult(new DataSourceStatus(false, _lastError ?? "Not connected"));
        if (_lastError != null)
            return Task.FromResult(new DataSourceStatus(false, _lastError));
        var msg = _lastSync == DateTime.MinValue
            ? $"Waiting for first sync · {_workspaceName}"
            : $"Last sync {_lastSync:HH:mm} UTC · {_workspaceName}";
        return Task.FromResult(new DataSourceStatus(true, msg));
    }

    public ValueTask DisposeAsync()
    {
        _cts?.Dispose();
        return ValueTask.CompletedTask;
    }

    public async Task<List<AsanaTask>> GetTasksAsync(CancellationToken ct = default)
    {
        if (_workspaceGid == null) return [];
        return await FetchTasksAsync(ct) ?? [];
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

        _sync?.Begin(Id, $"{Name} · Asana", "asana");

        try
        {
        _sync?.Progress(Id, "Fetching tasks", 0);
        var tasks = await FetchTasksAsync(ct);
        _lastSync = DateTime.UtcNow;

        if (tasks == null)
        {
            _sync?.Fail(Id, _lastError ?? "Fetch failed");
            return;
        }

        _lastError = null;
        _sync?.Progress(Id, $"Processing {tasks.Count} task{(tasks.Count == 1 ? "" : "s")}", tasks.Count);

        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var sb = new StringBuilder();
        sb.AppendLine($"# Asana Tasks — {today}");
        sb.AppendLine();

        foreach (var task in tasks)
        {
            sb.AppendLine($"## {task.Name}");

            if (task.Projects.Count > 0)
                sb.AppendLine($"**Project**: {string.Join(", ", task.Projects)}");
            if (task.DueOn.HasValue)
                sb.AppendLine($"**Due**: {task.DueOn.Value:yyyy-MM-dd}");
            sb.AppendLine($"**URL**: {task.PermalinkUrl}");

            if (!string.IsNullOrWhiteSpace(task.Notes))
            {
                sb.AppendLine();
                var notes = task.Notes.Length > 300 ? task.Notes[..300] + "…" : task.Notes;
                sb.AppendLine(notes);
            }

            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();
        }

        await _store.WriteDataAsync(Config.Name, "Asana", $"{today}.md", sb.ToString());

        var currentGids = tasks.Select(t => t.Gid).ToHashSet();
        var previouslyEmpty = _seenGids.Count == 0 && _lastSync == DateTime.UtcNow;

        foreach (var gid in currentGids.Except(_seenGids))
        {
            var task = tasks.First(t => t.Gid == gid);
            _bus.Publish(new AssistantNotification(
                PluginId: Config.Id,
                Title: "New Asana task assigned",
                Body: task.Name,
                Timestamp: DateTimeOffset.UtcNow,
                Category: "new-assigned-task",
                Payload: new NewTaskPayload(task.Gid, task.Name, task.Notes, Config.Name)));
        }

        _seenGids = currentGids;
        SaveSeenGids(_seenGids);
        _sync?.Complete(Id, tasks.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _sync?.Fail(Id, ex.Message);
            throw;
        }
    }

    private async Task<List<AsanaTask>?> FetchTasksAsync(CancellationToken ct)
    {
        if (_workspaceGid == null) return null;
        try
        {
            var url = $"tasks?assignee=me&workspace={_workspaceGid}&completed_since=now&opt_fields={TaskFields}&limit=100";
            var data = await GetAsync<JsonElement>(url, ct);

            var tasks = new List<AsanaTask>();
            if (!data.TryGetProperty("data", out var arr)) return tasks;

            foreach (var el in arr.EnumerateArray())
            {
                var gid = el.GetProperty("gid").GetString() ?? "";
                var name = el.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "";
                var notes = el.TryGetProperty("notes", out var nt) && nt.ValueKind != JsonValueKind.Null
                    ? nt.GetString() : null;
                var permalink = el.TryGetProperty("permalink_url", out var pl) ? pl.GetString() ?? "" : "";

                DateTime? dueOn = null;
                if (el.TryGetProperty("due_on", out var du) && du.ValueKind != JsonValueKind.Null
                    && DateTime.TryParse(du.GetString(), out var ddt))
                    dueOn = ddt;

                var modifiedAt = DateTime.MinValue;
                if (el.TryGetProperty("modified_at", out var ma))
                    DateTime.TryParse(ma.GetString(), out modifiedAt);

                var projects = new List<string>();
                if (el.TryGetProperty("projects", out var projs))
                    foreach (var p in projs.EnumerateArray())
                        if (p.TryGetProperty("name", out var pn))
                            projects.Add(pn.GetString() ?? "");

                tasks.Add(new AsanaTask(gid, name, notes, dueOn, modifiedAt, permalink, projects));
            }

            return tasks;
        }
        catch (Exception ex)
        {
            _lastError = $"Fetch error: {ex.Message}";
            Console.Error.WriteLine($"[Asana:{Name}] Fetch failed: {ex.Message}");
            return null;
        }
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        if (_accessToken.Length > 0 && DateTimeOffset.UtcNow < _tokenExpiresAt)
            return _accessToken;

        await _tokenLock.WaitAsync(ct);
        try
        {
            if (_accessToken.Length > 0 && DateTimeOffset.UtcNow < _tokenExpiresAt)
                return _accessToken;

            var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = _clientId,
                ["client_secret"] = _clientSecret,
                ["refresh_token"] = _refreshToken,
            });
            var resp = await _http.PostAsync("https://app.asana.com/-/oauth_token", form, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                throw new Exception($"Token refresh failed ({(int)resp.StatusCode}): {body}");

            var doc = JsonDocument.Parse(body).RootElement;
            _accessToken = doc.GetProperty("access_token").GetString() ?? throw new Exception("No access_token");
            var expiresIn = doc.TryGetProperty("expires_in", out var exp) ? exp.GetInt32() : 3600;
            _tokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn - 60);
            return _accessToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private async Task<T> GetAsync<T>(string relativeUrl, CancellationToken ct)
    {
        var token = await GetAccessTokenAsync(ct);
        using var req = new HttpRequestMessage(HttpMethod.Get, relativeUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new Exception($"HTTP {(int)resp.StatusCode}: {body}");
        }
        return (await resp.Content.ReadFromJsonAsync<T>(cancellationToken: ct))!;
    }

    private HashSet<string> LoadSeenGids()
    {
        var path = Path.Combine(_stateDir, "seen-gids.json");
        if (!File.Exists(path)) return [];
        try { return JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(path)) ?? []; }
        catch { return []; }
    }

    private void SaveSeenGids(HashSet<string> gids)
    {
        try { File.WriteAllText(Path.Combine(_stateDir, "seen-gids.json"), JsonSerializer.Serialize(gids)); }
        catch { }
    }

    private bool IsConfigured() =>
        !string.IsNullOrWhiteSpace(_clientId) &&
        !string.IsNullOrWhiteSpace(_clientSecret) &&
        !string.IsNullOrWhiteSpace(_refreshToken);
}

public record AsanaTask(
    string Gid,
    string Name,
    string? Notes,
    DateTime? DueOn,
    DateTime ModifiedAt,
    string PermalinkUrl,
    List<string> Projects);
