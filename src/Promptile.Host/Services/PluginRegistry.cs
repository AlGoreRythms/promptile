using Promptile.Sdk;

namespace Promptile.Host.Services;

/// <summary>
/// Collects nav items, dashboard widgets, and capture targets from all loaded plugins.
/// Respects disabled plugin/tool settings.
/// </summary>
public class PluginRegistry
{
    private readonly List<IPlugin> _plugins = [];
    private readonly List<(NavItem Item, IPlugin Plugin)> _navItems = [];
    private readonly List<(DashboardWidget Widget, IPlugin Plugin)> _widgets = [];
    private readonly Dictionary<string, ICaptureTarget> _captureTargets = [];
    private readonly SettingsService _settings;

    public PluginRegistry(SettingsService settings)
    {
        _settings = settings;
    }

    /// <summary>All registered plugins (regardless of enabled state).</summary>
    public IReadOnlyList<IPlugin> AllPlugins => _plugins;

    /// <summary>Only enabled plugins.</summary>
    public IReadOnlyList<IPlugin> Plugins =>
        _plugins.Where(p => !IsPluginDisabled(p.Id)).ToList();

    public IReadOnlyList<NavItem> NavItems =>
        _navItems.Where(x => !IsPluginDisabled(x.Plugin.Id)).Select(x => x.Item).ToList();

    public IReadOnlyList<DashboardWidget> Widgets =>
        _widgets.Where(x => !IsPluginDisabled(x.Plugin.Id)).Select(x => x.Widget).ToList();

    public IReadOnlyDictionary<string, ICaptureTarget> CaptureTargets =>
        _captureTargets.Where(x => !IsPluginDisabled(x.Key)).ToDictionary(x => x.Key, x => x.Value);

    public IReadOnlyList<(IPlugin Plugin, List<DashboardWidget> Widgets)> WidgetsByPlugin =>
        _widgets
            .Where(x => !IsPluginDisabled(x.Plugin.Id))
            .GroupBy(x => x.Plugin)
            .Select(g => (g.Key, g.Select(x => x.Widget).ToList()))
            .ToList();

    public IReadOnlyList<(IPlugin Plugin, List<NavItem> Items)> NavItemsByPlugin
    {
        get
        {
            var groups = _navItems
                .Where(x => !IsPluginDisabled(x.Plugin.Id))
                .GroupBy(x => x.Plugin)
                .Select(g => (Plugin: g.Key, Items: g.Select(x => x.Item).ToList()))
                .ToList();

            var order = _settings.LoadSync().PluginOrder;
            if (order.Count > 0)
            {
                groups.Sort((a, b) =>
                {
                    var ai = order.IndexOf(a.Plugin.Id);
                    var bi = order.IndexOf(b.Plugin.Id);
                    if (ai < 0) ai = int.MaxValue;
                    if (bi < 0) bi = int.MaxValue;
                    return ai.CompareTo(bi);
                });
            }

            return groups;
        }
    }

    public void Register(IPlugin plugin)
    {
        _plugins.Add(plugin);

        foreach (var nav in plugin.GetNavItems())
            _navItems.Add((nav, plugin));

        foreach (var widget in plugin.GetDashboardWidgets())
            _widgets.Add((widget, plugin));

        if (plugin is ICaptureTarget capture)
            _captureTargets[plugin.Id] = capture;
    }

    public ICaptureTarget? DefaultCaptureTarget =>
        CaptureTargets.Values.FirstOrDefault();

    public bool IsPluginDisabled(string pluginId) =>
        _settings.LoadSync().DisabledPlugins.Contains(pluginId);

    public bool IsMcpToolDisabled(string toolName) =>
        _settings.LoadSync().DisabledMcpTools.Contains(toolName);
}
