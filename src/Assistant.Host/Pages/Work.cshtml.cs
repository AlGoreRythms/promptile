using Microsoft.AspNetCore.Mvc.RazorPages;
using Assistant.Host.Services;
using Assistant.Sdk;

namespace Assistant.Host.Pages;

public class WorkModel : PageModel
{
    private readonly SettingsService _settings;
    private readonly DataSourcesService _sources;
    private readonly WorkJobService _workJobs;

    public WorkModel(SettingsService settings, DataSourcesService sources, WorkJobService workJobs)
    {
        _settings = settings;
        _sources = sources;
        _workJobs = workJobs;
    }

    public List<WorkJob> Jobs { get; set; } = [];
    public List<WorkJobRun> RecentRuns { get; set; } = [];
    public List<string> RunningIds { get; set; } = [];
    public List<DataSourceConfig> FolderSources { get; set; } = [];
    public List<AgentDefinition> Agents { get; set; } = [];
    public List<DataSourceConfig> AllSources { get; set; } = [];

    public async Task OnGetAsync()
    {
        var s = await _settings.LoadAsync();
        Jobs = s.WorkJobs;
        Agents = s.Agents;
        RecentRuns = _workJobs.GetRecentRuns();
        RunningIds = _workJobs.GetRunningIds();

        var hidden = new HashSet<string>(s.HiddenNames, StringComparer.OrdinalIgnoreCase);
        var allSources = await _sources.LoadAsync();
        var visibleSources = allSources.Where(c => c.Enabled && !hidden.Contains(c.Name)).ToList();
        AllSources = visibleSources;
        FolderSources = visibleSources.Where(c => c.Type == "folder").ToList();
    }
}
