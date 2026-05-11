using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Serilog;
using Promptile.Host.Services;
using Promptile.Sdk;
using Gmail;

namespace Promptile.Host.Services.Tray;

public static class TrayHost
{
    /// <summary>
    /// Builds and starts the web server on a background thread, then runs
    /// the native tray icon on the main thread (required by macOS AppKit).
    /// Blocks until the user quits from the tray.
    /// </summary>
    public static void RunWithTray(string[] args, string dataDir, bool noBrowser,
        IPlugin[] plugins, IDataSourceProvider[] dataSourceProviders,
        PluginRegistry registry, SettingsService settingsService)
    {
        WebApplication? app = null;

        ITrayHost tray = RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
            ? new MacTrayHost()
            : new WindowsTrayHost();

        var serverReady = new ManualResetEventSlim(false);
        var serverThread = new Thread(() =>
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = args,
                ContentRootPath = AppContext.BaseDirectory,
            });
            builder.Environment.EnvironmentName = "Development";
            builder.Services.AddRazorPages();
            builder.Services.AddSingleton<ITrayHost>(tray);
            builder.Services.AddSingleton(registry);
            builder.Services.AddSingleton<INotificationService, NotificationService>();
            builder.Services.AddSingleton<INotificationBus, NotificationHub>();
            builder.Services.AddSingleton(new DataSourcesService(dataDir));
            foreach (var provider in dataSourceProviders)
                builder.Services.AddSingleton<IDataSourceProvider>(provider);
            builder.Services.AddSingleton<IDataSourceManager, DataSourceManager>();
            builder.Services.AddSingleton<DataSourceManager>(sp =>
                (DataSourceManager)sp.GetRequiredService<IDataSourceManager>());

