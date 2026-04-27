using Cima;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Assistant.Sdk;

namespace Assistant.Host.Pages;

[IgnoreAntiforgeryToken]
public class MemoryModel : PageModel
{
    private readonly IDataSourceManager _manager;

    public MemoryModel(IDataSourceManager manager) => _manager = manager;

    public string? SourceName { get; set; }
    public string? StorePath { get; set; }
    public int EpisodeTotal { get; set; }
    public int FactTotal { get; set; }
    public int SkillTotal { get; set; }
    public int CueTotal { get; set; }
    public double AvgImportance { get; set; }
    public double AvgConfidence { get; set; }

    public List<CimaEpisode> Episodes { get; set; } = [];
    public List<CimaFact> Facts { get; set; } = [];
    public List<CimaSkill> Skills { get; set; } = [];
    public List<CimaCue> Cues { get; set; } = [];

    public string ActiveTab { get; set; } = "episodes";

    public void OnGet(string? source = null, string? tab = null)
    {
        ActiveTab = tab ?? "episodes";

        var inst = (source != null
            ? _manager.GetInstance(source, "cima")
            : _manager.GetInstances("cima").FirstOrDefault())
            as CimaDataSourceInstance;

        if (inst == null) return;

        SourceName = inst.Name;
        StorePath = inst.StorePath;

        Episodes = inst.GetEpisodes(200).ToList();
        Facts = inst.GetFacts().ToList();
        Skills = inst.GetSkills().ToList();
        Cues = inst.GetCues().ToList();

        EpisodeTotal = Episodes.Count;
        FactTotal = Facts.Count;
        SkillTotal = Skills.Count;
        CueTotal = Cues.Count;
        AvgImportance = EpisodeTotal > 0 ? Math.Round(Episodes.Average(e => e.Importance), 2) : 0;
        AvgConfidence = FactTotal > 0 ? Math.Round(Facts.Average(f => f.Confidence), 2) : 0;
    }

    public IActionResult OnPostDeleteEpisode(string id, string? source, string tab = "episodes")
    {
        GetInst(source)?.DeleteEpisode(id);
        return RedirectToPage(new { source, tab });
    }

    public IActionResult OnPostDeleteFact(string subject, string relation, string obj, string? source, string tab = "facts")
    {
        GetInst(source)?.DeleteFact(subject, relation, obj);
        return RedirectToPage(new { source, tab });
    }

    public IActionResult OnPostDeleteSkill(string trigger, string? source, string tab = "skills")
    {
        GetInst(source)?.DeleteSkill(trigger);
        return RedirectToPage(new { source, tab });
    }

    public IActionResult OnPostDeleteCue(string predicate, string? source, string tab = "cues")
    {
        GetInst(source)?.DeleteCue(predicate);
        return RedirectToPage(new { source, tab });
    }

    private CimaDataSourceInstance? GetInst(string? source) =>
        (source != null
            ? _manager.GetInstance(source, "cima")
            : _manager.GetInstances("cima").FirstOrDefault())
        as CimaDataSourceInstance;

    public List<CimaDataSourceInstance> GetAllInstances() =>
        _manager.GetInstances("cima").OfType<CimaDataSourceInstance>().ToList();

    public string TabUrl(string tab) =>
        SourceName != null ? $"/Memory/{Uri.EscapeDataString(SourceName)}?tab={tab}" : $"/Memory?tab={tab}";

    public static string FormatTimestamp(double timestamp)
    {
        var dt = DateTimeOffset.FromUnixTimeSeconds((long)timestamp).LocalDateTime;
        var age = DateTime.Now - dt;
        if (age.TotalDays < 1) return dt.ToString("HH:mm");
        if (age.TotalDays < 7) return dt.ToString("ddd HH:mm");
        return dt.ToString("MMM d, yyyy");
    }
}
