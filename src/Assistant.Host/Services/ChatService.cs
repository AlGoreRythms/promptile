using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Assistant.Sdk;
using Microsoft.Extensions.Logging;

namespace Assistant.Host.Services;

public class ChatService
{
    private readonly IAiServiceResolver _resolver;
    private readonly IMcpToolExecutor _executor;
    private readonly ConversationStore _store;
    private readonly SettingsService _settings;
    private readonly IInformationStore _infoStore;
    private readonly ILogger<ChatService> _logger;

    private readonly ConcurrentDictionary<string, byte> _inProgress = new();

    private const int MaxToolRounds = 10;

    private const string SystemPromptTemplate = """
        You are a personal AI assistant. Today is {today}. The user's emails and Slack messages are synced to a local information store as daily markdown files. Use your tools to read that data before answering.

        HOW TO READ DATA:
        1. Call store_list_sources to see what sources exist and their types. Available sources: {sources}
        2. Call store_list_data(sourceName, sourceType) to list available daily data files (e.g. "2026-04-21.md"). Files are sorted newest first.
        3. Call store_read_data(sourceName, sourceType, filename) to read the file contents.
        4. Read the most recent files when asked to summarize recent activity.

        IMPORTANT: Synced data is in store_list_data / store_read_data. The store_list_notes / store_read_note tools are for AI-written notes only, NOT for synced emails or Slack messages.
        For questions about who the user knows or works with, call store_list_entities("people") or store_list_entities("companies") first.

        To call a tool, respond ONLY with a <tool_call> block (nothing else in your response):
        <tool_call>{"tool": "tool_name", "args": {"param": "value"}}</tool_call>

        After receiving a result, call another tool or give your final answer without a <tool_call> block.

        AVAILABLE TOOLS:
        {tools}
        """;

    public ChatService(
        IAiServiceResolver resolver,
        IMcpToolExecutor executor,
        ConversationStore store,
        SettingsService settings,
        IInformationStore infoStore,
        ILogger<ChatService> logger)
    {
        _resolver = resolver;
        _executor = executor;
        _store = store;
        _settings = settings;
        _infoStore = infoStore;
        _logger = logger;
    }

    public async Task<string> StartAsync(string? conversationId, string userMessage, string agentTier = "heavy")
    {
        var conv = string.IsNullOrEmpty(conversationId)
            ? await _store.CreateAsync(userMessage)
            : (await _store.GetAsync(conversationId) ?? await _store.CreateAsync(userMessage));

        conv.Messages.Add(new ChatMessage("user", userMessage, DateTimeOffset.UtcNow));
        await _store.SaveAsync(conv with { Updated = DateTimeOffset.UtcNow });

        _inProgress[conv.Id] = 0;
        _ = Task.Run(async () =>
        {
            try { await ProcessAsync(conv.Id, userMessage, agentTier); }
            finally { _inProgress.TryRemove(conv.Id, out _); }
        });

        return conv.Id;
    }

    public async Task<(bool Pending, string? Reply, List<ToolCallRecord>? ToolCalls)> PollAsync(string conversationId)
    {
        if (_inProgress.ContainsKey(conversationId))
            return (true, null, null);

        var conv = await _store.GetAsync(conversationId);
        var last = conv?.Messages.LastOrDefault(m => m.Role == "assistant");
        return (false, last?.Content, last?.ToolCalls);
    }

