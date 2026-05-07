using Promptile.Sdk;

namespace Promptile.Host.Services;

public class McpToolAccessService : IMcpToolAccess
{
    private readonly SettingsService _settings;

    public McpToolAccessService(SettingsService settings) => _settings = settings;

    public IReadOnlySet<string> GetAllowedTools(string callingPluginId)
    {
        var settings = _settings.LoadSync();
        if (settings.PluginMcpAccess.TryGetValue(callingPluginId, out var allowed))
            return allowed;

        // No config yet — nothing allowed by default
        return new HashSet<string>();
    }
}
