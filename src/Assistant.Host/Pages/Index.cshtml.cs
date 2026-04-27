using Microsoft.AspNetCore.Mvc.RazorPages;
using Assistant.Host.Services;
using Assistant.Sdk;

namespace Assistant.Host.Pages;

public class IndexModel : PageModel
{
    private readonly SettingsService _settings;
    private readonly IInformationStore _store;
    private readonly DataSourcesService _dataSources;

    public IndexModel(SettingsService settings, IInformationStore store, DataSourcesService dataSources)
    {
        _settings = settings;
        _store = store;
        _dataSources = dataSources;
    }

    public List<(UserDashboardWidget Widget, string? Content, DateTimeOffset? CachedAt)> Widgets { get; set; } = [];
    public List<DashboardPage> Pages { get; set; } = [];
    public string HomeName { get; set; } = "Default";
    public List<string> AvailableSources { get; set; } = [];
    public Dictionary<string, List<string>> AvailableSourceTypes { get; set; } = [];

    public async Task OnGetAsync()
    {
        var s = await _settings.LoadAsync();
        var cachePath = _store.GetNotesPath("_Assistant", "DashboardCache");

        var hiddenNames = new HashSet<string>(s.HiddenNames, StringComparer.OrdinalIgnoreCase);
        foreach (var w in s.DashboardWidgets.OrderBy(x => x.Order)
            .Where(w => !w.DataSources.Any(ds => hiddenNames.Contains(ds))))
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
            .Where(p => !hiddenNames.Contains(p.Name))
            .ToList();
        HomeName = s.DashboardHomeName;

        var configs = await _dataSources.LoadAsync();
        AvailableSources = configs.Where(c => c.Enabled && !hiddenNames.Contains(c.Name))
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
    }
}
