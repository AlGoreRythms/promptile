using Assistant.Sdk;

namespace Rss;

public class RssDataSourceProvider : IDataSourceProvider
{
    public string Type => "rss";
    public string DisplayName => "RSS / Atom";
    public string Icon => "📰";

    public IReadOnlyList<DataSourceField> GetConfigFields() =>
    [
        new DataSourceField("feedUrls", "Feed URLs", "textarea",
            Required: true,
            Placeholder: "https://hnrss.org/frontpage\nhttps://www.theverge.com/rss/index.xml",
            Help: "One RSS 2.0 or Atom feed URL per line. All feeds are stored together under this source name."),

        new DataSourceField("maxItems", "Max items per poll", "text",
            Required: false,
            Placeholder: "20",
            Help: "Maximum number of new items to ingest each poll cycle."),

        new DataSourceField("pollIntervalSeconds", "Poll interval (seconds)", "text",
            Required: false,
            Placeholder: "900",
            Help: "How often to check the feed. Default is 900 (15 minutes)."),
    ];

    public IDataSourceInstance CreateInstance(DataSourceConfig config) =>
        new RssDataSourceInstance(config);

    public IReadOnlyList<Type> GetMcpToolTypes() => [];
}
