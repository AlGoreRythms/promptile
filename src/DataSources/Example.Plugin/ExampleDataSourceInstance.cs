// This is a minimal example IDataSourceInstance showing the expected lifecycle.
// Replace the SyncAsync method body with real API calls for your data source.
using Promptile.Sdk;

namespace ExamplePlugin;

public class ExampleDataSourceInstance : IDataSourceInstance
{
    public string Id => Config.Id;
    public string Name => Config.Name;
    public string Type => "example";
    public DataSourceConfig Config { get; private set; }

    private IInformationStore? _store;
    private ISyncReporter? _sync;
    private CancellationTokenSource? _cts;
    private Task? _pollTask;
    private bool _connected;

    // State directory for this instance: ~/.promptile/datasources/{id}/
    private readonly string _stateDir;

    public ExampleDataSourceInstance(DataSourceConfig config)
    {
        Config = config;
        _stateDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".promptile", "datasources", config.Id);
        Directory.CreateDirectory(_stateDir);
    }

    // Called when the source is enabled. Resolve services from the DI container and start polling.
    public Task StartAsync(INotificationBus bus, IServiceProvider services, CancellationToken ct)
    {
        _store = (IInformationStore)services.GetService(typeof(IInformationStore))!;
        _sync  = (ISyncReporter)services.GetService(typeof(ISyncReporter))!;
        _cts   = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _pollTask = PollLoopAsync(bus, _cts.Token);
        return Task.CompletedTask;
    }

    // Called when the source is disabled or the app is shutting down.
    public async Task StopAsync(CancellationToken ct)
    {
        _cts?.Cancel();
        if (_pollTask != null)
            await _pollTask.ConfigureAwait(false);
    }

    // Returns the current connection status shown in the Data Sources page.
    public Task<DataSourceStatus> GetStatusAsync() =>
        Task.FromResult(_connected
            ? new DataSourceStatus(true, "Connected")
            : new DataSourceStatus(false, "Not connected"));

    // Called by the host when the instance is being disposed.
    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        if (_pollTask != null)
            await _pollTask.ConfigureAwait(false);
        _cts?.Dispose();
    }

    // Clears any synced state so the next poll re-ingests everything from scratch.
    public Task ResetStateAsync()
    {
        var seenPath = Path.Combine(_stateDir, "seen-keys.json");
        if (File.Exists(seenPath)) File.Delete(seenPath);
        _connected = false;
        return Task.CompletedTask;
    }

    private async Task PollLoopAsync(INotificationBus bus, CancellationToken ct)
    {
        var intervalSeconds = int.TryParse(
            Config.Config.GetValueOrDefault("pollIntervalSeconds"), out var s) ? s : 300;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await SyncAsync(bus, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                // Log errors but keep polling
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    // Replace this with real API calls that fetch new data and write it to the information store.
    private async Task SyncAsync(INotificationBus bus, CancellationToken ct)
    {
        _sync?.Begin(Id, Name, Type);

        var url = Config.Config.GetValueOrDefault("feedUrl", "");
        if (string.IsNullOrEmpty(url))
        {
            _sync?.Fail(Id, "No feed URL configured");
            return;
        }

        // Example: fetch content from a URL and write it to the information store.
        using var http = new HttpClient();
        http.Timeout = TimeSpan.FromSeconds(30);
        var content = await http.GetStringAsync(url, ct);

        var markdown = $"# {Name}\n\nFetched at {DateTime.UtcNow:O}\n\n```\n{content[..Math.Min(content.Length, 500)]}\n```";
        await _store!.AppendDataAsync(Name, Type, $"{DateTime.UtcNow:yyyy-MM-dd}.md", markdown);

        // Publish a notification for new items.
        bus.Publish(new AssistantNotification(
            PluginId: Id,
            Title: $"{Name} updated",
            Body: $"Fetched new content from {url}",
            Timestamp: DateTimeOffset.UtcNow));

        _connected = true;
        _sync?.Complete(Id, 1);
    }
}
