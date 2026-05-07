// This is a minimal example plugin showing how to implement IDataSourceProvider.
// Copy this as a starting point for a new data source. See CONTRIBUTING.md for the full guide.
using Assistant.Sdk;

namespace ExamplePlugin;

public class ExampleDataSourceProvider : IDataSourceProvider
{
    // Unique lowercase identifier for this source type.
    public string Type => "example";

    // Display name shown in the UI.
    public string DisplayName => "Example";

    // Emoji or unicode icon shown next to the name.
    public string Icon => "🔌";

    // Config fields rendered as a form in the Data Sources page.
    public IReadOnlyList<DataSourceField> GetConfigFields() =>
    [
        new DataSourceField(
            Key: "feedUrl",
            Label: "Feed URL",
            FieldType: "text",
            Required: true,
            Placeholder: "https://example.com/data",
            Help: "URL that this source will poll for new content."),

        new DataSourceField(
            Key: "pollIntervalSeconds",
            Label: "Poll interval (seconds)",
            FieldType: "text",
            Required: false,
            Placeholder: "300",
            Help: "How often to check for new content. Default is 300 (5 minutes)."),
    ];

    // Return an instance for the given saved config.
    public IDataSourceInstance CreateInstance(DataSourceConfig config) =>
        new ExampleDataSourceInstance(config);

    // Return MCP tool types if this source should expose queryable tools to AI agents.
    public IReadOnlyList<Type> GetMcpToolTypes() => [];

    // Return null to use config-field auth. Return a URL to trigger an OAuth redirect instead.
    public string? GetAuthStartUrl(string instanceId) => null;

    // Fields to collect before starting an OAuth flow (return empty if no OAuth).
    public IReadOnlyList<DataSourceField> GetPreAuthFields() => [];
}
