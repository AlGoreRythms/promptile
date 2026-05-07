using Promptile.Sdk;

namespace GmailImap;

public class GmailImapDataSourceProvider : IDataSourceProvider
{
    public string Type => "gmail-imap";
    public string DisplayName => "Gmail (IMAP)";
    public string Icon => "✉️";

    public IReadOnlyList<DataSourceField> GetConfigFields() =>
    [
        new("email", "Gmail address", "text", Required: true,
            Placeholder: "you@gmail.com"),
        new("appPassword", "App password", "password", Required: true,
            Placeholder: "xxxx xxxx xxxx xxxx",
            Help: "Generate at myaccount.google.com/apppasswords — requires 2-Step Verification to be enabled on your Google account."),
        new("folder", "IMAP folder", "text", Required: false,
            Placeholder: "INBOX",
            Help: "IMAP folder to monitor. Default: INBOX"),
        new("pollIntervalSeconds", "Poll interval (seconds)", "text", Required: false,
            Placeholder: "60"),
    ];

    public string? GetAuthStartUrl(string instanceId) => null;

    public IDataSourceInstance CreateInstance(DataSourceConfig config) =>
        new GmailImapDataSourceInstance(config);

    public IReadOnlyList<Type> GetMcpToolTypes() => [typeof(Mcp.GmailImapMcpTools)];
}
