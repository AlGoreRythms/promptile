using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Promptile.Sdk;

namespace Rss;

public class RssDataSourceInstance : IDataSourceInstance
{
    public string Id => Config.Id;
    public string Name => Config.Name;
    public string Type => "rss";
    public DataSourceConfig Config { get; }

    private readonly List<string> _feedUrls;
    private readonly int _maxItems;
    private readonly int _pollSeconds;
    private readonly string _stateDir;

    private IInformationStore? _store;
    private INotificationBus? _bus;
    private ISyncReporter? _sync;
    private CancellationTokenSource? _cts;
    private Task? _pollTask;

    private Dictionary<string, DateTimeOffset> _lastSeen = new();
    private Dictionary<string, string> _feedTitles = new();
    private Dictionary<string, string> _lastErrors = new();

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public RssDataSourceInstance(DataSourceConfig config)
    {
        Config = config;
        var raw = config.Config.GetValueOrDefault("feedUrls", "");
        _feedUrls = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                       .Where(u => !string.IsNullOrEmpty(u))
                       .ToList();
        _maxItems = int.TryParse(config.Config.GetValueOrDefault("maxItems"), out var m) ? m : 20;
        _pollSeconds = int.TryParse(config.Config.GetValueOrDefault("pollIntervalSeconds"), out var s) ? s : 900;
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
            try { await _pollTask.WaitAsync(TimeSpan.FromSeconds(5), ct); } catch { }
        }
    }

    public Task ResetStateAsync()
    {
        _lastSeen.Clear();
        _feedTitles.Clear();
        _lastErrors.Clear();
        var path = Path.Combine(_stateDir, "state.json");
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    public Task<DataSourceStatus> GetStatusAsync()
    {
        if (_feedUrls.Count == 0)
            return Task.FromResult(new DataSourceStatus(false, "No feed URLs configured"));

        if (_lastErrors.Count > 0)
        {
            var firstError = _lastErrors.First();
            return Task.FromResult(new DataSourceStatus(false, $"{firstError.Key} — {firstError.Value}"));
        }

        DateTimeOffset latestSeen = _lastSeen.Count > 0 ? _lastSeen.Values.Max() : DateTimeOffset.MinValue;
        var suffix = latestSeen == DateTimeOffset.MinValue
            ? "waiting for first sync"
            : $"last sync {latestSeen.LocalDateTime:HH:mm}";

        string label;
        if (_feedUrls.Count == 1)
        {
            var url = _feedUrls[0];
            var title = _feedTitles.GetValueOrDefault(url);
            label = title != null ? $"{title} — {url} — {suffix}" : $"{url} — {suffix}";
        }
        else
        {
            label = $"{_feedUrls.Count} feeds — {suffix}";
        }

        return Task.FromResult(new DataSourceStatus(true, label));
    }

    public ValueTask DisposeAsync()
    {
        _cts?.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(10), ct);
        while (!ct.IsCancellationRequested)
        {
            try { await SyncAsync(ct); } catch (OperationCanceledException) { break; } catch { }
            await Task.Delay(TimeSpan.FromSeconds(_pollSeconds), ct).ConfigureAwait(false);
        }
    }

    private async Task SyncAsync(CancellationToken ct)
    {
        if (_feedUrls.Count == 0 || _store == null) return;

        _sync?.Begin(Id, $"{Name} · RSS", "rss");

        int totalNew = 0;
        try
        {
            foreach (var url in _feedUrls)
            {
                try
                {
                    var xml = await _http.GetStringAsync(url, ct);
                    var doc = XDocument.Parse(xml);

                    var (feedTitle, items) = ParseFeed(doc);
                    _feedTitles[url] = feedTitle;
                    _lastErrors.Remove(url);

                    var cutoff = _lastSeen.TryGetValue(url, out var ls) && ls != DateTimeOffset.MinValue
                        ? ls
                        : DateTimeOffset.UtcNow.AddDays(-7);

                    var newItems = items
                        .Where(i => i.PublishedAt > cutoff && i.PublishedAt != DateTimeOffset.MinValue)
                        .OrderBy(i => i.PublishedAt)
                        .Take(_maxItems)
                        .ToList();

                    foreach (var item in newItems)
                    {
                        var filename = $"{item.PublishedAt.LocalDateTime:yyyy-MM-dd}.md";
                        await _store.AppendDataAsync(Name, "RSS", feedTitle, filename, FormatEntry(item, feedTitle));
                    }

                    if (newItems.Count > 0)
                    {
                        var latest = newItems.Max(i => i.PublishedAt);
                        if (!_lastSeen.TryGetValue(url, out var prev) || latest > prev)
                            _lastSeen[url] = latest;
                        totalNew += newItems.Count;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _lastErrors[url] = ex.Message.Length > 80 ? ex.Message[..80] : ex.Message;
                }
            }

            if (totalNew > 0)
            {
                SaveState();
                _bus?.Publish(new AssistantNotification(
                    PluginId: Config.Id,
                    Title: $"{Name} · RSS",
                    Body: $"{totalNew} new item{(totalNew == 1 ? "" : "s")}",
                    Timestamp: DateTimeOffset.UtcNow,
                    Category: "sync-summary"));
            }

            _sync?.Complete(Id, totalNew);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _sync?.Fail(Id, ex.Message);
            throw;
        }
    }

    private (string FeedTitle, List<FeedItem> Items) ParseFeed(XDocument doc)
    {
        XNamespace atom = "http://www.w3.org/2005/Atom";

        if (doc.Root?.Name.LocalName == "feed")
        {
            var feedTitle = doc.Root.Element(atom + "title")?.Value ?? Name;
            var items = doc.Root.Elements(atom + "entry").Select(e =>
            {
                var title = e.Element(atom + "title")?.Value ?? "";
                var link = e.Element(atom + "link")?.Attribute("href")?.Value
                    ?? e.Elements(atom + "link").FirstOrDefault(l => l.Attribute("rel")?.Value != "alternate")?.Attribute("href")?.Value
                    ?? "";
                var summary = e.Element(atom + "summary")?.Value
                    ?? e.Element(atom + "content")?.Value ?? "";
                var pubStr = e.Element(atom + "published")?.Value
                    ?? e.Element(atom + "updated")?.Value ?? "";
                DateTimeOffset.TryParse(pubStr, out var pub);
                return new FeedItem(title.Trim(), link.Trim(), StripHtml(summary), pub);
            }).ToList();
            return (feedTitle, items);
        }
        else
        {
            var channel = doc.Root?.Element("channel") ?? doc.Root;
            var feedTitle = channel?.Element("title")?.Value ?? Name;
            var items = (channel?.Elements("item") ?? []).Select(e =>
            {
                var title = e.Element("title")?.Value ?? "";
                var link = e.Element("link")?.Value?.Trim()
                    ?? e.Elements().FirstOrDefault(x => x.Name.LocalName == "link")?.Value?.Trim() ?? "";
                var description = e.Element("description")?.Value ?? "";
                var pubStr = e.Element("pubDate")?.Value
                    ?? e.Elements().FirstOrDefault(x => x.Name.LocalName == "date")?.Value ?? "";
                DateTimeOffset pub = DateTimeOffset.MinValue;
                if (!string.IsNullOrEmpty(pubStr))
                    DateTimeOffset.TryParse(pubStr, out pub);
                return new FeedItem(title.Trim(), link.Trim(), StripHtml(description), pub);
            }).ToList();
            return (feedTitle, items);
        }
    }

    private static string FormatEntry(FeedItem item, string feedTitle)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"## {item.PublishedAt.LocalDateTime:HH:mm} · {item.Title}");
        sb.AppendLine($"**Source**: {feedTitle}");
        if (!string.IsNullOrEmpty(item.Link))
            sb.AppendLine($"**Link**: {item.Link}");
        if (!string.IsNullOrEmpty(item.Summary))
        {
            sb.AppendLine();
            var text = item.Summary.Length > 500 ? item.Summary[..500] + "…" : item.Summary;
            sb.AppendLine(text);
        }
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
        return sb.ToString();
    }

    private static string StripHtml(string html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        var stripped = Regex.Replace(html, "<[^>]+>", " ");
        stripped = Regex.Replace(stripped, @"\s{2,}", " ");
        return WebUtility.HtmlDecode(stripped).Trim();
    }

    private void LoadState()
    {
        try
        {
            var path = Path.Combine(_stateDir, "state.json");
            if (!File.Exists(path)) return;
            var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("lastSeen", out var ls) && ls.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in ls.EnumerateObject())
                {
                    if (DateTimeOffset.TryParse(prop.Value.GetString(), out var dt))
                        _lastSeen[prop.Name] = dt;
                }
            }
        }
        catch { }
    }

    private void SaveState()
    {
        try
        {
            var dict = _lastSeen.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToString("O"));
            File.WriteAllText(
                Path.Combine(_stateDir, "state.json"),
                JsonSerializer.Serialize(new { lastSeen = dict }));
        }
        catch { }
    }

    private record FeedItem(string Title, string Link, string Summary, DateTimeOffset PublishedAt);
}
