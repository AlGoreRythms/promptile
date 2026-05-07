using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Promptile.Sdk;
using ModelContextProtocol.Server;

namespace Slack.Mcp;

[McpServerToolType]
public class SlackMcpTools
{
    private readonly IDataSourceManager _manager;

    public SlackMcpTools(IDataSourceManager manager)
    {
        _manager = manager;
    }

    [McpServerTool(Name = "slack_list_sources"), Description("List configured Slack data sources")]
    public string ListSources()
    {
        var instances = _manager.GetInstances("slack");
        if (!instances.Any()) return "No Slack data sources configured.";
        var sb = new StringBuilder();
        foreach (var inst in instances)
            sb.AppendLine($"- {inst.Name} (id: {inst.Id})");
        return sb.ToString();
    }

    [McpServerTool(Name = "slack_list_channels"), Description("List Slack channels available in a data source")]
    public async Task<string> ListChannels(
        [Description("Name of the Slack data source (e.g. 'Work')")] string sourceName)
    {
        var instance = GetSlackInstance(sourceName);
        if (instance == null) return $"No Slack data source named '{sourceName}'.";

        var channels = await instance.GetChannelsAsync();
        if (!channels.Any()) return "No channels found.";

        var sb = new StringBuilder();
        foreach (var ch in channels.OrderBy(c => c.Name))
            sb.AppendLine($"#{ch.Name}{(ch.IsPrivate ? " (private)" : "")}");
        return sb.ToString();
    }

    [McpServerTool(Name = "slack_get_messages"), Description("Get recent messages from a Slack channel")]
    public async Task<string> GetMessages(
        [Description("Name of the Slack data source (e.g. 'Work')")] string sourceName,
        [Description("Channel name without # (e.g. 'general')")] string? channel = null,
        [Description("Maximum number of messages to return (default 20)")] int limit = 20)
    {
        var instance = GetSlackInstance(sourceName);
        if (instance == null) return $"No Slack data source named '{sourceName}'.";

        var messages = await instance.GetMessagesAsync(channel, limit);
        if (!messages.Any()) return "No messages found.";

        var sb = new StringBuilder();
        foreach (var (ch, msg) in messages)
        {
            var time = TsToDateTime(msg.Ts).ToString("yyyy-MM-dd HH:mm");
            sb.AppendLine($"[{time}] #{ch.Name} {msg.UserId}: {msg.Text}");
        }
        return sb.ToString();
    }

    private SlackDataSourceInstance? GetSlackInstance(string name) =>
        _manager.GetInstance(name, "slack") as SlackDataSourceInstance;

    private static DateTime TsToDateTime(string ts)
    {
        if (double.TryParse(ts.Split('.')[0], out var unix))
            return DateTimeOffset.FromUnixTimeSeconds((long)unix).LocalDateTime;
        return DateTime.Now;
    }
}
