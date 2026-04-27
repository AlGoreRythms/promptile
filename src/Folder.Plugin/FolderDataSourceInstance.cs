using Assistant.Sdk;

namespace Folder;

public class FolderDataSourceInstance : IDataSourceInstance
{
    public string Id => Config.Id;
    public string Name => Config.Name;
    public string Type => "folder";
    public DataSourceConfig Config { get; }

    public string FolderPath { get; }
    public string OpenCodeCommand { get; }

    public FolderDataSourceInstance(DataSourceConfig config)
    {
        Config = config;
        FolderPath = config.Config.GetValueOrDefault("path", "").Trim();
        OpenCodeCommand = config.Config.GetValueOrDefault("opencodeCommand") is { Length: > 0 } cmd
            ? cmd.Trim() : "opencode";
    }

    public Task StartAsync(INotificationBus bus, IServiceProvider services, CancellationToken ct)
    {
        var sync = services.GetService(typeof(ISyncReporter)) as ISyncReporter;
        if (sync != null)
        {
            sync.Begin(Id, $"{Name} · Folder", "folder");
            if (Directory.Exists(FolderPath))
                sync.Complete(Id, 0);
            else
                sync.Fail(Id, $"Path not found: {FolderPath}");
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    public Task ResetStateAsync() => Task.CompletedTask;

    public Task<DataSourceStatus> GetStatusAsync()
    {
        if (string.IsNullOrWhiteSpace(FolderPath))
            return Task.FromResult(new DataSourceStatus(false, "No path configured"));

        return Directory.Exists(FolderPath)
            ? Task.FromResult(new DataSourceStatus(true, FolderPath))
            : Task.FromResult(new DataSourceStatus(false, $"Not found: {FolderPath}"));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
