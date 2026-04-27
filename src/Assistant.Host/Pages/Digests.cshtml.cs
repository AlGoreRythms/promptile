using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Assistant.Host.Services;
using Assistant.Sdk;

namespace Assistant.Host.Pages;

public class DigestsModel : PageModel
{
    private readonly IInformationStore _store;

    public DigestsModel(IInformationStore store)
    {
        _store = store;
    }

    public List<(string Name, string Label)> Digests { get; set; } = [];
    public string SelectedDigest { get; set; } = "";
    public string SelectedContent { get; set; } = "";

    public async Task OnGetAsync(string? d)
    {
        var notesPath = _store.GetNotesPath(DigestService.DigestSource, DigestService.DigestType);
        if (!Directory.Exists(notesPath)) return;

        var files = Directory.GetFiles(notesPath, "*.md")
            .OrderByDescending(System.IO.File.GetLastWriteTimeUtc)
            .ToList();

        foreach (var file in files)
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var label = DateOnly.TryParse(name, out var date)
                ? date.ToString("MMM d, yyyy") : name;
            Digests.Add((name, label));
        }

        var selected = d ?? Digests.FirstOrDefault().Name;
        if (!string.IsNullOrEmpty(selected) && Digests.Any(dg => dg.Name == selected))
        {
            SelectedDigest = selected;
            var path = Path.Combine(notesPath, $"{selected}.md");
            if (System.IO.File.Exists(path))
                SelectedContent = await System.IO.File.ReadAllTextAsync(path);
        }
    }
}
