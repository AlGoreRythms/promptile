using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Assistant.Sdk;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Assistant.Host.Services;

public class WorkJobService : IHostedService
{
    private readonly SettingsService _settings;
    private readonly DataSourcesService _sources;
    private readonly IInformationStore _store;
    private readonly INotificationBus _bus;
    private readonly ILogger<WorkJobService> _logger;
    private readonly string _runsPath;

    private readonly ConcurrentDictionary<string, string> _activeRunId = new();          // jobId → runId
    private readonly ConcurrentDictionary<string, List<string>> _liveOutput = new();     // "jobId:runId" → lines
    private List<WorkJobRun> _runs = [];
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public WorkJobService(SettingsService settings, DataSourcesService sources,
        IInformationStore store, INotificationBus bus, ILogger<WorkJobService> logger, string dataDir)
    {
        _settings = settings;
        _sources = sources;
        _store = store;
        _bus = bus;
        _logger = logger;
        _runsPath = Path.Combine(dataDir, "work-runs.json");
    }

    public Task StartAsync(CancellationToken ct)
    {
        LoadRuns();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    public string RunNow(string jobId)
    {
        var runId = Guid.NewGuid().ToString("N")[..8];
        _activeRunId[jobId] = runId;
        _ = Task.Run(() => ExecuteAsync(jobId, runId, CancellationToken.None));
        return runId;
    }

    public List<string> GetLiveOutput(string jobId, string runId) =>
        _liveOutput.TryGetValue($"{jobId}:{runId}", out var lines) ? [..lines] : [];

    public List<string> GetRunningIds() => [.._activeRunId.Keys];

    public List<WorkJobRun> GetRecentRuns() =>
        [.._runs.OrderByDescending(r => r.StartedAt).Take(50)];

    public async Task GenerateAndSaveDescriptionAsync(string jobId)
    {
        try
        {
            var s = await _settings.LoadAsync();
            var job = s.WorkJobs.FirstOrDefault(j => j.Id == jobId);
            if (job == null || string.IsNullOrWhiteSpace(job.Prompt)) return;

            var agent = s.Agents.FirstOrDefault(a => a.Id == job.AgentId);
            var claudeBin = agent?.Type == "claude-code" && !string.IsNullOrEmpty(agent.CommandPath)
                ? agent.CommandPath : "claude";

            var descPrompt =
                $"Job prompt: {job.Prompt}\n\n" +
                $"Write a single sentence (max 15 words) describing what this job does in plain language. " +
                $"Reply with only the sentence, no quotes.";

            var psi = new ProcessStartInfo
            {
                FileName = claudeBin,
                Arguments = "--print --dangerously-skip-permissions",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            ApplyShellEnvironment(psi);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var proc = Process.Start(psi);
            if (proc == null) return;

            await proc.StandardInput.WriteAsync(descPrompt.AsMemory(), cts.Token);
            proc.StandardInput.Close();

            var output = await proc.StandardOutput.ReadToEndAsync(cts.Token);
            await proc.WaitForExitAsync(cts.Token);

            var description = StripAnsi(output).Trim();
            if (string.IsNullOrWhiteSpace(description)) return;

            var s2 = await _settings.LoadAsync();
            var job2 = s2.WorkJobs.FirstOrDefault(j => j.Id == jobId);
            if (job2 != null && string.IsNullOrWhiteSpace(job2.Description))
            {
                job2.Description = description;
                await _settings.SaveAsync(s2);
                _settings.InvalidateCache();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Description generation failed for job {JobId}", jobId);
        }
    }

    public bool DeleteRun(string runId)
    {
        var removed = _runs.RemoveAll(r => r.RunId == runId) > 0;
        if (removed) SaveRuns();
        return removed;
    }

    private async Task ExecuteAsync(string jobId, string runId, CancellationToken ct)
    {
        var key = $"{jobId}:{runId}";
        _liveOutput[key] = [];

        var startedAt = DateTime.UtcNow;
        var status = "failed";
        string? error = null;

        void Log(string line)
        {
            _liveOutput.GetValueOrDefault(key)?.Add(line);
            _logger.LogInformation("[Work:{JobId}] {Line}", jobId, line);
        }

        try
        {
            var s = await _settings.LoadAsync();
            var job = s.WorkJobs.FirstOrDefault(j => j.Id == jobId);
            if (job == null) { Log("Job not found."); return; }

            var agent = s.Agents.FirstOrDefault(a => a.Id == job.AgentId);
            if (agent == null) { Log("No agent configured. Set one in Settings → Agents."); return; }

            var allSources = await _sources.LoadAsync();
            var hidden = new HashSet<string>(s.HiddenNames, StringComparer.OrdinalIgnoreCase);
            var allowedNames = new HashSet<string>(
                allSources.Where(c => c.Enabled && !hidden.Contains(c.Name)).Select(c => c.Name),
                StringComparer.OrdinalIgnoreCase);
            var folderConfig = allSources.FirstOrDefault(c => c.Id == job.FolderSourceId && c.Type == "folder" && c.Enabled && !hidden.Contains(c.Name));
            if (folderConfig == null) { Log("Folder source not found."); return; }

            var folderPath = folderConfig.Config.GetValueOrDefault("path", "");
            if (!Directory.Exists(folderPath)) { Log($"Folder path does not exist: {folderPath}"); return; }

            // Gather context from data sources (round-robin, same as widget runner)
            Log("Gathering context from data sources...");
            var contextSb = new StringBuilder();
            const int MaxContextChars = 60_000;
            var cutoff = DateTime.UtcNow.AddDays(-Math.Max(1, job.ContextDays));

            var sourceFilters = new Dictionary<string, HashSet<string>?>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in job.DataSources)
            {
                var colon = entry.IndexOf(':');
                var entryName = colon < 0 ? entry : entry[..colon];
                if (!allowedNames.Contains(entryName)) continue;
                if (colon < 0) sourceFilters[entry] = null;
                else
                {
                    var name = entry[..colon]; var type = entry[(colon + 1)..];
                    if (!sourceFilters.TryGetValue(name, out var set) || set == null)
                        sourceFilters[name] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { type };
                    else set.Add(type);
                }
            }

            foreach (var (sourceName, typeFilter) in sourceFilters)
            {
                var sourceDir = Path.Combine(_store.RootPath, sourceName);
                if (!Directory.Exists(sourceDir)) continue;

                var buckets = Directory.GetDirectories(sourceDir)
                    .SelectMany(typeDir =>
                    {
                        var sourceType = Path.GetFileName(typeDir);
                        if (typeFilter != null && !typeFilter.Contains(sourceType))
                            return Enumerable.Empty<(Queue<string>, string, string)>();
                        var dataPath = Path.Combine(typeDir, "Data");
                        if (!Directory.Exists(dataPath)) return Enumerable.Empty<(Queue<string>, string, string)>();
                        var subdirs = Directory.GetDirectories(dataPath);
                        var groups = subdirs.Length > 0
                            ? subdirs.Select(sub => (
                                Files: Directory.GetFiles(sub, "*.md")
                                    .Where(f => File.GetLastWriteTimeUtc(f) >= cutoff)
                                    .OrderByDescending(File.GetLastWriteTimeUtc).ToList(), Type: sourceType))
                            : [(Files: Directory.GetFiles(dataPath, "*.md")
                                    .Where(f => File.GetLastWriteTimeUtc(f) >= cutoff)
                                    .OrderByDescending(File.GetLastWriteTimeUtc).ToList(), Type: sourceType)];
                        return groups.Where(g => g.Files.Count > 0)
                            .Select(g => (Q: new Queue<string>(g.Files), g.Type, SrcName: sourceName));
                    }).ToList();

                while (buckets.Count > 0 && contextSb.Length < MaxContextChars)
                {
                    for (var i = buckets.Count - 1; i >= 0; i--)
                    {
                        if (contextSb.Length >= MaxContextChars) break;
                        var (q, sourceType, srcName) = buckets[i];
                        if (q.Count == 0) { buckets.RemoveAt(i); continue; }
                        var file = q.Dequeue();
                        var text = await File.ReadAllTextAsync(file, ct);
                        var remaining = MaxContextChars - contextSb.Length;
                        if (text.Length > remaining) text = text[..remaining] + "\n[truncated]";
                        var label = sourceType == "Slack"
                            ? $"{srcName} / Slack / #{Path.GetFileName(Path.GetDirectoryName(file))} / {Path.GetFileName(file)}"
                            : $"{srcName} / {sourceType} / {Path.GetFileName(file)}";
                        contextSb.AppendLine($"=== {label} ===");
                        contextSb.AppendLine(text);
                        contextSb.AppendLine();
                        if (q.Count == 0) buckets.RemoveAt(i);
                    }
                }
            }

            Log($"Gathered {contextSb.Length:N0} chars of context.");

            // Write context to a temp file in the folder so the agent can read it
            string? contextFilePath = null;
            string agentPrompt = job.Prompt;
            if (contextSb.Length > 0)
            {
                contextFilePath = Path.Combine(folderPath, ".work-context.md");
                await File.WriteAllTextAsync(contextFilePath, contextSb.ToString(), ct);
                agentPrompt = $"Read `.work-context.md` in this directory for relevant context from data sources, then do the following:\n\n{job.Prompt}";
                Log("Wrote context to .work-context.md in the folder.");
            }

            try
            {
                Log($"Running {agent.Type} agent in {folderPath}...");
                await RunAgentProcessAsync(agent, folderPath, agentPrompt, key, ct);
                status = "completed";
            }
            finally
            {
                if (contextFilePath != null && File.Exists(contextFilePath))
                    File.Delete(contextFilePath);
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            _logger.LogError(ex, "WorkJob {JobId} failed", jobId);
            _liveOutput.GetValueOrDefault(key)?.Add($"Error: {ex.Message}");
        }
        finally
        {
            _activeRunId.TryRemove(jobId, out _);

            var s = await _settings.LoadAsync();
            var jobName = s.WorkJobs.FirstOrDefault(j => j.Id == jobId)?.Name ?? jobId;
            var job2 = s.WorkJobs.FirstOrDefault(j => j.Id == jobId);
            var agent2 = job2 != null ? s.Agents.FirstOrDefault(a => a.Id == job2.AgentId) : null;

            string? summary = null;
            if (status == "completed")
            {
                var logLines = _liveOutput.GetValueOrDefault(key) ?? [];
                summary = await GenerateSummaryAsync(job2?.Prompt ?? "", logLines, agent2, CancellationToken.None);
                if (summary != null) _liveOutput.GetValueOrDefault(key)?.Add($"📝 {summary}");
            }

            var run = new WorkJobRun(jobId, runId, jobName, startedAt, DateTime.UtcNow, status, summary);
            _runs.Add(run);
            if (_runs.Count > 100) _runs = [.._runs.OrderByDescending(r => r.StartedAt).Take(100)];
            SaveRuns();

            _bus.Publish(new AssistantNotification(
                PluginId: jobId,
                Title: $"{jobName} — {(status == "completed" ? "✓ Completed" : "✕ Failed")}",
                Body: summary ?? (status == "completed" ? "Job finished successfully." : error ?? "Job failed."),
                Timestamp: DateTimeOffset.UtcNow,
                Category: "job-complete"));
        }
    }

    private async Task<string?> GenerateSummaryAsync(string jobPrompt, List<string> logLines,
        AgentDefinition? agent, CancellationToken ct)
    {
        try
        {
            var claudeBin = agent?.Type == "claude-code" && !string.IsNullOrEmpty(agent.CommandPath)
                ? agent.CommandPath : "claude";

            var activityLines = logLines
                .Where(l => !l.StartsWith("[err]") && !string.IsNullOrWhiteSpace(l))
                .TakeLast(60)
                .ToList();

            var context = string.Join("\n", activityLines);
            var summaryPrompt =
                $"Job instruction: {jobPrompt}\n\n" +
                $"Activity log:\n{context}\n\n" +
                $"Write a single sentence (max 20 words) summarizing what was accomplished. " +
                $"Reply with only the sentence.";

            var psi = new ProcessStartInfo
            {
                FileName = claudeBin,
                Arguments = "--print --dangerously-skip-permissions",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            ApplyShellEnvironment(psi);

            using var proc = Process.Start(psi);
            if (proc == null) return null;

            await proc.StandardInput.WriteAsync(summaryPrompt.AsMemory(), ct);
            proc.StandardInput.Close();

            var output = await proc.StandardOutput.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);

            var summary = StripAnsi(output).Trim();
            return string.IsNullOrWhiteSpace(summary) ? null : summary;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Summary generation failed for job");
            return null;
        }
    }

    private async Task RunAgentProcessAsync(AgentDefinition agent, string folderPath,
        string prompt, string key, CancellationToken ct)
    {
        void Log(string line) => _liveOutput.GetValueOrDefault(key)?.Add(line);

        ProcessStartInfo psi;
        bool useStdin;
        bool isClaudeCode = agent.Type == "claude-code";

        if (isClaudeCode)
        {
            var args = "--print --verbose --dangerously-skip-permissions --output-format stream-json";
            if (!string.IsNullOrEmpty(agent.Model)) args += $" --model {agent.Model}";
            psi = new ProcessStartInfo
            {
                FileName = string.IsNullOrEmpty(agent.CommandPath) ? "claude" : agent.CommandPath,
                Arguments = args,
                WorkingDirectory = folderPath,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            useStdin = true;
        }
        else // opencode
        {
            psi = new ProcessStartInfo
            {
                FileName = string.IsNullOrEmpty(agent.CommandPath) ? "opencode" : agent.CommandPath,
                WorkingDirectory = folderPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add(prompt);
            useStdin = false;
        }

        ApplyShellEnvironment(psi);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start {psi.FileName}");

        if (useStdin)
        {
            await proc.StandardInput.WriteAsync(prompt.AsMemory(), ct);
            proc.StandardInput.Close();
        }

        var readOut = Task.Run(async () =>
        {
            while (!proc.StandardOutput.EndOfStream)
            {
                var line = await proc.StandardOutput.ReadLineAsync(ct);
                if (line == null) continue;
                if (isClaudeCode)
                {
                    foreach (var entry in ParseClaudeStreamJson(line))
                        Log(entry);
                }
                else
                {
                    var stripped = StripAnsi(line);
                    if (!string.IsNullOrWhiteSpace(stripped)) Log(stripped);
                }
            }
        }, ct);

        var readErr = Task.Run(async () =>
        {
            while (!proc.StandardError.EndOfStream)
            {
                var line = await proc.StandardError.ReadLineAsync(ct);
                if (line != null)
                {
                    var stripped = StripAnsi(line);
                    if (!string.IsNullOrWhiteSpace(stripped)) Log($"[err] {stripped}");
                }
            }
        }, ct);

        await Task.WhenAll(readOut, readErr);
        await proc.WaitForExitAsync(ct);
        Log($"Agent finished (exit {proc.ExitCode}).");
    }

    private static IEnumerable<string> ParseClaudeStreamJson(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) yield break;
        JsonElement root;
        try { root = JsonDocument.Parse(line).RootElement; }
        catch { yield break; }

        var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;

        if (type == "assistant")
        {
            if (!root.TryGetProperty("message", out var msg)) yield break;
            if (!msg.TryGetProperty("content", out var content)) yield break;
            foreach (var block in content.EnumerateArray())
            {
                var btype = block.TryGetProperty("type", out var bt) ? bt.GetString() : null;
                if (btype == "text")
                {
                    var text = block.TryGetProperty("text", out var tx) ? tx.GetString() ?? "" : "";
                    text = text.Trim();
                    if (!string.IsNullOrEmpty(text))
                    {
                        // Emit up to first 3 lines of assistant text as a note
                        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                        var preview = string.Join(" ", lines.Take(3));
                        if (preview.Length > 200) preview = preview[..200] + "…";
                        yield return $"  {preview}";
                    }
                }
                else if (btype == "tool_use")
                {
                    var name = block.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    var input = block.TryGetProperty("input", out var inp) ? inp : default;
                    yield return FormatToolUse(name, input);
                }
            }
        }
        else if (type == "result")
        {
            var subtype = root.TryGetProperty("subtype", out var st) ? st.GetString() : null;
            if (subtype == "success")
            {
                var result = root.TryGetProperty("result", out var r) ? r.GetString() ?? "" : "";
                if (!string.IsNullOrWhiteSpace(result))
                {
                    var preview = result.Trim();
                    if (preview.Length > 300) preview = preview[..300] + "…";
                    yield return $"✅ {preview}";
                }
                else
                {
                    yield return "✅ Done.";
                }
            }
            else if (subtype == "error" || subtype == "error_during_execution")
            {
                var errMsg = root.TryGetProperty("error", out var e) ? e.GetString() ?? subtype : subtype ?? "error";
                yield return $"❌ {errMsg}";
            }
        }
    }

    private static string FormatToolUse(string name, JsonElement input)
    {
        string Str(string field) =>
            input.ValueKind == JsonValueKind.Object && input.TryGetProperty(field, out var v)
                ? v.GetString() ?? "" : "";

        return name switch
        {
            "Read" or "read_file"    => $"📖 Read {Str("file_path")}",
            "Write" or "write_file"  => $"✏️  Write {Str("file_path")}",
            "Edit" or "edit_file"    => $"✏️  Edit {Str("file_path")}",
            "MultiEdit"              => $"✏️  MultiEdit {Str("file_path")}",
            "Bash"                   => $"🔧 Bash: {Truncate(Str("command"), 80)}",
            "Glob"                   => $"🔍 Glob: {Str("pattern")}",
            "Grep"                   => $"🔍 Grep: {Str("pattern")} {Str("path")}".TrimEnd(),
            "WebSearch"              => $"🌐 Search: {Str("query")}",
            "WebFetch"               => $"🌐 Fetch: {Str("url")}",
            "TodoWrite" or "TodoRead"=> $"📋 Todos",
            "Agent"                  => $"🤖 Spawning sub-agent…",
            "LS" or "ls"             => $"📂 List {Str("path")}",
            _                        => $"⚙️  {name}",
        };
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    private static readonly System.Text.RegularExpressions.Regex AnsiRe =
        new(@"\x1B\[[0-9;]*[mABCDEFGHJKSTfisu]|\x1B\][^\x07]*\x07|\x1B[>=]|\r", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static string StripAnsi(string s) => AnsiRe.Replace(s, "");

    private static void ApplyShellEnvironment(ProcessStartInfo psi)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        try
        {
            var shell = Environment.GetEnvironmentVariable("SHELL") ?? "/bin/zsh";
            var pathProc = Process.Start(new ProcessStartInfo
            {
                FileName = shell, Arguments = "-l -c \"echo $PATH\"",
                UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true,
            });
            if (pathProc != null)
            {
                var path = pathProc.StandardOutput.ReadToEnd().Trim();
                pathProc.WaitForExit(3000);
                if (!string.IsNullOrEmpty(path)) psi.Environment["PATH"] = path;
            }
        }
        catch { }
    }

    private void LoadRuns()
    {
        try
        {
            if (File.Exists(_runsPath))
                _runs = JsonSerializer.Deserialize<List<WorkJobRun>>(File.ReadAllText(_runsPath), JsonOpts) ?? [];
        }
        catch { _runs = []; }
    }

    private void SaveRuns()
    {
        try { File.WriteAllText(_runsPath, JsonSerializer.Serialize(_runs, JsonOpts)); }
        catch { }
    }
}
