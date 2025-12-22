# Jack - AI Media Library Agent

An AI-powered agent that manages a personal media library through Telegram chat, using OpenRouter LLMs and the Model
Context Protocol (MCP).

## Features

- **Dual Interface** - Chat via Telegram bot or local CLI terminal
- **Tool Approval System** - Approve, reject, or auto-approve AI tool calls with whitelist patterns
- **MCP Resource Subscriptions** - Real-time updates from MCP servers via resource subscriptions
- **OpenRouter LLMs** - Supports multiple models (Gemini, GPT-4, etc.) via OpenRouter API
- **MCP Architecture** - Modular tool servers for extensibility
- **Streaming Pipeline** - Concurrent message processing with GroupByStreaming and Merge operators
- **Docker Compose Stack** - Full media server setup with Plex, qBittorrent, Jackett, and FileBrowser

## Architecture

```
┌─────────────────┐     ┌─────────────────┐     ┌─────────────────┐
│   Telegram Bot  │────▶│                 │◀────│   CLI Terminal  │
└─────────────────┘     │      Jack       │     └─────────────────┘
                        │   (AI Agent)    │
                        └────────┬────────┘
                                 │
               ┌─────────────────┴─────────────────┐
               ▼                                   ▼
      ┌────────────────┐                  ┌────────────────┐
      │  MCP Library   │                  │ MCP CommandRun │
      │    Server      │                  │    Server      │
      └───────┬────────┘                  └────────────────┘
              │
      ┌───────┴───────┐
      │  qBittorrent  │
      │   Jackett     │
      │     Plex      │
      │  FileBrowser  │
      └───────────────┘
```

### MCP Servers

| Server                | Tools                                                   | Purpose                                             |
|-----------------------|---------------------------------------------------------|-----------------------------------------------------|
| **mcp-library**       | FileSearch, FileDownload, GetDownloadStatus, CleanupDownload, ContentRecommendationTool, ListFiles, ListDirectories, Move | Search and download content via Jackett/qBittorrent, organize media files into library structure |
| **mcp-commandrunner** | RunCommand, GetCliPlatform                              | Execute system commands (Not included in Docker Compose) |

## Projects

| Project                  | Description                                          |
|--------------------------|------------------------------------------------------|
| `Jack`                   | Main agent application with Telegram bot integration |
| `Domain`                 | Core domain logic, agent contracts, and services     |
| `Infrastructure`         | External service clients (Telegram, MCP, OpenRouter) |
| `McpServerLibrary`       | MCP server for torrent search, downloads, and file organization |
| `McpServerCommandRunner` | MCP server for CLI command execution                 |
| `DockerCompose`          | Docker Compose configuration for the full stack      |
| `Tests`                  | Unit and integration tests                           |

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Docker](https://www.docker.com/) and Docker Compose
- [OpenRouter API Key](https://openrouter.ai/)
- [Telegram Bot Token](https://core.telegram.org/bots#creating-a-new-bot)

## Getting Started

### 1. Clone and Configure

```bash
git clone https://github.com/yourusername/agent.git
cd agent
```

### 2. Set Environment Variables

Edit the environment file:

```bash
cd DockerCompose
# Edit .env with your configuration
```

Required variables:

```env
JACK_REPOSITORY_PATH=..
DATA_PATH=./volumes/data
PUID=1000
PGID=1000
OPENROUTER__APIKEY=your_openrouter_api_key
TELEGRAM__BOTTOKEN=your_telegram_bot_token
TELEGRAM__ALLOWEDUSERNAMES__0=your_telegram_username
JACKETT__APIKEY=your_jackett_api_key
QBITTORRENT__USERNAME=admin
QBITTORRENT__PASSWORD=your_password
```

### 3. Run with Docker Compose

```bash
cd DockerCompose
docker compose up -d
```

## Services & Ports

| Service      | Port  | Description           |
|--------------|-------|-----------------------|
| qBittorrent  | 8001  | Torrent client WebUI  |
| FileBrowser  | 8002  | File management WebUI |
| Jackett      | 8003  | Torrent indexer proxy |
| Plex         | 32400 | Media server          |
| MCP Library  | 6001  | Library MCP server    |

## Usage

### CLI Interface

Run Jack with the CLI interface for local terminal interaction:

```bash
dotnet run --project Jack -- --chat Cli
```

### Telegram Interface (Default)

```bash
dotnet run --project Jack -- --chat Telegram
```

Or simply:

```bash
dotnet run --project Jack
```

### Tool Approval

When the agent wants to execute a tool:
- **Approve** - Allow the tool to run once
- **Always** - Auto-approve this tool for the session
- **Reject** - Block the tool execution

Configure permanent auto-approvals in `appsettings.json` using glob patterns:

```json
{
  "whitelistPatterns": [
    "mcp:mcp-library:*",
    "mcp:localhost:*"
  ]
}
```

Pattern format: `mcp:<server>:<tool>` with `*` wildcard support.

## License

[CC BY-NC-ND 4.0](LICENSE) - Attribution-NonCommercial-NoDerivatives 4.0 International