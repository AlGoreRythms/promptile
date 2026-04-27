using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Assistant.Sdk;
using Microsoft.Extensions.Logging;

namespace Slack;

public class SlackDataSourceInstance : IDataSourceInstance
{
    public string Id => Config.Id;
    public string Name => Config.Name;
    public string Type => "slack";
    public DataSourceConfig Config { get; }

    internal SlackApiClient? ApiClient { get; private set; }

    private INotificationBus? _bus;
    private IInformationStore? _store;
    private ILogger? _logger;
    private ISyncReporter? _sync;
    private CancellationTokenSource? _cts;
    private Task? _pollTask;
    private string? _stateDir;

    // channelId → last seen top-level message ts
    private Dictionary<string, string> _lastSeen = [];
    // "channelId:threadTs" → last seen reply ts
    private Dictionary<string, string> _threadLastSeen = [];
    // userId → display name cache
    private readonly Dictionary<string, string> _userNames = [];

    public SlackDataSourceInstance(DataSourceConfig config)
    {
        Config = config;
    }

    public Task StartAsync(INotificationBus bus, IServiceProvider services, CancellationToken ct)
    {
        _bus = bus;
        _store = services.GetService(typeof(IInformationStore)) as IInformationStore;
        _logger = services.GetService(typeof(ILogger<SlackDataSourceInstance>)) as ILogger;
        _sync = services.GetService(typeof(ISyncReporter)) as ISyncReporter;
        _stateDir = Config.Config.TryGetValue("stateDir", out var sd) ? sd
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".assistant", "datasources", Id);
        Directory.CreateDirectory(_stateDir);

        var token = Config.Config.TryGetValue("botToken", out var t) ? t : "";
        ApiClient = new SlackApiClient(token);

