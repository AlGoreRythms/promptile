# Promptile

**An AI-powered context hub for your desktop.**

Promptile syncs your external data sources — Slack, Gmail, Calendar, GitHub, Linear, Jira, Obsidian, RSS, and more — into a local knowledge store. It exposes everything to Claude through a built-in MCP server or a web dashboard you can query with natural language widgets.

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET 9](https://img.shields.io/badge/.NET-9.0-purple.svg)](https://dotnet.microsoft.com/download/dotnet/9)
[![Platform: macOS](https://img.shields.io/badge/Platform-macOS-lightgrey.svg)](#requirements)

---

## Features

- **Dashboard widgets** — configure prompts that run on a schedule against your connected sources and display results as live cards
- **MCP server mode** — expose all your data to Claude Desktop or any MCP-compatible client via stdio transport
- **Work jobs** — run long-form agent tasks over your data and write results to a local folder
- **Memory pages** — maintain rolling or dated AI summaries of any source
- **Daily briefings** — automatically extracted situation reports and action items from across your sources
- **Chat** — conversational AI with access to all your connected data
- **Notifications** — surface new items from any source as native macOS alerts

---

## Requirements

- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9)
- macOS (for native tray icon and notifications — headless `serve` and `mcp` modes work on Linux/Windows)
- An [Anthropic API key](https://console.anthropic.com/) **or** the [Claude desktop app](https://claude.ai/download) installed (for CLI mode)

---

## Quick Start

```bash
git clone https://github.com/AlGoreRythms/Assistant.git
cd Assistant
dotnet build Promptile.sln
dotnet run --project src/Promptile.Host -- serve
```

The web UI opens at **http://localhost:5309**. The macOS tray icon appears in the menu bar.

To run without opening a browser window:

```bash
dotnet run --project src/Promptile.Host -- serve --no-browser
```

---

## Adding Data Sources

Open **http://localhost:5309/DataSources** and click **Add data source**. Promptile includes built-in providers for:

| Provider | Auth |
|----------|------|
| Slack | API token |
| Gmail | OAuth (Google) |
| Google Calendar | OAuth (Google) |
| GitHub | Personal access token |
| Linear | API key |
| Jira | API key + domain |
| Asana | Personal access token |
| Obsidian | Local vault path |
| RSS | Feed URL |
| Local folder | Directory path |

Each source syncs data to `~/Documents/Assistant/` and makes it available to all widgets, jobs, and memory pages.

---

## MCP Server Mode

Run Promptile as an MCP server over stdio, exposing all your data to Claude Desktop or any MCP-compatible agent:

```bash
dotnet run --project src/Promptile.Host -- mcp
```

To wire it into **Claude Desktop**, add to your `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "promptile": {
      "command": "dotnet",
      "args": ["run", "--project", "/path/to/Assistant/src/Promptile.Host", "--", "mcp"]
    }
  }
}
```

Claude will then have access to `store_read_data`, `store_list_sources`, and all plugin-specific tools (e.g. `slack_get_messages`, `github_list_prs`).

---

## Architecture

```
Promptile.sln
├── src/Promptile.Sdk/          Plugin contracts and shared abstractions
├── src/Promptile.Host/         Executable: web server, tray icon, AI services
│   ├── Pages/                  Razor Pages (Dashboard, Chats, Work, Memory, …)
│   ├── Services/               Background services, AI, settings, MCP executor
│   └── Mcp/                    Built-in MCP tool implementations
└── src/DataSources/            Data source plugins
    ├── Slack.Plugin/
    ├── Gmail.Plugin/
    ├── Calendar.Plugin/
    └── …
```

**Two operating modes:**

- **`serve`** — ASP.NET Core on port 5309 + native macOS tray icon. Plugins run as background services and push notifications.
- **`mcp`** — MCP server over stdio. All plugin tools and the information store are exposed to AI agents.

Plugins are compiled into the binary (no dynamic loading). Adding a new plugin is a matter of implementing one interface and registering it in `Program.cs`. See [CONTRIBUTING.md](CONTRIBUTING.md).

---

## Configuration

All settings are stored in `~/.promptile/settings.json` and editable through the **Settings** page in the web UI. Key options:

| Setting | Purpose |
|---------|---------|
| AI provider | `claude-api` (Anthropic SDK) or `claude-cli` (local Claude app) |
| Agent tiers | Separate model/key config for Light, Medium, and Heavy jobs |
| Named agents | Define specialized agents with custom system prompts |
| MCP tool access | Per-plugin allowlists for internal tool calls |

---

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for development setup, how to add a data source plugin, and the PR process.

---

## License

MIT — see [LICENSE](LICENSE).
