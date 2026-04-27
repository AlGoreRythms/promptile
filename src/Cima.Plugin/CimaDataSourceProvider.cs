using Assistant.Sdk;
using Cima.Mcp;

namespace Cima;

public class CimaDataSourceProvider : IDataSourceProvider
{
    public string Type => "cima";
    public string DisplayName => "CIMA Memory";
    public string Icon => "🧠";

    public IReadOnlyList<DataSourceField> GetConfigFields() =>
    [
        new DataSourceField("enricherTier", "Enrichment model", "select",
            Required: false,
            Help: "Which AI tier to use when extracting memories from synced data.",
            Options: ["light", "medium", "heavy"]),
        new DataSourceField("scanIntervalMinutes", "Scan interval (minutes)", "text",
            Required: false,
            Placeholder: "30",
            Help: "How often the agent scans data source documents for new memories (default: 30 minutes)."),
        new DataSourceField("persistDir", "Memory directory", "text",
            Required: false,
            Placeholder: "(defaults to store root)",
            Help: "Override the directory where CIMA JSON files are stored. Leave blank to use the configured store root."),
    ];

    public IDataSourceInstance CreateInstance(DataSourceConfig config) =>
        new CimaDataSourceInstance(config);

    public IReadOnlyList<Type> GetMcpToolTypes() => [typeof(CimaMcpTools)];
}
