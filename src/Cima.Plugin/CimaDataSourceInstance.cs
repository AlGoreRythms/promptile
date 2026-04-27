using System.Text.Json;
using System.Text.Json.Serialization;
using Assistant.Sdk;
using Microsoft.Extensions.Logging;

namespace Cima;

// ---------------------------------------------------------------------------
// JSON models — mirror the Python dataclasses, snake_case on disk
// ---------------------------------------------------------------------------

public class CimaEpisode
{
    public string Id { get; set; } = "";
    public double Timestamp { get; set; }
    public string Narrative { get; set; } = "";
    public List<string> Entities { get; set; } = [];
    public string Outcome { get; set; } = "unknown";
    public double Importance { get; set; } = 0.5;
    [JsonPropertyName("embedding")]
    public List<double> Embedding { get; set; } = [];

    public double AgeSeconds() => Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - Timestamp);
}

public class CimaFact
{
    public string Subject { get; set; } = "";
    public string Relation { get; set; } = "";
    [JsonPropertyName("obj")]
    public string Obj { get; set; } = "";
    public double Confidence { get; set; } = 0.8;
    [JsonPropertyName("last_reinforced")]
    public double LastReinforced { get; set; }
    [JsonPropertyName("source_episode_id")]
    public string SourceEpisodeId { get; set; } = "";
}

public class CimaSkill
{
    public string Trigger { get; set; } = "";
    public string Template { get; set; } = "";
    [JsonPropertyName("fire_count")]
    public int FireCount { get; set; }
    [JsonPropertyName("last_fired")]
    public double LastFired { get; set; }
}

public class CimaCue
{
    public string Predicate { get; set; } = "";
    public string Action { get; set; } = "";
    public bool Active { get; set; } = true;
    [JsonPropertyName("fire_count")]
    public int FireCount { get; set; }
    [JsonPropertyName("last_fired")]
    public double LastFired { get; set; }
}

// ---------------------------------------------------------------------------
// Instance — holds the in-memory state, reads/writes the JSON files
// ---------------------------------------------------------------------------

public class CimaDataSourceInstance : IDataSourceInstance
{
    public string Id => Config.Id;
    public string Name => Config.Name;
    public string Type => "cima";
    public DataSourceConfig Config { get; }

    public string StorePath => _dir;

    private string _dir = "";
    private IInformationStore? _store;
    private IAiServiceResolver? _aiResolver;
    private ILogger? _logger;
    private ISyncReporter? _sync;
    private CancellationTokenSource? _cts;
    private Task? _consolidationTask;

    private List<CimaEpisode> _episodes = [];
    private List<CimaFact> _facts = [];
    private List<CimaSkill> _skills = [];
    private List<CimaCue> _cues = [];
    private Dictionary<string, long> _cursors = [];
    private int _episodeCounter;

