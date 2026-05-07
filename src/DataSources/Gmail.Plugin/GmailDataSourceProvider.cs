using Promptile.Sdk;

namespace Gmail;

public class GmailDataSourceProvider : IDataSourceProvider
{
    public string Type => "gmail";
    public string DisplayName => "Gmail";
    public string Icon => "✉️";

    public IReadOnlyList<DataSourceField> GetConfigFields() =>
    [
        new("labelFilter", "Label to monitor", "text", Required: false,
            Placeholder: "INBOX",
            Help: "Gmail label to poll for new messages. Default: INBOX"),
        new("notifyOn", "Notify on", "select", Required: false,
            Options: ["all", "unread"],
            Help: "Which emails trigger a notification"),
        new("pollIntervalSeconds", "Poll interval (seconds)", "text", Required: false,
            Placeholder: "60"),
    ];

    public IReadOnlyList<DataSourceField> GetPreAuthFields() =>
    [
        new("clientId", "Client ID", "text", Required: true,
            Placeholder: "123456789-abc.apps.googleusercontent.com",
            Help: "Create a Desktop OAuth 2.0 client at Google Cloud Console → APIs & Services → Credentials."),
        new("clientSecret", "Client Secret", "password", Required: true,
            Placeholder: "GOCSPX-..."),
    ];

    public string? GetAuthStartUrl(string instanceId) => $"/api/gmail-authorize/{instanceId}";

    public IDataSourceInstance CreateInstance(DataSourceConfig config) =>
        new GmailDataSourceInstance(config);

    public IReadOnlyList<Type> GetMcpToolTypes() => [typeof(Mcp.GmailMcpTools)];
}
