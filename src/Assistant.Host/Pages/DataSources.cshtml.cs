using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Assistant.Host.Services;
using Assistant.Sdk;

namespace Assistant.Host.Pages;

[IgnoreAntiforgeryToken]
public class DataSourcesModel : PageModel
{
    private readonly DataSourcesService _sources;
    private readonly DataSourceManager _manager;
    private readonly SettingsService _settings;

    public DataSourcesModel(DataSourcesService sources, DataSourceManager manager, SettingsService settings)
    {
        _sources = sources;
        _manager = manager;
        _settings = settings;
    }

    public List<DataSourceConfig> Configs { get; set; } = [];
    public Dictionary<string, DataSourceStatus> Statuses { get; set; } = [];
    public IReadOnlyList<IDataSourceProvider> Providers { get; set; } = [];
    public string? EditingId { get; set; }
    public bool ShowAdd { get; set; }

    public async Task OnGetAsync(string? edit = null, bool add = false)
    {
        var allConfigs = await _sources.LoadAsync();
        var s = await _settings.LoadAsync();
        var hidden = new HashSet<string>(s.HiddenNames, StringComparer.OrdinalIgnoreCase);
        Configs = hidden.Count > 0 ? allConfigs.Where(c => !hidden.Contains(c.Name)).ToList() : allConfigs;
        Providers = _manager.GetProviders();
        EditingId = edit;
        ShowAdd = add;
        await LoadStatusesAsync();
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        var id = Request.Form["id"].ToString();
        var type = Request.Form["type"].ToString();
        var name = Request.Form["name"].ToString().Trim();
        var enabled = Request.Form["enabled"].ToString() != "false";

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(type))
            return RedirectToPage();

        var provider = _manager.GetProviders().FirstOrDefault(p => p.Type == type);
        var config = new Dictionary<string, string>();

        // Preserve existing config values (OAuth tokens, credentials) when editing
        if (!string.IsNullOrEmpty(id))
        {
            var existing = await _sources.LoadAsync();
            var existingCfg = existing.FirstOrDefault(c => c.Id == id);
            if (existingCfg != null)
                foreach (var kvp in existingCfg.Config)
                    config[kvp.Key] = kvp.Value;
        }

        if (provider != null)
        {
            foreach (var field in provider.GetConfigFields().Concat(provider.GetPreAuthFields()))
            {
                var val = Request.Form[field.Key].ToString().Trim();
                if (!string.IsNullOrEmpty(val))
                    config[field.Key] = val;
            }
        }

        var dsConfig = new DataSourceConfig(
            Id: string.IsNullOrEmpty(id) ? Guid.NewGuid().ToString("N")[..8] : id,
            Type: type,
            Name: name,
            Enabled: enabled,
            Config: config
        );

        await _sources.UpsertAsync(dsConfig);
        await _manager.ReloadAsync();

        // For new OAuth sources, redirect straight into the auth flow
        var dsProvider = _manager.GetProviders().FirstOrDefault(p => p.Type == dsConfig.Type);
        var authUrl = dsProvider?.GetAuthStartUrl(dsConfig.Id);
        if (authUrl != null && string.IsNullOrEmpty(dsConfig.Config.GetValueOrDefault("refreshToken")))
            return Redirect(authUrl);

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(string id)
    {
        await _sources.DeleteAsync(id);
        await _manager.ReloadAsync();
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostResetSyncAsync(string id)
    {
        await _manager.ResetInstanceAsync(id);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostResetAllAsync()
    {
        await _manager.ResetAllAsync();
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostToggleAsync(string id)
    {
        var all = await _sources.LoadAsync();
        var cfg = all.FirstOrDefault(c => c.Id == id);
        if (cfg != null)
        {
            var updated = cfg with { Enabled = !cfg.Enabled };
            await _sources.UpsertAsync(updated);
            await _manager.ReloadAsync();
        }
        return RedirectToPage();
    }

    private async Task LoadStatusesAsync()
    {
        foreach (var instance in _manager.GetInstances())
        {
            try { Statuses[instance.Id] = await instance.GetStatusAsync(); }
            catch { Statuses[instance.Id] = new DataSourceStatus(false, "Error"); }
        }
    }
}
