namespace Promptile.Sdk;

public record DataSourceConfig(
    string Id,
    string Type,
    string Name,
    bool Enabled,
    Dictionary<string, string> Config
);

public record DataSourceField(
    string Key,
    string Label,
    string FieldType,      // "text" | "password" | "textarea" | "select"
    bool Required = true,
    string? Placeholder = null,
    string? Help = null,
    string[]? Options = null  // for "select" type
);

public record DataSourceStatus(bool Connected, string? Message = null, string? AuthUrl = null);
