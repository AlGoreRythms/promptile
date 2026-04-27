using System.Collections.Concurrent;
using Assistant.Sdk;

namespace Obsidian;

public class ObsidianDataSourceInstance : IDataSourceInstance
{
    public string Id => Config.Id;
    public string Name => Config.Name;
    public string Type => "obsidian";
    public DataSourceConfig Config { get; private set; }

    private IInformationStore? _store;
    private INotificationBus? _bus;
    private ISyncReporter? _sync;
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _cts;
    private Task? _scanTask;
    private readonly ConcurrentQueue<string> _pendingPaths = new();
    private DateTime _lastScan = DateTime.MinValue;
    private bool _connected;

    public ObsidianDataSourceInstance(DataSourceConfig config)
    {
        Config = config;
    }

    public Task StartAsync(INotificationBus bus, IServiceProvider services, CancellationToken ct)
    {
        var vaultPath = GetVaultPath();
        if (string.IsNullOrEmpty(vaultPath) || !Directory.Exists(vaultPath))
            return Task.CompletedTask;

        _store = services.GetService(typeof(IInformationStore)) as IInformationStore;
        _sync = services.GetService(typeof(ISyncReporter)) as ISyncReporter;
        _bus = bus;
        _connected = true;

        // FileSystemWatcher for real-time events
        _watcher = new FileSystemWatcher(vaultPath, "*.md")
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
        };
        _watcher.Changed += (_, e) => _pendingPaths.Enqueue(e.FullPath);
        _watcher.Created += (_, e) => _pendingPaths.Enqueue(e.FullPath);
        _watcher.EnableRaisingEvents = true;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _scanTask = ScanLoopAsync(_cts.Token);

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _watcher?.Dispose();
        _watcher = null;
        _cts?.Cancel();
        if (_scanTask != null)
            try { await _scanTask; } catch { }
        _connected = false;
    }

    public Task ResetStateAsync()
    {
        _lastScan = DateTime.MinValue;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _watcher?.Dispose();
        return ValueTask.CompletedTask;
    }

    public Task<DataSourceStatus> GetStatusAsync()
    {
        var vaultPath = GetVaultPath();
        if (string.IsNullOrEmpty(vaultPath))
            return Task.FromResult(new DataSourceStatus(false, "Vault path not configured"));
        if (!Directory.Exists(vaultPath))
            return Task.FromResult(new DataSourceStatus(false, $"Vault path not found: {vaultPath}"));
        return Task.FromResult(new DataSourceStatus(_connected, $"Watching {vaultPath}"));
    }

    private async Task ScanLoopAsync(CancellationToken ct)
    {
        // Initial full scan
        await FullScanAsync(ct);

        var interval = Config.Config.TryGetValue("pollIntervalSeconds", out var s) && int.TryParse(s, out var sec)
            ? TimeSpan.FromSeconds(sec) : TimeSpan.FromMinutes(5);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Drain pending watcher events
                var processed = new HashSet<string>();
                while (_pendingPaths.TryDequeue(out var path))
                {
                    if (processed.Add(path))
                    {
                        try { await IngestFileAsync(path, ct); }
                        catch { }
                    }
                }

                if (processed.Count > 0)
                    PublishSyncSummary(processed.Count);

                await Task.Delay(interval, ct);
                await FullScanAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch { }
        }
    }

    private async Task FullScanAsync(CancellationToken ct)
    {
        var vaultPath = GetVaultPath();
        if (string.IsNullOrEmpty(vaultPath) || _store == null) return;

        _sync?.Begin(Id, $"{Name} · Obsidian", "obsidian");

        try
        {
            var scanFrom = _lastScan;
            _lastScan = DateTime.UtcNow;

            var excludeFolders = GetExcludedFolders();
            var files = Directory.GetFiles(vaultPath, "*.md", SearchOption.AllDirectories)
                .Where(f => !excludeFolders.Any(ex => f.Contains(Path.DirectorySeparatorChar + ex + Path.DirectorySeparatorChar)
                                                       || f.Contains(Path.DirectorySeparatorChar + ex + Path.AltDirectorySeparatorChar)))
                .Where(f => File.GetLastWriteTimeUtc(f) > scanFrom)
                .ToList();

            for (var i = 0; i < files.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                _sync?.Progress(Id, $"Ingesting note {i + 1}/{files.Count}", i);
                try { await IngestFileAsync(files[i], ct); }
                catch { }
            }

            if (files.Count > 0)
                PublishSyncSummary(files.Count);

            _sync?.Complete(Id, files.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _sync?.Fail(Id, ex.Message);
            throw;
        }
    }

    private async Task IngestFileAsync(string filePath, CancellationToken ct)
    {
        if (_store == null) return;

        var vaultPath = GetVaultPath();
        if (string.IsNullOrEmpty(vaultPath) || !filePath.StartsWith(vaultPath)) return;

        var relative = filePath[vaultPath.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        // Use the relative path as slug (replacing separators with __)
        var slug = relative.Replace(Path.DirectorySeparatorChar, '_').Replace(Path.AltDirectorySeparatorChar, '_');

        await Task.Delay(200, ct); // brief delay so file write completes
        var content = await File.ReadAllTextAsync(filePath, ct);
        // Overwrite: notes change in place, so we replace rather than append
        await _store.WriteDataAsync(Name, "Obsidian", slug, content);
    }

    private void PublishSyncSummary(int count)
    {
        _bus?.Publish(new AssistantNotification(
            PluginId: "obsidian",
            Title: $"Obsidian — {Name}",
            Body: $"{count} note{(count != 1 ? "s" : "")} synced",
            Timestamp: DateTimeOffset.UtcNow,
            Category: "sync-summary"));
    }

    private string GetVaultPath()
    {
        Config.Config.TryGetValue("vaultPath", out var path);
        if (string.IsNullOrWhiteSpace(path)) return "";
        return path.Replace("~", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }

    private HashSet<string> GetExcludedFolders()
    {
        Config.Config.TryGetValue("excludeFolders", out var raw);
        if (string.IsNullOrWhiteSpace(raw)) return [".obsidian"];
        var set = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                     .ToHashSet();
        set.Add(".obsidian"); // always exclude
        return set;
    }
}
