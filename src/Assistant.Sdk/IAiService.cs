namespace Assistant.Sdk;

public record AiResponse(string Text, int InputTokens, int OutputTokens, string Model);
public record ToolDefinition(string Name, string Description, string InputSchemaJson);
public record ToolCall(string Id, string Name, string InputJson);

public interface IAiService
{
    bool SupportsAgentMode => false;

    Task<AiResponse> CompleteAsync(string systemPrompt, string userMessage, CancellationToken ct = default);

    Task<AiResponse> RunAgentAsync(
        string systemPrompt,
        string userMessage,
        IReadOnlyList<ToolDefinition> tools,
        Func<ToolCall, Task<string>> executeTool,
        CancellationToken ct = default) => CompleteAsync(systemPrompt, userMessage, ct);
}
