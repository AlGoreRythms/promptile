# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Run

```bash
dotnet build Assistant.sln                                        # Build all projects
dotnet run --project src/Assistant.Host -- serve                  # Web UI + tray icon on http://localhost:5309
dotnet run --project src/Assistant.Host -- serve --no-browser
dotnet run --project src/Assistant.Host -- mcp                    # MCP server mode (stdio transport)
```

No test project exists yet.

## Architecture

**Assistant** is a plugin-based menu tray platform. The host provides a native tray icon (macOS menu bar), a web server (ASP.NET Core + Razor Pages), a shared dashboard, plugin infrastructure, and a notification hub. Plugins connect to external data sources (Obsidian, Slack, Email, etc.) and expose:
- MCP tools (queryable by AI agents via `mcp` mode)
- Notification events (pushed to the user and optionally activating AI processing)

### Solution Structure

```
Assistant.sln
  src/
    Assistant.Sdk/    — Plugin contracts and shared abstractions
    Assistant.Host/   — Executable: tray, web server, AI service, settings
```

### Two Operating Modes

- **serve** (default): ASP.NET Core on port 5309 + native tray icon (macOS AppKit). Plugins register background services.
- **mcp**: MCP server over stdio. MCP tools aggregated from all enabled plugins and exposed to AI agents.

### Plugin System

Plugins implement `IPlugin` (in `Assistant.Sdk`). The host loads plugins at startup, calls `ConfigureServices` and `ConfigureApp`, and collects nav items, dashboard widgets, MCP tool types, and background services.

- **No dynamic loading** — plugins are project references compiled into one binary. Add plugin instances to the `plugins` array in `Program.cs`.
- **Separate data dir per plugin** — `~/.assistant/{pluginId}/`
- **Optional SQLite DB per plugin** — `~/.assistant/{pluginId}/{pluginId}.db`
- **Single DI container** — plugins register into the host's container and consume shared services.
- **Razor Class Libraries** — plugin pages are composed at build time.

### Notification System

Plugins that implement `INotificationSource` are wired to `NotificationHub` at startup via `NotificationHub.WirePlugins`. When an external event occurs, the plugin calls `bus.Publish(notification)`, which:
1. Shows a native system notification (macOS via `osascript`; Windows/Linux stubs ready to fill in)
2. Logs the event (future: activate AI assistant to process it)

**Runtime state**: `~/.assistant/settings.json` (host), `~/.assistant/{pluginId}/` (per-plugin data).

## Key Layers

### Assistant.Sdk (`src/Assistant.Sdk/`)

Plugin contracts — all abstractions a plugin sees:

| File | Purpose |
|------|---------|
| `IPlugin.cs` | Core contract: `ConfigureServices`, `ConfigureApp`, MCP tools, nav items, dashboard widgets, background services |
| `INotificationSource.cs` | `INotificationSource` + `INotificationBus` + `AssistantNotification` — plugin event push system |
| `INotificationService.cs` | Simple `ShowNotification(title, message)` — consumed by host, bridges to tray |
| `ITrayStatusProvider.cs` | Plugin exposes a status label + badge count shown in the tray menu |
| `IAiService.cs` | `CompleteAsync(systemPrompt, userMessage)` — AI completion abstraction |
| `IAiServiceResolver.cs` | Resolves an `IAiService` by optional agent name |
| `IAiSettings.cs` | Returns `AiSettingsData` (provider, model, API key) |
| `IMcpToolAccess.cs` | Returns the set of MCP tools a given plugin is allowed to call |
| `IMcpToolExecutor.cs` | Executes MCP tool calls and returns their definitions |
| `IWebSearch.cs` | `SearchAsync(query)` + `FetchAsync(url)` — web research |
| `IPlugin.cs` | Also defines `ICaptureTarget`, `PluginContext`, `DashboardWidget`, `NavItem` |

### Assistant.Host (`src/Assistant.Host/`)

