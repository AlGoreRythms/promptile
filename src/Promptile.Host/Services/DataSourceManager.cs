using System.Text.Json;
using Promptile.Sdk;
using Microsoft.Extensions.Logging;

namespace Promptile.Host.Services;

public class DataSourceManager : IDataSourceManager, IAsyncDisposable
{
    private readonly DataSourcesService _sourcesService;
    private readonly INotificationBus _bus;
    private readonly IServiceProvider _services;
    private readonly ILogger<DataSourceManager> _logger;
    private readonly List<IDataSourceProvider> _providers;
    private readonly Dictionary<string, IDataSourceInstance> _running = [];
    private readonly SemaphoreSlim _lock = new(1, 1);

    public DataSourceManager(
        DataSourcesService sourcesService,
        INotificationBus bus,
        IServiceProvider services,
        ILogger<DataSourceManager> logger,
        IEnumerable<IDataSourceProvider> providers)
    {
        _sourcesService = sourcesService;
        _bus = bus;
        _services = services;
        _logger = logger;
        _providers = providers.ToList();
    }

    public IReadOnlyList<IDataSourceProvider> GetProviders() => _providers;

    public IReadOnlyList<IDataSourceInstance> GetInstances(string? type = null) =>
        _running.Values
            .Where(i => type == null || i.Type == type)
            .ToList();

    public IDataSourceInstance? GetInstance(string name, string? type = null) =>
        _running.Values.FirstOrDefault(i =>
            string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase)
            && (type == null || i.Type == type));

    public async Task StartAllAsync(CancellationToken ct = default)
    {
        var configs = await _sourcesService.LoadAsync();
        foreach (var config in configs.Where(c => c.Enabled))
            await StartInstanceAsync(config, ct);
    }

    public async Task ReloadAsync()
    {
        await _lock.WaitAsync();
        try
        {
            var configs = await _sourcesService.LoadAsync();
            var enabledById = configs.Where(c => c.Enabled)
                .ToDictionary(c => c.Id);

            // Stop instances that were removed or changed
            foreach (var (id, instance) in _running.ToList())
            {
                if (!enabledById.TryGetValue(id, out var newConfig)
                    || !ConfigsEqual(instance.Config, newConfig))
                {
                    _logger.LogInformation("Stopping data source {Name}", instance.Name);
                    await instance.StopAsync(CancellationToken.None);
                    await instance.DisposeAsync();
                    _running.Remove(id);
                }
            }

            // Start new/changed instances
            foreach (var config in enabledById.Values)
            {
                if (!_running.ContainsKey(config.Id))
                    await StartInstanceAsync(config, CancellationToken.None);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task ResetInstanceAsync(string id)
    {
        await _lock.WaitAsync();
        try
        {
            if (_running.TryGetValue(id, out var instance))
                await instance.ResetStateAsync();
        }
        finally { _lock.Release(); }

        await ReloadAsync();
    }

    public async Task ResetAllAsync()
    {
        await _lock.WaitAsync();
        try
        {
            foreach (var instance in _running.Values)
                await instance.ResetStateAsync();
        }
        finally { _lock.Release(); }

        await ReloadAsync();
    }

    private async Task StartInstanceAsync(DataSourceConfig config, CancellationToken ct)
    {
        var provider = _providers.FirstOrDefault(p => p.Type == config.Type);
        if (provider == null)
        {
            _logger.LogWarning("No provider for type '{Type}'", config.Type);
            return;
        }

        try
        {
            var instance = provider.CreateInstance(config);
            await instance.StartAsync(_bus, _services, ct);
            _running[config.Id] = instance;
            _logger.LogInformation("Started data source {Name} ({Type})", config.Name, config.Type);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start data source {Name}", config.Name);
        }
    }

    private static bool ConfigsEqual(DataSourceConfig a, DataSourceConfig b) =>
        a.Type == b.Type && a.Name == b.Name && a.Enabled == b.Enabled
        && JsonSerializer.Serialize(a.Config) == JsonSerializer.Serialize(b.Config);

    public async ValueTask DisposeAsync()
    {
        foreach (var instance in _running.Values)
        {
            try
            {
                await instance.StopAsync(CancellationToken.None);
                await instance.DisposeAsync();
            }
            catch { }
        }
        _running.Clear();
    }
}
