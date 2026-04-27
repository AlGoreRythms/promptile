namespace Assistant.Sdk;

/// <summary>
/// Resolves an IAiService for a given agent name, or returns the default.
/// Allows plugins to use named agents without referencing AgentRunner directly.
/// </summary>
public interface IAiServiceResolver
{
    /// <summary>
    /// Get an IAiService. If agentName is null/empty, returns the default host AI service.
    /// If agentName is specified, returns a service backed by that agent's provider/model.
    /// </summary>
    Task<IAiService> GetServiceAsync(string? agentName = null);

    /// <summary>
    /// Returns names of all available agents, for UI dropdowns.
    /// </summary>
    Task<List<string>> GetAvailableAgentsAsync();
}
