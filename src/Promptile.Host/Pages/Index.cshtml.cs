using Microsoft.AspNetCore.Mvc.RazorPages;
using Promptile.Host.Services;
using Promptile.Sdk;

namespace Promptile.Host.Pages;

public class IndexModel : PageModel
{
    private readonly SettingsService _settings;
    private readonly IInformationStore _store;
    private readonly DataSourcesService _dataSources;
    private readonly MemoryService _memory;

    public IndexModel(SettingsService settings, IInformationStore store, DataSourcesService dataSources, MemoryService memory)
    {
        _settings = settings;
        _store = store;
        _dataSources = dataSources;
        _memory = memory;
    }

    public List<(UserDashboardWidget Widget, string? Content, DateTimeOffset? CachedAt)> Widgets { get; set; } = [];
    public List<DashboardPage> Pages { get; set; } = [];
    public string HomeName { get; set; } = "Default";
    public List<string> AvailableSources { get; set; } = [];
    public Dictionary<string, List<string>> AvailableSourceTypes { get; set; } = [];
    public List<MemoryPageConfig> MemoryPages { get; set; } = [];

    public async Task OnGetAsync()
    {
        var s = await _settings.LoadAsync();
        var cachePath = _store.GetNotesPath("_Assistant", "DashboardCache");

        foreach (var w in s.DashboardWidgets.OrderBy(x => x.Order)
            .Where(w => !w.DataSources.Any(ds => s.IsHidden(ds))))
        {
            var filePath = Path.Combine(cachePath, $"{w.Id}.md");
            string? content = null;
            DateTimeOffset? cachedAt = null;
            if (System.IO.File.Exists(filePath))
            {
                content = await System.IO.File.ReadAllTextAsync(filePath);
                cachedAt = System.IO.File.GetLastWriteTimeUtc(filePath);
            }
            Widgets.Add((w, content, cachedAt));
        }

        Pages = s.DashboardPages.OrderBy(p => p.Order)
            .Where(p => !s.IsHidden(p.Name))
            .ToList();
        HomeName = s.DashboardHomeName;

        var configs = await _dataSources.LoadAsync();
        AvailableSources = configs.Where(c => c.Enabled && !s.IsHidden(c.Name))
            .Select(c => c.Name).Distinct().OrderBy(n => n).ToList();

        foreach (var name in AvailableSources)
        {
            var sourceDir = Path.Combine(_store.RootPath, name);
            if (!Directory.Exists(sourceDir)) continue;
            var types = Directory.GetDirectories(sourceDir)
                .Select(Path.GetFileName)
                .Where(t => t != null)
                .Select(t => t!)
                .OrderBy(t => t)
                .ToList();
            if (types.Count > 0)
                AvailableSourceTypes[name] = types;
        }

        MemoryPages = _memory.GetPages().ToList();
    }
}
