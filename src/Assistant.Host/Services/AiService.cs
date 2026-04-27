using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic.SDK;
using Anthropic.SDK.Common;
using Anthropic.SDK.Constants;
using Anthropic.SDK.Messaging;
using Microsoft.Extensions.Logging;
using Assistant.Sdk;

namespace Assistant.Host.Services;

/// <summary>
/// Spawns `claude` CLI as a subprocess with --print mode.
/// </summary>
public class ClaudeCliService : IAiService
{
    private readonly string _model;
    private readonly ILogger<ClaudeCliService> _logger;

    public ClaudeCliService(string model, ILogger<ClaudeCliService> logger)
    {
        _model = model;
        _logger = logger;
    }

    public async Task<AiResponse> CompleteAsync(string systemPrompt, string userMessage, CancellationToken ct = default)
    {
        var args = new StringBuilder("--print --dangerously-skip-permissions");
        if (!string.IsNullOrEmpty(_model))
            args.Append($" --model {_model}");

        var fullPrompt = $"{systemPrompt}\n\n---\n\n{userMessage}";

        var psi = new ProcessStartInfo
        {
            FileName = "claude",
            Arguments = args.ToString(),
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        ApplyShellEnvironment(psi);

        _logger.LogInformation("Spawning claude CLI: claude {Args}", args);

        var process = new Process { StartInfo = psi };
        process.Start();

        await process.StandardInput.WriteAsync(fullPrompt);
        process.StandardInput.Close();

        var output = await process.StandardOutput.ReadToEndAsync(ct);
        var error = await process.StandardError.ReadToEndAsync(ct);

        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
        {
            _logger.LogError("claude CLI exited with code {Code}: {Error}", process.ExitCode, error);
            throw new Exception($"claude CLI failed (exit {process.ExitCode}): {error}");
        }

        return new AiResponse(
            Text: output.Trim(),
            InputTokens: 0,
            OutputTokens: 0,
            Model: string.IsNullOrEmpty(_model) ? "claude-cli-default" : _model
        );
    }

    private static void ApplyShellEnvironment(ProcessStartInfo psi)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        try
        {
            var shell = Environment.GetEnvironmentVariable("SHELL") ?? "/bin/zsh";
            var pathProc = Process.Start(new ProcessStartInfo
            {
                FileName = shell,
                Arguments = "-l -c \"echo $PATH\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            });
            if (pathProc != null)
            {
                var path = pathProc.StandardOutput.ReadToEnd().Trim();
                pathProc.WaitForExit(3000);
                if (!string.IsNullOrEmpty(path))
                    psi.Environment["PATH"] = path;
            }
        }
        catch { }
    }
}

/// <summary>
/// Uses the Anthropic SDK to call Claude API directly. Requires ANTHROPIC_API_KEY.
/// </summary>
public class ClaudeApiService : IAiService
{
    private readonly string _model;
    private readonly string? _apiKey;
    private readonly ILogger<ClaudeApiService> _logger;

    public ClaudeApiService(string model, string? apiKey, ILogger<ClaudeApiService> logger)
    {
        _model = model;
        _apiKey = apiKey;
        _logger = logger;
    }

    public bool SupportsAgentMode => true;

    public async Task<AiResponse> CompleteAsync(string systemPrompt, string userMessage, CancellationToken ct = default)
    {
        var client = string.IsNullOrEmpty(_apiKey)
            ? new AnthropicClient()
            : new AnthropicClient(_apiKey);

        var model = string.IsNullOrEmpty(_model) ? AnthropicModels.Claude46Sonnet : _model;

        var parameters = new MessageParameters
        {
            Messages = [new Message(RoleType.User, userMessage)],
            MaxTokens = 4096,
            Model = model,
            Stream = false,
            System = [new SystemMessage(systemPrompt)],
            Temperature = 0,
        };

        _logger.LogInformation("Calling Claude API: model={Model}", model);

        var response = await client.Messages.GetClaudeMessageAsync(parameters, ct);

        return new AiResponse(
            Text: response.Message.ToString(),
            InputTokens: (int)(response.Usage?.InputTokens ?? 0),
            OutputTokens: (int)(response.Usage?.OutputTokens ?? 0),
            Model: model
        );
    }

    public async Task<AiResponse> RunAgentAsync(string systemPrompt, string userMessage,
        IReadOnlyList<ToolDefinition> tools, Func<ToolCall, Task<string>> executeTool, CancellationToken ct)
    {
        var client = string.IsNullOrEmpty(_apiKey) ? new AnthropicClient() : new AnthropicClient(_apiKey);
        var model = string.IsNullOrEmpty(_model) ? AnthropicModels.Claude46Sonnet : _model;

        var sdkTools = tools
            .Select(t => (Anthropic.SDK.Common.Tool)new Function(t.Name, t.Description, JsonNode.Parse(t.InputSchemaJson)!))
            .ToList<Anthropic.SDK.Common.Tool>();

        var messages = new List<Message> { new(RoleType.User, userMessage) };
        int totalIn = 0, totalOut = 0;

        for (var iter = 0; iter < 10; iter++)
        {
            var parameters = new MessageParameters
            {
                Messages = messages,
                MaxTokens = 4096,
                Model = model,
                Stream = false,
                System = [new SystemMessage(systemPrompt)],
                Tools = sdkTools,
                Temperature = 0,
            };

            _logger.LogInformation("Agent iter {Iter}: calling Claude API model={Model}", iter, model);
            var response = await client.Messages.GetClaudeMessageAsync(parameters, ct);
            totalIn += (int)(response.Usage?.InputTokens ?? 0);
            totalOut += (int)(response.Usage?.OutputTokens ?? 0);

            messages.Add(response.Message);

            if (response.StopReason != "tool_use")
                return new AiResponse(response.Message.ToString(), totalIn, totalOut, model);

            // Batch all tool results into a single user message
            var toolUseBlocks = response.Message.Content.OfType<ToolUseContent>().ToList();
            var resultMsg = new Message { Role = RoleType.User, Content = [] };
            foreach (var block in toolUseBlocks)
            {
                _logger.LogInformation("Executing tool {Tool} (id={Id})", block.Name, block.Id);
                var result = await executeTool(new ToolCall(block.Id, block.Name, block.Input?.ToJsonString() ?? "{}"));
                resultMsg.Content.Add(new ToolResultContent
                {
                    ToolUseId = block.Id,
                    Content = [new TextContent { Text = result }],
                });
            }
            messages.Add(resultMsg);
        }

        throw new Exception("Agent exceeded maximum tool-call iterations");
    }
}

/// <summary>
/// OpenAI-compatible service — works with OpenAI, Ollama, and LMStudio (all use the same chat completions API).
/// </summary>
public class OpenAiCompatService : IAiService
{
    private readonly string _baseUrl;
    private readonly string _model;
    private readonly string? _apiKey;
    private readonly ILogger _logger;

    // null = standard OpenAI compat (/v1/), non-null = LMStudio native (/api/v0/) with reasoning_effort value
    private readonly string? _reasoningEffort;

    public OpenAiCompatService(string baseUrl, string model, string? apiKey, ILogger logger, string? reasoningEffort = null)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _model = model;
        _apiKey = apiKey;
        _logger = logger;
        _reasoningEffort = reasoningEffort;
    }

    public bool SupportsAgentMode => true;

    public async Task<AiResponse> RunAgentAsync(string systemPrompt, string userMessage,
        IReadOnlyList<ToolDefinition> tools, Func<ToolCall, Task<string>> executeTool, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.BaseAddress = new Uri(_baseUrl);
        if (!string.IsNullOrEmpty(_apiKey))
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        var openAiTools = tools.Select(t => (object)new
        {
            type = "function",
            function = new
            {
                name = t.Name,
                description = t.Description,
                parameters = JsonNode.Parse(t.InputSchemaJson),
            }
        }).ToArray();

        var messages = new List<object>
        {
            new { role = "system", content = systemPrompt },
            new { role = "user", content = userMessage },
        };

        int totalIn = 0, totalOut = 0;

        for (var iter = 0; iter < 10; iter++)
        {
            string endpoint;
            string body;
            if (_reasoningEffort != null)
            {
                endpoint = "/api/v0/chat/completions";
                body = JsonSerializer.Serialize(new
                {
                    model = _model,
                    messages,
                    tools = openAiTools,
                    reasoning_effort = _reasoningEffort,
                    temperature = 0,
                });
            }
            else
            {
                endpoint = "/v1/chat/completions";
                body = JsonSerializer.Serialize(new
                {
                    model = _model,
                    messages,
                    tools = openAiTools,
                    temperature = 0,
                });
            }

            _logger.LogInformation("Agent iter {Iter}: calling OpenAI-compat API model={Model}", iter, _model);
            var response = await http.PostAsync(endpoint,
                new StringContent(body, Encoding.UTF8, "application/json"), ct);
            response.EnsureSuccessStatusCode();

            var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var usage = json.RootElement.TryGetProperty("usage", out var u) ? u : (JsonElement?)null;
            totalIn += usage?.TryGetProperty("prompt_tokens", out var pt) == true ? pt.GetInt32() : 0;
            totalOut += usage?.TryGetProperty("completion_tokens", out var ct2) == true ? ct2.GetInt32() : 0;

            var choice = json.RootElement.GetProperty("choices")[0];
            var finishReason = choice.TryGetProperty("finish_reason", out var fr) ? fr.GetString() : null;
            var message = choice.GetProperty("message");

            if (finishReason != "tool_calls")
            {
                var content = message.GetProperty("content");
                var text = content.ValueKind == JsonValueKind.String
                    ? content.GetString() ?? ""
                    : string.Concat(content.EnumerateArray()
                        .Where(b => b.TryGetProperty("type", out var t) && t.GetString() == "text")
                        .Select(b => b.TryGetProperty("text", out var tv) ? tv.GetString() ?? "" : ""));
                return new AiResponse(text, totalIn, totalOut, _model);
            }

            // Append assistant message with tool_calls
            messages.Add(JsonNode.Parse(message.GetRawText())!);

            // Execute each tool call and append results
            foreach (var toolCall in message.GetProperty("tool_calls").EnumerateArray())
            {
                var id = toolCall.GetProperty("id").GetString()!;
                var name = toolCall.GetProperty("function").GetProperty("name").GetString()!;
                var arguments = toolCall.GetProperty("function").GetProperty("arguments").GetString() ?? "{}";
                _logger.LogInformation("Executing tool {Tool} (id={Id})", name, id);
                var result = await executeTool(new ToolCall(id, name, arguments));
                messages.Add(new { role = "tool", tool_call_id = id, content = result });
            }
        }

        throw new Exception("Agent exceeded maximum tool-call iterations");
    }

    public async Task<AiResponse> CompleteAsync(string systemPrompt, string userMessage, CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.BaseAddress = new Uri(_baseUrl);
        if (!string.IsNullOrEmpty(_apiKey))
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        string endpoint;
        string body;

        if (_reasoningEffort != null)
        {
            // LMStudio native API — supports reasoning_effort to control thinking
            endpoint = "/api/v0/chat/completions";
            body = JsonSerializer.Serialize(new
            {
                model = _model,
                messages = new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userMessage },
                },
                reasoning_effort = _reasoningEffort,
                temperature = 0,
            });
        }
        else
        {
            endpoint = "/v1/chat/completions";
            body = JsonSerializer.Serialize(new
            {
                model = _model,
                messages = new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userMessage },
                },
                temperature = 0,
            });
        }

        _logger.LogInformation("Calling OpenAI-compat API: {BaseUrl} model={Model}", _baseUrl, _model);

        var response = await http.PostAsync(endpoint,
            new StringContent(body, Encoding.UTF8, "application/json"), ct);

        response.EnsureSuccessStatusCode();
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var content = json.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content");

        string text;
        if (content.ValueKind == JsonValueKind.Array)
        {
            // Content blocks: pick only "text" type, skip "thinking" blocks
            text = string.Concat(content.EnumerateArray()
                .Where(b => b.TryGetProperty("type", out var t) && t.GetString() == "text")
                .Select(b => b.TryGetProperty("text", out var tv) ? tv.GetString() ?? "" : ""));
        }
        else
        {
            text = content.GetString() ?? "";
        }

        var usage = json.RootElement.TryGetProperty("usage", out var u) ? u : (JsonElement?)null;
        return new AiResponse(
            Text: text,
            InputTokens: usage?.TryGetProperty("prompt_tokens", out var pt) == true ? pt.GetInt32() : 0,
            OutputTokens: usage?.TryGetProperty("completion_tokens", out var ct2) == true ? ct2.GetInt32() : 0,
            Model: _model
        );
    }
}
