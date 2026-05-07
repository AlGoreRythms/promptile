namespace Promptile.Sdk;

/// <summary>
/// Provides AI configuration to plugins that need model/provider info.
/// </summary>
public interface IAiSettings
{
    Task<AiSettingsData> GetAsync();
}

public record AiSettingsData(string AiProvider, string Model, string? AnthropicApiKey);
