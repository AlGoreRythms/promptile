using System.ComponentModel;
using Promptile.Host.Services;
using ModelContextProtocol.Server;

namespace Promptile.Host.Mcp;

[McpServerToolType]
public class MemoryMcpTools(MemoryService memoryService)
{
    [McpServerTool(Name = "memory_list_pages"),
     Description("List configured memory pages with their name, mode, and description")]
    public string ListPages()
    {
        var pages = memoryService.GetPages();
        if (!pages.Any()) return "No memory pages configured. Add pages in Settings → Memory.";

        var sb = new System.Text.StringBuilder();
        foreach (var p in pages)
        {
            sb.Append($"- **{p.Name}** [{p.Mode}/{p.AgentTier}]");
            if (!string.IsNullOrEmpty(p.Description)) sb.Append($": {p.Description}");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    [McpServerTool(Name = "memory_read_page"),
     Description("Read the content of a memory page. For dated pages omit label for the latest entry, or specify a label like '2026-04-27', '2026-W17', or '2026-04'")]
    public string ReadPage(
        [Description("Page name")] string name,
        [Description("Period label for dated pages (e.g. '2026-W17'). Omit for rolling or latest.")] string? label = null)
    {
        var content = memoryService.GetPageContent(name, label);
        return content ?? $"Page '{name}' has no content yet{(label != null ? $" for label '{label}'" : "")}.";
    }

    [McpServerTool(Name = "memory_write_page"),
     Description("Overwrite the content of a memory page or a specific dated snapshot")]
    public string WritePage(
        [Description("Page name")] string name,
        [Description("New markdown content for the page")] string content,
        [Description("Period label for dated pages (e.g. '2026-W17'). Omit for rolling pages.")] string? label = null)
    {
        var pages = memoryService.GetPages();
        if (pages.All(p => !p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            return $"Page '{name}' does not exist. Create it in Settings → Memory first.";

        memoryService.WritePageContent(name, content, label);
        return $"Page '{name}' updated{(label != null ? $" (label: {label})" : "")}.";
    }
}
