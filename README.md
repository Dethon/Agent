# Jack - AI Media Library Agent

An AI-powered agent that manages a personal media library through Telegram chat, using OpenRouter LLMs and the Model
Context Protocol (MCP).

## Features

- **Telegram Integration** - Chat-based interface for natural language media management
- **OpenRouter LLMs** - Supports multiple models (Gemini, GPT-4, etc.) via OpenRouter API
- **MCP Architecture** - Modular tool servers for extensibility
- **Docker Compose Stack** - Full media server setup with Plex, qBittorrent, Jackett, and FileBrowser

## Architecture

```
┌─────────────────┐     ┌─────────────────┐
│   Telegram Bot  │────▶│      Jack       │
└─────────────────┘     │   (AI Agent)    │
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
| **mcp-library**       | File search, download, status, cleanup, recommendations, list files/directories, move, cleanup downloads | Search and download content via Jackett/qBittorrent, organize media files into library structure |
| **mcp-commandrunner** | Run CLI commands, platform detection                    | Execute system commands (In development)            |

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

Copy and configure the environment file:

```bash
cd DockerCompose
cp .env.example .env  # or edit .env directly
```

Required variables:

```env
OPENROUTER__APIKEY=your_openrouter_api_key
TELEGRAM__BOTTOKEN=your_telegram_bot_token
TELEGRAM__ALLOWEDUSERNAMES__0=your_telegram_username
JACKETT__APIKEY=your_jackett_api_key
QBITTORRENT__USERNAME=admin
QBITTORRENT__PASSWORD=your_password
PUID=1000
PGID=1000
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

1. Start a group chat with with threads with your Telegram bot
2. Bot commands should stat with /

## License

[CC BY-NC-ND 4.0](LICENSE) - Attribution-NonCommercial-NoDerivatives 4.0 International