    private async Task ProcessAsync(string conversationId, string userMessage, string agentTier = "heavy")
    {
        var conv = await _store.GetAsync(conversationId);
        if (conv == null) return;

        var service = await _resolver.GetServiceAsync(agentTier);
        var tools = _executor.GetAllToolDefinitions()
            .Where(t => t.Name.StartsWith("store_"))
            .ToList();
        var s = await _settings.LoadAsync();
        var activeCtx = s.EphemeralContext.Where(e => e.ExpiresAt > DateTimeOffset.UtcNow).ToList();
        var systemPrompt = BuildSystemPrompt(tools, s.UserProfile, s.ChatSystemPrompt, activeCtx, _infoStore);
        var toolCalls = new List<ToolCallRecord>();

        // History excludes the user message we just saved (it's appended below)
        var history = conv.Messages.Take(conv.Messages.Count - 1).ToList();
        var workingPrompt = BuildHistoryPrompt(history) + $"User: {userMessage}\nAssistant:";

        for (int round = 0; round < MaxToolRounds; round++)
        {
            var response = await service.CompleteAsync(systemPrompt, workingPrompt, CancellationToken.None);
            var text = response.Text.Trim();

            var match = Regex.Match(text, @"<tool_call>\s*(\{.*?\})\s*</tool_call>", RegexOptions.Singleline);

            if (!match.Success)
            {
                conv.Messages.Add(new ChatMessage("assistant", text, DateTimeOffset.UtcNow,
                    toolCalls.Count > 0 ? toolCalls : null));
                await _store.SaveAsync(conv with { Updated = DateTimeOffset.UtcNow });
                return;
            }

            var argsJson = match.Groups[1].Value;
            string toolName = "";
            string toolResult;
            try
            {
                var doc = JsonDocument.Parse(argsJson);
                toolName = doc.RootElement.GetProperty("tool").GetString() ?? "";
                var argsDict = new Dictionary<string, object?>();
                if (doc.RootElement.TryGetProperty("args", out var argsEl) && argsEl.ValueKind == JsonValueKind.Object)
                    foreach (var prop in argsEl.EnumerateObject())
                        argsDict[prop.Name] = prop.Value;
                _logger.LogInformation("[Chat] Calling tool: {Tool}", toolName);
                toolResult = await _executor.ExecuteToolAsync(toolName, argsDict);
            }
            catch (Exception ex)
            {
                toolResult = JsonSerializer.Serialize(new { error = ex.Message });
            }

            toolCalls.Add(new ToolCallRecord(toolName, argsJson, toolResult));
            workingPrompt += $"\n{text}\n[Tool result for {toolName}: {toolResult}]\nAssistant:";
        }

        const string fallback = "I wasn't able to complete this request within the tool call limit.";
        conv.Messages.Add(new ChatMessage("assistant", fallback, DateTimeOffset.UtcNow,
            toolCalls.Count > 0 ? toolCalls : null));
        await _store.SaveAsync(conv with { Updated = DateTimeOffset.UtcNow });
    }

    // Kept for any callers that still need synchronous send behaviour
    public async Task<(string Reply, string ConversationId, List<ToolCallRecord> ToolCalls)> SendAsync(
        string? conversationId, string userMessage, CancellationToken ct)
    {
        var conv = string.IsNullOrEmpty(conversationId)
            ? await _store.CreateAsync(userMessage)
            : (await _store.GetAsync(conversationId) ?? await _store.CreateAsync(userMessage));

        var service = await _resolver.GetServiceAsync("heavy");
        // Chat reads from the local store — live API tools (slack_*, gmail_*) add noise
        var tools = _executor.GetAllToolDefinitions()
            .Where(t => t.Name.StartsWith("store_"))
            .ToList();
        var s = await _settings.LoadAsync();
        var activeCtx = s.EphemeralContext.Where(e => e.ExpiresAt > DateTimeOffset.UtcNow).ToList();
        var systemPrompt = BuildSystemPrompt(tools, s.UserProfile, s.ChatSystemPrompt, activeCtx, _infoStore);
        var toolCalls = new List<ToolCallRecord>();

        // Build the working prompt (accumulated across tool rounds within this turn)
        var workingPrompt = BuildHistoryPrompt(conv.Messages) + $"User: {userMessage}\nAssistant:";

        for (int round = 0; round < MaxToolRounds; round++)
        {
            var response = await service.CompleteAsync(systemPrompt, workingPrompt, ct);
            var text = response.Text.Trim();

            var match = Regex.Match(text, @"<tool_call>\s*(\{.*?\})\s*</tool_call>",
                RegexOptions.Singleline);

            if (!match.Success)
            {
                // Final answer — persist and return
                conv.Messages.Add(new ChatMessage("user", userMessage, DateTimeOffset.UtcNow));
                conv.Messages.Add(new ChatMessage("assistant", text, DateTimeOffset.UtcNow,
                    toolCalls.Count > 0 ? toolCalls : null));
                await _store.SaveAsync(conv with { Updated = DateTimeOffset.UtcNow });
                return (text, conv.Id, toolCalls);
            }

            // Parse and execute the tool call
            var argsJson = match.Groups[1].Value;
            string toolName = "";
            string toolResult;
            try
            {
                var doc = JsonDocument.Parse(argsJson);
                toolName = doc.RootElement.GetProperty("tool").GetString() ?? "";
                var argsDict = new Dictionary<string, object?>();
                if (doc.RootElement.TryGetProperty("args", out var argsEl)
                    && argsEl.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in argsEl.EnumerateObject())
                        argsDict[prop.Name] = prop.Value;
                }
                _logger.LogInformation("[Chat] Calling tool: {Tool}", toolName);
                toolResult = await _executor.ExecuteToolAsync(toolName, argsDict);
            }
            catch (Exception ex)
            {
                toolResult = JsonSerializer.Serialize(new { error = ex.Message });
            }

            toolCalls.Add(new ToolCallRecord(toolName, argsJson, toolResult));
            workingPrompt += $"\n{text}\n[Tool result for {toolName}: {toolResult}]\nAssistant:";
        }

