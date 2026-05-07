using System.Collections.Concurrent;
using Promptile.Sdk;

namespace Promptile.Host.Services;

public class SyncStatusService : ISyncReporter
{
    private readonly ConcurrentDictionary<string, SyncEntryMutable> _entries = new();

    public void Begin(string instanceId, string name, string type)
    {
        var e = _entries.GetOrAdd(instanceId, _ => new SyncEntryMutable(instanceId, name, type));
        e.Name = name;
        e.Type = type;
        e.IsSyncing = true;
        e.CurrentMessage = null;
        e.ItemsSoFar = 0;
        e.LastError = null;
    }

    public void Progress(string instanceId, string message, int itemsSoFar = 0)
    {
        if (_entries.TryGetValue(instanceId, out var e))
        {
            e.CurrentMessage = message;
            e.ItemsSoFar = itemsSoFar;
        }
    }

    public void Complete(string instanceId, int totalItems)
    {
        if (_entries.TryGetValue(instanceId, out var e))
        {
            e.IsSyncing = false;
            e.CurrentMessage = null;
            e.ItemsLastRun = totalItems;
            e.ItemsSoFar = 0;
            e.LastSyncAt = DateTimeOffset.UtcNow;
            e.LastError = null;
        }
    }

    public void Fail(string instanceId, string error)
    {
        if (_entries.TryGetValue(instanceId, out var e))
        {
            e.IsSyncing = false;
            e.CurrentMessage = null;
            e.ItemsSoFar = 0;
            e.LastError = error;
            e.LastSyncAt = DateTimeOffset.UtcNow;
        }
    }

    public IReadOnlyList<SyncEntry> GetAll() =>
        _entries.Values.Select(e => e.ToRecord()).ToList();

    private class SyncEntryMutable(string instanceId, string name, string type)
    {
        public string InstanceId = instanceId;
        public string Name = name;
        public string Type = type;
        public bool IsSyncing;
        public string? CurrentMessage;
        public int ItemsSoFar;
        public int ItemsLastRun;
        public DateTimeOffset? LastSyncAt;
        public string? LastError;

        public SyncEntry ToRecord() => new(
            InstanceId, Name, Type, IsSyncing,
            IsSyncing ? (CurrentMessage ?? $"syncing… ({ItemsSoFar} items)") : CurrentMessage,
            ItemsLastRun, LastSyncAt, LastError);
    }
}
