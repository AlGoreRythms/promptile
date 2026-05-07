using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Promptile.Sdk;
using Microsoft.Extensions.Logging;

namespace Promptile.Host.Services;

public class BriefingService : IHostedService
{
    private readonly INotificationBus _bus;
    private readonly IAiServiceResolver _resolver;
    private readonly IInformationStore _store;
    private readonly IDataSourceManager _dataSourceManager;
    private readonly ILogger<BriefingService> _logger;
    private readonly string _stateFile;

    private DateTime _lastRun = DateTime.MinValue;
    private CancellationTokenSource? _debounceCts;
    private readonly SemaphoreSlim _runLock = new(1, 1);

    private static readonly TimeSpan DebounceDelay = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(15);
    private const int MaxDataChars = 80_000;

    public const string BriefingSource = "_Assistant";

    public BriefingService(
        INotificationBus bus,
        IAiServiceResolver resolver,
        IInformationStore store,
        IDataSourceManager dataSourceManager,
        ILogger<BriefingService> logger,
        SettingsService settings)
    {
        _bus = bus;
        _resolver = resolver;
        _store = store;
        _dataSourceManager = dataSourceManager;
        _logger = logger;
        _stateFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".promptile", "briefing-state.json");
    }

    public Task StartAsync(CancellationToken ct)
    {
        LoadState();
        _bus.Subscribe(OnNotification);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _debounceCts?.Cancel();
        return Task.CompletedTask;
    }

    private void OnNotification(AssistantNotification notification)
    {
        if (notification.Category != "sync-summary") return;

        _debounceCts?.Cancel();
        _debounceCts = new CancellationTokenSource();
        var token = _debounceCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(DebounceDelay, token);
                if (token.IsCancellationRequested) return;
                if (DateTime.UtcNow - _lastRun < Cooldown)
                {
                    _logger.LogInformation("Briefing skipped — cooldown active");
                    return;
                }
                await RunAsync(CancellationToken.None);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _logger.LogError(ex, "Briefing run failed"); }
        }, CancellationToken.None);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        if (!await _runLock.WaitAsync(0, ct)) return;
        try
        {
            _logger.LogInformation("Starting briefing run (last run: {LastRun})", _lastRun);

            var since = _lastRun == DateTime.MinValue
                ? DateTime.UtcNow.AddDays(-1)
                : _lastRun;

            var briefNames = _dataSourceManager.GetInstances()
                .Select(i => i.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n)
                .ToList();

            if (briefNames.Count == 0)
            {
                _logger.LogInformation("No data sources configured for briefing");
                return;
            }

            var service = await _resolver.GetServiceAsync("heavy");
            var allNewData = new StringBuilder();
            var notableMessages = new List<string>();

            foreach (var briefName in briefNames)
            {
                var newData = GatherNewData(since, briefName);
                if (string.IsNullOrWhiteSpace(newData))
                {
                    _logger.LogInformation("No new data for '{Brief}' since {Since}", briefName, since);
                    continue;
                }

                allNewData.Append(newData);
                var existingNotes = await ReadExistingNotesAsync(briefName);
                var notable = await UpdateSituationalNotesAsync(service, newData, existingNotes, ct, briefName);
                if (!string.IsNullOrEmpty(notable))
                    notableMessages.Add($"[{briefName}] {notable}");
            }

            if (allNewData.Length > 0)
            {
                var lightService = await _resolver.GetServiceAsync("light");
                await UpdateEntityIndexAsync(lightService, allNewData.ToString(), ct);
            }

            _lastRun = DateTime.UtcNow;
            SaveState();

            if (notableMessages.Count > 0)
            {
                _bus.Publish(new AssistantNotification(
                    PluginId: "host",
                    Title: "Assistant — Notable Update",
                    Body: string.Join(" | ", notableMessages),
                    Timestamp: DateTimeOffset.UtcNow,
                    Category: "briefing"
                ));
            }

            _logger.LogInformation("Briefing run complete");
        }
        finally { _runLock.Release(); }
    }

    private string GatherNewData(DateTime since, string briefName)
    {
        var sb = new StringBuilder();
        var sourceDir = Path.Combine(_store.RootPath, SanitizeName(briefName));
        if (!Directory.Exists(sourceDir)) return "";

        foreach (var typeDir in Directory.GetDirectories(sourceDir).OrderBy(d => d))
        {
            var sourceType = Path.GetFileName(typeDir);
            var dataPath = Path.Combine(typeDir, "Data");
            if (!Directory.Exists(dataPath)) continue;

            var files = Directory.GetFiles(dataPath, "*.md")
                .Where(f => File.GetLastWriteTimeUtc(f) > since)
                .OrderBy(f => f)
                .ToList();

            foreach (var file in files)
            {
                if (sb.Length >= MaxDataChars) break;
                var content = File.ReadAllText(file);
                var remaining = MaxDataChars - sb.Length;
                if (content.Length > remaining) content = content[..remaining] + "\n[truncated]";

                sb.AppendLine($"=== {briefName} / {sourceType} / {Path.GetFileName(file)} ===");
                sb.AppendLine(content);
                sb.AppendLine();
            }

            if (sb.Length >= MaxDataChars) break;
        }

        return sb.ToString();
    }

    private async Task<string> ReadExistingNotesAsync(string briefName)
    {
        var notesPath = _store.GetNotesPath(BriefingSource, briefName);
        if (!Directory.Exists(notesPath)) return "";

        var sb = new StringBuilder();
        var files = Directory.GetFiles(notesPath, "situation-*.md")
            .Where(f => !f.EndsWith(".archived.md"))
            .OrderBy(f => f);
        foreach (var file in files)
        {
            sb.AppendLine($"=== {Path.GetFileNameWithoutExtension(file)} ===");
            sb.AppendLine(await File.ReadAllTextAsync(file));
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private async Task<string?> UpdateSituationalNotesAsync(
        IAiService service, string newData, string existingNotes, CancellationToken ct, string briefName)
    {
        var systemPrompt = """
            You are a personal AI assistant maintaining situational notes for the user.
            Each note tracks an ongoing situation, project, or thread.
            Write in plain, readable English with clear structure.
            Do not pad notes with generic content — only include what's real and relevant.
            """;

        var userPrompt = $"""
            Below is new communication data plus the user's existing situational notes.
            Review both and decide which situations need a new note or an update.

            For each situation you want to create or update, output a block like this:
            <note slug="short-kebab-case-slug" title="Readable Title">
            # Readable Title

            **Status**: One line current status

            ## Recent Developments
            ...

            ## Key People
            ...

            ## What to Watch For
            ...
            </note>

            Only output notes that have meaningful new content. Do not repeat existing notes unchanged.

            If a situation has clearly gone quiet (no activity for weeks, nothing pending, fully resolved),
            archive it by outputting: <archive slug="the-exact-slug"/>

            Also output any concrete next steps the user should take as:
            <action priority="high|medium|low" deadline="optional YYYY-MM-DD">Action text here</action>
            Only include real, specific actions — not generic advice.

            If anything in the new data is urgent or notably important, end your response with:
            <notable>One sentence describing what the user should know immediately.</notable>
            Otherwise omit the <notable> tag entirely.

            EXISTING NOTES:
            {(string.IsNullOrWhiteSpace(existingNotes) ? "(none yet)" : existingNotes)}

            NEW DATA:
            {newData}
            """;

        var response = await service.CompleteAsync(systemPrompt, userPrompt, ct);
        var text = response.Text;

        var noteMatches = Regex.Matches(text,
            @"<note\s+slug=""([^""]+)""\s+title=""([^""]+)"">([\s\S]*?)</note>",
            RegexOptions.IgnoreCase);

        foreach (Match m in noteMatches)
        {
            var slug    = m.Groups[1].Value.Trim();
            var content = m.Groups[3].Value.Trim();
            await _store.WriteNoteAsync(BriefingSource, briefName, $"situation-{slug}.md", content);
            _logger.LogInformation("Situation note updated: {Brief}/{Slug}", briefName, slug);
        }

        var notesPath = _store.GetNotesPath(BriefingSource, briefName);
        var archiveMatches = Regex.Matches(text, @"<archive\s+slug=""([^""]+)""\s*/?>", RegexOptions.IgnoreCase);
        foreach (Match m in archiveMatches)
        {
            var slug = m.Groups[1].Value.Trim();
            var from = Path.Combine(notesPath, $"situation-{slug}.md");
            var to   = Path.Combine(notesPath, $"situation-{slug}.archived.md");
            if (File.Exists(from))
            {
                File.Move(from, to, overwrite: true);
                _logger.LogInformation("Situation archived: {Brief}/{Slug}", briefName, slug);
            }
        }

        var actionMatches = Regex.Matches(text,
            @"<action\s+priority=""([^""]*)""\s*(?:deadline=""([^""]*)"")?>([\s\S]*?)</action>",
            RegexOptions.IgnoreCase);
        if (actionMatches.Count > 0)
        {
            var existing = await LoadActionsAsync(notesPath);
            var newActions = actionMatches.Select(m => new ActionItem(
                Text:     m.Groups[3].Value.Trim(),
                Priority: m.Groups[1].Value.Trim(),
                Deadline: m.Groups[2].Success ? m.Groups[2].Value.Trim() : null)).ToList();
            var merged = existing.Where(a => a.Done).ToList();
            merged.AddRange(newActions);
            var json = JsonSerializer.Serialize(merged, new JsonSerializerOptions { WriteIndented = true });
            await _store.WriteNoteAsync(BriefingSource, briefName, "actions.json", json);
            _logger.LogInformation("Action list updated for '{Brief}' ({Count} actions)", briefName, newActions.Count);
        }

        var notableMatch = Regex.Match(text, @"<notable>([\s\S]*?)</notable>", RegexOptions.IgnoreCase);
        if (notableMatch.Success)
        {
            var reason = notableMatch.Groups[1].Value.Trim();
            if (!string.IsNullOrEmpty(reason)) return reason;
        }

        return null;
    }

    private async Task UpdateEntityIndexAsync(IAiService service, string newData, CancellationToken ct)
    {
        var entitySource = BriefingSource;
        var entityType   = "Entities";
        var notesPath    = _store.GetNotesPath(entitySource, entityType);

        var existing = new StringBuilder();
        foreach (var name in new[] { "people.md", "companies.md", "projects.md" })
        {
            var existing_ = await _store.ReadNoteAsync(entitySource, entityType, name);
            if (!string.IsNullOrWhiteSpace(existing_))
            {
                existing.AppendLine($"=== {name} ===");
                existing.AppendLine(existing_);
            }
        }

        var systemPrompt = "You are extracting named entities from communication data. Be concise and factual.";
        var userPrompt = $"""
            Extract named entities from the new data below and merge with the existing lists.
            Output exactly three XML sections (even if empty):

            <people>
            Name: role/company (if known), one per line
            </people>
            <companies>
            Name: brief description, one per line
            </companies>
            <projects>
            Name: current status or owner, one per line
            </projects>

            Keep only entities that appear in the actual data. Remove duplicates. Don't invent entries.

            EXISTING:
            {(existing.Length > 0 ? existing.ToString() : "(none)")}

            NEW DATA:
            {newData[..Math.Min(newData.Length, 30_000)]}
            """;

        try
        {
            var response = await service.CompleteAsync(systemPrompt, userPrompt, ct);
            var text = response.Text;

            foreach (var (tag, filename) in new[] { ("people", "people.md"), ("companies", "companies.md"), ("projects", "projects.md") })
            {
                var match = Regex.Match(text, $@"<{tag}>([\s\S]*?)</{tag}>", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    var content = match.Groups[1].Value.Trim();
                    if (!string.IsNullOrEmpty(content))
                        await _store.WriteNoteAsync(entitySource, entityType, filename, content);
                }
            }
            _logger.LogInformation("Entity index updated");
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Entity extraction failed"); }
    }

    private static async Task<List<ActionItem>> LoadActionsAsync(string notesPath)
    {
        var path = Path.Combine(notesPath, "actions.json");
        if (!File.Exists(path)) return [];
        try { return JsonSerializer.Deserialize<List<ActionItem>>(await File.ReadAllTextAsync(path)) ?? []; }
        catch { return []; }
    }

    private static string SanitizeName(string name) =>
        string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    private void LoadState()
    {
        try
        {
            if (!File.Exists(_stateFile)) return;
            var json = JsonDocument.Parse(File.ReadAllText(_stateFile));
            if (json.RootElement.TryGetProperty("lastRun", out var lr))
                _lastRun = lr.GetDateTime();
        }
        catch { }
    }

    private void SaveState()
    {
        try
        {
            File.WriteAllText(_stateFile,
                JsonSerializer.Serialize(new { lastRun = _lastRun }));
        }
        catch { }
    }
}

public record ActionItem(string Text, string Priority, string? Deadline, bool Done = false, string? BriefCardId = null);
