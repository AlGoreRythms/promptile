using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Assistant.Sdk;

/// <summary>
/// Contract for an Assistant platform plugin. Plugins register services, pages,
/// dashboard widgets, navigation items, MCP tools, and background services.
/// </summary>
public interface IPlugin
{
    string Id { get; }
    string DisplayName { get; }
    string Icon { get; }

    void ConfigureServices(IServiceCollection services, PluginContext context);
    void ConfigureApp(WebApplication app, PluginContext context);

    IReadOnlyList<Type> GetMcpToolTypes();
    IReadOnlyList<DashboardWidget> GetDashboardWidgets();
    IReadOnlyList<NavItem> GetNavItems();
    IReadOnlyList<Type> GetBackgroundServices() => [];
}

/// <summary>
/// Plugins that accept quick-capture input from the host dashboard implement this.
/// </summary>
public interface ICaptureTarget
{
    string CaptureLabel { get; }
    Task CaptureAsync(string content, string? source);
}

public record PluginContext(string DataDirectory, string HostDataDirectory, string DatabasePath);

public record DashboardWidget(string Id, string Title, int Order, string PartialViewName, string Section, int? PollSeconds = null);

public record NavItem(string Label, string Href, int Order, string? Group = null);
