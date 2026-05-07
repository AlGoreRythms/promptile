using System.ComponentModel;
using System.Text;
using Promptile.Sdk;
using ModelContextProtocol.Server;

namespace GmailImap.Mcp;

[McpServerToolType]
public class GmailImapMcpTools
{
    private readonly IDataSourceManager _manager;
    private readonly IInformationStore _store;

    public GmailImapMcpTools(IDataSourceManager manager, IInformationStore store)
    {
        _manager = manager;
        _store = store;
    }

    [McpServerTool(Name = "gmail_imap_list_sources"), Description("List configured Gmail (IMAP) data sources")]
    public string ListSources()
    {
        var instances = _manager.GetInstances("gmail-imap");
        if (!instances.Any()) return "No Gmail (IMAP) data sources configured.";
        var sb = new StringBuilder();
        foreach (var inst in instances)
            sb.AppendLine($"- {inst.Name} (id: {inst.Id})");
        return sb.ToString();
    }

    [McpServerTool(Name = "gmail_imap_list_recent"), Description("List recent emails synced from a Gmail (IMAP) data source")]
    public string ListRecent(
        [Description("Name of the Gmail data source (e.g. 'Work')")] string sourceName,
        [Description("Number of days to look back (default 3)")] int days = 3)
    {
        var dataPath = _store.GetDataPath(sourceName, "Gmail");
        if (!Directory.Exists(dataPath)) return $"No synced Gmail data found for '{sourceName}'.";

        var cutoff = DateOnly.FromDateTime(DateTime.Today.AddDays(-days));
        var files = Directory.GetFiles(dataPath, "*.md")
            .Where(f => DateOnly.TryParse(Path.GetFileNameWithoutExtension(f), out var d) && d >= cutoff)
            .OrderByDescending(f => f)
            .Take(5)
            .ToList();

        if (files.Count == 0) return $"No emails found for '{sourceName}' in the last {days} days.";

        var sb = new StringBuilder();
        foreach (var file in files)
        {
            sb.AppendLine($"### {Path.GetFileNameWithoutExtension(file)}");
            sb.AppendLine(File.ReadAllText(file).Trim());
            sb.AppendLine();
        }
        return sb.ToString();
    }

    [McpServerTool(Name = "gmail_imap_search"), Description("Search synced Gmail (IMAP) emails by keyword")]
    public string Search(
        [Description("Name of the Gmail data source (e.g. 'Work')")] string sourceName,
        [Description("Keyword or phrase to search for")] string query,
        [Description("Number of days to search back (default 30)")] int days = 30)
    {
        var dataPath = _store.GetDataPath(sourceName, "Gmail");
        if (!Directory.Exists(dataPath)) return $"No synced Gmail data found for '{sourceName}'.";

        var cutoff = DateOnly.FromDateTime(DateTime.Today.AddDays(-days));
        var files = Directory.GetFiles(dataPath, "*.md")
            .Where(f => DateOnly.TryParse(Path.GetFileNameWithoutExtension(f), out var d) && d >= cutoff)
            .OrderByDescending(f => f);

        var sb = new StringBuilder();
        var matches = 0;
        foreach (var file in files)
        {
            var content = File.ReadAllText(file);
            foreach (var section in content.Split("\n---\n", StringSplitOptions.RemoveEmptyEntries))
            {
                if (!section.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
                sb.AppendLine(section.Trim());
                sb.AppendLine();
                sb.AppendLine("---");
                sb.AppendLine();
                if (++matches >= 10) break;
            }
            if (matches >= 10) break;
        }

        return matches == 0
            ? $"No emails matching '{query}' found for '{sourceName}' in the last {days} days."
            : sb.ToString();
    }
}
