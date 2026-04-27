using System.Text;
using System.Text.Json;
using Assistant.Sdk;
using Microsoft.Extensions.Logging;

namespace Assistant.Host.Services;

public class SchedulerService : IHostedService
{
    private readonly IAiServiceResolver _resolver;
    private readonly IInformationStore _store;
    private readonly SettingsService _settings;
    private readonly WorkJobService _workJobs;
    private readonly ILogger<SchedulerService> _logger;
    private readonly string _stateFile;

    private Dictionary<string, DateTime> _lastRun = [];
    private CancellationTokenSource? _cts;
    private Task? _tickTask;

    public SchedulerService(
        IAiServiceResolver resolver,
        IInformationStore store,
        SettingsService settings,
        WorkJobService workJobs,
        ILogger<SchedulerService> logger)
    {
        _resolver = resolver;
        _store = store;
        _settings = settings;
        _workJobs = workJobs;
        _logger = logger;
        _stateFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".assistant", "scheduler-state.json");
    }

    public Task StartAsync(CancellationToken ct)
    {
        LoadState();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _tickTask = TickLoopAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _cts?.Cancel();
        return Task.CompletedTask;
    }

    private async Task TickLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await CheckJobsAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogError(ex, "Scheduler tick failed"); }

            try { await Task.Delay(TimeSpan.FromMinutes(1), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task CheckJobsAsync(CancellationToken ct)
    {
        var s = await _settings.LoadAsync();
        var now = DateTime.Now;
        var today = now.Date;

        foreach (var job in s.ScheduledJobs)
        {
            if (job.Hour != now.Hour) continue;
            if (job.DayOfWeek.HasValue && (int)now.DayOfWeek != job.DayOfWeek.Value) continue;
            if (_lastRun.TryGetValue(job.Slug, out var last) && last.Date == today) continue;

            _logger.LogInformation("Running scheduled job: {Name}", job.Name);
            _ = Task.Run(async () =>
            {
                try { await RunJobAsync(job, ct); }
                catch (Exception ex) { _logger.LogError(ex, "Scheduled job {Name} failed", job.Name); }
            }, CancellationToken.None);

            _lastRun[job.Slug] = now;
            SaveState();
        }

        foreach (var job in s.WorkJobs)
        {
            if (!job.ScheduleHour.HasValue) continue;
            if (job.ScheduleHour.Value != now.Hour) continue;
            if (job.ScheduleDay.HasValue && (int)now.DayOfWeek != job.ScheduleDay.Value) continue;

            var key = $"work:{job.Id}";
            if (_lastRun.TryGetValue(key, out var lastWork) && lastWork.Date == today) continue;
            if (_workJobs.GetRunningIds().Contains(job.Id)) continue;

            _logger.LogInformation("Running scheduled work job: {Name}", job.Name);
            _workJobs.RunNow(job.Id);

            _lastRun[key] = now;
            SaveState();
        }
    }

    private async Task RunJobAsync(ScheduledJob job, CancellationToken ct)
    {
        var lookbackDays = job.DayOfWeek.HasValue ? 7 : 1;
        var since = DateTime.UtcNow.AddDays(-lookbackDays);
        var data = GatherData(since);
        if (string.IsNullOrWhiteSpace(data)) return;

        var service = await _resolver.GetServiceAsync(job.Tier);
        var prompt = job.Prompt
            .Replace("{date}", DateTime.Now.ToString("MMMM d, yyyy"))
            .Replace("{dayOfWeek}", DateTime.Now.DayOfWeek.ToString());

        var userPrompt = $"{prompt}\n\nDATA:\n{data}";
        var response = await service.CompleteAsync(
            "You are a personal AI assistant. Be concise and relevant.", userPrompt, ct);

        var filename = $"{DateTime.Now:yyyy-MM-dd}.md";
        var content = $"*Generated {DateTime.Now:h:mm tt} by scheduled job \"{job.Name}\"*\n\n{response.Text.Trim()}";
        await _store.WriteNoteAsync("_Assistant", $"Scheduled/{job.Slug}", filename, content);
        _logger.LogInformation("Scheduled job {Name} complete", job.Name);
    }

    private string GatherData(DateTime since)
    {
        var sb = new StringBuilder();
        const int maxChars = 60_000;

        foreach (var (sourceName, types) in _store.ListSources())
        {
            if (sourceName == "_Assistant") continue;
            foreach (var sourceType in types)
            {
                var dataPath = _store.GetDataPath(sourceName, sourceType);
                if (!Directory.Exists(dataPath)) continue;

                foreach (var file in Directory.GetFiles(dataPath, "*.md")
                    .Where(f => File.GetLastWriteTimeUtc(f) > since).OrderBy(f => f))
                {
                    if (sb.Length >= maxChars) break;
                    var content = File.ReadAllText(file);
                    var remaining = maxChars - sb.Length;
                    if (content.Length > remaining) content = content[..remaining] + "\n[truncated]";
                    sb.AppendLine($"=== {sourceName} / {sourceType} / {Path.GetFileName(file)} ===");
                    sb.AppendLine(content);
                    sb.AppendLine();
                }
                if (sb.Length >= maxChars) break;
            }
            if (sb.Length >= maxChars) break;
        }
        return sb.ToString();
    }

    private void LoadState()
    {
        try
        {
            if (!File.Exists(_stateFile)) return;
            _lastRun = JsonSerializer.Deserialize<Dictionary<string, DateTime>>(
                File.ReadAllText(_stateFile)) ?? [];
        }
        catch { }
    }

    private void SaveState()
    {
        try { File.WriteAllText(_stateFile, JsonSerializer.Serialize(_lastRun)); }
        catch { }
    }
}
