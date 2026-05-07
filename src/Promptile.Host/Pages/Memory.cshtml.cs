using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Promptile.Host.Services;
using Promptile.Sdk;

namespace Promptile.Host.Pages;

[IgnoreAntiforgeryToken]
public class MemoryModel : PageModel
{
    private readonly MemoryService _memory;
    private readonly SettingsService _settings;
    private readonly IInformationStore _store;
    private readonly DataSourcesService _dataSources;

    public MemoryModel(MemoryService memory, SettingsService settings, IInformationStore store, DataSourcesService dataSources)
    {
        _memory = memory;
        _settings = settings;
        _store = store;
        _dataSources = dataSources;
    }

    public List<MemoryPageConfig> Pages { get; set; } = [];
    public Dictionary<string, string?> PagePreviews { get; set; } = [];
    public Dictionary<string, List<string>> PageSnapshots { get; set; } = [];
    public string? EditingPage { get; set; }
    public MemoryPageConfig? EditConfig { get; set; }
    public int ScanIntervalMinutes { get; set; }
    public List<string> AvailableSources { get; set; } = [];
    public Dictionary<string, List<string>> AvailableSourceTypes { get; set; } = [];

    // View mode
    public string? ViewingPage { get; set; }
    public MemoryPageConfig? ViewConfig { get; set; }
    public List<(string Label, string Content)> ViewSnapshots { get; set; } = [];
    public string? ViewLabel { get; set; }   // selected snapshot label (dated) or null (rolling)
    public string? ViewContent { get; set; }

    public async Task OnGetAsync(string? edit = null, string? view = null, string? label = null)
    {
        EditingPage = edit;
        ViewingPage = view;
        var s = await _settings.LoadAsync();
        ScanIntervalMinutes = s.MemoryScanIntervalMinutes > 0 ? s.MemoryScanIntervalMinutes : 30;
        Pages = s.MemoryPages;

        if (edit != null && edit != "new")
            EditConfig = Pages.FirstOrDefault(p => p.Name.Equals(edit, StringComparison.OrdinalIgnoreCase));

        if (view != null)
        {
            ViewConfig = Pages.FirstOrDefault(p => p.Name.Equals(view, StringComparison.OrdinalIgnoreCase));
            if (ViewConfig != null)
            {
                if (ViewConfig.Mode == "rolling")
                {
                    ViewContent = _memory.GetPageContent(view);
                }
                else
                {
                    // Load all snapshots for dated pages
                    ViewSnapshots = _memory.GetPageHistory(view);
                    ViewLabel = label ?? ViewSnapshots.FirstOrDefault().Label;
                    ViewContent = ViewLabel != null
                        ? _memory.GetPageContent(view, ViewLabel)
                        : null;
                }
            }
        }

        foreach (var page in Pages)
        {
            PagePreviews[page.Name] = _memory.GetPagePreview(page.Name);
            if (page.Mode != "rolling")
                PageSnapshots[page.Name] = _memory.GetPageSnapshots(page.Name);
        }

        var configs = await _dataSources.LoadAsync();
        AvailableSources = configs.Where(c => c.Enabled && !s.IsHidden(c.Name))
            .Select(c => c.Name).Distinct().OrderBy(n => n).ToList();

        foreach (var name in AvailableSources)
        {
            var sourceDir = Path.Combine(_store.RootPath, name);
            if (!Directory.Exists(sourceDir)) continue;
            var types = Directory.GetDirectories(sourceDir)
                .Select(Path.GetFileName).Where(t => t != null).Select(t => t!).OrderBy(t => t).ToList();
            if (types.Count > 0) AvailableSourceTypes[name] = types;
        }
    }

    public async Task<IActionResult> OnPostSavePageAsync(string name, string? oldName, string? description,
        string prompt, string agentTier, string mode, int? retentionPeriods, string? scanSources)
    {
        var s = await _settings.LoadAsync();
        var config = new MemoryPageConfig
        {
            Name = name,
            Description = description ?? "",
            Prompt = prompt,
            AgentTier = agentTier,
            Mode = mode,
            RetentionPeriods = retentionPeriods,
            ScanSources = string.IsNullOrWhiteSpace(scanSources)
                ? []
                : [.. scanSources.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)],
        };

        if (!string.IsNullOrEmpty(oldName) && !oldName.Equals(name, StringComparison.OrdinalIgnoreCase))
        {
            s.MemoryPages.RemoveAll(p => p.Name.Equals(oldName, StringComparison.OrdinalIgnoreCase));
            _memory.RenamePage(oldName, name);
        }

        var idx = s.MemoryPages.FindIndex(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0) s.MemoryPages[idx] = config;
        else s.MemoryPages.Add(config);

        await _settings.SaveAsync(s);
        _settings.InvalidateCache();
        return RedirectToPage();
    }

    public IActionResult OnPostDeleteSnapshot(string name, string label)
    {
        _memory.DeletePageSnapshot(name, label);
        return RedirectToPage(new { view = name });
    }

    public async Task<IActionResult> OnPostDeletePageAsync(string name)
    {
        var s = await _settings.LoadAsync();
        s.MemoryPages.RemoveAll(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        await _settings.SaveAsync(s);
        _settings.InvalidateCache();
        _memory.DeletePageContent(name);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRefreshPageAsync(string name)
    {
        var s = await _settings.LoadAsync();
        var page = s.MemoryPages.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (page != null)
        {
            _memory.ResetPageCursors(name);
            await _memory.ScanPageAsync(page, HttpContext.RequestAborted, force: true);
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostScanAllAsync()
    {
        await _memory.ScanAllAsync(HttpContext.RequestAborted);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnGetSourceAsync(string name, string? label)
    {
        var s = await _settings.LoadAsync();
        var page = s.MemoryPages.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (page == null) return NotFound();
        var data = await _memory.GetPeriodSourceDataAsync(page, label);
        return Content(data, "text/plain; charset=utf-8");
    }

    public static string FormatLabel(string label) => MemoryService.FormatLabel(label);
}
