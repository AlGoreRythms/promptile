namespace Assistant.Sdk;

/// <summary>
/// Allows a plugin to query which MCP tools it's allowed to use when calling Claude.
/// The host resolves this per-plugin based on user configuration.
/// </summary>
public interface IMcpToolAccess
{
    /// <summary>
    /// Returns the set of MCP tool names this plugin is allowed to use.
    /// Empty set means no tools available.
    /// </summary>
    IReadOnlySet<string> GetAllowedTools(string callingPluginId);
}
