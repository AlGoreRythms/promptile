using System.Text.Json;
using System.Text.Json.Serialization;

namespace Promptile.Host.Services;

public class AgentTierSettings
{
    public string Provider { get; set; } = "claude-cli";
    public string Model { get; set; } = "";
    public string? ApiKey { get; set; }
    public string? BaseUrl { get; set; }  // for ollama / lmstudio
    public bool Thinking { get; set; } = false;
}

public class AgentDefinition
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = "opencode";  // "opencode" | "claude-code"
    public string Provider { get; set; } = "";       // opencode: anthropic/openai/ollama/lmstudio
    public string Model { get; set; } = "";
    public string? ApiKey { get; set; }
    public string? BaseUrl { get; set; }
    public string? CommandPath { get; set; }
}

public class AssistantSettings
{
    public string InformationStorePath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Assistant");

    public AgentTierSettings Light { get; set; } = new();
    public AgentTierSettings Medium { get; set; } = new();
    public AgentTierSettings Heavy { get; set; } = new();

    public string ChatSystemPrompt { get; set; } = "";
    public string UserProfile { get; set; } = "";

    /// <summary>
    /// Plugin IDs that have been disabled. Disabled plugins don't show
    /// nav items, dashboard cards, or MCP tools.
    /// </summary>
    public HashSet<string> DisabledPlugins { get; set; } = [];

    /// <summary>
    /// Ordered list of plugin IDs for the nav bar. Plugins not in this list
    /// appear at the end in registration order.
    /// </summary>
    public List<string> PluginOrder { get; set; } = [];

    /// <summary>
    /// Individual MCP tool names that have been disabled (e.g. "vault_add_note").
    /// Checked at MCP registration time.
    /// </summary>
    public HashSet<string> DisabledMcpTools { get; set; } = [];

    /// <summary>
    /// Per-plugin allow-list of MCP tools available when that plugin calls Claude.
    /// Key = calling plugin ID, Value = set of allowed tool names.
    /// Tools not in the list are not available. New tools default to off.
    /// </summary>
    public Dictionary<string, HashSet<string>> PluginMcpAccess { get; set; } = [];

    public bool NotifyOnJobCompletion { get; set; } = true;

    // Keywords/patterns to watch for across all synced data (one per line; /regex/ syntax supported)
    public List<string> Watchlist { get; set; } = [];

    // Scheduled intelligence runs
    public List<ScheduledJob> ScheduledJobs { get; set; } = [];

    // Short-lived context items injected into every chat prompt (auto-expire after 24h)
    public List<EphemeralContextEntry> EphemeralContext { get; set; } = [];

    // Daily digest configuration
    public DigestSettings Digest { get; set; } = new();

    // Embedding endpoint for semantic search (LMStudio / OpenAI compatible)
    public string? EmbeddingBaseUrl { get; set; }
    public string? EmbeddingModel { get; set; }

    // Named agent configurations (opencode, claude-code)
    public List<AgentDefinition> Agents { get; set; } = [];

    // Work jobs — context from data sources piped to an agent acting on a folder
    public List<WorkJob> WorkJobs { get; set; } = [];

    // Memory pages — global curation layer over data sources
    public List<MemoryPageConfig> MemoryPages { get; set; } = [];
    public int MemoryScanIntervalMinutes { get; set; } = 30;

    // User-configured dashboard pages and widgets
    public string DashboardHomeName { get; set; } = "Default";
    public List<DashboardPage> DashboardPages { get; set; } = [];
    public List<UserDashboardWidget> DashboardWidgets { get; set; } = [];

    // Names hidden from all pages — sources, widgets, tasks, and plugin content matching these are invisible
    public List<string> HiddenNames { get; set; } = [];

    public bool IsHidden(string name) =>
        HiddenNames.Any(h => !string.IsNullOrEmpty(h) && name.Contains(h, StringComparison.OrdinalIgnoreCase));
}

