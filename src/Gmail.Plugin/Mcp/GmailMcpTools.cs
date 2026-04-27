using System.ComponentModel;
using System.Text;
using Assistant.Sdk;
using ModelContextProtocol.Server;
using GmailMessage = Google.Apis.Gmail.v1.Data.Message;

namespace Gmail.Mcp;

[McpServerToolType]
public class GmailMcpTools
{
    private readonly IDataSourceManager _manager;

    public GmailMcpTools(IDataSourceManager manager)
    {
        _manager = manager;
    }

    [McpServerTool(Name = "gmail_list_sources"), Description("List configured Gmail data sources")]
    public string ListSources()
    {
        var instances = _manager.GetInstances("gmail");
        if (!instances.Any()) return "No Gmail data sources configured.";
        var sb = new StringBuilder();
        foreach (var inst in instances)
            sb.AppendLine($"- {inst.Name} (id: {inst.Id})");
        return sb.ToString();
    }

    [McpServerTool(Name = "gmail_list_recent"), Description("List recent emails from a Gmail data source")]
    public async Task<string> ListRecent(
        [Description("Name of the Gmail data source (e.g. 'Work')")] string sourceName,
        [Description("Gmail label to read (default: INBOX)")] string? label = null,
        [Description("Maximum number of messages to return (default 20)")] int limit = 20)
    {
        var instance = GetGmailInstance(sourceName);
        if (instance == null) return $"No Gmail data source named '{sourceName}'.";

        var messages = await instance.GetRecentMessagesAsync(label, limit);
        if (!messages.Any()) return "No messages found.";

        var sb = new StringBuilder();
        foreach (var msg in messages)
            sb.AppendLine($"[{msg.GetDate()}] From: {msg.GetFrom()}\n  Subject: {msg.GetSubject()}\n  {msg.GetSnippet()}\n  ID: {msg.Id}\n");
        return sb.ToString();
    }

    [McpServerTool(Name = "gmail_search"), Description("Search emails in a Gmail data source")]
    public async Task<string> Search(
        [Description("Name of the Gmail data source (e.g. 'Work')")] string sourceName,
        [Description("Gmail search query (same syntax as Gmail search box)")] string query,
        [Description("Maximum number of results (default 20)")] int limit = 20)
    {
        var instance = GetGmailInstance(sourceName);
        if (instance == null) return $"No Gmail data source named '{sourceName}'.";

        var messages = await instance.SearchMessagesAsync(query, limit);
        if (!messages.Any()) return "No messages found.";

        var sb = new StringBuilder();
        foreach (var msg in messages)
            sb.AppendLine($"[{msg.GetDate()}] From: {msg.GetFrom()}\n  Subject: {msg.GetSubject()}\n  {msg.GetSnippet()}\n  ID: {msg.Id}\n");
        return sb.ToString();
    }

    [McpServerTool(Name = "gmail_get_message"), Description("Get the full content of a Gmail message by ID")]
    public async Task<string> GetMessage(
        [Description("Name of the Gmail data source (e.g. 'Work')")] string sourceName,
        [Description("Gmail message ID (from gmail_list_recent or gmail_search)")] string messageId)
    {
        var instance = GetGmailInstance(sourceName);
        if (instance == null) return $"No Gmail data source named '{sourceName}'.";

        var msg = await instance.GetMessageAsync(messageId);
        if (msg == null) return $"Message '{messageId}' not found.";

        var sb = new StringBuilder();
        sb.AppendLine($"From: {msg.GetFrom()}");
        sb.AppendLine($"Subject: {msg.GetSubject()}");
        sb.AppendLine($"Date: {msg.GetDate()}");
        sb.AppendLine();
        sb.AppendLine(msg.GetSnippet());
        return sb.ToString();
    }

    private GmailDataSourceInstance? GetGmailInstance(string name) =>
        _manager.GetInstance(name, "gmail") as GmailDataSourceInstance;
}
