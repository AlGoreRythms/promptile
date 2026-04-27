using Assistant.Sdk;

namespace GitHub;

public class GitHubDataSourceProvider : IDataSourceProvider
{
    public string Type => "github";
    public string DisplayName => "GitHub";
    public string Icon => "🐙";

    public IReadOnlyList<DataSourceField> GetConfigFields() =>
    [
        new DataSourceField("token", "Personal Access Token", "password",
            Required: true,
            Placeholder: "ghp_...",
            Help: "Generate at github.com → Settings → Developer settings → Personal access tokens. Needs repo and read:user scopes."),

        new DataSourceField("repos", "Repositories (optional)", "text",
            Required: false,
            Placeholder: "owner/repo1, owner/repo2",
            Help: "Comma-separated list of repos to monitor (e.g. 'myorg/backend'). Leave blank to fetch all assigned issues/PRs."),

        new DataSourceField("pollIntervalSeconds", "Poll interval (seconds)", "text",
            Required: false,
            Placeholder: "300"),
    ];

    public IDataSourceInstance CreateInstance(DataSourceConfig config) =>
        new GitHubDataSourceInstance(config);

    public IReadOnlyList<Type> GetMcpToolTypes() => [];
}
