using Assistant.Sdk;

namespace Jira;

public class JiraDataSourceProvider : IDataSourceProvider
{
    public string Type => "jira";
    public string DisplayName => "Jira";
    public string Icon => "🎯";

    public IReadOnlyList<DataSourceField> GetConfigFields() =>
    [
        new DataSourceField("baseUrl", "Jira URL", "text",
            Required: true,
            Placeholder: "https://yourcompany.atlassian.net",
            Help: "Your Jira Cloud instance URL."),

        new DataSourceField("email", "Email", "text",
            Required: true,
            Placeholder: "you@company.com",
            Help: "Your Atlassian account email."),

        new DataSourceField("apiToken", "API Token", "password",
            Required: true,
            Placeholder: "ATATT...",
            Help: "Generate at id.atlassian.com → Security → API tokens."),

        new DataSourceField("jql", "JQL Filter (optional)", "text",
            Required: false,
            Placeholder: "assignee = currentUser() AND resolution = Unresolved",
            Help: "Custom JQL to filter issues. Leave blank for all open assigned issues."),

        new DataSourceField("pollIntervalSeconds", "Poll interval (seconds)", "text",
            Required: false,
            Placeholder: "300"),
    ];

    public IDataSourceInstance CreateInstance(DataSourceConfig config) =>
        new JiraDataSourceInstance(config);

    public IReadOnlyList<Type> GetMcpToolTypes() => [];
}
