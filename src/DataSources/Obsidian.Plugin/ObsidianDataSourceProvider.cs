using Assistant.Sdk;

namespace Obsidian;

public class ObsidianDataSourceProvider : IDataSourceProvider
{
    public string Type => "obsidian";
    public string DisplayName => "Obsidian Vault";
    public string Icon => "📓";

    public IReadOnlyList<DataSourceField> GetConfigFields() =>
    [
        new("vaultPath", "Vault path", "text", Required: true,
            Placeholder: "/Users/you/Documents/MyVault",
            Help: "Full path to your Obsidian vault directory."),
        new("excludeFolders", "Exclude folders", "text", Required: false,
            Placeholder: ".obsidian, templates, archive",
            Help: "Comma-separated folder names to skip."),
        new("pollIntervalSeconds", "Full scan interval (seconds)", "text", Required: false,
            Placeholder: "300",
            Help: "How often to do a full scan in addition to file-watcher events."),
    ];

    public IDataSourceInstance CreateInstance(DataSourceConfig config) =>
        new ObsidianDataSourceInstance(config);
}
