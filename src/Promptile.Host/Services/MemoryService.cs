using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Promptile.Sdk;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Promptile.Host.Services;

public class MemoryPageConfig
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Prompt { get; set; } = "";
    public string AgentTier { get; set; } = "light";
    public List<string> ScanSources { get; set; } = []; // e.g. ["Work:Slack", "Personal:Gmail"]
    public string Mode { get; set; } = "rolling"; // "rolling" | "daily" | "weekly" | "monthly"
    public int? RetentionPeriods { get; set; } // null = keep all, N = keep last N periods
}

public class MemoryService(
    SettingsService settings,
    IInformationStore store,
    IAiServiceResolver aiResolver,
    ILogger<MemoryService> logger) : BackgroundService
{
    private Dictionary<string, long> _cursors = [];
    private string _dataDir = "";
    private string _stateDir = "";
    private bool _initialized;

    private const int ChunkSizeChars = 4000;       // rolling: one file at a time
    private const int DatedChunkSizeChars = 16000;  // dated: whole period concatenated

    private static readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    private void EnsureInitialized()
    {
        if (_initialized) return;
        _dataDir = Path.Combine(store.RootPath, "_Memory", "Data");
        _stateDir = Path.Combine(store.RootPath, "_Memory", "State");
        LoadCursors();
        _initialized = true;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        EnsureInitialized();
        await Task.Delay(TimeSpan.FromSeconds(15), ct);

        while (!ct.IsCancellationRequested)
        {
            try { await ScanAllAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "[Memory] Scan failed"); }

            var s = await settings.LoadAsync();
            var interval = s.MemoryScanIntervalMinutes > 0 ? s.MemoryScanIntervalMinutes : 30;
            try { await Task.Delay(TimeSpan.FromMinutes(interval), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    public async Task ScanAllAsync(CancellationToken ct)
    {
        EnsureInitialized();
        var s = await settings.LoadAsync();
        foreach (var page in s.MemoryPages)
        {
            ct.ThrowIfCancellationRequested();
            try { await ScanPageAsync(page, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { logger.LogWarning(ex, "[Memory] Scan failed for page '{Page}'", page.Name); }
        }
    }

    public async Task ScanPageAsync(MemoryPageConfig page, CancellationToken ct, bool force = false)
    {
        EnsureInitialized();
        if (page.Mode == "rolling")
            await ScanRollingPageAsync(page, ct);
        else
            await ScanDatedPageAsync(page, ct, force);
    }

    // Returns the raw source file contents that would be (or were) fed to the AI for a given page/period.
    // For dated pages, label filters to files belonging to that period. For rolling, returns all source files.
    public async Task<string> GetPeriodSourceDataAsync(MemoryPageConfig page, string? label, int maxChars = 80_000)
    {
        EnsureInitialized();
        var combined = new StringBuilder();
        foreach (var (file, _) in GetSourceFiles(page.ScanSources).OrderBy(t => t.File))
        {
            if (label != null)
            {
                var fileLabel = GetFilePeriodLabel(file, page.Mode);
                if (fileLabel != label) continue;
            }
            try
            {
                var text = await File.ReadAllTextAsync(file);
                if (text.Length < 10) continue;
                if (combined.Length > 0) combined.Append("\n\n---\n\n");
                combined.Append($"[{Path.GetRelativePath(store.RootPath, file)}]\n\n");
                combined.Append(text);
                if (combined.Length >= maxChars) { combined.Append("\n\n…(truncated)"); break; }
            }
            catch { }
        }
        return combined.Length > 0 ? combined.ToString() : "(no source files found for this period)";
    }

    public string? GetPageContent(string name, string? label = null)
    {
        EnsureInitialized();
        if (label != null)
        {
            var path = Path.Combine(_dataDir, name, label + ".md");
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }

        var s = settings.LoadSync();
        var page = s.MemoryPages.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (page?.Mode == "rolling" || page == null)
        {
            var rollingPath = Path.Combine(_dataDir, name + ".md");
            return File.Exists(rollingPath) ? File.ReadAllText(rollingPath) : null;
        }
        return ReadLatestDated(name);
    }

    public string? GetPagePreview(string name, int maxChars = 400)
    {
        var content = GetPageContent(name);
        if (content == null) return null;
        return content.Length > maxChars ? content[..maxChars] + "…" : content;
    }

    public List<(string Label, string Content)> GetPageHistory(string name, int? days = null)
    {
        EnsureInitialized();
        var datedDir = Path.Combine(_dataDir, name);
        if (!Directory.Exists(datedDir)) return [];

        DateTime? cutoff = days.HasValue ? DateTime.UtcNow.AddDays(-Math.Max(1, days.Value)).Date : null;
        var result = new List<(string Label, string Content)>();

        foreach (var file in Directory.GetFiles(datedDir, "*.md").OrderByDescending(f => f))
        {
            var label = Path.GetFileNameWithoutExtension(file);
            if (cutoff.HasValue)
            {
                var date = ParseLabelToDate(label);
                if (date.HasValue && date.Value < cutoff.Value) break;
            }
            result.Add((label, File.ReadAllText(file)));
        }

        return result;
    }

    public List<string> GetPageSnapshots(string name, int take = 5)
    {
        EnsureInitialized();
        var datedDir = Path.Combine(_dataDir, name);
        if (!Directory.Exists(datedDir)) return [];
        return Directory.GetFiles(datedDir, "*.md")
            .OrderByDescending(f => f)
            .Take(take)
            .Select(f => Path.GetFileNameWithoutExtension(f))
            .ToList();
    }

    public void WritePageContent(string name, string content, string? label = null)
    {
        EnsureInitialized();
        string path;
        if (label != null)
        {
            Directory.CreateDirectory(Path.Combine(_dataDir, name));
            path = Path.Combine(_dataDir, name, label + ".md");
        }
        else
        {
            Directory.CreateDirectory(_dataDir);
            path = Path.Combine(_dataDir, name + ".md");
        }
        WriteFile(path, content);
    }

    public void DeletePageSnapshot(string name, string label)
    {
        EnsureInitialized();
        var path = Path.Combine(_dataDir, name, label + ".md");
        if (File.Exists(path)) File.Delete(path);
    }

    public void DeletePageContent(string name)
    {
        EnsureInitialized();
        var rolling = Path.Combine(_dataDir, name + ".md");
        if (File.Exists(rolling)) File.Delete(rolling);

        var datedDir = Path.Combine(_dataDir, name);
        if (Directory.Exists(datedDir)) Directory.Delete(datedDir, recursive: true);

        var toRemove = _cursors.Keys.Where(k => k.EndsWith($"|{name}")).ToList();
        foreach (var k in toRemove) _cursors.Remove(k);
        SaveCursors();
    }

    public void RenamePage(string oldName, string newName)
    {
        EnsureInitialized();
        var oldRolling = Path.Combine(_dataDir, oldName + ".md");
        var newRolling = Path.Combine(_dataDir, newName + ".md");
        if (File.Exists(oldRolling)) File.Move(oldRolling, newRolling, overwrite: true);

        var oldDir = Path.Combine(_dataDir, oldName);
        var newDir = Path.Combine(_dataDir, newName);
        if (Directory.Exists(oldDir)) Directory.Move(oldDir, newDir);

        var toRename = _cursors.Keys.Where(k => k.EndsWith($"|{oldName}")).ToList();
        foreach (var k in toRename)
        {
            var val = _cursors[k];
            _cursors.Remove(k);
            _cursors[k[..^(oldName.Length + 1)] + "|" + newName] = val;
        }
        SaveCursors();
    }

    public void ResetPageCursors(string name)
    {
        EnsureInitialized();
        var toRemove = _cursors.Keys.Where(k => k.EndsWith($"|{name}")).ToList();
        foreach (var k in toRemove) _cursors.Remove(k);
        SaveCursors();
    }

    public IReadOnlyList<MemoryPageConfig> GetPages()
    {
        var s = settings.LoadSync();
        return s.MemoryPages.AsReadOnly();
    }

    // -------------------------------------------------------------------------
    // Rolling scan
    // -------------------------------------------------------------------------

    private async Task ScanRollingPageAsync(MemoryPageConfig page, CancellationToken ct)
    {
        var rollingPath = Path.Combine(_dataDir, page.Name + ".md");
        var currentContent = File.Exists(rollingPath) ? await File.ReadAllTextAsync(rollingPath, ct) : "";
        var filesProcessed = 0;

        foreach (var (file, mtime) in GetSourceFiles(page.ScanSources))
        {
            ct.ThrowIfCancellationRequested();
            var cursorKey = $"{file}|{page.Name}";
            if (_cursors.TryGetValue(cursorKey, out var prev) && prev == mtime) continue;

            try
            {
                var text = await File.ReadAllTextAsync(file, ct);
                if (text.Length < 100) { _cursors[cursorKey] = mtime; continue; }

                foreach (var chunk in Chunk(text, ChunkSizeChars))
                {
                    ct.ThrowIfCancellationRequested();
                    var ai = await aiResolver.GetServiceAsync(page.AgentTier);
                    var resp = await ai.CompleteAsync(page.Prompt, BuildPrompt(currentContent, chunk, file), ct);
                    currentContent = resp.Text.Trim();
                }

                _cursors[cursorKey] = mtime;
                SaveCursors();
                filesProcessed++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "[Memory] Failed to process '{File}' for page '{Page}'", file, page.Name);
            }
        }

        if (filesProcessed > 0)
        {
            WriteFile(rollingPath, currentContent);
            logger.LogInformation("[Memory] Updated rolling page '{Page}' from {Count} file(s)", page.Name, filesProcessed);
        }
    }

    // -------------------------------------------------------------------------
    // Dated scan
    // -------------------------------------------------------------------------

    private async Task ScanDatedPageAsync(MemoryPageConfig page, CancellationToken ct, bool force = false)
    {
        // Group all source files by their period label so we can process every
        // period that has data, not just the current one.
        var byPeriod = new Dictionary<string, List<string>>();
        foreach (var (file, _) in GetSourceFiles(page.ScanSources))
        {
            var label = GetFilePeriodLabel(file, page.Mode);
            if (label == null) continue;
            if (!byPeriod.TryGetValue(label, out var list))
                byPeriod[label] = list = [];
            list.Add(file);
        }

        if (byPeriod.Count == 0) return;

        var datedDir = Path.Combine(_dataDir, page.Name);

        // Current period may receive new source data on every sync — use mtime comparison.
        // Past periods are complete once written; only re-process if the snapshot is missing
        // (or if force=true, e.g. from the Refresh button).
        var currentLabel = page.Mode switch
        {
            "daily"   => DateTime.UtcNow.ToString("yyyy-MM-dd"),
            "weekly"  => GetWeekLabel(DateTime.UtcNow),
            "monthly" => DateTime.UtcNow.ToString("yyyy-MM"),
            _         => DateTime.UtcNow.ToString("yyyy-MM-dd"),
        };

        // Process from oldest to newest so retention trims the right end.
        foreach (var (label, files) in byPeriod.OrderBy(kv => kv.Key))
        {
            ct.ThrowIfCancellationRequested();
            var targetPath = Path.Combine(datedDir, label + ".md");
            DateTime? targetMtime = File.Exists(targetPath) ? File.GetLastWriteTimeUtc(targetPath) : null;

            bool needsUpdate;
            if (force)
                needsUpdate = true;
            else if (label == currentLabel)
                needsUpdate = targetMtime == null || files.Any(f => File.GetLastWriteTimeUtc(f) > targetMtime.Value);
            else
                needsUpdate = targetMtime == null; // past periods: only write once

            if (!needsUpdate) continue;

            // Concatenate all source files for this period into one text so the AI
            // summarises the whole period in one pass rather than file-by-file.
            var combined = new StringBuilder();
            foreach (var file in files.OrderBy(f => f))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var text = await File.ReadAllTextAsync(file, ct);
                    if (text.Length < 100) continue;
                    if (combined.Length > 0) combined.Append("\n\n---\n\n");
                    combined.Append(text);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "[Memory] Failed to read '{File}' for page '{Page}'", file, page.Name);
                }
            }

            if (combined.Length == 0) continue;

            var currentContent = File.Exists(targetPath) ? await File.ReadAllTextAsync(targetPath, ct) : "";

            try
            {
                foreach (var chunk in Chunk(combined.ToString(), DatedChunkSizeChars))
                {
                    ct.ThrowIfCancellationRequested();
                    var ai = await aiResolver.GetServiceAsync(page.AgentTier);
                    var resp = await ai.CompleteAsync(page.Prompt, BuildPrompt(currentContent, chunk, label), ct);
                    currentContent = resp.Text.Trim();
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "[Memory] Failed to summarise period '{Label}' for page '{Page}'", label, page.Name);
                continue;
            }

            Directory.CreateDirectory(datedDir);
            WriteFile(targetPath, currentContent);
            logger.LogInformation("[Memory] Updated dated page '{Page}' label={Label}", page.Name, label);
        }

        ApplyRetention(page);
    }

    // Map a source file to the period label it belongs to (returns null if filename has no parseable date).
    private static string? GetFilePeriodLabel(string filePath, string mode)
    {
        var name = Path.GetFileNameWithoutExtension(filePath);
        if (!DateTime.TryParseExact(name, "yyyy-MM-dd", null, DateTimeStyles.None, out var date))
            return null;
        return mode switch
        {
            "daily" => date.ToString("yyyy-MM-dd"),
            "weekly" => GetWeekLabel(date),
            "monthly" => date.ToString("yyyy-MM"),
            _ => date.ToString("yyyy-MM-dd"),
        };
    }

    private static string GetWeekLabel(DateTime date)
    {
        var week = ISOWeek.GetWeekOfYear(date);
        var year = ISOWeek.GetYear(date);
        return $"{year}-W{week:D2}";
    }

    // -------------------------------------------------------------------------
    // Source enumeration
    // -------------------------------------------------------------------------

    private IEnumerable<(string File, long Mtime)> GetSourceFiles(List<string> scanSources)
    {
        var parsed = ParseScanSources(scanSources);

        foreach (var (sourceName, types) in store.ListSources())
        {
            if (sourceName.StartsWith("_", StringComparison.Ordinal)) continue;

            foreach (var sourceType in types)
            {
                if (!IsIncluded(sourceName, sourceType, parsed, scanSources)) continue;

                var dataPath = Path.Combine(store.RootPath, sourceName, sourceType, "Data");
                if (!Directory.Exists(dataPath)) continue;

                foreach (var file in Directory.GetFiles(dataPath, "*.md", SearchOption.AllDirectories).OrderBy(f => f))
                    yield return (file, ((DateTimeOffset)File.GetLastWriteTimeUtc(file)).ToUnixTimeSeconds());
            }
        }
    }

    private static List<(string Name, string? Type)> ParseScanSources(List<string> sources) =>
        sources.Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => { var c = s.IndexOf(':'); return c < 0 ? (s.Trim(), (string?)null) : (s[..c].Trim(), (string?)s[(c + 1)..].Trim()); })
            .ToList();

    private static bool IsIncluded(string sourceName, string sourceType,
        List<(string Name, string? Type)> parsed, List<string> scanSources)
    {
        if (scanSources.Count == 0) return true;
        return parsed.Any(p =>
            p.Name.Equals(sourceName, StringComparison.OrdinalIgnoreCase) &&
            (p.Type == null || p.Type.Equals(sourceType, StringComparison.OrdinalIgnoreCase)));
    }

    // -------------------------------------------------------------------------
    // Period helpers
    // -------------------------------------------------------------------------

    // Converts a storage label to a human-readable date string.
    // yyyy-Www  → "Apr 21–27"  (or "Apr 21–27 2025" if not current year)
    // yyyy-MM   → "April"      (or "April 2025")
    // yyyy-MM-dd → "Apr 21"    (or "Apr 21, 2025")
    public static string FormatLabel(string label)
    {
        var thisYear = DateTime.UtcNow.Year;

        if (label.Length == 8 && label[5] == 'W' &&
            int.TryParse(label[..4], out var wy) &&
            int.TryParse(label[6..], out var wn))
        {
            var mon = ISOWeek.ToDateTime(wy, wn, DayOfWeek.Monday);
            var sun = mon.AddDays(6);
            var range = mon.Month == sun.Month
                ? $"{mon:MMM d}–{sun.Day}"
                : $"{mon:MMM d}–{sun:MMM d}";
            return wy == thisYear ? range : $"{range} {wy}";
        }

        if (label.Length == 7 &&
            DateTime.TryParseExact(label + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var md))
            return md.Year == thisYear ? md.ToString("MMMM") : md.ToString("MMMM yyyy");

        if (label.Length == 10 &&
            DateTime.TryParseExact(label, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dd))
            return dd.Year == thisYear ? dd.ToString("MMM d") : $"{dd:MMM d}, {dd.Year}";

        return label;
    }

    public DateTimeOffset? GetPageLastModified(string name)
    {
        EnsureInitialized();
        var rolling = Path.Combine(_dataDir, name + ".md");
        if (File.Exists(rolling)) return File.GetLastWriteTimeUtc(rolling);
        var datedDir = Path.Combine(_dataDir, name);
        if (!Directory.Exists(datedDir)) return null;
        var latest = Directory.GetFiles(datedDir, "*.md").OrderByDescending(f => f).FirstOrDefault();
        return latest != null ? (DateTimeOffset)File.GetLastWriteTimeUtc(latest) : null;
    }

    private static DateTime? ParseLabelToDate(string label)
    {
        if (DateTime.TryParseExact(label, "yyyy-MM-dd", null, DateTimeStyles.None, out var daily)) return daily;
        if (DateTime.TryParseExact(label, "yyyy-MM", null, DateTimeStyles.None, out var monthly)) return monthly;
        var m = Regex.Match(label, @"^(\d{4})-W(\d{2})$");
        if (m.Success && int.TryParse(m.Groups[1].Value, out var year) && int.TryParse(m.Groups[2].Value, out var week))
        {
            try { return ISOWeek.ToDateTime(year, week, DayOfWeek.Monday); } catch { }
        }
        return null;
    }

    private string? ReadLatestDated(string name)
    {
        var datedDir = Path.Combine(_dataDir, name);
        if (!Directory.Exists(datedDir)) return null;
        var latest = Directory.GetFiles(datedDir, "*.md").OrderByDescending(f => f).FirstOrDefault();
        return latest != null ? File.ReadAllText(latest) : null;
    }

    private void ApplyRetention(MemoryPageConfig page)
    {
        if (page.RetentionPeriods == null) return;
        var datedDir = Path.Combine(_dataDir, page.Name);
        if (!Directory.Exists(datedDir)) return;

        foreach (var file in Directory.GetFiles(datedDir, "*.md").OrderByDescending(f => f).Skip(page.RetentionPeriods.Value))
        {
            try { File.Delete(file); } catch { }
        }
    }

    // -------------------------------------------------------------------------
    // Prompt building + chunking
    // -------------------------------------------------------------------------

    private static string BuildPrompt(string currentContent, string chunk, string filePath)
    {
        var label = Path.GetFileName(filePath);
        if (string.IsNullOrWhiteSpace(currentContent))
            return $"New data from {label}:\n\n{chunk}\n\nWrite the initial memory page content based on this data.";
        return $"Current memory page content:\n\n{currentContent}\n\n---\n\nNew data from {label}:\n\n{chunk}\n\nUpdate the memory page to incorporate any new relevant information. Preserve existing content unless it is contradicted or superseded.";
    }

    private static List<string> Chunk(string content, int maxChars)
    {
        var chunks = new List<string>();
        var sections = content.Split("\n---\n", StringSplitOptions.RemoveEmptyEntries);
        var current = new StringBuilder();

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

        if (current.Length > 0) chunks.Add(current.ToString());
        return chunks.Count > 0 ? chunks : [content];
    }

    // -------------------------------------------------------------------------
    // Cursor persistence
    // -------------------------------------------------------------------------

    private void LoadCursors()
    {
        try
        {
            var path = Path.Combine(_stateDir, "cursors.json");
            if (File.Exists(path))
                _cursors = JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(path)) ?? [];
        }
        catch { _cursors = []; }
    }

    private void SaveCursors()
    {
        Directory.CreateDirectory(_stateDir);
        var path = Path.Combine(_stateDir, "cursors.json");
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_cursors, _json));
        File.Move(tmp, path, overwrite: true);
    }

    private static void WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }
}
