namespace Promptile.Sdk;

/// <summary>
/// A running instance of a configured data source.
/// </summary>
public interface IDataSourceInstance : IAsyncDisposable
{
    string Id { get; }
    string Name { get; }
    string Type { get; }
    DataSourceConfig Config { get; }

    Task StartAsync(INotificationBus bus, IServiceProvider services, CancellationToken ct);
    Task StopAsync(CancellationToken ct);
    Task<DataSourceStatus> GetStatusAsync();
    Task ResetStateAsync() => Task.CompletedTask;
}