        // Exceeded max rounds
        const string fallback = "I wasn't able to complete this request within the tool call limit.";
        conv.Messages.Add(new ChatMessage("user", userMessage, DateTimeOffset.UtcNow));
        conv.Messages.Add(new ChatMessage("assistant", fallback, DateTimeOffset.UtcNow,
            toolCalls.Count > 0 ? toolCalls : null));
        await _store.SaveAsync(conv with { Updated = DateTimeOffset.UtcNow });
        return (fallback, conv.Id, toolCalls);
    }

    private static string BuildHistoryPrompt(List<ChatMessage> messages)
    {
        if (messages.Count == 0) return "";

        var sb = new StringBuilder();
        sb.AppendLine("CONVERSATION HISTORY:");
        foreach (var msg in messages)
        {
            var role = msg.Role == "user" ? "User" : "Assistant";
            sb.AppendLine($"{role}: {msg.Content}");
        }
        sb.AppendLine();
        return sb.ToString();
    }

    private static string BuildSystemPrompt(List<McpToolDef> tools, string userProfile, string customPrompt,
        List<EphemeralContextEntry>? ephemeralContext, IInformationStore infoStore)
    {
        var sources = string.Join(", ", infoStore.ListSources()
            .Select(s => $"\"{s.Name}\" ({string.Join(", ", s.Types)})")
        );
        var toolList = new StringBuilder();
        foreach (var tool in tools)
        {
            var paramStr = tool.Parameters.Count > 0
                ? string.Join(", ", tool.Parameters.Select(p =>
                    $"{p.Key}: {p.Value.Type}{(p.Value.Required ? "" : "?")} — {p.Value.Description}"))
                : "no parameters";
            toolList.AppendLine($"- {tool.Name}({paramStr}): {tool.Description}");
        }

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(userProfile))
            parts.Add("ABOUT THE USER:\n" + userProfile.Trim());
        if (ephemeralContext is { Count: > 0 })
            parts.Add("CURRENT CONTEXT (user-set, temporary):\n" +
                      string.Join("\n", ephemeralContext.Select(e => $"- {e.Text}")));
        if (!string.IsNullOrWhiteSpace(customPrompt))
            parts.Add(customPrompt.Trim());
        parts.Add(SystemPromptTemplate
            .Replace("{today}", DateTime.Now.ToString("dddd, MMMM d, yyyy HH:mm"))
            .Replace("{sources}", string.IsNullOrEmpty(sources) ? "none configured" : sources)
            .Replace("{tools}", toolList.ToString().TrimEnd()));
        return string.Join("\n\n", parts);
    }
}
