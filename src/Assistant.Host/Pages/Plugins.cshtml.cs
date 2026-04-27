using System.ComponentModel;
using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ModelContextProtocol.Server;
using Assistant.Host.Services;
using Assistant.Sdk;

namespace Assistant.Host.Pages;

[IgnoreAntiforgeryToken]
public class PluginsModel : PageModel
{
    private readonly PluginRegistry _registry;
    private readonly SettingsService _settings;

    public PluginsModel(PluginRegistry registry, SettingsService settings)
    {
        _registry = registry;
        _settings = settings;
    }

    public record McpToolInfo(string Name, string Description, bool Enabled);

    /// <summary>A tool from another plugin that this plugin can access when calling Claude.</summary>
    public record AccessibleTool(string PluginId, string PluginName, string ToolName, string Description, bool Allowed);

    public record PluginInfo(
        IPlugin Plugin,
        bool Enabled,
        List<NavItem> NavItems,
        List<DashboardWidget> Widgets,
        List<McpToolInfo> McpTools,
        List<AccessibleTool> AccessibleTools,
        bool IsCaptureTarget,
        TrayStatus? Status);

    public List<PluginInfo> PluginInfos { get; set; } = [];

    public async Task OnGetAsync()
    {
        var settings = await _settings.LoadAsync();

        // Build a map of all MCP tools across all plugins
        var allToolsByPlugin = new Dictionary<string, List<(string Name, string Desc)>>();
        foreach (var plugin in _registry.AllPlugins)
        {
            allToolsByPlugin[plugin.Id] = GetMcpToolsRaw(plugin);
        }

        foreach (var plugin in _registry.AllPlugins)
        {
            var enabled = !settings.DisabledPlugins.Contains(plugin.Id);

            TrayStatus? status = null;
            if (enabled && plugin is ITrayStatusProvider provider)
            {
                try { status = await provider.GetStatusAsync(); }
                catch { }
            }

            var navItems = plugin.GetNavItems().ToList();
            var widgets = plugin.GetDashboardWidgets().ToList();
            var mcpTools = GetMcpTools(plugin, settings.DisabledMcpTools);

            // Build accessible tools: all tools from ALL plugins (including self)
            var allowedSet = settings.PluginMcpAccess.GetValueOrDefault(plugin.Id) ?? [];
            var accessibleTools = new List<AccessibleTool>();
            foreach (var (otherPluginId, tools) in allToolsByPlugin)
            {
                var otherPlugin = _registry.AllPlugins.First(p => p.Id == otherPluginId);
                foreach (var (toolName, toolDesc) in tools)
                {
                    accessibleTools.Add(new AccessibleTool(
                        otherPluginId, otherPlugin.DisplayName,
                        toolName, toolDesc,
                        allowedSet.Contains(toolName)));
                }
            }

            PluginInfos.Add(new PluginInfo(
                plugin, enabled, navItems, widgets, mcpTools, accessibleTools,
                plugin is ICaptureTarget, status));
        }
    }

    // --- Plugin toggle ---

    public async Task<IActionResult> OnPostTogglePluginAsync(string pluginId)
    {
        var settings = await _settings.LoadAsync();
        if (settings.DisabledPlugins.Contains(pluginId))
            settings.DisabledPlugins.Remove(pluginId);
        else
            settings.DisabledPlugins.Add(pluginId);
        await _settings.SaveAsync(settings);
        _settings.InvalidateCache();
        return RedirectToPage();
    }

    // --- Tool toggle handlers (all return the refreshed tools partial) ---

    public async Task<IActionResult> OnPostEnableAllToolsAsync(string pluginId) =>
        await ToggleAndRefresh(pluginId, async settings => {
            var plugin = _registry.AllPlugins.FirstOrDefault(p => p.Id == pluginId);
            if (plugin != null) foreach (var t in GetMcpToolsRaw(plugin)) settings.DisabledMcpTools.Remove(t.Name);
        });

