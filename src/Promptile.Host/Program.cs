using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using HostBuilder = Microsoft.Extensions.Hosting.Host;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Serilog;
using Promptile.Host.Mcp;
using Promptile.Host.Services;
using Promptile.Host.Services.Tray;
using Promptile.Sdk;
using Slack;
using Gmail;
using GmailImap;
using Calendar;
using Obsidian;
using Linear;
using GitHub;
using Jira;
using Folder;
using Asana;
using Rss;

namespace Promptile.Host;

public static class Program
{
    public static void Main(string[] args)
    {
        var noBrowser = args.Contains("--no-browser");
        var mode = args.FirstOrDefault(a => !a.StartsWith("--")) ?? "serve";

        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".promptile");
        Directory.CreateDirectory(dataDir);
        Directory.CreateDirectory(Path.Combine(dataDir, "logs"));

        var settingsService = new SettingsService(dataDir);

        // IPlugin instances — add new plugin instances here
        var plugins = Array.Empty<IPlugin>();

        // IDataSourceProvider instances — add new data source providers here
        var dataSourceProviders = new IDataSourceProvider[]
        {
            new SlackDataSourceProvider(),
            new GmailDataSourceProvider(),
            new GmailImapDataSourceProvider(),
            new CalendarDataSourceProvider(),
            new ObsidianDataSourceProvider(),
            new LinearDataSourceProvider(),
            new GitHubDataSourceProvider(),
            new JiraDataSourceProvider(),
            new FolderDataSourceProvider(),
            new AsanaDataSourceProvider(),
            new RssDataSourceProvider(),
        };

        var registry = new PluginRegistry(settingsService);
        foreach (var plugin in plugins)
            registry.Register(plugin);

        if (mode == "serve")
            KillExistingInstance();

        if (mode == "mcp")
            RunMcp(args, dataDir, plugins, dataSourceProviders, registry, settingsService).GetAwaiter().GetResult();
        else
            TrayHost.RunWithTray(args, dataDir, noBrowser, plugins, dataSourceProviders, registry, settingsService);
    }

    internal static void RegisterHostServices(IServiceCollection services, SettingsService settingsService)
    {
        services.AddSingleton(settingsService);
        services.AddSingleton<IWebSearch, WebSearchService>();
        services.AddSingleton<IMcpToolAccess, McpToolAccessService>();
        services.AddSingleton<IMcpToolExecutor, HostMcpToolExecutor>();
        services.AddSingleton<IAiServiceResolver>(sp => new AiServiceResolver(
            sp.GetRequiredService<SettingsService>(),
            sp.GetRequiredService<ILoggerFactory>()));
        services.AddSingleton<IInformationStore>(sp =>
        {
            var s = sp.GetRequiredService<SettingsService>().LoadSync();
            return new InformationStoreService(s.InformationStorePath);
        });

    }

    static async Task RunMcp(string[] args, string dataDir, IPlugin[] plugins,
        IDataSourceProvider[] dataSourceProviders, PluginRegistry registry, SettingsService settingsService)
    {
        var host = HostBuilder.CreateApplicationBuilder(args);

        host.Services.AddSingleton(registry);
        RegisterHostServices(host.Services, settingsService);

        // Data sources
        host.Services.AddSingleton(new DataSourcesService(dataDir));
        host.Services.AddSingleton<INotificationBus, NullNotificationBus>();
        foreach (var provider in dataSourceProviders)
            host.Services.AddSingleton<IDataSourceProvider>(provider);
        host.Services.AddSingleton<IDataSourceManager, DataSourceManager>();
        host.Services.AddSingleton<DataSourceManager>(sp =>
            (DataSourceManager)sp.GetRequiredService<IDataSourceManager>());

        foreach (var plugin in plugins)
        {
            var ctx = TrayHost.CreatePluginContext(plugin, dataDir);
            plugin.ConfigureServices(host.Services, ctx);
        }

        var mcpBuilder = host.Services.AddMcpServer()
            .WithStdioServerTransport();

        // Memory feature temporarily disabled
        // host.Services.AddSingleton<MemoryService>();
        // host.Services.AddHostedService(sp => sp.GetRequiredService<MemoryService>());

        var settings = settingsService.LoadSync();
        var toolTypes = new List<Type> { typeof(InformationStoreMcpTools) /*, typeof(MemoryMcpTools)*/ };

        foreach (var plugin in plugins)
        {
            if (settings.DisabledPlugins.Contains(plugin.Id))
                continue;
            toolTypes.AddRange(plugin.GetMcpToolTypes());
        }

        foreach (var provider in dataSourceProviders)
            toolTypes.AddRange(provider.GetMcpToolTypes());

        foreach (var toolType in toolTypes)
        {
            var method = typeof(McpServerBuilderExtensions)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "WithTools" && m.IsGenericMethod
                    && m.GetParameters().Length == 2
                    && m.GetParameters()[1].ParameterType == typeof(System.Text.Json.JsonSerializerOptions));

            if (method != null)
                method.MakeGenericMethod(toolType).Invoke(null, [mcpBuilder, null]);
        }

        host.Logging.ClearProviders();
        host.Services.AddSerilog(config =>
            config.WriteTo.File(Path.Combine(dataDir, "logs", "mcp.log"),
                rollingInterval: RollingInterval.Day));

        var mcpApp = host.Build();

        var dsManager = mcpApp.Services.GetRequiredService<DataSourceManager>();
        await dsManager.StartAllAsync();

        await mcpApp.RunAsync();
    }

    static void KillExistingInstance()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var netstat = Process.Start(new ProcessStartInfo("cmd", "/c netstat -ano | findstr LISTENING | findstr :5309")
                    { RedirectStandardOutput = true, UseShellExecute = false });
                if (netstat is not null)
                {
                    var output = netstat.StandardOutput.ReadToEnd();
                    netstat.WaitForExit(2000);
                    foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length > 0 && int.TryParse(parts[^1], out var pid) && pid != 0)
                        {
                            try { Process.GetProcessById(pid).Kill(); } catch { }
                        }
                    }
                    Thread.Sleep(500);
                }
            }
            else
            {
                var kill = Process.Start(new ProcessStartInfo("bash", "-c \"lsof -ti:5309 | xargs kill -9 2>/dev/null\"")
                    { UseShellExecute = false });
                kill?.WaitForExit(3000);
                Thread.Sleep(500);
            }
        }
        catch { }
    }
}
