namespace Assistant.Sdk;

/// <summary>
/// Registered once per data source type. Creates instances from config.
/// </summary>
public interface IDataSourceProvider
{
    string Type { get; }
    string DisplayName { get; }
    string Icon { get; }

    IReadOnlyList<DataSourceField> GetConfigFields();
    IDataSourceInstance CreateInstance(DataSourceConfig config);

    /// <summary>MCP tool classes that handle all instances of this type via IDataSourceManager.</summary>
    IReadOnlyList<Type> GetMcpToolTypes() => [];

    /// <summary>
    /// If non-null, this provider uses an external OAuth flow instead of generic config fields.
    /// The URL should initiate auth for a given instance ID.
    /// Return null to use the default config-field form.
    /// </summary>
    string? GetAuthStartUrl(string instanceId) => null;

    /// <summary>
    /// Config fields shown when creating a new instance before OAuth has run.
    /// Only used when GetAuthStartUrl returns non-null.
    /// </summary>
    IReadOnlyList<DataSourceField> GetPreAuthFields() => [];
}
