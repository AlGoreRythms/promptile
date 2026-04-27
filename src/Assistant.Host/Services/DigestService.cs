using System.Net;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using Assistant.Sdk;
using Microsoft.Extensions.Logging;

namespace Assistant.Host.Services;

public class DigestService : IHostedService
{
    private readonly IAiServiceResolver _resolver;
    private readonly IInformationStore _store;
    private readonly IDataSourceManager _dataSourceManager;
    private readonly SettingsService _settings;
    private readonly ILogger<DigestService> _logger;

    private DateTime _lastRun = DateTime.MinValue;
    private CancellationTokenSource? _cts;
    private Task? _tickTask;

    public const string DigestSource = "_Assistant";
    public const string DigestType   = "Digests";

    public DigestService(
        IAiServiceResolver resolver,
        IInformationStore store,
        IDataSourceManager dataSourceManager,
        SettingsService settings,
        ILogger<DigestService> logger)
    {
        _resolver = resolver;
        _store = store;
        _dataSourceManager = dataSourceManager;
        _settings = settings;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _tickTask = TickLoopAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _cts?.Cancel();
        return Task.CompletedTask;
    }

    private async Task TickLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await CheckAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogError(ex, "Digest tick failed"); }

            try { await Task.Delay(TimeSpan.FromMinutes(1), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task CheckAsync(CancellationToken ct)
    {
        var s = await _settings.LoadAsync();
        if (!s.Digest.Enabled) return;

        var now = DateTime.Now;
        if (now.Hour != s.Digest.Hour) return;
        if (_lastRun.Date == now.Date) return;

        _lastRun = now;
        _logger.LogInformation("Generating daily digest");
        await GenerateDigestAsync(s, ct);
    }

    private async Task GenerateDigestAsync(AssistantSettings s, CancellationToken ct)
    {
        var briefNames = _dataSourceManager.GetInstances()
            .Select(i => i.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n)
            .ToList();

        var parts = new List<string>();

        foreach (var briefName in briefNames)
        {
            var briefsPath = _store.GetNotesPath(BriefingService.BriefingSource, briefName);
            if (!Directory.Exists(briefsPath)) continue;

            var groupParts = new List<string>();

            // Include active situations
            var situations = Directory.GetFiles(briefsPath, "situation-*.md")
                .Where(f => !f.EndsWith(".archived.md"))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .Take(5)
                .ToList();
            if (situations.Any())
            {
                var sb = new StringBuilder($"### Situations — {briefName}\n\n");
                foreach (var f in situations)
                    sb.AppendLine(await File.ReadAllTextAsync(f, ct)).AppendLine();
                groupParts.Add(sb.ToString());
            }

            // Include pending actions
            var actionsPath = Path.Combine(briefsPath, "actions.json");
            if (File.Exists(actionsPath))
            {
                try
                {
                    var actions = JsonSerializer.Deserialize<List<ActionItem>>(
                        await File.ReadAllTextAsync(actionsPath, ct),
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
                    var pending = actions.Where(a => !a.Done).ToList();
                    if (pending.Any())
                    {
                        var sb = new StringBuilder($"### Actions — {briefName}\n\n");
                        foreach (var a in pending)
                            sb.AppendLine($"- [{a.Priority.ToUpper()}] {a.Text}" +
                                          (a.Deadline != null ? $" (due {a.Deadline})" : ""));
                        groupParts.Add(sb.ToString());
                    }
                }
                catch { }
            }

            if (groupParts.Any())
                parts.Add($"## {briefName}\n\n" + string.Join("\n\n", groupParts));
        }

        if (parts.Count == 0) return;

        var markdown = $"# Daily Digest — {DateTime.Now:MMMM d, yyyy}\n\n" +
                       $"*Generated at {DateTime.Now:h:mm tt}*\n\n" +
                       string.Join("\n\n---\n\n", parts);

        var filename = $"{DateTime.Now:yyyy-MM-dd}.md";
        await _store.WriteNoteAsync(DigestSource, DigestType, filename, markdown);
        _logger.LogInformation("Digest saved: {Filename}", filename);

        if (!string.IsNullOrWhiteSpace(s.Digest.SmtpHost) &&
            !string.IsNullOrWhiteSpace(s.Digest.ToEmail))
        {
            await SendEmailAsync(s.Digest, markdown, ct);
        }
    }

    private async Task SendEmailAsync(DigestSettings d, string markdown, CancellationToken ct)
    {
        try
        {
            var client = new SmtpClient(d.SmtpHost, d.SmtpPort)
            {
                EnableSsl = true,
                Credentials = !string.IsNullOrEmpty(d.SmtpUser)
                    ? new NetworkCredential(d.SmtpUser, d.SmtpPassword)
                    : null,
            };

            var from = !string.IsNullOrWhiteSpace(d.SmtpUser) ? d.SmtpUser : d.ToEmail!;
            var msg = new MailMessage(from, d.ToEmail!)
            {
                Subject = $"Daily Digest — {DateTime.Now:MMMM d, yyyy}",
                Body = markdown,
                IsBodyHtml = false,
            };

            await client.SendMailAsync(msg, ct);
            _logger.LogInformation("Digest email sent to {To}", d.ToEmail);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Digest email failed"); }
    }
}