        LoadState();

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _pollTask = Task.Run(() => PollLoopAsync(_cts.Token), _cts.Token);

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _cts?.Cancel();
        if (_pollTask != null)
        {
            try { await _pollTask.WaitAsync(TimeSpan.FromSeconds(5), ct); }
            catch { }
        }
    }

    public async Task<DataSourceStatus> GetStatusAsync()
    {
        if (ApiClient == null) return new DataSourceStatus(false, "Not started");
        try
        {
            var info = await ApiClient.AuthTestAsync();
            if (info == null) return new DataSourceStatus(false, "Auth failed — check bot token");
            return new DataSourceStatus(true, $"{info.Team} (@{info.User})");
        }
        catch (Exception ex)
        {
            return new DataSourceStatus(false, ex.Message);
        }
    }

    public async Task<List<SlackChannel>> GetChannelsAsync(CancellationToken ct = default)
    {
        if (ApiClient == null) return [];
        return await ApiClient.ListChannelsAsync(ct);
    }

    public async Task<List<(SlackChannel Channel, SlackMessage Message)>> GetMessagesAsync(
        string? channelName, int limit, CancellationToken ct = default)
    {
        if (ApiClient == null) return [];

        var channels = await ApiClient.ListChannelsAsync(ct);
        var filtered = string.IsNullOrEmpty(channelName)
            ? channels
            : channels.Where(c => c.Name.Equals(channelName, StringComparison.OrdinalIgnoreCase)).ToList();

        var result = new List<(SlackChannel, SlackMessage)>();
        foreach (var ch in filtered)
        {
            var msgs = await ApiClient.GetMessagesAsync(ch.Id, limit, ct: ct);
            foreach (var msg in msgs)
                result.Add((ch, msg));
        }

        return result.OrderByDescending(x => x.Item2.Ts).Take(limit).ToList();
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        var intervalSeconds = 60;
        if (Config.Config.TryGetValue("pollIntervalSeconds", out var ps)
            && int.TryParse(ps, out var parsed))
            intervalSeconds = parsed;

        // Initial delay so startup isn't noisy
        await Task.Delay(TimeSpan.FromSeconds(10), ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PollAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "[Slack:{Name}] Poll error", Name);
            }

            await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), ct);
        }
    }

    private async Task PollAsync(CancellationToken ct)
    {
        if (ApiClient == null) return;

        _sync?.Begin(Id, $"{Name} · Slack", "slack");

        try
        {
            var channels = await ApiClient.ListChannelsAsync(ct);
            var watchedNames = GetWatchedChannelNames();
            var toWatch = watchedNames.Any()
                ? channels.Where(c => watchedNames.Contains(c.Name)).ToList()
                : channels;

            var notifyOn = Config.Config.TryGetValue("notifyOn", out var n) ? n : "all";
            var botUserId = "";
            var newCount = 0;

            for (var ci = 0; ci < toWatch.Count; ci++)
            {
                var ch = toWatch[ci];
                _sync?.Progress(Id, $"#{ch.Name} ({ci + 1}/{toWatch.Count})", newCount);

                _lastSeen.TryGetValue(ch.Id, out var oldest);
                List<SlackMessage> messages;
                if (oldest == null)
                {
                    var backfillTs = ((DateTimeOffset)DateTime.UtcNow.AddDays(-90))
                        .ToUnixTimeSeconds().ToString();
                    messages = await ApiClient.GetMessagesSinceAsync(ch.Id, backfillTs, ct);
                }
                else
                {
                    messages = await ApiClient.GetMessagesSinceAsync(ch.Id, oldest, ct);
                }

                var threadParentsThisPoll = new HashSet<string>();

                foreach (var msg in messages)
                {
                    if (oldest != null && string.Compare(msg.Ts, oldest, StringComparison.Ordinal) <= 0)
                        continue;

                    var shouldNotify = notifyOn switch
                    {
                        "mentions" => msg.Text.Contains($"<@{botUserId}>", StringComparison.OrdinalIgnoreCase),
                        "dms" => false,
                        _ => true,
                    };

                    var userName = await GetUserNameAsync(msg.UserId, ct);
                    await WriteToStoreAsync(ch.Name, userName, msg);
                    newCount++;

                    if (shouldNotify)
                    {
                        var preview = msg.Text.Length > 80 ? msg.Text[..80] + "…" : msg.Text;
                        _bus?.Publish(new AssistantNotification(
                            PluginId: Id,
                            Title: $"{Name} · #{ch.Name}",
                            Body: $"{userName}: {preview}",
                            Timestamp: DateTimeOffset.UtcNow,
                            ActionUrl: null,
                            Payload: new { channelId = ch.Id, channelName = ch.Name, msg.Ts }
                        ));
                    }

                    if (!_lastSeen.TryGetValue(ch.Id, out var current)
                        || string.Compare(msg.Ts, current, StringComparison.Ordinal) > 0)
                        _lastSeen[ch.Id] = msg.Ts;

                    if (msg.ReplyCount > 0)
                        threadParentsThisPoll.Add(msg.Ts);
                }

                foreach (var key in _threadLastSeen.Keys)
                {
                    if (key.StartsWith(ch.Id + ":"))
                        threadParentsThisPoll.Add(key[(ch.Id.Length + 1)..]);
                }

                // Catch threads missed on first scrape: re-check the most recent 200 messages
                // for any that now have replies but were never tracked
                var recentMessages = await ApiClient.GetMessagesAsync(ch.Id, 200, ct: ct);
                foreach (var recentMsg in recentMessages)
                {
                    if (recentMsg.ReplyCount > 0 && !_threadLastSeen.ContainsKey($"{ch.Id}:{recentMsg.Ts}"))
                        threadParentsThisPoll.Add(recentMsg.Ts);
                }

                foreach (var threadTs in threadParentsThisPoll)
                {
                    var threadKey = $"{ch.Id}:{threadTs}";
                    _threadLastSeen.TryGetValue(threadKey, out var lastReplyTs);

                    var replies = await ApiClient.GetThreadRepliesAsync(ch.Id, threadTs, lastReplyTs, ct);

                    foreach (var reply in replies)
                    {
                        if (lastReplyTs != null && string.Compare(reply.Ts, lastReplyTs, StringComparison.Ordinal) <= 0)
                            continue;

                        var replyUser = await GetUserNameAsync(reply.UserId, ct);
                        await WriteReplyToStoreAsync(ch.Name, replyUser, reply);
                        newCount++;

                        if (!_threadLastSeen.TryGetValue(threadKey, out var cur)
                            || string.Compare(reply.Ts, cur, StringComparison.Ordinal) > 0)
                            _threadLastSeen[threadKey] = reply.Ts;
                    }
                }

                SaveChannelState(ch.Id);
            }

            _sync?.Complete(Id, newCount);

            if (newCount > 0)
            {
                _bus?.Publish(new AssistantNotification(
                    PluginId: Id,
                    Title: $"{Name} · Slack",
                    Body: $"{newCount} new message{(newCount == 1 ? "" : "s")}",
                    Timestamp: DateTimeOffset.UtcNow,
                    Category: "sync-summary"
                ));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _sync?.Fail(Id, ex.Message);
            throw;
        }
    }

    public Task ResetStateAsync()
    {
        _lastSeen.Clear();
        _threadLastSeen.Clear();
        if (_stateDir != null)
        {
            foreach (var f in Directory.GetFiles(_stateDir, "ch-*.json"))
                File.Delete(f);
        }
        return Task.CompletedTask;
    }

    private string FormatUserLabel(string userName)
    {
        Config.Config.TryGetValue("myUsername", out var myName);
        if (!string.IsNullOrWhiteSpace(myName)
            && userName.Equals(myName.Trim(), StringComparison.OrdinalIgnoreCase))
            return $"{userName} (you)";
        return userName;
    }

    private async Task WriteToStoreAsync(string channelName, string userName, SlackMessage msg)
    {
        if (_store == null) return;

        var date = TsToDateTime(msg.Ts);
        var filename = $"{date:yyyy-MM-dd}.md";
        var time = date.ToString("HH:mm");
        var text = msg.Text.Length > 0 ? msg.Text : "(no text)";

        var entry = new StringBuilder();
        entry.AppendLine($"## {date:yyyy-MM-dd} {time} · #{channelName} · {FormatUserLabel(userName)}");
        entry.AppendLine(text);
        entry.AppendLine();
        entry.AppendLine("---");
        entry.AppendLine();

        var content = entry.ToString();
        await _store.AppendDataAsync(Name, "Slack", channelName, filename, content);
    }

    private async Task WriteReplyToStoreAsync(string channelName, string userName, SlackMessage reply)
    {
        if (_store == null) return;

        var date = TsToDateTime(reply.Ts);
        var filename = $"{date:yyyy-MM-dd}.md";
        var time = date.ToString("HH:mm");
        var text = reply.Text.Length > 0 ? reply.Text : "(no text)";

        var entry = new StringBuilder();
        entry.AppendLine($"### ↳ {date:yyyy-MM-dd} {time} · #{channelName} · {FormatUserLabel(userName)} (thread reply)");
        entry.AppendLine(text);
        entry.AppendLine();
        entry.AppendLine("---");
        entry.AppendLine();

        var content = entry.ToString();
        await _store.AppendDataAsync(Name, "Slack", channelName, filename, content);
    }

    private static DateTime TsToDateTime(string ts)
    {
        if (double.TryParse(ts.Split('.')[0], System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var unix))
            return DateTimeOffset.FromUnixTimeSeconds((long)unix).LocalDateTime;
        return DateTime.Now;
    }

    private async Task<string> GetUserNameAsync(string userId, CancellationToken ct)
    {
        if (_userNames.TryGetValue(userId, out var name)) return name;
        name = await ApiClient!.GetUserNameAsync(userId, ct);
        _userNames[userId] = name;
        return name;
    }

    private HashSet<string> GetWatchedChannelNames()
    {
        if (!Config.Config.TryGetValue("channels", out var raw) || string.IsNullOrWhiteSpace(raw))
            return [];
        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim().TrimStart('#'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private void LoadState()
    {
        if (_stateDir == null) return;
        // Legacy single-file migration
        var legacy = Path.Combine(_stateDir, "state.json");
        if (File.Exists(legacy))
        {
            try { _lastSeen = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(legacy)) ?? []; } catch { }
            foreach (var (chId, ts) in _lastSeen)
                SaveChannelState(chId);
            File.Delete(legacy);
        }
        var legacyThread = Path.Combine(_stateDir, "thread_state.json");
        if (File.Exists(legacyThread))
        {
            try { _threadLastSeen = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(legacyThread)) ?? []; } catch { }
            File.Delete(legacyThread);
        }

        // Load per-channel state files
        foreach (var file in Directory.GetFiles(_stateDir, "ch-*.json"))
        {
            try
            {
                var entry = JsonSerializer.Deserialize<ChannelState>(File.ReadAllText(file));
                if (entry == null) continue;
                if (entry.LastTs != null) _lastSeen[entry.ChannelId] = entry.LastTs;
                foreach (var (k, v) in entry.ThreadLastTs)
                    _threadLastSeen[k] = v;
            }
            catch { }
        }
    }

    private void SaveChannelState(string channelId)
    {
        if (_stateDir == null) return;
        try
        {
            _lastSeen.TryGetValue(channelId, out var lastTs);
            var threadEntries = _threadLastSeen
                .Where(kv => kv.Key.StartsWith(channelId + ":"))
                .ToDictionary(kv => kv.Key, kv => kv.Value);
            var state = new ChannelState(channelId, lastTs, threadEntries);
            File.WriteAllText(
                Path.Combine(_stateDir, $"ch-{channelId}.json"),
                JsonSerializer.Serialize(state));
        }
        catch { }
    }

    private record ChannelState(
        string ChannelId,
        string? LastTs,
        Dictionary<string, string> ThreadLastTs);

    public ValueTask DisposeAsync()
    {
        _cts?.Dispose();
        return ValueTask.CompletedTask;
    }
}