            // Host-level services
            Program.RegisterHostServices(builder.Services, settingsService);
            builder.Services.AddSingleton(_ => new ConversationStore(dataDir));
            builder.Services.AddSingleton<ChatService>();
            builder.Services.AddSingleton<BriefingService>();
            builder.Services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<BriefingService>());
            builder.Services.AddSingleton<CodeAnalysisService>();
            builder.Services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<CodeAnalysisService>());

            builder.Services.AddSingleton<EmbeddingService>();
            builder.Services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<EmbeddingService>());
            builder.Services.AddSingleton<SyncStatusService>();
            builder.Services.AddSingleton<ISyncReporter>(sp => sp.GetRequiredService<SyncStatusService>());
            builder.Services.AddSingleton<MemoryService>();
            builder.Services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<MemoryService>());
            builder.Services.AddSingleton<WidgetRunnerService>();
            builder.Services.AddSingleton<WidgetRefreshService>();
            builder.Services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<WidgetRefreshService>());
            builder.Services.AddSingleton(sp => new WorkJobService(
                settingsService,
                new DataSourcesService(dataDir),
                sp.GetRequiredService<IInformationStore>(),
                sp.GetRequiredService<INotificationBus>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<WorkJobService>>(),
                dataDir));
            builder.Services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<WorkJobService>());

            // Let each plugin register its services
            foreach (var plugin in plugins)
            {
                var ctx = CreatePluginContext(plugin, dataDir);
                plugin.ConfigureServices(builder.Services, ctx);

                // Register background services
                foreach (var bgSvcType in plugin.GetBackgroundServices())
                {
                    builder.Services.AddSingleton(typeof(IHostedService), bgSvcType);
                }
            }

            builder.Host.UseSerilog((context, config) =>
            {
                config.WriteTo.File(Path.Combine(dataDir, "logs", "promptile.log"),
                    rollingInterval: RollingInterval.Day);
                config.WriteTo.Console();
            });

            app = builder.Build();

            // Wire notification sources and start data source instances
            var notificationBus = app.Services.GetRequiredService<INotificationBus>();
            NotificationHub.WirePlugins(plugins, notificationBus);

            var dataSourceManager = app.Services.GetRequiredService<DataSourceManager>();
            dataSourceManager.StartAllAsync().GetAwaiter().GetResult();

            foreach (var plugin in plugins)
            {
                var ctx = CreatePluginContext(plugin, dataDir);
                plugin.ConfigureApp(app, ctx);
            }

            app.UseStaticFiles();
            app.MapRazorPages();

            // Host-level API endpoints
            app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));
            app.MapGet("/api/sync/status", (SyncStatusService sync) => Results.Ok(sync.GetAll()));

            // Live widget refresh endpoint (used by HTMX polling)
            app.MapGet("/dashboard/widget/{id}", (string id, PluginRegistry reg, HttpContext ctx) =>
            {
                var widget = reg.Plugins
                    .SelectMany(p => p.GetDashboardWidgets())
                    .FirstOrDefault(w => w.Id == id);
                if (widget == null) return Results.NotFound();

                // Return a minimal HTML wrapper that HTMX can swap in
                var trigger = widget.PollSeconds.HasValue
                    ? $" hx-get=\"/dashboard/widget/{id}\" hx-trigger=\"every {widget.PollSeconds}s\" hx-swap=\"outerHTML\""
                    : "";
                var html = $"<div class=\"dashboard-widget\" id=\"widget-{id}\"{trigger}>" +
                           $"<!-- widget {id}: rendered server-side via Razor partial --></div>";
                return Results.Content(html, "text/html");
            });

            app.MapPost("/api/notifications/test", (INotificationBus bus) =>
            {
                bus.Publish(new AssistantNotification(
                    PluginId: "host",
                    Title: "Assistant",
                    Body: "Notifications are working.",
                    Timestamp: DateTimeOffset.UtcNow
                ));
                return Results.Ok();
            });

            app.MapPost("/api/agents", async (HttpContext ctx, SettingsService settings) =>
            {
                var body = await ctx.Request.ReadFromJsonAsync<AgentDefinition>();
                if (body == null || string.IsNullOrWhiteSpace(body.Name)) return Results.BadRequest();
                var s = await settings.LoadAsync();
                var existing = s.Agents.FirstOrDefault(a => a.Id == body.Id);
                if (existing != null)
                {
                    existing.Name = body.Name; existing.Type = body.Type;
                    existing.Provider = body.Provider; existing.Model = body.Model;
                    existing.ApiKey = body.ApiKey; existing.BaseUrl = body.BaseUrl;
                    existing.CommandPath = body.CommandPath;
                }
                else
                {
                    body.Id = Guid.NewGuid().ToString("N")[..8];
                    s.Agents.Add(body);
                }
                await settings.SaveAsync(s);
                settings.InvalidateCache();
                return Results.Ok(new { body.Id });
            });

            app.MapDelete("/api/agents/{id}", async (string id, SettingsService settings) =>
            {
                var s = await settings.LoadAsync();
                s.Agents.RemoveAll(a => a.Id == id);
                await settings.SaveAsync(s);
                settings.InvalidateCache();
                return Results.Ok();
            });

            // Work job API
            app.MapPost("/api/work/jobs", async (HttpContext ctx, SettingsService settings, WorkJobService workJobs) =>
            {
                var body = await ctx.Request.ReadFromJsonAsync<WorkJobSaveRequest>();
                if (body == null || string.IsNullOrWhiteSpace(body.Name)) return Results.BadRequest();
                var s = await settings.LoadAsync();
                var existing = s.WorkJobs.FirstOrDefault(j => j.Id == body.Id);
                bool promptChanged;
                if (existing != null)
                {
                    promptChanged = existing.Prompt != (body.Prompt ?? "");
                    existing.Name = body.Name;
                    existing.DataSources = body.DataSources ?? [];
                    existing.ContextDays = body.ContextDays > 0 ? body.ContextDays : 7;
                    existing.Prompt = body.Prompt ?? "";
                    existing.Description = body.Description;
                    existing.ScheduleHour = body.ScheduleHour;
                    existing.ScheduleDay = body.ScheduleDay;
                    existing.FolderSourceId = body.FolderSourceId ?? "";
                    existing.AgentId = body.AgentId ?? "";
                }
                else
                {
                    promptChanged = true;
                    var newJob = new WorkJob
                    {
                        Id = Guid.NewGuid().ToString("N")[..8],
                        Name = body.Name,
                        DataSources = body.DataSources ?? [],
                        ContextDays = body.ContextDays > 0 ? body.ContextDays : 7,
                        Prompt = body.Prompt ?? "",
                        Description = body.Description,
                        ScheduleHour = body.ScheduleHour,
                        ScheduleDay = body.ScheduleDay,
                        FolderSourceId = body.FolderSourceId ?? "",
                        AgentId = body.AgentId ?? "",
                    };
                    s.WorkJobs.Add(newJob);
                    body = body with { Id = newJob.Id };
                }
                await settings.SaveAsync(s);
                settings.InvalidateCache();
                if (string.IsNullOrWhiteSpace(body.Description) && promptChanged && !string.IsNullOrWhiteSpace(body.Prompt))
                    _ = workJobs.GenerateAndSaveDescriptionAsync(body.Id!);
                return Results.Ok(new { id = body.Id });
            });

            app.MapDelete("/api/work/jobs/{id}", async (string id, SettingsService settings) =>
            {
                var s = await settings.LoadAsync();
                s.WorkJobs.RemoveAll(j => j.Id == id);
                await settings.SaveAsync(s);
                settings.InvalidateCache();
                return Results.Ok();
            });

            app.MapPost("/api/work/jobs/{id}/run", (string id, WorkJobService workJobs) =>
            {
                var runId = workJobs.RunNow(id);
                return Results.Ok(new { runId });
            });

            app.MapGet("/api/work/jobs/{id}/output/{runId}", (string id, string runId, WorkJobService workJobs, int? offset) =>
            {
                var all = workJobs.GetLiveOutput(id, runId);
                var skip = offset ?? 0;
                var lines = all.Skip(skip).ToList();
                var running = workJobs.GetRunningIds().Contains(id);
                return Results.Ok(new { lines, running });
            });

            app.MapGet("/api/work/jobs/running", (WorkJobService workJobs, SettingsService settings) =>
            {
                var s = settings.LoadSync();
                var ids = workJobs.GetRunningIds();
                var result = ids.Select(jobId => new { jobId, runId = (string?)null }).ToList();
                return Results.Ok(result);
            });

            app.MapGet("/api/work/jobs/runs", (WorkJobService workJobs) =>
                Results.Ok(workJobs.GetRecentRuns()));

            app.MapDelete("/api/work/jobs/runs/{runId}", (string runId, WorkJobService workJobs) =>
                workJobs.DeleteRun(runId) ? Results.Ok() : Results.NotFound());

            app.MapPost("/api/work/jobs/grid", async (HttpContext ctx, SettingsService settings) =>
            {
                var updates = await ctx.Request.ReadFromJsonAsync<List<GridPositionUpdate>>();
                if (updates == null) return Results.BadRequest();
                var s = await settings.LoadAsync();
                foreach (var u in updates)
                {
                    var j = s.WorkJobs.FirstOrDefault(x => x.Id == u.Id);
                    if (j != null) { j.GridX = u.X; j.GridY = u.Y; j.GridW = u.W; j.GridH = u.H; }
                }
                await settings.SaveAsync(s);
                settings.InvalidateCache();
                return Results.Ok();
            });

            app.MapPost("/api/store/open", (IInformationStore store) =>
            {
                try
                {
                    Directory.CreateDirectory(store.RootPath);
                    if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                        Process.Start("open", store.RootPath);
                    else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                        Process.Start(new ProcessStartInfo("explorer", store.RootPath) { UseShellExecute = true });
                }
                catch { }
                return Results.Ok();
            });

            // Dashboard widget API
            app.MapPost("/api/dashboard/widgets", async (HttpContext ctx, SettingsService settings) =>
            {
                var body = await ctx.Request.ReadFromJsonAsync<DashboardWidgetSaveRequest>();
                if (body == null || string.IsNullOrWhiteSpace(body.Title) || string.IsNullOrWhiteSpace(body.Prompt))
                    return Results.BadRequest("Title and prompt required");

                var s = await settings.LoadAsync();
                var existing = s.DashboardWidgets.FirstOrDefault(w => w.Id == body.Id);
                if (existing != null)
                {
                    existing.Title = body.Title;
                    existing.Prompt = body.Prompt;
                    existing.AgentTier = body.AgentTier ?? "medium";
                    existing.OutputFormat = body.OutputFormat ?? "markdown";
                    existing.DataSources = body.DataSources ?? [];
                    existing.MemoryPages = body.MemoryPages ?? [];
                    existing.ContextDays = body.ContextDays > 0 ? body.ContextDays : 7;
                    existing.Color = string.IsNullOrEmpty(body.Color) ? null : body.Color;
                    existing.HideHeader = body.HideHeader ?? false;
                    existing.HideFooter = body.HideFooter ?? false;
                    if (body.PageId != null) existing.PageId = body.PageId;
                }
                else
                {
                    s.DashboardWidgets.Add(new UserDashboardWidget
                    {
                        Id = Guid.NewGuid().ToString("N")[..8],
                        PageId = body.PageId ?? "",
                        Title = body.Title,
                        Prompt = body.Prompt,
                        AgentTier = body.AgentTier ?? "medium",
                        OutputFormat = body.OutputFormat ?? "markdown",
                        DataSources = body.DataSources ?? [],
                        MemoryPages = body.MemoryPages ?? [],
                        ContextDays = body.ContextDays > 0 ? body.ContextDays : 7,
                        Color = string.IsNullOrEmpty(body.Color) ? null : body.Color,
                        HideHeader = body.HideHeader ?? false,
                        HideFooter = body.HideFooter ?? false,
                        Order = s.DashboardWidgets.Count,
                    });
                }
                await settings.SaveAsync(s);
                settings.InvalidateCache();
                return Results.Ok();
            });

            app.MapDelete("/api/dashboard/widgets/{id}", async (string id, SettingsService settings) =>
            {
                var s = await settings.LoadAsync();
                s.DashboardWidgets.RemoveAll(w => w.Id == id);
                await settings.SaveAsync(s);
                settings.InvalidateCache();
                return Results.Ok();
            });

            app.MapPost("/api/dashboard/widgets/grid", async (HttpContext ctx, SettingsService settings) =>
            {
                var updates = await ctx.Request.ReadFromJsonAsync<List<GridPositionUpdate>>();
                if (updates == null) return Results.BadRequest();
                var s = await settings.LoadAsync();
                foreach (var u in updates)
                {
                    var w = s.DashboardWidgets.FirstOrDefault(x => x.Id == u.Id);
                    if (w != null) { w.GridX = u.X; w.GridY = u.Y; w.GridW = u.W; w.GridH = u.H; }
                }
                await settings.SaveAsync(s);
                settings.InvalidateCache();
                return Results.Ok();
            });

            app.MapPost("/api/dashboard/widgets/reorder", async (HttpContext ctx, SettingsService settings) =>
            {
                var ids = await ctx.Request.ReadFromJsonAsync<List<string>>();
                if (ids == null) return Results.BadRequest();
                var s = await settings.LoadAsync();
                for (var i = 0; i < ids.Count; i++)
                {
                    var w = s.DashboardWidgets.FirstOrDefault(x => x.Id == ids[i]);
                    if (w != null) w.Order = i;
                }
                await settings.SaveAsync(s);
                settings.InvalidateCache();
                return Results.Ok();
            });

