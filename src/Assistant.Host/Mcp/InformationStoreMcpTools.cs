using System.ComponentModel;
using System.Text;
using Assistant.Host.Services;
using Assistant.Sdk;
using ModelContextProtocol.Server;

namespace Assistant.Host.Mcp;

[McpServerToolType]
public class InformationStoreMcpTools
{
    private readonly IInformationStore _store;
    private readonly EmbeddingService _embeddings;

    public InformationStoreMcpTools(IInformationStore store, EmbeddingService embeddings)
    {
        _store = store;
        _embeddings = embeddings;
    }

    [McpServerTool(Name = "store_list_sources"), Description("List all names and source types in the information store")]
    public string ListSources()
    {
        var sources = _store.ListSources();
        if (!sources.Any()) return $"Information store is empty. Root: {_store.RootPath}";
        var sb = new StringBuilder();
        foreach (var (name, types) in sources)
            sb.AppendLine($"- {name}: {string.Join(", ", types)}");
        return sb.ToString();
    }

    [McpServerTool(Name = "store_list_data"), Description("List synced data files for a given source name and type (e.g. daily Slack/Gmail files)")]
    public string ListData(
        [Description("Source name (e.g. 'Work', 'Personal', 'Home')")] string sourceName,
        [Description("Source type (e.g. 'Slack', 'Gmail')")] string sourceType)
    {
        var path = _store.GetDataPath(sourceName, sourceType);
        if (!Directory.Exists(path)) return $"No data found for {sourceName}/{sourceType}.";
        var files = Directory.GetFiles(path)
            .Select(Path.GetFileName).Where(f => f != null).Select(f => f!)
            .OrderByDescending(f => f).ToList();
        if (!files.Any()) return $"No data files found for {sourceName}/{sourceType}.";
        return string.Join("\n", files.Select(n => $"- {n}"));
    }

    [McpServerTool(Name = "store_read_data"), Description("Read a synced data file (e.g. a daily Slack or Gmail file)")]
    public async Task<string> ReadData(
        [Description("Source name (e.g. 'Work', 'Personal', 'Home')")] string sourceName,
        [Description("Source type (e.g. 'Slack', 'Gmail')")] string sourceType,
        [Description("Filename (e.g. '2026-04-21.md')")] string filename)
    {
        var path = Path.Combine(_store.GetDataPath(sourceName, sourceType), filename);
        if (!File.Exists(path)) return $"File '{filename}' not found in {sourceName}/{sourceType}/Data/";
        return await File.ReadAllTextAsync(path);
    }

    [McpServerTool(Name = "store_list_notes"), Description("List note files for a given source name and type")]
    public string ListNotes(
        [Description("Source name (e.g. 'Work', 'Personal')")] string sourceName,
        [Description("Source type (e.g. 'Slack', 'Gmail')")] string sourceType)
    {
        var notes = _store.ListNotes(sourceName, sourceType);
        if (!notes.Any()) return $"No notes found for {sourceName}/{sourceType}.";
        return string.Join("\n", notes.Select(n => $"- {n}"));
    }

    [McpServerTool(Name = "store_read_note"), Description("Read a note file from the information store")]
    public async Task<string> ReadNote(
        [Description("Source name (e.g. 'Work')")] string sourceName,
        [Description("Source type (e.g. 'Slack')")] string sourceType,
        [Description("Filename (e.g. 'summary.md')")] string filename)
    {
        var content = await _store.ReadNoteAsync(sourceName, sourceType, filename);
        return content ?? $"Note '{filename}' not found in {sourceName}/{sourceType}/Notes/";
    }

    [McpServerTool(Name = "store_write_note"), Description("Write or overwrite a note file in the information store")]
    public async Task<string> WriteNote(
        [Description("Source name (e.g. 'Work')")] string sourceName,
        [Description("Source type (e.g. 'Slack')")] string sourceType,
        [Description("Filename (e.g. 'summary.md')")] string filename,
        [Description("Markdown content to write")] string content)
    {
        await _store.WriteNoteAsync(sourceName, sourceType, filename, content);
        return $"Written: {sourceName}/{sourceType}/Notes/{filename}";
    }

    [McpServerTool(Name = "store_list_entities"), Description("Read the entity index (people, companies, or projects) extracted from synced data")]
    public async Task<string> ListEntities(
        [Description("Entity type: 'people', 'companies', or 'projects'")] string entityType)
    {
        var validTypes = new[] { "people", "companies", "projects" };
        if (!validTypes.Contains(entityType.ToLower()))
            return $"Invalid entity type. Use one of: {string.Join(", ", validTypes)}";
        var content = await _store.ReadNoteAsync("_Assistant", "Entities", $"{entityType.ToLower()}.md");
        return content ?? $"No {entityType} entity index yet — will be built after the next briefing run.";
    }

    [McpServerTool(Name = "store_append_data"), Description("Append content to a data file in the information store")]
    public async Task<string> AppendData(
        [Description("Source name (e.g. 'Work')")] string sourceName,
        [Description("Source type (e.g. 'Slack')")] string sourceType,
        [Description("Filename (e.g. '2024-01-15.md')")] string filename,
        [Description("Content to append")] string content)
    {
        await _store.AppendDataAsync(sourceName, sourceType, filename, content);
        return $"Appended to: {sourceName}/{sourceType}/Data/{filename}";
    }

    [McpServerTool(Name = "store_search"), Description("Semantic search across all indexed store content using embeddings. Requires EmbeddingBaseUrl to be configured in Settings.")]
    public async Task<string> SearchStore(
        [Description("Natural language query, e.g. 'what did Alex say about the launch?'")] string query,
        [Description("Maximum number of results to return (default: 8)")] int limit = 8)
    {
        return await _embeddings.SearchAsync(query, limit);
    }
}
