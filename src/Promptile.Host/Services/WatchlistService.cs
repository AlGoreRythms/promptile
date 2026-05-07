using System.Text.RegularExpressions;
using Promptile.Sdk;
using Microsoft.Extensions.Logging;

namespace Promptile.Host.Services;

public class WatchlistService : IHostedService
{
    private readonly INotificationBus _bus;
    private readonly IInformationStore _store;
    private readonly SettingsService _settings;
    private readonly ILogger<WatchlistService> _logger;

    private DateTime _lastScan = DateTime.MinValue;

    public WatchlistService(
        INotificationBus bus,
        IInformationStore store,
        SettingsService settings,
        ILogger<WatchlistService> logger)
    {
        _bus = bus;
        _store = store;
        _settings = settings;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _lastScan = DateTime.UtcNow;
        _bus.Subscribe(OnNotification);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    private void OnNotification(AssistantNotification notification)
    {
        if (notification.Category != "sync-summary") return;
        _ = Task.Run(() => ScanAsync());
    }

    private async Task ScanAsync()
    {
        var keywords = _settings.LoadSync().Watchlist;
        if (keywords.Count == 0) return;

        var scanFrom = _lastScan;
        _lastScan = DateTime.UtcNow;

        var sources = _store.ListSources();
        foreach (var (sourceName, types) in sources)
        {
            foreach (var sourceType in types)
            {
                var dataPath = _store.GetDataPath(sourceName, sourceType);
                if (!Directory.Exists(dataPath)) continue;

                var files = Directory.GetFiles(dataPath, "*.md")
                    .Where(f => File.GetLastWriteTimeUtc(f) > scanFrom);

                foreach (var file in files)
                {
                    string content;
                    try { content = await File.ReadAllTextAsync(file); }
                    catch { continue; }

                    foreach (var kw in keywords)
                    {
                        if (!IsMatch(content, kw)) continue;

                        _logger.LogInformation("Watchlist hit: {Keyword} in {Source}/{Type}/{File}",
                            kw, sourceName, sourceType, Path.GetFileName(file));

                        _bus.Publish(new AssistantNotification(
                            PluginId: "host",
                            Title: $"Watchlist: {kw}",
                            Body: $"Mentioned in {sourceName} · {sourceType}",
                            Timestamp: DateTimeOffset.UtcNow,
                            Category: "watchlist-alert"));

                        break; // one notification per file per scan is enough
                    }
                }
            }
        }
    }

    private static bool IsMatch(string content, string keyword)
    {
        if (keyword.StartsWith('/') && keyword.EndsWith('/') && keyword.Length > 2)
        {
            var pattern = keyword[1..^1];
            try { return Regex.IsMatch(content, pattern, RegexOptions.IgnoreCase); }
            catch { return false; }
        }
        return content.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }
}
