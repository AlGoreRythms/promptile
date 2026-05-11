using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ModelContextProtocol.Server;
using Promptile.Host.Services;
using Promptile.Sdk;

namespace Promptile.Host.Pages;

[IgnoreAntiforgeryToken]
public class SettingsModel : PageModel
{
    private readonly SettingsService _settings;
    private readonly DataSourcesService _sources;
    private readonly DataSourceManager _manager;
    private readonly PluginRegistry _registry;

    public SettingsModel(SettingsService settings, DataSourcesService sources, DataSourceManager manager, PluginRegistry registry)
    {
        _settings = settings;
        _sources = sources;
        _manager = manager;
        _registry = registry;
    }

    public AssistantSettings Settings { get; set; } = new();
    public bool ClaudeCliAvailable { get; set; }
    public bool ApiKeyEnvSet { get; set; }
    public List<DataSourceConfig> SourceConfigs { get; set; } = [];
    public Dictionary<string, DataSourceStatus> SourceStatuses { get; set; } = [];
    public IReadOnlyList<IDataSourceProvider> SourceProviders { get; set; } = [];
    public string? AgentsMessage { get; set; }
    public string? StoreMessage { get; set; }
    public string? NotificationsMessage { get; set; }
    public string? ChatPromptMessage { get; set; }
    public string? ProfileMessage { get; set; }
    public string? DigestMessage { get; set; }
    public string? EmbeddingMessage { get; set; }
    public string? SecurityMessage { get; set; }
    public List<PluginsModel.PluginInfo> PluginInfos { get; set; } = [];
    public List<AgentDefinition> Agents { get; set; } = [];

    public async Task OnGetAsync()
    {
        Settings = await _settings.LoadAsync();
        Agents = Settings.Agents;
        CheckStatus();
        await LoadSourcesAsync();
        await LoadPluginsAsync();
    }

    private async Task LoadSourcesAsync()
    {
        var all = await _sources.LoadAsync();
        SourceConfigs = Settings.HiddenNames.Count > 0 ? all.Where(c => !Settings.IsHidden(c.Name)).ToList() : all;
        SourceProviders = _manager.GetProviders();
        foreach (var instance in _manager.GetInstances())
        {
            try { SourceStatuses[instance.Id] = await instance.GetStatusAsync(); }
            catch { SourceStatuses[instance.Id] = new DataSourceStatus(false, "Error"); }
        }
    }

    public async Task<IActionResult> OnPostToggleSourceAsync(string id)
    {
        var all = await _sources.LoadAsync();
        var cfg = all.FirstOrDefault(c => c.Id == id);
        if (cfg != null)
        {
            await _sources.UpsertAsync(cfg with { Enabled = !cfg.Enabled });
            await _manager.ReloadAsync();
        }
        return Redirect("/Settings?tab=sources");
    }

    public async Task<IActionResult> OnPostDeleteSourceAsync(string id)
    {
        await _sources.DeleteAsync(id);
        await _manager.ReloadAsync();
        return Redirect("/Settings?tab=sources");
    }

    public async Task<IActionResult> OnPostResetSourceAsync(string id)
    {
        await _manager.ResetInstanceAsync(id);
        return Redirect("/Settings?tab=sources");
    }

    public async Task<IActionResult> OnPostResetAllSourcesAsync()
    {
        await _manager.ResetAllAsync();
        return Redirect("/Settings?tab=sources");
    }

