using Assistant.Sdk;

namespace Asana;

public class AsanaDataSourceProvider : IDataSourceProvider
{
    public string Type => "asana";
    public string DisplayName => "Asana";
    public string Icon => "📋";

    public string? GetAuthStartUrl(string instanceId) => $"/api/asana-authorize/{instanceId}";

    public IReadOnlyList<DataSourceField> GetPreAuthFields() =>
    [
        new DataSourceField("clientId", "Client ID", "text",
            Required: true,
            Placeholder: "123456789...",
            Help: "From your Asana app at app.asana.com/0/my-apps. Add http://localhost:5309/api/asana-callback as a redirect URI."),
        new DataSourceField("clientSecret", "Client Secret", "password",
            Required: true,
            Placeholder: "...",
            Help: "Client secret from the same Asana app."),
    ];

    public IReadOnlyList<DataSourceField> GetConfigFields() =>
    [
        new DataSourceField("workspaceGid", "Workspace GID (optional)", "text",
            Required: false,
            Placeholder: "123456789",
            Help: "Leave blank to use your first workspace. Find it in app.asana.com URLs."),
        new DataSourceField("pollIntervalSeconds", "Poll interval (seconds)", "text",
            Required: false,
            Placeholder: "300"),
    ];

    public IDataSourceInstance CreateInstance(DataSourceConfig config) =>
        new AsanaDataSourceInstance(config);

    public IReadOnlyList<Type> GetMcpToolTypes() => [typeof(Mcp.AsanaMcpTools)];
}