app.MapPost("/api/dashboard/widgets/{id}/run", async (string id, SettingsService settings, WidgetRunnerService widgetRunner) =>
            {
                var s = await settings.LoadAsync();
                var widget = s.DashboardWidgets.FirstOrDefault(w => w.Id == id);
                if (widget == null) return Results.NotFound();

                var (content, agentTier, outputFormat) = await widgetRunner.RunAsync(widget);
                return Results.Ok(new { content, agentTier, outputFormat });
            });

            app.MapGet("/api/dashboard/widgets/{id}/context", (string id, IInformationStore store) =>
            {
                var cachePath = store.GetNotesPath("_Assistant", "DashboardCache");
                var path = Path.Combine(cachePath, $"{id}.context.md");
                if (!File.Exists(path)) return Results.NotFound();
                return Results.Text(File.ReadAllText(path), "text/plain");
            });

            app.MapPatch("/api/dashboard/home-name", async (HttpContext ctx, SettingsService settings) =>
            {
                var body = await ctx.Request.ReadFromJsonAsync<DashboardPageRequest>();
                if (body == null || string.IsNullOrWhiteSpace(body.Name)) return Results.BadRequest();
                var s = await settings.LoadAsync();
                s.DashboardHomeName = body.Name.Trim();
                await settings.SaveAsync(s);
                settings.InvalidateCache();
                return Results.Ok();
            });

            // Dashboard page management
            app.MapPost("/api/dashboard/pages", async (HttpContext ctx, SettingsService settings) =>
            {
                var body = await ctx.Request.ReadFromJsonAsync<DashboardPageRequest>();
                if (body == null || string.IsNullOrWhiteSpace(body.Name)) return Results.BadRequest();
                var s = await settings.LoadAsync();
                var page = new DashboardPage
                {
                    Id = Guid.NewGuid().ToString("N")[..8],
                    Name = body.Name.Trim(),
                    Order = s.DashboardPages.Count,
                };
                s.DashboardPages.Add(page);
                await settings.SaveAsync(s);
                settings.InvalidateCache();
                return Results.Ok(new { page.Id });
            });

            app.MapPatch("/api/dashboard/pages/{id}", async (string id, HttpContext ctx, SettingsService settings) =>
            {
                var body = await ctx.Request.ReadFromJsonAsync<DashboardPageRequest>();
                if (body == null || string.IsNullOrWhiteSpace(body.Name)) return Results.BadRequest();
                var s = await settings.LoadAsync();
                var page = s.DashboardPages.FirstOrDefault(p => p.Id == id);
                if (page == null) return Results.NotFound();
                page.Name = body.Name.Trim();
                await settings.SaveAsync(s);
                settings.InvalidateCache();
                return Results.Ok();
            });

            app.MapPost("/api/dashboard/widgets/{id}/move", async (string id, HttpContext ctx, SettingsService settings) =>
            {
                var body = await ctx.Request.ReadFromJsonAsync<DashboardWidgetMoveRequest>();
                if (body == null) return Results.BadRequest();
                var s = await settings.LoadAsync();
                var widget = s.DashboardWidgets.FirstOrDefault(w => w.Id == id);
                if (widget == null) return Results.NotFound();
                widget.PageId = body.PageId ?? "";
                await settings.SaveAsync(s);
                settings.InvalidateCache();
                return Results.Ok();
            });

            app.MapDelete("/api/dashboard/pages/{id}", async (string id, SettingsService settings) =>
            {
                var s = await settings.LoadAsync();
                s.DashboardPages.RemoveAll(p => p.Id == id);
                foreach (var w in s.DashboardWidgets.Where(w => w.PageId == id))
                    w.PageId = "";
                await settings.SaveAsync(s);
                settings.InvalidateCache();
                return Results.Ok();
            });


            // Gmail OAuth endpoints
            app.MapGet("/api/gmail-authorize/{id}", (string id, DataSourceManager manager) =>
                GmailOAuthHelper.BeginAuthorize(id, manager));

            app.MapGet("/api/gmail-callback", async (HttpContext ctx, DataSourcesService sources, DataSourceManager manager) =>
            {
                var result = await GmailOAuthHelper.HandleCallbackAsync(ctx, sources, manager);
                return result;
            });

            app.MapGet("/api/asana-authorize/{id}", (string id, DataSourcesService sources) =>
                AsanaOAuthHelper.BeginAuthorizeAsync(id, sources));

            app.MapGet("/api/asana-callback", async (HttpContext ctx, DataSourcesService sources, DataSourceManager manager) =>
            {
                var result = await AsanaOAuthHelper.HandleCallbackAsync(ctx, sources, manager);
                return result;
            });

            app.MapGet("/api/calendar-authorize/{id}", (string id, DataSourceManager manager) =>
                CalendarOAuthHelper.BeginAuthorize(id, manager));

            app.MapGet("/api/calendar-callback", async (HttpContext ctx, DataSourcesService sources, DataSourceManager manager) =>
            {
                var result = await CalendarOAuthHelper.HandleCallbackAsync(ctx, sources, manager);
                return result;
            });

            app.MapPost("/api/plugin-order", async (HttpContext ctx, SettingsService settings) =>
            {
                var form = await ctx.Request.ReadFormAsync();
                var order = form["order"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
                var s = await settings.LoadAsync();
                s.PluginOrder = order;
                await settings.SaveAsync(s);
                settings.InvalidateCache();
                return Results.Ok();
            });

            app.MapPost("/api/capture", async (HttpContext ctx, PluginRegistry reg) =>
            {
                var form = await ctx.Request.ReadFormAsync();
                var content = form["content"].ToString();
                var pluginId = form["pluginId"].ToString();

                if (string.IsNullOrWhiteSpace(content))
                    return Results.BadRequest(new { error = "content is required" });

                ICaptureTarget? target;
                if (!string.IsNullOrEmpty(pluginId) && reg.CaptureTargets.TryGetValue(pluginId, out target))
                {
                    await target.CaptureAsync(content, "dashboard");
                }
                else
                {
                    target = reg.DefaultCaptureTarget;
                    if (target == null)
                        return Results.BadRequest(new { error = "no capture target registered" });
                    await target.CaptureAsync(content, "dashboard");
                }

                return Results.Ok(new { message = "Captured" });
            });

            app.MapGet("/api/tray/status", async (IServiceScopeFactory scopeFactory, PluginRegistry reg) =>
            {
                var statuses = new List<object>();
                foreach (var plugin in reg.Plugins)
                {
                    if (plugin is ITrayStatusProvider provider)
                    {
                        var status = await provider.GetStatusAsync();
                        statuses.Add(new { pluginId = plugin.Id, status.Label, status.BadgeCount });
                    }
                }
                return Results.Ok(statuses);
            });

            // Ephemeral context
            app.MapPost("/api/context", async (HttpContext ctx, SettingsService settings) =>
            {
                var form = await ctx.Request.ReadFormAsync();
                var text = form["content"].ToString().Trim();
                if (string.IsNullOrWhiteSpace(text)) return Results.BadRequest(new { error = "content required" });
                var s = await settings.LoadAsync();
                s.EphemeralContext = s.EphemeralContext
                    .Where(e => e.ExpiresAt > DateTimeOffset.UtcNow).ToList();
                s.EphemeralContext.Add(new EphemeralContextEntry(text, DateTimeOffset.UtcNow.AddHours(24)));
                await settings.SaveAsync(s);
                settings.InvalidateCache();
                return Results.Ok(new { message = "Context set" });
            });

            // Chat endpoints
            app.MapPost("/api/chat/send", async (HttpContext ctx, ChatService chat) =>
            {
                try
                {
                    var body = await JsonDocument.ParseAsync(ctx.Request.Body);
                    var conversationId = body.RootElement.TryGetProperty("conversationId", out var cid)
                        ? cid.GetString() : null;
                    var message = body.RootElement.TryGetProperty("message", out var msg)
                        ? msg.GetString() ?? "" : "";
                    var agentTier = body.RootElement.TryGetProperty("agentTier", out var tier)
                        ? tier.GetString() ?? "heavy" : "heavy";
                    if (string.IsNullOrWhiteSpace(message))
                        return Results.BadRequest(new { error = "message is required" });
                    var convId = await chat.StartAsync(conversationId, message, agentTier);
                    return Results.Ok(new { conversationId = convId });
                }
                catch (Exception ex)
                {
                    return Results.Json(new { error = ex.Message }, statusCode: 500);
                }
            });

            app.MapGet("/api/chat/poll/{id}", async (string id, ChatService chat) =>
            {
                var (pending, reply, toolCalls) = await chat.PollAsync(id);
                return Results.Ok(new
                {
                    pending,
                    reply,
                    toolCalls = toolCalls?.Select(t => new { t.Name, t.ResultJson }).ToList(),
                });
            });

            app.MapGet("/api/chat/conversations", async (ConversationStore store) =>
            {
                var convs = await store.ListAsync();
                return Results.Ok(convs.Select(c => new
                {
                    c.Id, c.Title, c.Updated, messageCount = c.Messages.Count,
                }));
            });

            app.MapGet("/api/chat/conversations/{id}", async (string id, ConversationStore store) =>
            {
                var conv = await store.GetAsync(id);
                return conv == null ? Results.NotFound() : Results.Ok(conv);
            });

            app.MapDelete("/api/chat/conversations/{id}", (string id, ConversationStore store) =>
            {
                store.Delete(id);
                return Results.Ok();
            });

            if (!noBrowser)
            {
                app.Lifetime.ApplicationStarted.Register(() =>
                {
                    var url = "http://localhost:5309";
                    if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                        Process.Start("open", url);
                    else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                });
            }

            serverReady.Set();
            app.Run("http://localhost:5309");
        });

        serverThread.IsBackground = true;
        serverThread.Start();

        serverReady.Wait();

        tray.Run(onQuit: () =>
        {
            try { app?.StopAsync().Wait(3000); } catch { }
            Environment.Exit(0);
        });
    }

    public static PluginContext CreatePluginContext(IPlugin plugin, string dataDir)
    {
        var pluginDir = Path.Combine(dataDir, plugin.Id);
        Directory.CreateDirectory(pluginDir);
        return new PluginContext(
            DataDirectory: pluginDir,
            HostDataDirectory: dataDir,
            DatabasePath: Path.Combine(pluginDir, $"{plugin.Id}.db")
        );
    }
}

file record DashboardWidgetSaveRequest(string? Id, string? PageId, string Title, string Prompt, string? AgentTier, string? OutputFormat, List<string>? DataSources, List<string>? MemoryPages, int ContextDays, string? Color, bool? HideHeader, bool? HideFooter);
file record DashboardWidgetMoveRequest(string? PageId);
file record DashboardPageRequest(string Name);
file record GridPositionUpdate(string Id, int X, int Y, int W, int H);
file record WorkJobSaveRequest(string? Id, string Name, List<string>? DataSources, int ContextDays, string? Prompt, string? Description, int? ScheduleHour, int? ScheduleDay, string? FolderSourceId, string? AgentId);
