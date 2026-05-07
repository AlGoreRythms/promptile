using Microsoft.AspNetCore.Mvc.RazorPages;
using Promptile.Host.Services;
using Promptile.Sdk;

namespace Promptile.Host.Pages;

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
        Jobs = s.WorkJobs
            .Where(j => !s.IsHidden(j.Name) && !j.DataSources.Any(ds => s.IsHidden(ds)))
            .ToList();
        Agents = s.Agents;
        var visibleJobIds = Jobs.Select(j => j.Id).ToHashSet();
        RecentRuns = _workJobs.GetRecentRuns()
            .Where(r => visibleJobIds.Contains(r.JobId))
            .ToList();
        RunningIds = _workJobs.GetRunningIds()
            .Where(id => visibleJobIds.Contains(id))
            .ToList();

        var allSources = await _sources.LoadAsync();
        var visibleSources = allSources.Where(c => c.Enabled && !s.IsHidden(c.Name)).ToList();
        AllSources = visibleSources;
        FolderSources = visibleSources.Where(c => c.Type == "folder").ToList();
    }
}