    public async Task<IActionResult> OnPostDisableAllToolsAsync(string pluginId) =>
        await ToggleAndRefresh(pluginId, async settings => {
            var plugin = _registry.AllPlugins.FirstOrDefault(p => p.Id == pluginId);
            if (plugin != null) foreach (var t in GetMcpToolsRaw(plugin)) settings.DisabledMcpTools.Add(t.Name);
        });

    public async Task<IActionResult> OnPostToggleToolAsync(string pluginId, string toolName) =>
        await ToggleAndRefresh(pluginId, async settings => {
            if (settings.DisabledMcpTools.Contains(toolName)) settings.DisabledMcpTools.Remove(toolName);
            else settings.DisabledMcpTools.Add(toolName);
        });

    public async Task<IActionResult> OnPostToggleAccessAsync(string pluginId, string toolName) =>
        await ToggleAndRefresh(pluginId, async settings => {
            if (!settings.PluginMcpAccess.ContainsKey(pluginId)) settings.PluginMcpAccess[pluginId] = [];
            var set = settings.PluginMcpAccess[pluginId];
            if (set.Contains(toolName)) set.Remove(toolName); else set.Add(toolName);
        });

    public async Task<IActionResult> OnPostEnableAllAccessAsync(string pluginId) =>
        await ToggleAndRefresh(pluginId, async settings => {
            if (!settings.PluginMcpAccess.ContainsKey(pluginId)) settings.PluginMcpAccess[pluginId] = [];
            var set = settings.PluginMcpAccess[pluginId];
            foreach (var p in _registry.AllPlugins) foreach (var t in GetMcpToolsRaw(p)) set.Add(t.Name);
        });

    public async Task<IActionResult> OnPostDisableAllAccessAsync(string pluginId) =>
        await ToggleAndRefresh(pluginId, async settings => {
            settings.PluginMcpAccess[pluginId] = [];
        });

    private async Task<IActionResult> ToggleAndRefresh(string pluginId, Func<AssistantSettings, Task> mutate)
    {
        var settings = await _settings.LoadAsync();
        await mutate(settings);
        await _settings.SaveAsync(settings);
        _settings.InvalidateCache();

        // Re-build the PluginInfo for this plugin and return the tools partial
        var plugin = _registry.AllPlugins.FirstOrDefault(p => p.Id == pluginId);
        if (plugin == null) return Content("");

        settings = _settings.LoadSync(); // reload after invalidation
        var mcpTools = GetMcpTools(plugin, settings.DisabledMcpTools);
        var allowedSet = settings.PluginMcpAccess.GetValueOrDefault(pluginId) ?? [];
        var allToolsByPlugin = _registry.AllPlugins.ToDictionary(p => p.Id, p => GetMcpToolsRaw(p));
        var accessibleTools = new List<AccessibleTool>();
        foreach (var (otherId, tools) in allToolsByPlugin)
        {
            var other = _registry.AllPlugins.First(p => p.Id == otherId);
            foreach (var (name, desc) in tools)
                accessibleTools.Add(new AccessibleTool(otherId, other.DisplayName, name, desc, allowedSet.Contains(name)));
        }

        var info = new PluginInfo(plugin, true, [], [], mcpTools, accessibleTools, false, null);
        return Partial("Shared/_PluginToolsColumns", info);
    }

    // --- Helpers ---

    private static List<McpToolInfo> GetMcpTools(IPlugin plugin, HashSet<string> disabledTools)
    {
        return GetMcpToolsRaw(plugin)
            .Select(t => new McpToolInfo(t.Name, t.Desc, !disabledTools.Contains(t.Name)))
            .ToList();
    }

    private static List<(string Name, string Desc)> GetMcpToolsRaw(IPlugin plugin)
    {
        var tools = new List<(string, string)>();
        foreach (var toolType in plugin.GetMcpToolTypes())
        {
            foreach (var method in toolType.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance))
            {
                var attr = method.GetCustomAttribute<McpServerToolAttribute>();
                if (attr == null) continue;

                var name = attr.Name ?? method.Name;
                var desc = method.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "";
                tools.Add((name, desc));
            }
        }
        return tools;
    }
}
