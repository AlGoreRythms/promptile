using System.Diagnostics;
using System.Text;
using Promptile.Sdk;
using Folder;
using Microsoft.Extensions.Logging;

namespace Promptile.Host.Services;

public class CodeAnalysisService : IHostedService
{
    private readonly INotificationBus _bus;
    private readonly IDataSourceManager _manager;
    private readonly IInformationStore _store;
    private readonly ILogger<CodeAnalysisService> _logger;

    private readonly HashSet<string> _inProgress = [];
    private readonly SemaphoreSlim _progressLock = new(1, 1);

    public CodeAnalysisService(
        INotificationBus bus,
        IDataSourceManager manager,
        IInformationStore store,
        ILogger<CodeAnalysisService> logger)
    {
        _bus = bus;
        _manager = manager;
        _store = store;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _bus.Subscribe(OnNotification);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    private void OnNotification(AssistantNotification notification)
    {
        if (notification.Category != "new-assigned-task") return;
        if (notification.Payload is not NewTaskPayload payload) return;

        var folder = _manager.GetInstance(payload.SourceName, "folder") as FolderDataSourceInstance;
        if (folder == null) return;

        var key = payload.Key;
        _ = Task.Run(async () =>
        {
            await _progressLock.WaitAsync();
            if (!_inProgress.Add(key)) { _progressLock.Release(); return; }
            _progressLock.Release();

            try { await RunAnalysisAsync(payload, folder); }
            catch (Exception ex) { _logger.LogError(ex, "Code analysis failed for {Key}", key); }
            finally
            {
                await _progressLock.WaitAsync();
                _inProgress.Remove(key);
                _progressLock.Release();
            }
        });
    }

    private async Task RunAnalysisAsync(NewTaskPayload payload, FolderDataSourceInstance folder)
    {
        _logger.LogInformation("Running code analysis for {Key} in {Path}", payload.Key, folder.FolderPath);

        var prompt = BuildPrompt(payload);

        string output;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = folder.OpenCodeCommand,
                WorkingDirectory = folder.FolderPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add(prompt);

            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException($"Failed to start {folder.OpenCodeCommand}");

            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();

            output = await stdoutTask;
            var stderr = await stderrTask;

            if (string.IsNullOrWhiteSpace(output) && !string.IsNullOrWhiteSpace(stderr))
                output = stderr;

            if (proc.ExitCode != 0)
                _logger.LogWarning("opencode exited {Code} for {Key}", proc.ExitCode, payload.Key);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "opencode subprocess failed for {Key}", payload.Key);
            output = $"opencode failed to run: {ex.Message}";
        }

        if (string.IsNullOrWhiteSpace(output))
        {
            _logger.LogWarning("opencode produced no output for {Key}", payload.Key);
            return;
        }

        var slug = $"task-{payload.Key.ToLower().Replace("/", "-").Replace(" ", "-")}";
        var sb = new StringBuilder();
        sb.AppendLine($"# Task Analysis: [{payload.Key}] {payload.Summary}");
        sb.AppendLine();
        sb.AppendLine($"**Source**: {payload.SourceName}  **Folder**: {folder.FolderPath}");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine(output.Trim());

        await _store.WriteNoteAsync(
            BriefingService.BriefingSource,
            payload.SourceName,
            $"situation-{slug}.md",
            sb.ToString());

        _logger.LogInformation("Code analysis written for {Key}", payload.Key);

        _bus.Publish(new AssistantNotification(
            PluginId: "host",
            Title: $"Analysis ready: {payload.Key}",
            Body: payload.Summary,
            Timestamp: DateTimeOffset.UtcNow,
            Category: "briefing"));
    }

    private static string BuildPrompt(NewTaskPayload payload)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"I have a new task assigned to me: [{payload.Key}] {payload.Summary}");
        if (!string.IsNullOrWhiteSpace(payload.Description))
        {
            sb.AppendLine();
            sb.AppendLine(payload.Description.Trim());
        }
        sb.AppendLine();
        sb.AppendLine("Please analyze this codebase and suggest a clear, specific plan to address this task.");
        sb.AppendLine("Focus on which files to change and how. Be concrete and actionable.");
        return sb.ToString().Trim();
    }
}
