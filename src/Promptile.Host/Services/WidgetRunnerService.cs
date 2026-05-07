using System.Text;
using Promptile.Sdk;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Promptile.Host.Services;

public class WidgetRunnerService(
    SettingsService settings,
    IAiServiceResolver resolver,
    IInformationStore store,
    MemoryService memoryService,
    ILogger<WidgetRunnerService> logger)
{
    public async Task<(string Content, string AgentTier, string OutputFormat)> RunAsync(
        UserDashboardWidget widget, CancellationToken ct = default)
    {
        var s = await settings.LoadAsync();

        var now = DateTime.Now;
        var dateBlurb = $"\n\nToday is {now:dddd, MMMM d, yyyy}. Local time is {now:HH:mm}.";

        var profileBlurb = string.IsNullOrWhiteSpace(s.UserProfile)
            ? ""
            : $"\n\nAbout the user:\n{s.UserProfile}";

        var formatInstructions = widget.OutputFormat switch
        {
            "html" => "Respond with a single self-contained HTML fragment (no <html>/<body> tags). You may use inline <style> and inline SVG. Do not include markdown fences.",
            "text" => "Respond with plain text only — no markdown, no HTML.",
            _ => "Answer concisely and clearly in markdown.",
        };

        var ai = await resolver.GetServiceAsync(widget.AgentTier);

        // Build memory context from selected pages
        var memoryContext = BuildMemoryContext(widget);

        string content;
        string debugContext;

        if (widget.DataSources.Count > 0 && ai.SupportsAgentMode)
        {
            // Agent path: LLM uses search tools to fetch exactly what it needs
            var toolService = new WidgetToolService(store, widget.DataSources, widget.ContextDays);
            var tools = toolService.GetToolDefinitions();
            var inventory = toolService.BuildSourceInventory();
            var roster = toolService.BuildAuthorRoster();

            var systemPrompt = (memoryContext.Length > 0
                ? $"CURATED MEMORY CONTEXT (pre-distilled from data sources):\n\n{memoryContext}\n\n"
                : "") +
                $"You are a personal assistant.{dateBlurb}{profileBlurb}\n\n" +
                $"You MUST call the available search tools to retrieve data before answering. " +
                $"Do not answer from memory or general knowledge — always search first. " +
                $"Search multiple sources and data types when they are relevant — do not stop after one tool call.\n\n" +
                $"Available data sources (use the exact source_name= value shown, not the data type name):\n{inventory}\n\n" +
                (!string.IsNullOrEmpty(roster)
                    ? $"Known Slack authors: {roster}\n\nWhen a question is about a specific person, you MUST pass their exact name from the list above as the author= parameter. Never search without author= when looking for one person's messages.\n\n"
                    : "") +
                formatInstructions;

            logger.LogInformation("Running widget {Id} in agent mode with {ToolCount} tools", widget.Id, tools.Count);
            var toolLog = new StringBuilder();
            var response = await ai.RunAgentAsync(systemPrompt, widget.Prompt, tools,
                async call =>
                {
                    var result = await toolService.ExecuteAsync(call, ct);
                    toolLog.AppendLine($"### {call.Name}");
                    toolLog.AppendLine($"**Input:** `{call.InputJson}`");
                    toolLog.AppendLine();
                    toolLog.AppendLine(result);
                    toolLog.AppendLine();
                    return result;
                }, ct);
            content = StripCodeFence(response.Text);
            debugContext = $"<!--SECTION:System Prompt-->\n\n{systemPrompt}\n\n<!--SECTION:Prompt-->\n\n{widget.Prompt}\n\n<!--SECTION:Tool Calls-->\n\n{toolLog}\n<!--SECTION:Answer-->\n\n{content}";
        }
        else
        {
            // Fallback path: pre-read files and dump context
            var systemPrompt = (memoryContext.Length > 0
                ? $"CURATED MEMORY CONTEXT (pre-distilled from data sources):\n\n{memoryContext}\n\n"
                : "") +
                $"You are a personal assistant.{dateBlurb}{profileBlurb}\n\n{formatInstructions}";
            var contextSb = new StringBuilder();
            const int MaxContextChars = 80_000;
            var cutoff = DateTime.UtcNow.AddDays(-Math.Max(1, widget.ContextDays));

            var sourceFilters = new Dictionary<string, HashSet<string>?>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in widget.DataSources)
            {
                var colon = entry.IndexOf(':');
                if (colon < 0)
                    sourceFilters[entry] = null;
                else
                {
                    var name = entry[..colon];
                    var type = entry[(colon + 1)..];
                    if (!sourceFilters.TryGetValue(name, out var set) || set == null)
                        sourceFilters[name] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { type };
                    else
                        set.Add(type);
                }
            }

            foreach (var (sourceName, typeFilter) in sourceFilters)
            {
                var sourceDir = Path.Combine(store.RootPath, sourceName);
                if (!Directory.Exists(sourceDir)) continue;

                var buckets = Directory.GetDirectories(sourceDir)
                    .SelectMany(typeDir =>
                    {
                        var sourceType = Path.GetFileName(typeDir);
                        if (typeFilter != null && !typeFilter.Contains(sourceType))
                            return Enumerable.Empty<(Queue<string> Q, string Type, string SrcName)>();
                        var dataPath = Path.Combine(typeDir, "Data");
                        if (!Directory.Exists(dataPath))
                            return Enumerable.Empty<(Queue<string> Q, string Type, string SrcName)>();
                        var subdirs = Directory.GetDirectories(dataPath);
                        var groups = subdirs.Length > 0
                            ? subdirs.Select(sub => (
                                Files: Directory.GetFiles(sub, "*.md")
                                    .Where(f => File.GetLastWriteTimeUtc(f) >= cutoff)
                                    .OrderByDescending(File.GetLastWriteTimeUtc)
                                    .ToList(),
                                Type: sourceType))
                            : [(
                                Files: Directory.GetFiles(dataPath, "*.md")
                                    .Where(f => File.GetLastWriteTimeUtc(f) >= cutoff)
                                    .OrderByDescending(File.GetLastWriteTimeUtc)
                                    .ToList(),
                                Type: sourceType)];
                        return groups
                            .Where(g => g.Files.Count > 0)
                            .Select(g => (Q: new Queue<string>(g.Files), g.Type, SrcName: sourceName));
                    })
                    .ToList();

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

            var userPrompt = contextSb.Length > 0
                ? $"{widget.Prompt}\n\nContext from your data sources:\n\n{contextSb}"
                : widget.Prompt;

            var response = await ai.CompleteAsync(systemPrompt, userPrompt, ct);
            content = StripCodeFence(response.Text);
            debugContext = $"<!--SECTION:System Prompt-->\n\n{systemPrompt}\n\n<!--SECTION:User Prompt-->\n\n{userPrompt}\n\n<!--SECTION:Mode-->\n\nContext dump (provider does not support agent mode)";
        }

        var cachePath = store.GetNotesPath("_Assistant", "DashboardCache");
        Directory.CreateDirectory(cachePath);
        await File.WriteAllTextAsync(Path.Combine(cachePath, $"{widget.Id}.md"), content, ct);
        await File.WriteAllTextAsync(Path.Combine(cachePath, $"{widget.Id}.context.md"), debugContext, ct);

        return (content, widget.AgentTier, widget.OutputFormat);
    }

    private string BuildMemoryContext(UserDashboardWidget widget)
    {
        if (widget.MemoryPages.Count == 0) return "";
        var pages = memoryService.GetPages();
        var sb = new StringBuilder();
        foreach (var pageName in widget.MemoryPages)
        {
            var page = pages.FirstOrDefault(p => p.Name.Equals(pageName, StringComparison.OrdinalIgnoreCase));
            string? content;
            string header;
            if (page?.Mode != "rolling" && page != null)
            {
                var history = memoryService.GetPageHistory(pageName, widget.ContextDays);
                if (history.Count == 0) continue;
                content = string.Join("\n\n", history.Select(h =>
                    $"### {MemoryService.FormatLabel(h.Label)}\n{h.Content}"));
                header = $"## Memory: {pageName}";
            }
            else
            {
                content = memoryService.GetPageContent(pageName);
                var updated = memoryService.GetPageLastModified(pageName);
                header = updated.HasValue
                    ? $"## Memory: {pageName} (as of {updated.Value.LocalDateTime:MMM d})"
                    : $"## Memory: {pageName}";
            }
            if (!string.IsNullOrEmpty(content))
                sb.AppendLine($"{header}\n{content}\n");
        }
        return sb.ToString();
    }

    private static string StripCodeFence(string text)
    {
        var s = text.Trim();
        if (!s.StartsWith("```")) return s;
        var newline = s.IndexOf('\n');
        if (newline < 0) return s;
        s = s[(newline + 1)..].TrimStart();
        if (s.EndsWith("```")) s = s[..^3].TrimEnd();
        return s;
    }

    public bool IsStale(UserDashboardWidget widget, TimeSpan? maxAge = null)
    {
        if (widget.DataSources.Count == 0) return false;

        var cachePath = store.GetNotesPath("_Assistant", "DashboardCache");
        var cacheFile = Path.Combine(cachePath, $"{widget.Id}.md");
        if (!File.Exists(cacheFile)) return true; // never run

        var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(cacheFile);
        return age >= (maxAge ?? TimeSpan.FromHours(24));
    }
}