public record ScheduledJob(
    string Slug,
    string Name,
    int Hour,
    int? DayOfWeek,   // null = daily; 0 = Sun … 6 = Sat
    string Prompt,
    string Tier = "heavy");

public record EphemeralContextEntry(string Text, DateTimeOffset ExpiresAt);

public class WorkJob
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> DataSources { get; set; } = [];
    public int ContextDays { get; set; } = 7;
    public string Prompt { get; set; } = "";
    public string? Description { get; set; }
    public int? ScheduleHour { get; set; }   // null = no schedule; 0-23 = run at this UTC hour
    public int? ScheduleDay { get; set; }    // null = daily; 0=Sun…6=Sat
    public string FolderSourceId { get; set; } = "";
    public string AgentId { get; set; } = "";
    public int? GridX { get; set; }
    public int? GridY { get; set; }
    public int? GridW { get; set; }
    public int? GridH { get; set; }
}

public record WorkJobRun(
    string JobId,
    string RunId,
    string JobName,
    DateTime StartedAt,
    DateTime? CompletedAt,
    string Status,    // "running" | "completed" | "failed"
    string? Summary = null);

public class DashboardPage
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int Order { get; set; }
}

public class UserDashboardWidget
{
    public string Id { get; set; } = "";
    public string PageId { get; set; } = "";   // "" = Home (default page)
    public string Title { get; set; } = "";
    public string Prompt { get; set; } = "";
    public string AgentTier { get; set; } = "medium";
    public string OutputFormat { get; set; } = "markdown"; // "markdown" | "text" | "html"
    public List<string> DataSources { get; set; } = [];
    public List<string> MemoryPages { get; set; } = [];
    public int Order { get; set; }
    public string? Color { get; set; }
    public bool HideHeader { get; set; } = false;
    public bool HideFooter { get; set; } = false;
    public int? GridX { get; set; }
    public int? GridY { get; set; }
    public int? GridW { get; set; }
    public int? GridH { get; set; }
    public string RefreshSchedule { get; set; } = ""; // "" = 24h default; "daily:HH:mm" = specific UTC time
    public int ContextDays { get; set; } = 7;
}

public class DigestSettings
{
    public bool Enabled { get; set; } = false;
    public int Hour { get; set; } = 7;
    public string? SmtpHost { get; set; }
    public int SmtpPort { get; set; } = 587;
    public string? SmtpUser { get; set; }
    public string? SmtpPassword { get; set; }
    public string? ToEmail { get; set; }
}

public class SettingsService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _settingsPath;
    private AssistantSettings? _cached;

    public SettingsService(string dataDir)
    {
        _settingsPath = Path.Combine(dataDir, "settings.json");
    }

    public async Task<AssistantSettings> LoadAsync()
    {
        if (_cached != null) return _cached;

        try
        {
            if (File.Exists(_settingsPath))
            {
                var json = await File.ReadAllTextAsync(_settingsPath);
                _cached = JsonSerializer.Deserialize<AssistantSettings>(json, JsonOpts) ?? new AssistantSettings();
                return _cached;
            }
        }
        catch { }

        _cached = new AssistantSettings();
        return _cached;
    }

    public AssistantSettings LoadSync()
    {
        if (_cached != null) return _cached;
        try
        {
            if (File.Exists(_settingsPath))
            {
                var json = File.ReadAllText(_settingsPath);
                _cached = JsonSerializer.Deserialize<AssistantSettings>(json, JsonOpts) ?? new AssistantSettings();
                return _cached;
            }
        }
        catch { }
        _cached = new AssistantSettings();
        return _cached;
    }

    public async Task SaveAsync(AssistantSettings settings)
    {
        _cached = settings;
        var json = JsonSerializer.Serialize(settings, JsonOpts);
        await File.WriteAllTextAsync(_settingsPath, json);
    }

    public void InvalidateCache() => _cached = null;
}
