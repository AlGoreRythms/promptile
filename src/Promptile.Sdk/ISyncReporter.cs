namespace Promptile.Sdk;

public interface ISyncReporter
{
    void Begin(string instanceId, string name, string type);
    void Progress(string instanceId, string message, int itemsSoFar = 0);
    void Complete(string instanceId, int totalItems);
    void Fail(string instanceId, string error);
    IReadOnlyList<SyncEntry> GetAll();
}

public record SyncEntry(
    string InstanceId,
    string Name,
    string Type,
    bool IsSyncing,
    string? CurrentMessage,
    int ItemsLastRun,
    DateTimeOffset? LastSyncAt,
    string? LastError
);