public class WidgetRefreshService(
    WidgetRunnerService runner,
    SettingsService settings,
    ILogger<WidgetRefreshService> logger) : BackgroundService
{
    public async Task RefreshAllAsync(CancellationToken ct = default)
    {
        var s = await settings.LoadAsync();
        foreach (var widget in s.DashboardWidgets)
        {
            if (ct.IsCancellationRequested) break;
            logger.LogInformation("Refreshing widget {Id} ({Title})", widget.Id, widget.Title);
            try { await runner.RunAsync(widget, ct); }
            catch (Exception ex) { logger.LogWarning(ex, "Failed to refresh widget {Id}", widget.Id); }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // On startup: refresh widgets older than 24h, with a short delay for data sources to connect
        await Task.Delay(TimeSpan.FromSeconds(30), ct);
        await RunStaleAsync(ct);

        // Hourly check for anything that's crossed the 24h mark
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromHours(1), ct);
            await RunStaleAsync(ct);
        }
    }

    private async Task RunStaleAsync(CancellationToken ct)
    {
        try
        {
            var s = await settings.LoadAsync();
            foreach (var widget in s.DashboardWidgets)
            {
                if (ct.IsCancellationRequested) break;
                if (!runner.IsStale(widget)) continue;
                logger.LogInformation("Auto-refreshing widget {Id} ({Title})", widget.Id, widget.Title);
                try { await runner.RunAsync(widget, ct); }
                catch (Exception ex) { logger.LogWarning(ex, "Failed to auto-refresh widget {Id}", widget.Id); }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Widget refresh cycle failed");
        }
    }
}
