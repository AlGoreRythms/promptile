namespace Assistant.Sdk;

/// <summary>
/// Executes MCP tool calls and provides tool definitions.
/// Implemented by the host, consumed by plugins that need to call tools.
/// </summary>
public interface IMcpToolExecutor
{
    /// <summary>
    /// Returns tool definitions for tools the calling plugin is allowed to use.
    /// </summary>
    List<McpToolDef> GetToolDefinitions(string callingPluginId);

    /// <summary>
    /// Executes a tool call and returns the result as a JSON string.
    /// </summary>
    Task<string> ExecuteToolAsync(string toolName, Dictionary<string, object?> arguments);

    /// <summary>
    /// Returns all tool definitions with no access-control filter. Used by the host chat feature.
    /// </summary>
    List<McpToolDef> GetAllToolDefinitions();
}

public record McpToolDef(string Name, string Description, Dictionary<string, McpToolParam> Parameters);
public record McpToolParam(string Type, string Description, bool Required);
