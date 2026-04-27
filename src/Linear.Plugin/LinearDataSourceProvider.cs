using Assistant.Sdk;

namespace Linear;

public class LinearDataSourceProvider : IDataSourceProvider
{
    public string Type => "linear";
    public string DisplayName => "Linear";
    public string Icon => "📋";

    public IReadOnlyList<DataSourceField> GetConfigFields() =>
    [
        new DataSourceField("apiKey", "API Key", "password",
            Required: true,
            Placeholder: "lin_api_...",
            Help: "Generate a personal API key at linear.app → Settings → API."),

        new DataSourceField("teamId", "Team ID (optional)", "text",
            Required: false,
            Placeholder: "TEAM-123",
            Help: "Filter to a specific team. Leave blank for all teams."),

        new DataSourceField("pollIntervalSeconds", "Poll interval (seconds)", "text",
            Required: false,
            Placeholder: "300"),
    ];

    public IDataSourceInstance CreateInstance(DataSourceConfig config) =>
        new LinearDataSourceInstance(config);

    public IReadOnlyList<Type> GetMcpToolTypes() => [];
}
