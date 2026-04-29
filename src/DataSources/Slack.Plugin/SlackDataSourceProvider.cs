using Assistant.Sdk;
using Slack.Mcp;

namespace Slack;

public class SlackDataSourceProvider : IDataSourceProvider
{
    public string Type => "slack";
    public string DisplayName => "Slack";
    public string Icon => "💬";

    public IReadOnlyList<DataSourceField> GetConfigFields() =>
    [
        new DataSourceField("botToken", "Bot Token", "password",
            Required: true,
            Placeholder: "xoxb-...",
            Help: "Create a Slack app at api.slack.com, add bot scopes (channels:read, channels:history, groups:read, groups:history, im:history, users:read), install to workspace."),

        new DataSourceField("myUsername", "Your Slack display name", "text",
            Required: false,
            Placeholder: "Jane Smith",
            Help: "Your display name in this Slack workspace. Used to mark your own messages in the data store so the AI knows which messages are yours."),

        new DataSourceField("channels", "Channels to monitor", "text",
            Required: false,
            Placeholder: "general, engineering, random",
            Help: "Comma-separated channel names (without #). Leave blank to monitor all joined channels."),

        new DataSourceField("notifyOn", "Notify on", "select",
            Required: true,
            Options: ["all", "mentions", "dms"],
            Help: "Which messages trigger a system notification."),

        new DataSourceField("pollIntervalSeconds", "Poll interval (seconds)", "text",
            Required: false,
            Placeholder: "60"),
    ];

    public IDataSourceInstance CreateInstance(DataSourceConfig config) =>
        new SlackDataSourceInstance(config);

    public IReadOnlyList<Type> GetMcpToolTypes() => [typeof(SlackMcpTools)];
}
