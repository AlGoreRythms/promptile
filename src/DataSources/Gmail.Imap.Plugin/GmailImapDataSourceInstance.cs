using System.Text;
using System.Text.Json;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using MimeKit;
using MimeKit.Text;
using Promptile.Sdk;

namespace GmailImap;

public class GmailImapDataSourceInstance : IDataSourceInstance
{
    public string Id => Config.Id;
    public string Name => Config.Name;
    public string Type => "gmail-imap";
    public DataSourceConfig Config { get; private set; }

    private IInformationStore? _store;
    private ISyncReporter? _sync;
    private CancellationTokenSource? _cts;
    private Task? _pollTask;
    private bool _isConnected;
    private string? _statusMessage;
    private uint _lastUid;
    private readonly string _stateDir;

    public GmailImapDataSourceInstance(DataSourceConfig config)
    {
        Config = config;
        _stateDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".promptile", "datasources", config.Id);
        Directory.CreateDirectory(_stateDir);
    }

    public void UpdateConfig(DataSourceConfig config) => Config = config;

    public Task StartAsync(INotificationBus bus, IServiceProvider services, CancellationToken ct)
    {
        if (!Config.Config.TryGetValue("email", out var email) || string.IsNullOrEmpty(email)) return Task.CompletedTask;
        if (!Config.Config.TryGetValue("appPassword", out var pwd) || string.IsNullOrEmpty(pwd)) return Task.CompletedTask;

        _store = services.GetService(typeof(IInformationStore)) as IInformationStore;
        _sync = services.GetService(typeof(ISyncReporter)) as ISyncReporter;

        LoadState();

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _pollTask = PollLoopAsync(bus, _cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_cts != null)
        {
            await _cts.CancelAsync();
            if (_pollTask != null)
                try { await _pollTask; } catch { }
        }
    }

    public Task ResetStateAsync()
    {
        _lastUid = 0;
        var path = Path.Combine(_stateDir, "state.json");
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    public Task<DataSourceStatus> GetStatusAsync()
    {
        if (!Config.Config.TryGetValue("email", out var email) || string.IsNullOrEmpty(email))
            return Task.FromResult(new DataSourceStatus(false, "Email not configured"));
        if (!Config.Config.TryGetValue("appPassword", out var pwd) || string.IsNullOrEmpty(pwd))
            return Task.FromResult(new DataSourceStatus(false, "App password not configured"));
        if (_pollTask == null)
            return Task.FromResult(new DataSourceStatus(false, "Not started"));
        return Task.FromResult(new DataSourceStatus(_isConnected, _statusMessage ?? email));
    }

    private async Task PollLoopAsync(INotificationBus bus, CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(15), ct);

        var interval = TimeSpan.FromSeconds(
            Config.Config.TryGetValue("pollIntervalSeconds", out var s) && int.TryParse(s, out var sec) ? sec : 60);

        while (!ct.IsCancellationRequested)
        {
            try { await PollAsync(bus, ct); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _isConnected = false;
                _statusMessage = ex.Message;
                _sync?.Fail(Id, ex.Message);
                Console.Error.WriteLine($"[GmailImap:{Name}] Poll error: {ex.Message}");
            }

            try { await Task.Delay(interval, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task PollAsync(INotificationBus bus, CancellationToken ct)
    {
        var email = Config.Config.TryGetValue("email", out var e) ? e : "";
        var pwd = Config.Config.TryGetValue("appPassword", out var p) ? p : "";
        var folderName = Config.Config.TryGetValue("folder", out var f) && !string.IsNullOrEmpty(f) ? f : "INBOX";

        _sync?.Begin(Id, $"{Name} · Gmail (IMAP)", "gmail-imap");

        using var client = new ImapClient();
        await client.ConnectAsync("imap.gmail.com", 993, SecureSocketOptions.SslOnConnect, ct);
        await client.AuthenticateAsync(email, pwd, ct);

        _isConnected = true;
        _statusMessage = email;

        var folder = folderName.Equals("INBOX", StringComparison.OrdinalIgnoreCase)
            ? client.Inbox
            : await client.GetFolderAsync(folderName, ct) ?? client.Inbox;

        await folder!.OpenAsync(FolderAccess.ReadOnly, ct);

        IList<UniqueId> uids;
        if (_lastUid == 0)
        {
            _sync?.Progress(Id, "First run — fetching last 7 days", 0);
            uids = await folder.SearchAsync(SearchQuery.DeliveredAfter(DateTime.Now.AddDays(-7)), ct);
        }
        else
        {
            var range = new UniqueIdRange(new UniqueId(_lastUid + 1), UniqueId.MaxValue);
            uids = await folder.SearchAsync(SearchQuery.Uids(range), ct);
        }

        var count = 0;
        var maxUid = _lastUid;

        foreach (var uid in uids)
        {
            ct.ThrowIfCancellationRequested();
            var message = await folder.GetMessageAsync(uid, ct);
            await WriteToStoreAsync(message);
            if (_lastUid > 0) PublishNotification(bus, message);
            if (uid.Id > maxUid) maxUid = uid.Id;
            count++;
            _sync?.Progress(Id, $"Stored {count} new email{(count == 1 ? "" : "s")}", count);
        }

        await folder.CloseAsync(false, ct);
        await client.DisconnectAsync(true, ct);

        if (maxUid > _lastUid)
        {
            _lastUid = maxUid;
            SaveState();
        }

        if (count > 0 && _lastUid > 0)
        {
            bus.Publish(new AssistantNotification(
                PluginId: Id,
                Title: $"Gmail — {Name}",
                Body: $"{count} new email{(count == 1 ? "" : "s")}",
                Timestamp: DateTimeOffset.UtcNow,
                Category: "sync-summary"
            ));
        }

        _sync?.Complete(Id, count);
    }

    private async Task WriteToStoreAsync(MimeMessage message)
    {
        if (_store == null) return;

        var date = message.Date.LocalDateTime;
        var filename = $"{date:yyyy-MM-dd}.md";
        var from = message.From.Mailboxes.FirstOrDefault()?.ToString() ?? "Unknown";
        var subject = message.Subject ?? "(no subject)";
        var body = (message.GetTextBody(TextFormat.Plain) ?? "").Trim();

        var entry = new StringBuilder();
        entry.AppendLine($"## {date:HH:mm} · {from}");
        entry.AppendLine($"**Subject:** {subject}");
        entry.AppendLine();
        entry.AppendLine(string.IsNullOrWhiteSpace(body) ? "(no plain text body)" : body);
        entry.AppendLine();
        entry.AppendLine("---");
        entry.AppendLine();

        await _store.AppendDataAsync(Name, "Gmail", filename, entry.ToString());
    }

    private void PublishNotification(INotificationBus bus, MimeMessage message)
    {
        var from = message.From.Mailboxes.FirstOrDefault()?.ToString() ?? "Unknown";
        bus.Publish(new AssistantNotification(
            PluginId: "gmail-imap",
            Title: $"Gmail — {Name}",
            Body: $"From: {from}\n{message.Subject}",
            Timestamp: DateTimeOffset.UtcNow,
            ActionUrl: null
        ));
    }

    private void LoadState()
    {
        var path = Path.Combine(_stateDir, "state.json");
        if (!File.Exists(path)) return;
        try
        {
            var state = JsonSerializer.Deserialize<ImapState>(File.ReadAllText(path));
            _lastUid = state?.LastUid ?? 0;
        }
        catch { }
    }

    private void SaveState()
    {
        var path = Path.Combine(_stateDir, "state.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new ImapState(_lastUid)));
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        if (_pollTask != null)
            try { await _pollTask; } catch { }
        _cts?.Dispose();
    }

    private record ImapState(uint LastUid);
}
