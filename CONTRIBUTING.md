# Contributing to Promptile

Thank you for your interest in contributing. This document covers development setup, project structure, how to add new data source plugins, and the PR process.

---

## Development Setup

**Requirements:** [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9)

```bash
git clone https://github.com/AlGoreRythms/Assistant.git
cd Assistant
dotnet build Promptile.sln
```

**Run in serve mode** (web UI + macOS tray):
```bash
dotnet run --project src/Promptile.Host -- serve
```

**Run in MCP mode** (stdio transport):
```bash
dotnet run --project src/Promptile.Host -- mcp
```

The web UI is at `http://localhost:5309`. Runtime data is stored in `~/.promptile/`.

---

## Project Structure

```
src/
  Promptile.Sdk/          Plugin contracts — all interfaces a plugin depends on
  Promptile.Host/         Executable host: web server, tray, AI, MCP server
    Pages/                Razor Pages (Dashboard, Chats, Work, Memory, Settings…)
    Services/             DI services: AI, settings, notifications, sync, jobs
    Mcp/                  Built-in MCP tools (information store, memory)
  DataSources/            Data source plugins (one folder per provider)
    Slack.Plugin/
    Gmail.Plugin/
    …
```

Plugins are **project references** compiled into one binary. There is no dynamic loading. This keeps the dependency graph explicit and avoids runtime discovery surprises.

---

## Adding a Data Source Plugin

A data source plugin has two required classes and registers into `Program.cs`.

### 1. Create the project

```bash
mkdir src/DataSources/MySource.Plugin
cd src/DataSources/MySource.Plugin
dotnet new classlib -n MySource.Plugin --framework net9.0
```

Add a project reference to `Promptile.Sdk`:

```xml
<!-- MySource.Plugin.csproj -->
<ItemGroup>
  <ProjectReference Include="..\..\Promptile.Sdk\Promptile.Sdk.csproj" />
</ItemGroup>
```

Add the project to `Promptile.sln`:
```bash
dotnet sln ../../.. add MySource.Plugin.csproj
```

### 2. Implement `IDataSourceProvider`

The provider describes the plugin and creates instances from saved configuration.

```csharp
using Promptile.Sdk;

public class MySourceDataSourceProvider : IDataSourceProvider
{
    public string Type => "my-source";
    public string DisplayName => "My Source";
    public string Icon => "🔌";

    public IReadOnlyList<DataSourceField> GetConfigFields() =>
    [
        new DataSourceField("apiKey", "API Key", "password", Required: true,
            Help: "Found in your account settings."),
        new DataSourceField("pollIntervalSeconds", "Poll interval (seconds)", "text",
            Required: false, Placeholder: "300"),
    ];

    public IDataSourceInstance CreateInstance(DataSourceConfig config) =>
        new MySourceDataSourceInstance(config);

    // Return MCP tool types if your plugin exposes queryable tools
    public IReadOnlyList<Type> GetMcpToolTypes() => [];

    // Return null to use config fields; return a URL to trigger OAuth
    public string? GetAuthStartUrl(string instanceId) => null;
    public IReadOnlyList<DataSourceField> GetPreAuthFields() => [];
}
```

### 3. Implement `IDataSourceInstance`

The instance runs the sync loop and pushes notifications.

```csharp
using Promptile.Sdk;

public class MySourceDataSourceInstance : IDataSourceInstance
{
    public string Id => Config.Id;
    public string Name => Config.Name;
    public string Type => "my-source";
    public DataSourceConfig Config { get; private set; }

    private IInformationStore? _store;
    private ISyncReporter? _sync;
    private CancellationTokenSource? _cts;
    private Task? _pollTask;
    private readonly string _stateDir;

    public MySourceDataSourceInstance(DataSourceConfig config)
    {
        Config = config;
        _stateDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".promptile", "datasources", config.Id);
        Directory.CreateDirectory(_stateDir);
    }

    public Task StartAsync(INotificationBus bus, IServiceProvider services, CancellationToken ct)
    {
        _store = services.GetRequiredService<IInformationStore>();
        _sync  = services.GetRequiredService<ISyncReporter>();
        _cts   = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _pollTask = PollLoopAsync(bus, _cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _cts?.Cancel();
        if (_pollTask != null) await _pollTask.ConfigureAwait(false);
    }

    public Task<DataSourceStatus> GetStatusAsync() =>
        Task.FromResult(new DataSourceStatus(true, "Connected"));

    public Task ResetStateAsync()
    {
        var seenPath = Path.Combine(_stateDir, "seen-keys.json");
        if (File.Exists(seenPath)) File.Delete(seenPath);
        return Task.CompletedTask;
    }

    private async Task PollLoopAsync(INotificationBus bus, CancellationToken ct)
    {
        var intervalSeconds = int.TryParse(
            Config.Config.GetValueOrDefault("pollIntervalSeconds"), out var s) ? s : 300;

        while (!ct.IsCancellationRequested)
        {
            try { await SyncAsync(bus, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { /* log */ }

            await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), ct).ConfigureAwait(false);
        }
    }

    private async Task SyncAsync(INotificationBus bus, CancellationToken ct)
    {
        _sync?.Begin(Id, Name, Type);

        // Fetch data from your API, write to the information store, publish notifications
        var content = $"# {Name}\nFetched at {DateTime.UtcNow:O}";
        await _store!.AppendDataAsync(Name, Type, content);

        _sync?.Complete(Id, 1);
    }
}
```

### 4. Register the provider

In `src/Promptile.Host/Program.cs`, add your provider to the `dataSourceProviders` array:

```csharp
var dataSourceProviders = new IDataSourceProvider[]
{
    // existing providers…
    new MySourceDataSourceProvider(),
};
```

Also add a `<ProjectReference>` to `src/Promptile.Host/Promptile.Host.csproj` and the `using MySource;` namespace import.

### 5. Optional: MCP tools

If your plugin should expose queryable tools to AI agents, add a tools class:

```csharp
using Promptile.Sdk;
using ModelContextProtocol.Server;
using System.ComponentModel;

[McpServerToolType]
public class MySourceMcpTools(IDataSourceManager manager)
{
    [McpServerTool(Name = "my_source_list")]
    [Description("List configured My Source data sources")]
    public string ListSources()
    {
        var instances = manager.GetInstances("my-source");
        return string.Join("\n", instances.Select(i => i.Name));
    }
}
```

Return it from `IDataSourceProvider.GetMcpToolTypes()`:

```csharp
public IReadOnlyList<Type> GetMcpToolTypes() => [typeof(MySourceMcpTools)];
```

---

## Adding a Plugin (IPlugin)

For plugins that contribute nav tabs, dashboard widgets, and Razor pages (not just data sources), implement `IPlugin` and add it to the `plugins` array in `Program.cs`. See `src/Promptile.Sdk/IPlugin.cs` for the full interface.

---

## Code Conventions

- **Nullable enabled** — all projects have `<Nullable>enable</Nullable>`
- **C# 12+** — use primary constructors, collection expressions, and pattern matching where they improve clarity
- **No comments for obvious code** — only comment non-obvious invariants or workarounds
- **Error handling** — catch exceptions in poll loops; let them propagate in request handlers so the framework returns 500

---

## Pull Request Process

1. Fork the repo and create a feature branch
2. `dotnet build Promptile.sln` must pass with zero errors
3. Open a PR against `main` — describe what changed and why
4. Reference any related issue in the PR description
5. CI will run the build automatically; fix any failures before requesting review

---

## Reporting Issues

Use the GitHub issue tracker. For bugs, include your OS, .NET version, and the relevant section of `~/.promptile/logs/assistant.log`.
