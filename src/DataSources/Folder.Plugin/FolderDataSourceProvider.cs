using Assistant.Sdk;

namespace Folder;

public class FolderDataSourceProvider : IDataSourceProvider
{
    public string Type => "folder";
    public string DisplayName => "Folder";
    public string Icon => "📁";

    public IReadOnlyList<DataSourceField> GetConfigFields() =>
    [
        new DataSourceField("path", "Folder Path", "text",
            Required: true,
            Placeholder: "/Users/you/code/my-project",
            Help: "Absolute path to the codebase. Used as the working directory for opencode."),

        new DataSourceField("opencodeCommand", "OpenCode command (optional)", "text",
            Required: false,
            Placeholder: "opencode",
            Help: "Command used to invoke opencode. Defaults to 'opencode' if left blank."),
    ];

    public IDataSourceInstance CreateInstance(DataSourceConfig config) =>
        new FolderDataSourceInstance(config);

    public IReadOnlyList<Type> GetMcpToolTypes() => [];
}