    public async Task<IActionResult> OnPostSaveStoreAsync()
    {
        var s = await _settings.LoadAsync();
        var path = Request.Form["InformationStorePath"].ToString().Trim();
        if (!string.IsNullOrEmpty(path))
        {
            path = path.Replace("~", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            s.InformationStorePath = path;
            Directory.CreateDirectory(path);
        }
        await _settings.SaveAsync(s);
        Settings = s;

        StoreMessage = "Store path saved.";
        CheckStatus();
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAgentsAsync()
    {
        var s = await _settings.LoadAsync();
        s.Light = ReadTier("light");
        s.Medium = ReadTier("medium");
        s.Heavy = ReadTier("heavy");
        await _settings.SaveAsync(s);
        Settings = s;

        AgentsMessage = "Agent settings saved.";
        CheckStatus();
        return Page();
    }

    private AgentTierSettings ReadTier(string prefix) => new()
    {
        Provider = Request.Form[$"{prefix}_provider"].ToString(),
        Model = Request.Form[$"{prefix}_model"].ToString().Trim(),
        ApiKey = Request.Form[$"{prefix}_apiKey"].ToString().Trim() is { Length: > 0 } k ? k : null,
        BaseUrl = Request.Form[$"{prefix}_baseUrl"].ToString().Trim() is { Length: > 0 } u ? u : null,
        Thinking = Request.Form[$"{prefix}_provider"].ToString() is "lmstudio" or "ollama"
            && Request.Form[$"{prefix}_thinking"].Contains("true"),
    };

    public async Task<IActionResult> OnPostSaveProfileAsync()
    {
        var s = await _settings.LoadAsync();
        s.UserProfile = Request.Form["UserProfile"].ToString();
        await _settings.SaveAsync(s);
        Settings = s;

        ProfileMessage = "Profile saved.";
        CheckStatus();
        return Page();
    }

    public async Task<IActionResult> OnPostSaveChatPromptAsync()
    {
        var s = await _settings.LoadAsync();
        s.ChatSystemPrompt = Request.Form["ChatSystemPrompt"].ToString();
        await _settings.SaveAsync(s);
        Settings = s;

        ChatPromptMessage = "Chat prompt saved.";
        CheckStatus();
        return Page();
    }

    public async Task<IActionResult> OnPostSaveNotificationsAsync()
    {
        var s = await _settings.LoadAsync();
        s.NotifyOnJobCompletion = Request.Form["NotifyOnJobCompletion"] == "true";
        await _settings.SaveAsync(s);
        Settings = s;

        NotificationsMessage = "Notification settings saved.";
        CheckStatus();
        return Page();
    }

    public async Task<IActionResult> OnPostSaveMemoryAsync()
    {
        var s = await _settings.LoadAsync();
        if (int.TryParse(Request.Form["MemoryScanIntervalMinutes"], out var interval) && interval > 0)
            s.MemoryScanIntervalMinutes = interval;
        await _settings.SaveAsync(s);
        _settings.InvalidateCache();
        Settings = s;
        CheckStatus();
        return Page();
    }

    public async Task<IActionResult> OnPostSaveDigestAsync()
    {
        var s = await _settings.LoadAsync();
        s.Digest.Enabled = Request.Form["DigestEnabled"] == "true";
        int.TryParse(Request.Form["DigestHour"].ToString(), out var hour);
        s.Digest.Hour = Math.Clamp(hour, 0, 23);
        s.Digest.ToEmail = Request.Form["DigestToEmail"].ToString().Trim() is { Length: > 0 } e ? e : null;
        s.Digest.SmtpHost = Request.Form["DigestSmtpHost"].ToString().Trim() is { Length: > 0 } h ? h : null;
        int.TryParse(Request.Form["DigestSmtpPort"].ToString(), out var port);
        s.Digest.SmtpPort = port > 0 ? port : 587;
        s.Digest.SmtpUser = Request.Form["DigestSmtpUser"].ToString().Trim() is { Length: > 0 } u ? u : null;
        var pw = Request.Form["DigestSmtpPassword"].ToString();
        if (!string.IsNullOrEmpty(pw)) s.Digest.SmtpPassword = pw;
        await _settings.SaveAsync(s);
        Settings = s;

        DigestMessage = "Digest settings saved.";
        CheckStatus();
        return Page();
    }

    public async Task<IActionResult> OnPostSaveEmbeddingAsync()
    {
        var s = await _settings.LoadAsync();
        s.EmbeddingBaseUrl = Request.Form["EmbeddingBaseUrl"].ToString().Trim() is { Length: > 0 } u ? u : null;
        s.EmbeddingModel = Request.Form["EmbeddingModel"].ToString().Trim() is { Length: > 0 } m ? m : null;
        await _settings.SaveAsync(s);
        Settings = s;

        EmbeddingMessage = "Embedding settings saved.";
        CheckStatus();
        return Page();
    }

    public async Task<IActionResult> OnPostSaveHiddenNamesAsync()
    {
        var s = await _settings.LoadAsync();
        var raw = Request.Form["HiddenNames"].ToString();
        s.HiddenNames = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        await _settings.SaveAsync(s);
        Settings = s;

        SecurityMessage = "Hidden names saved.";
        CheckStatus();
        await LoadSourcesAsync();
        return Page();
    }

    private async Task LoadPluginsAsync()
    {
        var settings = await _settings.LoadAsync();
        var allToolsByPlugin = _registry.AllPlugins.ToDictionary(p => p.Id, p => GetMcpToolsRaw(p));

        foreach (var plugin in _registry.AllPlugins)
        {
            var enabled = !settings.DisabledPlugins.Contains(plugin.Id);

            TrayStatus? status = null;
            if (enabled && plugin is ITrayStatusProvider provider)
            {
                try { status = await provider.GetStatusAsync(); } catch { }
            }

            var mcpTools = GetMcpToolsForPlugin(plugin, settings.DisabledMcpTools);
            var allowedSet = settings.PluginMcpAccess.GetValueOrDefault(plugin.Id) ?? [];
            var accessibleTools = new List<PluginsModel.AccessibleTool>();
            foreach (var (otherId, tools) in allToolsByPlugin)
            {
                var other = _registry.AllPlugins.First(p => p.Id == otherId);
                foreach (var (name, desc) in tools)
                    accessibleTools.Add(new PluginsModel.AccessibleTool(otherId, other.DisplayName, name, desc, allowedSet.Contains(name)));
            }

            PluginInfos.Add(new PluginsModel.PluginInfo(
                plugin, enabled,
                plugin.GetNavItems().ToList(),
                plugin.GetDashboardWidgets().ToList(),
                mcpTools, accessibleTools,
                plugin is ICaptureTarget, status));
        }
    }

    public async Task<IActionResult> OnPostTogglePluginAsync(string pluginId)
    {
        var settings = await _settings.LoadAsync();
        if (settings.DisabledPlugins.Contains(pluginId))
            settings.DisabledPlugins.Remove(pluginId);
        else
            settings.DisabledPlugins.Add(pluginId);
        await _settings.SaveAsync(settings);
        _settings.InvalidateCache();
        return Redirect("/Settings?tab=plugins");
    }

    private static List<PluginsModel.McpToolInfo> GetMcpToolsForPlugin(IPlugin plugin, HashSet<string> disabledTools) =>
        GetMcpToolsRaw(plugin)
            .Select(t => new PluginsModel.McpToolInfo(t.Name, t.Desc, !disabledTools.Contains(t.Name)))
            .ToList();

    private static List<(string Name, string Desc)> GetMcpToolsRaw(IPlugin plugin)
    {
        var tools = new List<(string, string)>();
        foreach (var toolType in plugin.GetMcpToolTypes())
        {
            foreach (var method in toolType.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance))
            {
                var attr = method.GetCustomAttribute<McpServerToolAttribute>();
                if (attr == null) continue;
                tools.Add((attr.Name ?? method.Name, method.GetCustomAttribute<DescriptionAttribute>()?.Description ?? ""));
            }
        }
        return tools;
    }

    private void CheckStatus()
    {
        ApiKeyEnvSet = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"));

        try
        {
            var proc = Process.Start(new ProcessStartInfo("which", "claude")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            });
            proc?.WaitForExit(2000);
            ClaudeCliAvailable = proc?.ExitCode == 0;
        }
        catch
        {
            ClaudeCliAvailable = false;
        }
    }
}
