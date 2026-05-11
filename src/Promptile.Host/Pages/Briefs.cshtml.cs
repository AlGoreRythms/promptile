using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Promptile.Host.Services;
using Promptile.Sdk;

namespace Promptile.Host.Pages;

public class BriefsModel : PageModel
{
    private readonly IInformationStore _store;
    private readonly IDataSourceManager _dataSourceManager;
    private readonly SettingsService _settings;

    public BriefsModel(IInformationStore store, IDataSourceManager dataSourceManager, SettingsService settings)
    {
        _store = store;
        _dataSourceManager = dataSourceManager;
        _settings = settings;
    }

    public List<BriefGroup> Groups { get; set; } = [];

    public async Task OnGetAsync()
    {
        var s = await _settings.LoadAsync();
        var briefNames = _dataSourceManager.GetInstances()
            .Select(i => i.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(n => !s.IsHidden(n))
            .OrderBy(n => n)
            .ToList();

        foreach (var name in briefNames)
            Groups.Add(await LoadGroupAsync(name));

    }

    private async Task<BriefGroup> LoadGroupAsync(string name)
    {
        var notesPath = GetNotesPath(name);
        var situations = new List<SituationNote>();
        var archivedSituations = new List<SituationNote>();
        var actions = new List<ActionItem>();

        var actionsPath = Path.Combine(notesPath, "actions.json");
        if (System.IO.File.Exists(actionsPath))
        {
            try
            {
                actions = JsonSerializer.Deserialize<List<ActionItem>>(
                    await System.IO.File.ReadAllTextAsync(actionsPath),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
            }
            catch { }
        }

        var promotedSlugs = actions
            .Where(a => a.BriefCardId != null)
            .Select(a => a.BriefCardId!)
            .ToHashSet();

        if (Directory.Exists(notesPath))
        {
            foreach (var file in Directory.GetFiles(notesPath, "situation-*.md")
                .Where(f => !f.EndsWith(".archived.md"))
                .OrderByDescending(System.IO.File.GetLastWriteTimeUtc))
            {
                var content = await System.IO.File.ReadAllTextAsync(file);
                var slug = Path.GetFileNameWithoutExtension(file)["situation-".Length..];
                var title = ExtractTitle(content, slug);
                situations.Add(new SituationNote(title, slug, content, System.IO.File.GetLastWriteTimeUtc(file)));
            }

            foreach (var file in Directory.GetFiles(notesPath, "situation-*.archived.md")
                .OrderByDescending(System.IO.File.GetLastWriteTimeUtc))
            {
                var content = await System.IO.File.ReadAllTextAsync(file);
                var nameWithoutExt = Path.GetFileNameWithoutExtension(file);
                var slug = nameWithoutExt["situation-".Length..].Replace(".archived", "");
                var title = ExtractTitle(content, slug);
                archivedSituations.Add(new SituationNote(title, slug, content, System.IO.File.GetLastWriteTimeUtc(file)));
            }
        }

        return new BriefGroup(name, situations, archivedSituations, actions, promotedSlugs);
    }

    private static RedirectResult RedirectToBriefGroup(string briefName) =>
        new($"/Briefs#{Uri.EscapeDataString(briefName)}");

    public async Task<IActionResult> OnPostAddSituationToTodoAsync(string briefName, string slug)
    {
        var notesPath = GetNotesPath(briefName);
        var actionsPath = Path.Combine(notesPath, "actions.json");
        List<ActionItem> actions = [];
        if (System.IO.File.Exists(actionsPath))
        {
            try
            {
                actions = JsonSerializer.Deserialize<List<ActionItem>>(
                    await System.IO.File.ReadAllTextAsync(actionsPath),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
            }
            catch { }
        }

        if (!actions.Any(a => a.BriefCardId == slug))
        {
            var file = Path.Combine(notesPath, $"situation-{slug}.md");
            var title = System.IO.File.Exists(file)
                ? ExtractTitle(await System.IO.File.ReadAllTextAsync(file), slug)
                : slug;
            actions.Add(new ActionItem($"Follow up: {title}", "medium", null, false, BriefCardId: slug));
            await System.IO.File.WriteAllTextAsync(actionsPath,
                JsonSerializer.Serialize(actions, new JsonSerializerOptions { WriteIndented = true }));
        }

        return RedirectToBriefGroup(briefName);
    }

    public IActionResult OnPostArchiveSituationAsync(string briefName, string slug)
    {
        var notesPath = GetNotesPath(briefName);
        var from = Path.Combine(notesPath, $"situation-{slug}.md");
        var to = Path.Combine(notesPath, $"situation-{slug}.archived.md");
        if (System.IO.File.Exists(from))
            System.IO.File.Move(from, to, overwrite: true);
        return RedirectToBriefGroup(briefName);
    }

    public IActionResult OnPostRestoreSituationAsync(string briefName, string slug)
    {
        var notesPath = GetNotesPath(briefName);
        var from = Path.Combine(notesPath, $"situation-{slug}.archived.md");
        var to = Path.Combine(notesPath, $"situation-{slug}.md");
        if (System.IO.File.Exists(from))
            System.IO.File.Move(from, to, overwrite: true);
        return RedirectToBriefGroup(briefName);
    }

    public async Task<IActionResult> OnPostActionDoneAsync(string briefName, int index)
    {
        var actionsPath = Path.Combine(GetNotesPath(briefName), "actions.json");
        if (System.IO.File.Exists(actionsPath))
        {
            try
            {
                var actions = JsonSerializer.Deserialize<List<ActionItem>>(
                    await System.IO.File.ReadAllTextAsync(actionsPath),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
                if (index >= 0 && index < actions.Count)
                {
                    actions[index] = actions[index] with { Done = true };
                    await System.IO.File.WriteAllTextAsync(actionsPath,
                        JsonSerializer.Serialize(actions, new JsonSerializerOptions { WriteIndented = true }));
                }
            }
            catch { }
        }
        return RedirectToBriefGroup(briefName);
    }

    private string GetNotesPath(string briefName) =>
        _store.GetNotesPath(BriefingService.BriefingSource, briefName);

    private static string ExtractTitle(string markdown, string slug)
    {
        var firstLine = markdown.TrimStart().Split('\n')[0];
        if (firstLine.StartsWith("# ")) return firstLine[2..].Trim();
        return System.Globalization.CultureInfo.CurrentCulture.TextInfo
            .ToTitleCase(slug.Replace("-", " "));
    }
}

public record BriefGroup(
    string Name,
    List<SituationNote> Situations,
    List<SituationNote> ArchivedSituations,
    List<ActionItem> Actions,
    HashSet<string> PromotedSlugs);

public record SituationNote(string Title, string Slug, string Markdown, DateTimeOffset Updated);
