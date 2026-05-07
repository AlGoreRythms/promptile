using System.Text;
using System.Text.Json;
using Promptile.Sdk;

namespace Promptile.Host.Services;

public class WidgetToolService(IInformationStore store, List<string> dataSources, int contextDays)
{
    private readonly Dictionary<string, HashSet<string>?> _sourceFilters = ParseFilters(dataSources);

    private static Dictionary<string, HashSet<string>?> ParseFilters(List<string> sources)
    {
        var filters = new Dictionary<string, HashSet<string>?>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in sources)
        {
            var colon = entry.IndexOf(':');
            if (colon < 0)
            {
                filters[entry] = null;
            }
            else
            {
                var name = entry[..colon];
                var type = entry[(colon + 1)..];
                if (!filters.TryGetValue(name, out var set) || set == null)
                    filters[name] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { type };
                else
                    set.Add(type);
            }
        }
        return filters;
    }

    public string BuildSourceInventory()
    {
        var sb = new StringBuilder();
        foreach (var (sourceName, typeFilter) in _sourceFilters)
        {
            var sourceDir = Path.Combine(store.RootPath, sourceName);
            if (!Directory.Exists(sourceDir)) continue;
            var types = Directory.GetDirectories(sourceDir)
                .Select(Path.GetFileName)
                .Where(t => t != null && (typeFilter == null || typeFilter.Contains(t!)))
                .ToList();
            if (types.Count > 0)
                sb.AppendLine($"- source_name=\"{sourceName}\" contains: {string.Join(", ", types)}");
        }
        return sb.ToString().TrimEnd();
    }

    public string BuildAuthorRoster(int days = 30)
    {
        var cutoff = DateTime.UtcNow.AddDays(-days);
        var authors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (sourceName, typeFilter) in _sourceFilters)
        {
            if (typeFilter != null && !typeFilter.Contains("Slack")) continue;
            var slackDataPath = Path.Combine(store.RootPath, sourceName, "Slack", "Data");
            if (!Directory.Exists(slackDataPath)) continue;

            foreach (var channelDir in Directory.GetDirectories(slackDataPath))
            foreach (var file in Directory.GetFiles(channelDir, "*.md")
                .Where(f => File.GetLastWriteTimeUtc(f) >= cutoff))
            {
                foreach (var line in File.ReadLines(file))
                {
                    if (!line.StartsWith("##")) continue;
                    var lastDot = line.LastIndexOf('·');
                    if (lastDot < 0) continue;
                    var author = line[(lastDot + 1)..].Trim();
                    var paren = author.IndexOf('(');
                    if (paren >= 0) author = author[..paren].Trim();
                    if (!string.IsNullOrEmpty(author)) authors.Add(author);
                }
            }
        }

        return string.Join(", ", authors.OrderBy(a => a, StringComparer.OrdinalIgnoreCase));
    }

    public IReadOnlyList<ToolDefinition> GetToolDefinitions()
    {
        var tools = new List<ToolDefinition>();
        if (HasSourceType("Slack"))
            tools.Add(new ToolDefinition(
                "search_slack_messages",
                "Search Slack messages across all channels. Filter by author name, channel, or keyword. All filters are optional and combinable.",
                """
                {
                    "type": "object",
                    "properties": {
                        "source_name": {"type": "string", "description": "Data source name (e.g. 'Work', 'Personal')"},
                        "author": {"type": "string", "description": "Filter by author name (partial match, case-insensitive). Omit to search all authors."},
                        "channel": {"type": "string", "description": "Filter by channel name without # (e.g. 'general'). Omit to search all channels."},
                        "keyword": {"type": "string", "description": "Search for keyword in message text (case-insensitive). Omit to return all messages."},
                        "days": {"type": "integer", "description": "Days back to search. Defaults to widget context window."}
                    },
                    "required": ["source_name"]
                }
                """));

        if (HasSourceType("Jira"))
            tools.Add(new ToolDefinition(
                "search_jira_issues",
                "Search Jira issues. Filter by assignee, status, or keyword in title/description.",
                """
                {
                    "type": "object",
                    "properties": {
                        "source_name": {"type": "string", "description": "Data source name"},
                        "assignee": {"type": "string", "description": "Filter by assignee name (partial match, case-insensitive)"},
                        "status": {"type": "string", "description": "Filter by status (e.g. 'In Progress', 'Open', 'Done')"},
                        "keyword": {"type": "string", "description": "Search keyword in issue title or description"}
                    },
                    "required": ["source_name"]
                }
                """));

        return tools;
    }

    public async Task<string> ExecuteAsync(ToolCall call, CancellationToken ct) =>
        call.Name switch
        {
            "search_slack_messages" => await SearchSlackAsync(call.InputJson, ct),
            "search_jira_issues" => await SearchJiraAsync(call.InputJson, ct),
            _ => $"Unknown tool: {call.Name}",
        };

    private async Task<string> SearchSlackAsync(string inputJson, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(inputJson);
        var root = doc.RootElement;

        var sourceName = root.TryGetProperty("source_name", out var sn) ? sn.GetString() ?? "" : "";
        var author = root.TryGetProperty("author", out var a) ? a.GetString() : null;
        var channel = root.TryGetProperty("channel", out var ch) ? ch.GetString() : null;
        var keyword = root.TryGetProperty("keyword", out var kw) ? kw.GetString() : null;
        var days = root.TryGetProperty("days", out var d) ? d.GetInt32() : contextDays;

        var cutoff = DateTime.UtcNow.AddDays(-Math.Max(1, days));
        var slackDataPath = Path.Combine(store.RootPath, sourceName, "Slack", "Data");
        if (!Directory.Exists(slackDataPath))
            return $"No Slack data found for source '{sourceName}'.";

        var channelDirs = string.IsNullOrWhiteSpace(channel)
            ? Directory.GetDirectories(slackDataPath)
            : Directory.GetDirectories(slackDataPath)
                .Where(d => Path.GetFileName(d).Equals(channel, StringComparison.OrdinalIgnoreCase))
                .ToArray();

        // Phase 1: collect all matching sections from all channels (no char limit yet)
        var allMatches = new List<(DateTime Ts, string Channel, string Section)>();
        var searchedChannels = new List<string>();

        foreach (var channelDir in channelDirs)
        {
            var channelName = Path.GetFileName(channelDir);
            searchedChannels.Add(channelName);
            var files = Directory.GetFiles(channelDir, "*.md")
                .Where(f => File.GetLastWriteTimeUtc(f) >= cutoff)
                .OrderByDescending(File.GetLastWriteTimeUtc);

            foreach (var file in files)
            {
                var text = await File.ReadAllTextAsync(file, ct);
                var sections = text.Split("\n---\n", StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim()).Where(s => !string.IsNullOrEmpty(s)).ToList();

                if (!string.IsNullOrEmpty(author))
                {
                    // Group into threads (parent + replies) and include whole thread if author participated
                    foreach (var thread in GroupIntoThreads(sections))
                    {
                        if (!thread.Any(s => SectionMatchesAuthor(s, author))) continue;
                        if (!string.IsNullOrEmpty(keyword) &&
                            !thread.Any(s => s.Contains(keyword, StringComparison.OrdinalIgnoreCase))) continue;
                        var combined = string.Join("\n\n---\n\n", thread);
                        allMatches.Add((ExtractTimestamp(thread[0], file), channelName, combined));
                    }
                }
                else
                {
                    foreach (var trimmed in sections)
                    {
                        if (!string.IsNullOrEmpty(keyword) &&
                            !trimmed.Contains(keyword, StringComparison.OrdinalIgnoreCase)) continue;
                        allMatches.Add((ExtractTimestamp(trimmed, file), channelName, trimmed));
                    }
                }
            }
        }

        if (allMatches.Count == 0)
            return "No Slack messages found matching the criteria.";

        // Phase 2: sort newest-first, then fill result up to the char limit
        allMatches.Sort((x, y) => y.Ts.CompareTo(x.Ts));

        const int MaxChars = 20_000;
        var results = new StringBuilder();
        var included = 0;
        foreach (var (_, channelName, section) in allMatches)
        {
            if (results.Length >= MaxChars) break;
            var remaining = MaxChars - results.Length;
            results.AppendLine($"[#{channelName}]");
            results.AppendLine(section.Length > remaining ? section[..remaining] : section);
            results.AppendLine();
            included++;
        }

        var omitted = allMatches.Count - included;
        var header = new StringBuilder();
        header.AppendLine($"Searched {searchedChannels.Count} channel(s): {string.Join(", ", searchedChannels.Select(c => "#" + c))}");
        header.AppendLine(omitted > 0
            ? $"Found {allMatches.Count} matching message(s) — showing {included} ({omitted} omitted, oldest first; use channel= to search a specific channel for complete results)."
            : $"Found {allMatches.Count} matching message(s) — all included.");
        header.AppendLine();

        return header.ToString() + results;
    }

    private static List<List<string>> GroupIntoThreads(List<string> sections)
    {
        var threads = new List<List<string>>();
        List<string>? current = null;
        foreach (var section in sections)
        {
            var firstLine = section.Split('\n')[0];
            var isReply = firstLine.StartsWith("### ↳") || firstLine.StartsWith("### ");
            if (!isReply || current == null)
            {
                current = [section];
                threads.Add(current);
            }
            else
            {
                current.Add(section);
            }
        }
        return threads;
    }

    private static bool SectionMatchesAuthor(string section, string author)
    {
        var firstLine = section.Split('\n')[0];
        var lastDot = firstLine.LastIndexOf('·');
        if (lastDot < 0) return false;
        var sectionAuthor = firstLine[(lastDot + 1)..].Trim();
        var paren = sectionAuthor.IndexOf('(');
        if (paren >= 0) sectionAuthor = sectionAuthor[..paren].Trim();
        return sectionAuthor.Contains(author, StringComparison.OrdinalIgnoreCase);
    }

    private static DateTime ExtractTimestamp(string section, string filePath)
    {
        var dateStr = Path.GetFileNameWithoutExtension(filePath);
        var firstLine = section.Split('\n')[0];
        var m = System.Text.RegularExpressions.Regex.Match(firstLine, @"\b(\d{2}:\d{2})\b");
        if (DateTime.TryParse(dateStr, out var date) && m.Success
            && TimeSpan.TryParse(m.Value, out var time))
            return date + time;
        return File.GetLastWriteTimeUtc(filePath);
    }

    private async Task<string> SearchJiraAsync(string inputJson, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(inputJson);
        var root = doc.RootElement;

        var sourceName = root.TryGetProperty("source_name", out var sn) ? sn.GetString() ?? "" : "";
        var assignee = root.TryGetProperty("assignee", out var a) ? a.GetString() : null;
        var status = root.TryGetProperty("status", out var st) ? st.GetString() : null;
        var keyword = root.TryGetProperty("keyword", out var kw) ? kw.GetString() : null;

        var jiraDataPath = Path.Combine(store.RootPath, sourceName, "Jira", "Data");
        if (!Directory.Exists(jiraDataPath))
            return $"No Jira data found for source '{sourceName}'.";

        var results = new StringBuilder();
        const int MaxChars = 20_000;

        foreach (var file in Directory.GetFiles(jiraDataPath, "*.md", SearchOption.AllDirectories))
        {
            if (results.Length >= MaxChars) break;
            var text = await File.ReadAllTextAsync(file, ct);

            if (!string.IsNullOrEmpty(assignee) &&
                !text.Contains(assignee, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrEmpty(status) &&
                !text.Contains(status, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrEmpty(keyword) &&
                !text.Contains(keyword, StringComparison.OrdinalIgnoreCase)) continue;

            var remaining = MaxChars - results.Length;
            results.AppendLine(text.Length > remaining ? text[..remaining] + "\n[truncated]" : text);
            results.AppendLine();
        }

        return results.Length == 0
            ? "No Jira issues found matching the criteria."
            : results.ToString();
    }

    private bool HasSourceType(string type)
    {
        foreach (var (sourceName, typeFilter) in _sourceFilters)
        {
            if (typeFilter != null && !typeFilter.Contains(type)) continue;
            if (Directory.Exists(Path.Combine(store.RootPath, sourceName, type))) return true;
        }
        return false;
    }
}
