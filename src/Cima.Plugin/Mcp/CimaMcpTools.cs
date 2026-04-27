using System.ComponentModel;
using System.Text.Json;
using Assistant.Sdk;
using ModelContextProtocol.Server;

namespace Cima.Mcp;

[McpServerToolType]
public class CimaMcpTools
{
    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };

    private readonly IDataSourceManager _manager;

    public CimaMcpTools(IDataSourceManager manager) => _manager = manager;

    private CimaDataSourceInstance? GetInstance(string? sourceName = null) =>
        (sourceName != null
            ? _manager.GetInstance(sourceName, "cima")
            : _manager.GetInstances("cima").FirstOrDefault())
        as CimaDataSourceInstance;

    // -------------------------------------------------------------------------
    // Core memory operations
    // -------------------------------------------------------------------------

    [McpServerTool(Name = "cima_query"), Description(
        "Retrieve context from all CIMA memory stores for a query. Returns relevant episodes, " +
        "semantic facts, skill patterns, and prospective cues.")]
    public string Query(
        [Description("The query or user input to retrieve context for")] string query,
        [Description("Optional comma-separated entities to focus retrieval (e.g. 'Alice,ProjectX')")] string? entities = null,
        [Description("Name of the CIMA data source (omit if only one is configured)")] string? sourceName = null)
    {
        var inst = GetInstance(sourceName);
        if (inst == null) return "No CIMA memory configured.";

        var entityList = entities?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var ctx = inst.QueryContext(query, entityList);
        return JsonSerializer.Serialize(ctx, _json);
    }

    [McpServerTool(Name = "cima_record"), Description(
        "Record a new episode in episodic memory. Write the narrative as a causal arc: " +
        "what was asked, what was tried, what the outcome was.")]
    public string Record(
        [Description("Narrative describing the interaction (who, what, outcome)")] string narrative,
        [Description("Comma-separated list of entities/concepts involved")] string entities,
        [Description("Outcome: success, failure, partial, or unknown")] string outcome = "unknown",
        [Description("Importance 0–1 (higher = retained longer)")] double importance = 0.5,
        [Description("Name of the CIMA data source")] string? sourceName = null)
    {
        var inst = GetInstance(sourceName);
        if (inst == null) return "No CIMA memory configured.";

        var entityList = entities.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var ep = inst.RecordEpisode(narrative, entityList, outcome, importance);
        return JsonSerializer.Serialize(new { ep.Id, ep.Timestamp, ep.Importance }, _json);
    }

    [McpServerTool(Name = "cima_assert_fact"), Description(
        "Assert a semantic fact (subject → relation → object) into the knowledge graph. " +
        "Repeating the same triple reinforces its confidence.")]
    public string AssertFact(
        [Description("Subject entity (e.g. 'Alice')")] string subject,
        [Description("Relation (e.g. 'works_on', 'prefers', 'is_made_by')")] string relation,
        [Description("Object entity or value (e.g. 'GAIA project')")] string obj,
        [Description("Confidence 0–1")] double confidence = 0.8,
        [Description("Name of the CIMA data source")] string? sourceName = null)
    {
        var inst = GetInstance(sourceName);
        if (inst == null) return "No CIMA memory configured.";

        var fact = inst.AssertFact(subject, relation, obj, confidence);
        return JsonSerializer.Serialize(new
        {
            fact.Subject, fact.Relation, fact.Obj, Confidence = Math.Round(fact.Confidence, 2)
        }, _json);
    }

    [McpServerTool(Name = "cima_learn_skill"), Description(
        "Register a procedural skill pattern: when the trigger context is seen, apply the template. " +
        "Example trigger: 'SQL large table count'. Example template: 'Use HLL for approximate counts.'")]
    public string LearnSkill(
        [Description("Plain-English trigger description (keywords that indicate when to use this skill)")] string trigger,
        [Description("What to do when this skill fires — the template or guidance")] string template,
        [Description("Name of the CIMA data source")] string? sourceName = null)
    {
        var inst = GetInstance(sourceName);
        if (inst == null) return "No CIMA memory configured.";

        inst.LearnSkill(trigger, template);
        return JsonSerializer.Serialize(new { message = "Skill registered", trigger }, _json);
    }

    [McpServerTool(Name = "cima_remember_when"), Description(
        "Register a prospective cue: the next time the predicate appears in context, " +
        "surface the action. Example: predicate='Atlas project deadline', action='Atlas demo is Q2.'")]
    public string RememberWhen(
        [Description("Condition to watch for in future context")] string predicate,
        [Description("What to surface when the predicate fires")] string action,
        [Description("Name of the CIMA data source")] string? sourceName = null)
    {
        var inst = GetInstance(sourceName);
        if (inst == null) return "No CIMA memory configured.";

        inst.RememberWhen(predicate, action);
        return JsonSerializer.Serialize(new { message = "Cue registered", predicate }, _json);
    }

    [McpServerTool(Name = "cima_forget"), Description(
        "Remove all memory about a subject — deletes matching semantic facts and episodes.")]
    public string Forget(
        [Description("Subject to forget (all facts and episodes mentioning this are removed)")] string subject,
        [Description("Name of the CIMA data source")] string? sourceName = null)
    {
        var inst = GetInstance(sourceName);
        if (inst == null) return "No CIMA memory configured.";

        var (facts, episodes) = inst.ForgetSubject(subject);
        return JsonSerializer.Serialize(new { semantic_facts = facts, episodes }, _json);
    }

    // -------------------------------------------------------------------------
    // Inspection
    // -------------------------------------------------------------------------

    [McpServerTool(Name = "cima_stats"), Description(
        "Return a health snapshot of CIMA memory: episode count, fact count, average importance, etc.")]
    public string Stats(
        [Description("Name of the CIMA data source")] string? sourceName = null)
    {
        var inst = GetInstance(sourceName);
        if (inst == null) return "No CIMA memory configured.";
        return JsonSerializer.Serialize(inst.GetStats(), _json);
    }

    [McpServerTool(Name = "cima_get_episodes"), Description(
        "List recent episodes, optionally filtered by entity relevance.")]
    public string GetEpisodes(
        [Description("Max number of episodes to return")] int limit = 10,
        [Description("Optional comma-separated entities to filter by relevance")] string? entities = null,
        [Description("Name of the CIMA data source")] string? sourceName = null)
    {
        var inst = GetInstance(sourceName);
        if (inst == null) return "No CIMA memory configured.";

        var entityList = entities?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var eps = inst.GetEpisodes(limit, entityList);
        return JsonSerializer.Serialize(eps.Select(e => new
        {
            e.Id, e.Narrative, e.Outcome, e.Importance,
            age_hours = Math.Round(e.AgeSeconds() / 3600, 1),
        }), _json);
    }

    [McpServerTool(Name = "cima_get_facts"), Description(
        "Query semantic facts from the knowledge graph. All filters are optional.")]
    public string GetFacts(
        [Description("Filter by subject entity (exact match)")] string? subject = null,
        [Description("Filter by relation")] string? relation = null,
        [Description("Filter by object entity")] string? obj = null,
        [Description("Name of the CIMA data source")] string? sourceName = null)
    {
        var inst = GetInstance(sourceName);
        if (inst == null) return "No CIMA memory configured.";

        var facts = inst.GetFacts(subject, relation, obj);
        return JsonSerializer.Serialize(facts.Select(f => new
        {
            f.Subject, f.Relation, f.Obj, Confidence = Math.Round(f.Confidence, 2)
        }), _json);
    }
}
