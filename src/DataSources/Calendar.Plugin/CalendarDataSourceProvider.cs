using Promptile.Sdk;

namespace Calendar;

public class CalendarDataSourceProvider : IDataSourceProvider
{
    public string Type => "google-calendar";
    public string DisplayName => "Google Calendar";
    public string Icon => "📅";

    public IReadOnlyList<DataSourceField> GetConfigFields() =>
    [
        new("calendarId", "Calendar ID", "text", Required: false,
            Placeholder: "primary",
            Help: "Calendar ID from Google Calendar settings. Leave blank for primary calendar."),
        new("daysAhead", "Days ahead to sync", "text", Required: false,
            Placeholder: "14",
            Help: "How many days of upcoming events to include."),
        new("pollIntervalSeconds", "Poll interval (seconds)", "text", Required: false,
            Placeholder: "300"),
    ];

    public IReadOnlyList<DataSourceField> GetPreAuthFields() =>
    [
        new("clientId", "Client ID", "text", Required: true,
            Placeholder: "123456789-abc.apps.googleusercontent.com",
            Help: "Create a Desktop OAuth 2.0 client at Google Cloud Console → APIs & Services → Credentials."),
        new("clientSecret", "Client Secret", "password", Required: true,
            Placeholder: "GOCSPX-..."),
    ];

    public string? GetAuthStartUrl(string instanceId) => $"/api/calendar-authorize/{instanceId}";

    public IDataSourceInstance CreateInstance(DataSourceConfig config) =>
        new CalendarDataSourceInstance(config);
}