    private const int ChunkSizeChars = 4000;

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private static readonly HashSet<string> _stopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the","a","an","is","are","was","were","i","you","he","she","it","we","they",
        "to","of","in","on","for","with","about","what","how","do","can","my","your","this","that"
    };

    public CimaDataSourceInstance(DataSourceConfig config)
    {
        Config = config;
    }

    public Task StartAsync(INotificationBus bus, IServiceProvider services, CancellationToken ct)
    {
        _store     = services.GetService(typeof(IInformationStore)) as IInformationStore;
        _aiResolver = services.GetService(typeof(IAiServiceResolver)) as IAiServiceResolver;
        _logger    = services.GetService(typeof(ILogger<CimaDataSourceInstance>)) as ILogger;
        _sync      = services.GetService(typeof(ISyncReporter)) as ISyncReporter;

        var configured = Config.Config.GetValueOrDefault("persistDir", "").Trim()
            .Replace("~", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        _dir = !string.IsNullOrEmpty(configured) ? configured : Path.Combine(_store!.RootPath, Name, "cima");

        Load();

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _consolidationTask = Task.Run(() => ConsolidationLoopAsync(_cts.Token), _cts.Token);

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _cts?.Cancel();
        if (_consolidationTask != null)
            try { await _consolidationTask.WaitAsync(TimeSpan.FromSeconds(5), ct); } catch { }
    }

    public Task<DataSourceStatus> GetStatusAsync()
    {
        var msg = $"{_episodes.Count} episodes · {_facts.Count} facts · {_skills.Count} skills · {_cues.Count} cues";
        return Task.FromResult(new DataSourceStatus(Directory.Exists(_dir), msg));
    }

    public Task ResetStateAsync()
    {
        Load();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // -------------------------------------------------------------------------
    // CIMA operations (called by MCP tools)
    // -------------------------------------------------------------------------

    public object QueryContext(string input, IList<string>? entities = null)
    {
        Load();
        var queryEntities = entities?.Count > 0
            ? entities.Select(e => e.ToLower()).ToHashSet()
            : ExtractWords(input);

        // Episodic retrieval — score by entity overlap + narrative keyword match + recency + importance
        var scored = _episodes.Select(ep =>
        {
            var epSet = ep.Entities.Select(e => e.ToLower()).ToHashSet();
            double entityScore = queryEntities.Count == 0 ? 0
                : (double)queryEntities.Intersect(epSet).Count() / queryEntities.Count;
            double narrativeScore = queryEntities.Count == 0 ? 0
                : (double)queryEntities.Count(w => ep.Narrative.Contains(w, StringComparison.OrdinalIgnoreCase)) / queryEntities.Count;
            double recency = Math.Exp(-ep.AgeSeconds() / (86400.0 * 30)); // decay over 30 days
            double score = 0.3 * entityScore + 0.35 * narrativeScore + 0.15 * recency + 0.2 * ep.Importance;
            return (score, ep);
        })
        .OrderByDescending(x => x.score)
        .Take(8)
        .Where(x => x.score > 0)
        .Select(x => new { x.ep.Narrative, x.ep.Outcome, x.ep.Importance,
            Timestamp = DateTimeOffset.FromUnixTimeSeconds((long)x.ep.Timestamp).ToString("yyyy-MM-dd") })
        .ToList();

        // Semantic neighbors — match query words against subject, relation, and object text
        var facts = queryEntities
            .SelectMany(e => _facts.Where(f =>
                f.Subject.Contains(e, StringComparison.OrdinalIgnoreCase) ||
                f.Obj.Contains(e, StringComparison.OrdinalIgnoreCase) ||
                f.Relation.Contains(e, StringComparison.OrdinalIgnoreCase)))
            .GroupBy(f => (f.Subject, f.Relation, f.Obj))
            .Select(g => g.First())
            .OrderByDescending(f => f.Confidence)
            .Take(15)
            .Select(f => new { f.Subject, f.Relation, f.Obj, Confidence = Math.Round(f.Confidence, 2) })
            .ToList();

        // Procedural matches
        var procedures = _skills
            .Where(p =>
            {
                if (string.IsNullOrEmpty(p.Trigger)) return false;
                var triggerWords = p.Trigger.ToLower().Split(' ').ToHashSet();
                var contextWords = input.ToLower().Split(' ').ToHashSet();
                return (double)triggerWords.Intersect(contextWords).Count() / triggerWords.Count > 0.4;
            })
            .Select(p => p.Template)
            .ToList();

        // Prospective cues
        var prospective = _cues
            .Where(c =>
            {
                if (!c.Active || string.IsNullOrEmpty(c.Predicate)) return false;
                var predWords = c.Predicate.ToLower().Split(' ').ToHashSet();
                var ctxWords = input.ToLower().Split(' ').ToHashSet();
                return (double)predWords.Intersect(ctxWords).Count() / predWords.Count > 0.5;
            })
            .Select(c => c.Action)
            .ToList();

        return new
        {
            working_memory = new { goal = input, entities = queryEntities.ToList() },
            episodic = scored,
            semantic = facts,
            procedural = procedures,
            prospective,
        };
    }

    public CimaEpisode RecordEpisode(string narrative, IList<string> entities,
        string outcome = "unknown", double importance = 0.5, double? occurredAt = null)
    {
        Load();
        var ts = occurredAt ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var day = DateTimeOffset.FromUnixTimeSeconds((long)ts).UtcDateTime.Date;
        var prefix = narrative[..Math.Min(80, narrative.Length)].ToLower();

        var existing = _episodes.FirstOrDefault(e =>
            DateTimeOffset.FromUnixTimeSeconds((long)e.Timestamp).UtcDateTime.Date == day &&
            e.Narrative[..Math.Min(80, e.Narrative.Length)].ToLower() == prefix);
        if (existing != null) return existing;

        _episodeCounter++;
        var ep = new CimaEpisode
        {
            Id = $"ep_{_episodeCounter:D4}",
            Timestamp = ts,
            Narrative = narrative,
            Entities = [.. entities],
            Outcome = outcome,
            Importance = importance,
        };
        _episodes.Add(ep);
        Save();
        return ep;
    }

    public CimaFact AssertFact(string subject, string relation, string obj, double confidence = 0.8)
    {
        Load();
        var existing = _facts.FirstOrDefault(f =>
            f.Subject.Equals(subject, StringComparison.OrdinalIgnoreCase) &&
            f.Relation.Equals(relation, StringComparison.OrdinalIgnoreCase) &&
            f.Obj.Equals(obj, StringComparison.OrdinalIgnoreCase));

        if (existing != null)
        {
            existing.Confidence = Math.Min(1.0, existing.Confidence + 0.1);
            existing.LastReinforced = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
            Save();
            return existing;
        }

        var fact = new CimaFact
        {
            Subject = subject, Relation = relation, Obj = obj,
            Confidence = confidence,
            LastReinforced = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
        };
        _facts.Add(fact);
        Save();
        return fact;
    }

    public CimaSkill LearnSkill(string trigger, string template)
    {
        Load();
        var skill = new CimaSkill { Trigger = trigger, Template = template };
        _skills.Add(skill);
        Save();
        return skill;
    }

    public CimaCue RememberWhen(string predicate, string action)
    {
        Load();
        var cue = new CimaCue { Predicate = predicate, Action = action };
        _cues.Add(cue);
        Save();
        return cue;
    }

    public bool DeleteEpisode(string id)
    {
        Load();
        var before = _episodes.Count;
        _episodes = _episodes.Where(e => e.Id != id).ToList();
        if (_episodes.Count == before) return false;
        Save();
        return true;
    }

    public bool DeleteFact(string subject, string relation, string obj)
    {
        Load();
        var before = _facts.Count;
        _facts = _facts.Where(f =>
            !(f.Subject.Equals(subject, StringComparison.OrdinalIgnoreCase) &&
              f.Relation.Equals(relation, StringComparison.OrdinalIgnoreCase) &&
              f.Obj.Equals(obj, StringComparison.OrdinalIgnoreCase))).ToList();
        if (_facts.Count == before) return false;
        Save();
        return true;
    }

    public bool DeleteSkill(string trigger)
    {
        Load();
        var idx = _skills.FindIndex(s => s.Trigger == trigger);
        if (idx < 0) return false;
        _skills.RemoveAt(idx);
        Save();
        return true;
    }

    public bool DeleteCue(string predicate)
    {
        Load();
        var idx = _cues.FindIndex(c => c.Predicate == predicate);
        if (idx < 0) return false;
        _cues.RemoveAt(idx);
        Save();
        return true;
    }

    public (int SemanticFacts, int Episodes) ForgetSubject(string subject)
    {
        Load();
        int factsBefore = _facts.Count;
        _facts = _facts.Where(f =>
            !f.Subject.Contains(subject, StringComparison.OrdinalIgnoreCase) &&
            !f.Obj.Contains(subject, StringComparison.OrdinalIgnoreCase)).ToList();

        int epsBefore = _episodes.Count;
        _episodes = _episodes.Where(e =>
            !e.Entities.Any(en => en.Contains(subject, StringComparison.OrdinalIgnoreCase))).ToList();

        Save();
        return (factsBefore - _facts.Count, epsBefore - _episodes.Count);
    }

    public object GetStats()
    {
        Load();
        double avgImportance = _episodes.Count > 0 ? _episodes.Average(e => e.Importance) : 0;
        double avgAgeDays = _episodes.Count > 0 ? _episodes.Average(e => e.AgeSeconds()) / 86400.0 : 0;
        double avgConfidence = _facts.Count > 0 ? _facts.Average(f => f.Confidence) : 0;

        return new
        {
            episodes = new
            {
                total = _episodes.Count,
                avg_importance = Math.Round(avgImportance, 3),
                avg_age_days = Math.Round(avgAgeDays, 2),
            },
            semantic = new
            {
                total = _facts.Count,
                avg_confidence = Math.Round(avgConfidence, 3),
            },
            procedural = new
            {
                total = _skills.Count,
                total_fires = _skills.Sum(s => s.FireCount),
            },
            prospective = new
            {
                total = _cues.Count,
                active = _cues.Count(c => c.Active),
            },
        };
    }

    public IReadOnlyList<CimaEpisode> GetEpisodes(int limit = 10, IList<string>? entities = null)
    {
        Load();
        if (entities?.Count > 0)
        {
            var q = entities.Select(e => e.ToLower()).ToHashSet();
            return _episodes
                .Select(ep =>
                {
                    var epSet = ep.Entities.Select(e => e.ToLower()).ToHashSet();
                    double score = q.Count > 0
                        ? (double)q.Intersect(epSet).Count() / q.Count
                        : 0;
                    return (score, ep);
                })
                .OrderByDescending(x => x.score)
                .Take(limit)
                .Select(x => x.ep)
                .ToList();
        }
        return _episodes.OrderByDescending(e => e.Timestamp).Take(limit).ToList();
    }

    public IReadOnlyList<CimaSkill> GetSkills()
    {
        Load();
        return _skills.OrderByDescending(s => s.FireCount).ToList();
    }

    public IReadOnlyList<CimaCue> GetCues()
    {
        Load();
        return _cues.OrderByDescending(c => c.FireCount).ToList();
    }

    public IReadOnlyList<CimaFact> GetFacts(string? subject = null, string? relation = null, string? obj = null)
    {
        Load();
        return _facts
            .Where(f =>
                (subject == null || f.Subject.Equals(subject, StringComparison.OrdinalIgnoreCase)) &&
                (relation == null || f.Relation.Equals(relation, StringComparison.OrdinalIgnoreCase)) &&
                (obj == null || f.Obj.Equals(obj, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(f => f.Confidence)
            .ToList();
    }

    // -------------------------------------------------------------------------
    // Consolidation agent
    // -------------------------------------------------------------------------

    private async Task ConsolidationLoopAsync(CancellationToken ct)
    {
        var intervalMinutes = int.TryParse(Config.Config.GetValueOrDefault("scanIntervalMinutes", "30"), out var m) ? m : 30;

        // Short initial delay so other data sources finish starting before we scan
        await Task.Delay(TimeSpan.FromSeconds(15), ct);

        while (!ct.IsCancellationRequested)
        {
            try { await ScanAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger?.LogWarning(ex, "[CIMA:{Name}] Consolidation scan failed", Name); }

            await Task.Delay(TimeSpan.FromMinutes(intervalMinutes), ct);
        }
    }

    private async Task ScanAsync(CancellationToken ct)
    {
        if (_store == null || _aiResolver == null) return;

        var sources = _store.ListSources();
        var filesProcessed = 0;

        foreach (var (sourceName, sourceTypes) in sources)
        {
            if (!sourceName.Equals(Name, StringComparison.OrdinalIgnoreCase)) continue;

            foreach (var sourceType in sourceTypes)
            {
                if (sourceType.Equals("cima", StringComparison.OrdinalIgnoreCase)) continue;

                var dataPath = _store.GetDataPath(sourceName, sourceType);
                if (!Directory.Exists(dataPath)) continue;

                var files = Directory.GetFiles(dataPath, "*.md", SearchOption.AllDirectories)
                    .OrderBy(f => File.GetLastWriteTimeUtc(f));

                foreach (var file in files)
                {
                    ct.ThrowIfCancellationRequested();
                    var mtime = ((DateTimeOffset)File.GetLastWriteTimeUtc(file)).ToUnixTimeSeconds();
                    if (_cursors.TryGetValue(file, out var prev) && prev == mtime) continue;

                    try
                    {
                        var content = await File.ReadAllTextAsync(file, ct);
                        await ProcessFileAsync(content, sourceName, sourceType, ct);
                        _cursors[file] = mtime;
                        SaveCursors();
                        filesProcessed++;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger?.LogWarning(ex, "[CIMA:{Name}] Failed to process {File}", Name, file);
                    }
                }
            }
        }

        if (filesProcessed > 0)
            _logger?.LogInformation("[CIMA:{Name}] Consolidated {Count} file(s)", Name, filesProcessed);
    }

    private async Task ProcessFileAsync(string content, string sourceName, string sourceType, CancellationToken ct)
    {
        if (content.Length < 100) return;

        var tier = Config.Config.GetValueOrDefault("enricherTier", "light");
        var prompt = BuildPrompt(sourceType);
        var chunks = Chunk(content, ChunkSizeChars);

        foreach (var chunk in chunks)
        {
            try
            {
                var ai = await _aiResolver!.GetServiceAsync(tier);
                var response = await ai.CompleteAsync(prompt, chunk, ct);
                ParseAndStore(response.Text, sourceName);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogWarning(ex, "[CIMA:{Name}] Enrichment failed for {Source}:{Type}", Name, sourceName, sourceType);
            }
        }
    }

    private static List<string> Chunk(string content, int maxChars)
    {
        var chunks = new List<string>();
        var sections = content.Split("\n---\n", StringSplitOptions.RemoveEmptyEntries);
        var current = new System.Text.StringBuilder();

        foreach (var section in sections)
        {
            if (current.Length + section.Length > maxChars && current.Length > 0)
            {
                chunks.Add(current.ToString());
                current.Clear();
            }
            current.Append(section);
            current.Append("\n---\n");
        }

        if (current.Length > 0)
            chunks.Add(current.ToString());

        return chunks.Count > 0 ? chunks : [content];
    }

    private static string BuildPrompt(string sourceType) => sourceType switch
    {
        "Slack" => SlackPrompt,
        "Gmail" => GmailPrompt,
        "Calendar" or "google-calendar" => CalendarPrompt,
        _ => GenericPrompt,
    };

    private void ParseAndStore(string responseText, string sourceName)
    {
        var start = responseText.IndexOf('{');
        var end = responseText.LastIndexOf('}');
        if (start < 0 || end < 0) return;
        var json = responseText[start..(end + 1)];

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("facts", out var facts))
            {
                foreach (var f in facts.EnumerateArray())
                {
                    var subject    = f.TryGetProperty("subject", out var s) ? s.GetString() ?? "" : "";
                    var relation   = f.TryGetProperty("relation", out var r) ? r.GetString() ?? "" : "";
                    var obj        = f.TryGetProperty("obj", out var o) ? o.GetString() ?? "" : "";
                    var confidence = f.TryGetProperty("confidence", out var c) ? c.GetDouble() : 0.7;

                    if (!string.IsNullOrWhiteSpace(subject) && !string.IsNullOrWhiteSpace(relation) && !string.IsNullOrWhiteSpace(obj))
                        AssertFact(subject, relation, obj, confidence);
                }
            }

            if (root.TryGetProperty("episodes", out var episodes))
            {
                foreach (var e in episodes.EnumerateArray())
                {
                    var narrative  = e.TryGetProperty("narrative", out var n) ? n.GetString() ?? "" : "";
                    var outcome    = e.TryGetProperty("outcome", out var oc) ? oc.GetString() ?? "unknown" : "unknown";
                    var importance = e.TryGetProperty("importance", out var imp) ? imp.GetDouble() : 0.5;

                    double? occurredAt = null;
                    if (e.TryGetProperty("occurred_at", out var oat) && oat.GetString() is { } oatStr
                        && oatStr.Contains('-')
                        && DateTimeOffset.TryParse(oatStr, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
                        occurredAt = parsed.ToUnixTimeSeconds();

                    if (importance < 0.5) continue;

                    var entities = new List<string>();
                    if (e.TryGetProperty("entities", out var ents))
                        foreach (var ent in ents.EnumerateArray())
                            if (ent.GetString() is { } sv && IsValidEntity(sv))
                                entities.Add(sv);

                    if (!string.IsNullOrWhiteSpace(narrative) && occurredAt.HasValue)
                        RecordEpisode(narrative, entities, outcome, importance, occurredAt);
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "[CIMA:{Name}] Failed to parse enrichment response", Name);
        }
    }

    private const string ResponseSchema = """
        Respond with valid JSON only — no prose, no markdown fences:
        {
          "facts": [
            {"subject": "...", "relation": "...", "obj": "...", "confidence": 0.0-1.0}
          ],
          "episodes": [
            {"narrative": "...", "entities": ["..."], "outcome": "success|partial|unknown", "importance": 0.5-1.0, "occurred_at": "ISO8601 date (YYYY-MM-DD) or datetime (YYYY-MM-DDTHH:MM:SS) — must include a date, never a bare time"}
          ]
        }

        Confidence guide: 0.9 = direct statement, 0.7 = inferred, 0.5 = uncertain.
        Importance guide: 0.9 = major incident/decision with clear impact, 0.7 = notable decision or discovery, 0.5 = minimum threshold — only use if something specific was actually decided or discovered. Do not record that a meeting occurred or a message was sent.
        Entities: use only human-readable display names (e.g. "Alice", "Project X"). Never include email addresses, Slack user IDs like <@U08350LU8HF>, or system identifiers.
        Facts: only extract durable relationships. Do NOT extract transient states (e.g. "is sick", "is waiting", "is in a meeting").
        occurred_at: REQUIRED for episodes — extract from timestamps/dates visible in the content. If no date is determinable, omit the episode entirely.
        Omit either array if nothing relevant was found. Emit an empty JSON object {} if no signal.
        """;

    private const string SlackPrompt = $"""
        You extract structured knowledge from Slack conversations for a personal memory system.

        Extract:
        - FACTS: durable relationships — who owns what, who works on what, team structure, technical decisions, project associations.
          Good: subject="Alice", relation="owns", obj="Authentication service"
          Bad: subject="Alice", relation="is_online", obj="checking Slack"
        - EPISODES: something specific was DECIDED or DISCOVERED — a bug root cause, a policy change, a shipped feature, a notable escalation. The narrative must state what was decided or found, not just that a conversation happened.

        Skip: greetings, status updates ("I'll be back in 30"), trivial chatter, meeting announcements, and anything that doesn't add durable knowledge.

        {ResponseSchema}
        """;

    private const string GmailPrompt = $"""
        You extract structured knowledge from email threads for a personal memory system.

        Extract:
        - FACTS: durable relationships, responsibilities, commitments, project ownership.
          Good: subject="Alice", relation="is_responsible_for", obj="Q3 roadmap delivery"
          Bad: subject="Alice", relation="sent_email", obj="weekly update"
        - EPISODES: a specific decision was made, a commitment was given, or important information was communicated. The narrative must state the substance, not just that an email was sent.

        Skip: routine notifications, newsletters, automated emails, meeting invites, and anything without durable knowledge.

        {ResponseSchema}
        """;

    private const string CalendarPrompt = $"""
        You extract structured knowledge from calendar events for a personal memory system.

        Extract:
        - FACTS: recurring meeting patterns and attendee relationships.
          Good: subject="Engineering team", relation="meets_weekly_on", obj="Tuesdays at 10am"
          Bad: subject="Engineering team", relation="met_on", obj="2026-04-27"
        - EPISODES: only truly exceptional one-off events — a cancelled key meeting, an emergency incident call, a post-mortem, a notable all-hands. Do NOT record that a recurring meeting occurred.

        {ResponseSchema}
        """;

    private const string GenericPrompt = $"""
        You extract structured knowledge from content for a personal memory system.

        Extract:
        - FACTS: subject-relation-object triples that capture durable knowledge about people, projects, or decisions
        - EPISODES: notable events, decisions, or interactions worth remembering.
          Include any date or time context visible in the content.

        {ResponseSchema}
        """;

    // -------------------------------------------------------------------------
    // Persistence
    // -------------------------------------------------------------------------

    private void Load()
    {
        if (!Directory.Exists(_dir)) return;
        _episodes = LoadFile<List<CimaEpisode>>("episodic.json") ?? [];
        _facts    = LoadFile<List<CimaFact>>("semantic.json") ?? [];
        _skills   = LoadFile<List<CimaSkill>>("procedural.json") ?? [];
        _cues     = LoadFile<List<CimaCue>>("prospective.json") ?? [];
        _cursors  = LoadFile<Dictionary<string, long>>("enrichment_cursors.json") ?? [];

        _episodeCounter = _episodes.Count == 0 ? 0
            : _episodes.Select(e =>
            {
                var parts = e.Id.Split('_');
                return parts.Length > 1 && int.TryParse(parts[^1], out var n) ? n : 0;
            }).Max();
    }

    private void Save()
    {
        Directory.CreateDirectory(_dir);
        SaveFile("episodic.json", _episodes);
        SaveFile("semantic.json", _facts);
        SaveFile("procedural.json", _skills);
        SaveFile("prospective.json", _cues);
    }

    private void SaveCursors()
    {
        Directory.CreateDirectory(_dir);
        SaveFile("enrichment_cursors.json", _cursors);
    }

    private T? LoadFile<T>(string name)
    {
        try
        {
            var path = Path.Combine(_dir, name);
            if (!File.Exists(path)) return default;
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), _json);
        }
        catch { return default; }
    }

    private void SaveFile<T>(string name, T data)
    {
        var path = Path.Combine(_dir, name);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(data, _json));
        File.Move(tmp, path, overwrite: true);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static bool IsValidEntity(string s) =>
        !string.IsNullOrWhiteSpace(s) &&
        s.Length > 1 &&
        !s.Contains('@') &&                          // no email addresses
        !s.StartsWith("<@") &&                       // no Slack mention IDs
        !System.Text.RegularExpressions.Regex.IsMatch(s, @"^U[A-Z0-9]{8,10}$"); // no raw Slack user IDs

    private static HashSet<string> ExtractWords(string text) =>
        text.ToLower()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Trim('.', ',', '?', '!', ';', ':', '"', '\''))
            .Where(w => w.Length > 2 && !_stopwords.Contains(w))
            .ToHashSet();
}