| File | Purpose |
|------|---------|
| `Program.cs` | Entry point: sets up `~/.assistant/`, loads plugins, routes to serve/mcp mode |
| `Services/Tray/TrayHost.cs` | Builds web server on background thread, runs native tray on main thread |
| `Services/Tray/MacTrayHost.cs` | P/Invoke to macOS AppKit — status item "A", polling status menu, `osascript` notifications |
| `Services/Tray/WindowsTrayHost.cs` | Stub (currently just cancellation token — Windows toast TBD) |
| `Services/Tray/ITrayHost.cs` | `Run`, `UpdateStatus`, `ShowNotification`, `Shutdown` |
| `Services/AiService.cs` | `ClaudeCliService` (spawns `claude` CLI), `ClaudeApiService` (Anthropic SDK) |
| `Services/AiSettingsAdapter.cs` | `SettingsAwareAiService` — picks provider at call time; `AiSettingsAdapter` bridges settings → `IAiSettings` |
| `Services/AiServiceResolver.cs` | `IAiServiceResolver` impl — returns default service (extend here for named agents) |
| `Services/SettingsService.cs` | Loads/saves `AssistantSettings` from `~/.assistant/settings.json` |
| `Services/PluginRegistry.cs` | Aggregates nav items, widgets, capture targets; respects disabled-plugin settings |
| `Services/NotificationHub.cs` | `INotificationBus` impl — routes plugin events to `ITrayHost.ShowNotification` |
| `Services/NotificationService.cs` | `INotificationService` impl — thin bridge to `ITrayHost` |
| `Services/McpToolAccessService.cs` | `IMcpToolAccess` impl — reads per-plugin tool allowlist from `AssistantSettings.PluginMcpAccess` |
| `Services/McpToolExecutor.cs` | `IMcpToolExecutor` impl — reflects over MCP tool types, resolves DI dependencies, executes calls |
| `Services/WebSearchService.cs` | `IWebSearch` impl — DuckDuckGo HTML search + HTTP fetch with HTML-to-text (no API key needed) |
| `Pages/Index.cshtml` | Dashboard — composites plugin widgets |
| `Pages/Plugins.cshtml` | Plugin management (enable/disable, reorder) |
| `Pages/Settings.cshtml` | AI provider, model, API key settings |
| `Pages/Shared/_Layout.cshtml` | Nav bar — "ASSISTANT" logo, dynamic plugin tabs from `PluginRegistry` |

### AssistantSettings (`~/.assistant/settings.json`)

| Field | Purpose |
|-------|---------|
| `aiProvider` | `"claude-cli"` (default) or `"claude-api"` |
| `model` | Model string (empty = default) |
| `anthropicApiKey` | Only for `claude-api` provider |
| `disabledPlugins` | Set of plugin IDs hidden from nav/dashboard/MCP |
| `pluginOrder` | Ordered plugin IDs for nav tab order |
| `disabledMcpTools` | Individual MCP tool names excluded from MCP server |
| `pluginMcpAccess` | `{ pluginId: [allowedToolNames] }` — per-plugin MCP tool allow-list for internal calls |

### Adding a Plugin

1. Create `src/MySource.Plugin/` as a Razor Class Library referencing `Assistant.Sdk`
2. Implement `IPlugin` (and optionally `INotificationSource`, `ICaptureTarget`, `ITrayStatusProvider`)
3. Add a `<ProjectReference>` to `Assistant.Host.csproj`
4. Instantiate and add to the `plugins` array in `Program.cs`

## REST API (Host)

| Endpoint | Purpose |
|----------|---------|
| `GET /api/health` | Liveness check |
| `POST /api/capture` | Quick capture — routes to plugin via `ICaptureTarget` |
| `GET /api/tray/status` | Aggregated status from all `ITrayStatusProvider` plugins |
| `POST /api/plugin-order` | Reorder plugin nav tabs (body: `order=id1,id2,...`) |
