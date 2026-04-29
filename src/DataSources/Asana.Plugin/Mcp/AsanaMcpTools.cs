using System.ComponentModel;
using System.Text;
using Assistant.Sdk;
using ModelContextProtocol.Server;

namespace Asana.Mcp;

[McpServerToolType]
public class AsanaMcpTools
{
    private readonly IDataSourceManager _manager;

    public AsanaMcpTools(IDataSourceManager manager)
    {
        _manager = manager;
    }

    [McpServerTool(Name = "asana_list_sources"), Description("List configured Asana data sources")]
    public string ListSources()
    {
        var instances = _manager.GetInstances("asana");
        if (!instances.Any()) return "No Asana data sources configured.";
        var sb = new StringBuilder();
        foreach (var inst in instances)
            sb.AppendLine($"- {inst.Name} (id: {inst.Id})");
        return sb.ToString();
    }

    [McpServerTool(Name = "asana_list_tasks"), Description("List incomplete tasks assigned to me in an Asana workspace")]
    public async Task<string> ListTasks(
        [Description("Name of the Asana data source")] string sourceName,
        [Description("Filter by project name (optional, case-insensitive substring match)")] string? project = null)
    {
        var instance = GetInstance(sourceName);
        if (instance == null) return $"No Asana data source named '{sourceName}'.";

        var tasks = await instance.GetTasksAsync();
        if (!tasks.Any()) return "No incomplete tasks found.";

        if (!string.IsNullOrEmpty(project))
            tasks = tasks.Where(t => t.Projects.Any(p => p.Contains(project, StringComparison.OrdinalIgnoreCase))).ToList();

        if (!tasks.Any()) return $"No tasks found in project '{project}'.";

        var sb = new StringBuilder();
        foreach (var task in tasks.OrderBy(t => t.DueOn ?? DateTime.MaxValue))
        {
            sb.Append($"- [{task.Gid}] {task.Name}");
            if (task.Projects.Count > 0)
                sb.Append($" ({string.Join(", ", task.Projects)})");
            if (task.DueOn.HasValue)
                sb.Append($" · due {task.DueOn.Value:yyyy-MM-dd}");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    [McpServerTool(Name = "asana_get_task"), Description("Get full details of an Asana task by GID")]
    public async Task<string> GetTask(
        [Description("Name of the Asana data source")] string sourceName,
        [Description("Task GID (the numeric ID from the task URL or list)")] string taskGid)
    {
        var instance = GetInstance(sourceName);
        if (instance == null) return $"No Asana data source named '{sourceName}'.";

        var tasks = await instance.GetTasksAsync();
        var task = tasks.FirstOrDefault(t => t.Gid == taskGid);
        if (task == null) return $"Task '{taskGid}' not found in current task list.";

        var sb = new StringBuilder();
        sb.AppendLine($"**{task.Name}**");
        if (task.Projects.Count > 0)
            sb.AppendLine($"Project: {string.Join(", ", task.Projects)}");
        if (task.DueOn.HasValue)
            sb.AppendLine($"Due: {task.DueOn.Value:yyyy-MM-dd}");
        sb.AppendLine($"Last modified: {task.ModifiedAt:yyyy-MM-dd HH:mm} UTC");
        sb.AppendLine($"URL: {task.PermalinkUrl}");
        if (!string.IsNullOrWhiteSpace(task.Notes))
        {
            sb.AppendLine();
            sb.AppendLine(task.Notes);
        }
        return sb.ToString();
    }

    private AsanaDataSourceInstance? GetInstance(string name) =>
        _manager.GetInstance(name, "asana") as AsanaDataSourceInstance;
}
