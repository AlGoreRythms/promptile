using Promptile.Sdk;

namespace Promptile.Host.Services;

public class AiServiceResolver : IAiServiceResolver
{
    private readonly SettingsService _settings;
    private readonly ILoggerFactory _loggerFactory;

    public AiServiceResolver(SettingsService settings, ILoggerFactory loggerFactory)
    {
        _settings = settings;
        _loggerFactory = loggerFactory;
    }

    public async Task<IAiService> GetServiceAsync(string? agentName = null)
    {
        var s = await _settings.LoadAsync();
        var tier = agentName?.ToLowerInvariant() switch
        {
            "light"  => s.Light,
            "medium" => s.Medium,
            _        => s.Heavy,  // default to heavy
        };
        return BuildService(tier);
    }

    public Task<List<string>> GetAvailableAgentsAsync() =>
        Task.FromResult(new List<string> { "light", "medium", "heavy" });

    private IAiService BuildService(AgentTierSettings tier) => tier.Provider switch
    {
        "claude-api" => new ClaudeApiService(
            tier.Model,
            tier.ApiKey,
            _loggerFactory.CreateLogger<ClaudeApiService>()),
        "openai" => new OpenAiCompatService(
            "https://api.openai.com",
            tier.Model.Length > 0 ? tier.Model : "gpt-4o",
            tier.ApiKey,
            _loggerFactory.CreateLogger<OpenAiCompatService>()),
        "ollama" => new OpenAiCompatService(
            !string.IsNullOrEmpty(tier.BaseUrl) ? tier.BaseUrl : "http://localhost:11434",
            tier.Model.Length > 0 ? tier.Model : "llama3",
            apiKey: null,
            _loggerFactory.CreateLogger<OpenAiCompatService>()),
        "lmstudio" => new OpenAiCompatService(
            !string.IsNullOrEmpty(tier.BaseUrl) ? tier.BaseUrl : "http://localhost:1234",
            tier.Model,
            apiKey: null,
            _loggerFactory.CreateLogger<OpenAiCompatService>(),
            reasoningEffort: tier.Thinking ? "high" : "none"),
        _ => new ClaudeCliService(tier.Model, _loggerFactory.CreateLogger<ClaudeCliService>()),
    };
}
