# Agent - AI Media Library Agent

An AI-powered agent that manages a personal media library through Telegram chat, web interface, or CLI terminal,
using OpenRouter LLMs and the Model Context Protocol (MCP).

## Features

- **Multi-Agent Support** - Run multiple agents from a single container, each with unique configurations
- **Quad Interface** - Chat via Telegram bot, web browser, CLI terminal, or Azure Service Bus
- **WebChat** - Browser-based chat with real-time streaming, topic management, and multi-agent selection
- **Conversation Persistence** - Redis-backed chat history survives application restarts
- **Tool Approval System** - Approve, reject, or auto-approve AI tool calls with whitelist patterns
- **MCP Resource Subscriptions** - Real-time updates from MCP servers via resource subscriptions
- **Download Resubscription** - Resume tracking in-progress downloads after restart
- **Web Search** - Search the web and fetch content via Brave Search API
- **Memory System** - Vector-based semantic memory storage and recall using Redis
- **OpenRouter LLMs** - Supports multiple models (Gemini, GPT-4, etc.) via OpenRouter API
- **MCP Architecture** - Modular tool servers for extensibility
- **Streaming Pipeline** - Concurrent message processing with GroupByStreaming and Merge operators
- **Docker Compose Stack** - Full media server setup with Plex, qBittorrent, Jackett, and FileBrowser

## Architecture

```
┌─────────────────┐        ┌─────────────────┐     ┌─────────────────┐
│   Telegram Bot  │───────▶│                 │◀────│   CLI Terminal  │
└─────────────────┘        │   Jack/Jonas    │     └─────────────────┘
        ┌─────────────────▶│   (AI Agents)   │◀────────────────┐
        │                  └────────┬────────┘                 │
┌───────┴───────┐                   │                 ┌────────┴────────┐
│    WebChat    │                   │                 │  Azure Service  │
│ (Blazor WASM) │                   │                 │       Bus       │
└───────────────┘                   │                 └─────────────────┘
                                    │
      ┌──────────────┬──────────────┼───────────────┬───────────────┬───────────────┐
      ▼              ▼              ▼               ▼               ▼               ▼
┌────────────┐ ┌────────────┐ ┌─────────────┐ ┌────────────┐ ┌─────────────┐ ┌─────────────┐
│MCP Library │ │ MCP Text   │ │MCP WebSearch│ │ MCP Memory │ │MCP Idealista│ │    Redis    │
└─────┬──────┘ └────────────┘ └─────────────┘ └─────┬──────┘ └──────┬──────┘ │(Persistence)│
      │                                             │               │        └─────────────┘
┌─────┴──────┐                                ┌─────┴──────┐  ┌─────┴──────┐
│ qBittorrent│                                │Redis Vector│  │ Idealista  │
│  Jackett   │                                │   Store    │  │    API     │
│    Plex    │                                └────────────┘  └────────────┘
│ FileBrowser│
└────────────┘
```

### MCP Servers

| Server                | Tools                                                                                                                                           | Purpose                                                                                                                          |
|-----------------------|-------------------------------------------------------------------------------------------------------------------------------------------------|----------------------------------------------------------------------------------------------------------------------------------|
| **mcp-library**       | FileSearch, FileDownload, GetDownloadStatus, CleanupDownload, ResubscribeDownloads, ContentRecommendationTool, ListFiles, ListDirectories, Move | Search and download content via Jackett/qBittorrent, organize media files into library structure                                 |
| **mcp-text**          | TextListDirectories, TextListFiles, TextSearch, TextInspect, TextRead, TextPatch, TextCreate, Move, RemoveFile                                  | Manage a knowledge vault of markdown notes and text files                                                                        |
| **mcp-websearch**     | WebSearch, WebBrowse, WebClick, WebInspect                                                                                                      | Search the web and browse pages with persistent browser sessions, modal auto-dismissal, and element interaction                  |
| **mcp-memory**        | MemoryStore, MemoryRecall, MemoryForget, MemoryList, MemoryReflect                                                                              | Vector-based memory storage and retrieval using Redis                                                                            |
| **mcp-idealista**     | IdealistaPropertySearch                                                                                                                         | Search real estate properties on Idealista (Spain, Italy, Portugal) with comprehensive filters                                   |
| **mcp-commandrunner** | RunCommand, GetCliPlatform                                                                                                                      | Execute system commands (Not included in Docker Compose)                                                                         |

### Agents

| Agent     | MCP Servers                                         | Purpose                                                                   |
|-----------|-----------------------------------------------------|---------------------------------------------------------------------------|
| **Jack**  | mcp-library, mcp-websearch                          | Media acquisition and library management ("Captain Jack" pirate persona)  |
| **Jonas** | mcp-text, mcp-websearch, mcp-memory, mcp-idealista  | Knowledge base management ("Scribe" persona for managing markdown vaults) |

### Multi-Agent Configuration

Agents are defined as configuration data, each with:
- Unique Telegram bot token for routing
- Custom LLM model selection
- Specific MCP server endpoints
- Tool whitelist patterns
- Custom system instructions

Agent routing:
- **Telegram**: Each bot token maps to one agent (bot token hash matching)
- **WebChat**: User selects agent from available list in the UI
- **CLI**: Uses the first configured agent
- **Service Bus**: Agent specified in message `agentId` field (falls back to first agent)

## Projects

| Project                  | Description                                                     |
|--------------------------|-----------------------------------------------------------------|
| `Agent`                  | Main agent application with Telegram bot integration            |
| `Domain`                 | Core domain logic, agent contracts, and services                |
| `Infrastructure`         | External service clients (Telegram, MCP, OpenRouter)            |
| `McpServerLibrary`       | MCP server for torrent search, downloads, and file organization |
| `McpServerText`          | MCP server for text/markdown file inspection and editing        |
| `McpServerWebSearch`     | MCP server for web search and content fetching                  |
| `McpServerMemory`        | MCP server for vector-based memory storage and recall           |
| `McpServerIdealista`     | MCP server for Idealista real estate property search            |
| `McpServerCommandRunner` | MCP server for CLI command execution                            |
| `WebChat`                | Blazor WebAssembly host server for browser-based chat           |
| `WebChat.Client`         | Blazor WebAssembly client with chat UI and SignalR integration  |
| `DockerCompose`          | Docker Compose configuration for the full stack                 |
| `Tests`                  | Unit and integration tests                                      |

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
REPOSITORY_PATH=..
DATA_PATH=./volumes/data
VAULT_PATH=./volumes/vault
PUID=1000
PGID=1000
OPENROUTER__APIKEY=your_openrouter_api_key
OPENROUTER__APIURL=https://openrouter.ai/api/v1/
BRAVE__APIKEY=your_brave_search_api_key
IDEALISTA__APIKEY=your_idealista_api_key
IDEALISTA__APISECRET=your_idealista_api_secret
TELEGRAM__ALLOWEDUSERNAMES__0=your_telegram_username
JACKETT__APIKEY=your_jackett_api_key
QBITTORRENT__USERNAME=admin
QBITTORRENT__PASSWORD=your_password

# Agent definitions (array)
AGENTS__0__ID=jack
AGENTS__0__NAME=Jack
AGENTS__0__MODEL=google/gemini-2.0-flash-001
AGENTS__0__TELEGRAMBOTTOKEN=your_telegram_bot_token_for_jack
AGENTS__0__MCPSERVERENDPOINTS__0=http://mcp-library:8080/sse
AGENTS__0__MCPSERVERENDPOINTS__1=http://mcp-websearch:8080/sse
AGENTS__0__WHITELISTPATTERNS__0=mcp:mcp-library:*
AGENTS__0__WHITELISTPATTERNS__1=mcp:mcp-websearch:*
AGENTS__0__CUSTOMINSTRUCTIONS=You are Jack, a media library assistant...

AGENTS__1__ID=jonas
AGENTS__1__NAME=Jonas
AGENTS__1__MODEL=google/gemini-2.0-flash-001
AGENTS__1__TELEGRAMBOTTOKEN=your_telegram_bot_token_for_jonas
AGENTS__1__MCPSERVERENDPOINTS__0=http://mcp-text:8080/sse
AGENTS__1__MCPSERVERENDPOINTS__1=http://mcp-websearch:8080/sse
AGENTS__1__MCPSERVERENDPOINTS__2=http://mcp-memory:8080/sse
AGENTS__1__MCPSERVERENDPOINTS__3=http://mcp-idealista:8080/sse

# Service Bus (optional, for external system integration)
SERVICEBUS__CONNECTIONSTRING=Endpoint=sb://yournamespace.servicebus.windows.net/;SharedAccessKeyName=...
SERVICEBUS__PROMPTQUEUENAME=agent-prompts
SERVICEBUS__RESPONSEQUEUENAME=agent-responses
SERVICEBUS__MAXCONCURRENTCALLS=10
```

### 3. Run with Docker Compose

```bash
cd DockerCompose
docker compose up -d
```

## Services & Ports

| Service        | Port  | Description                    |
|----------------|-------|--------------------------------|
| WebChat        | 5001  | Browser-based chat interface   |
| Redis          | 6379  | Conversation state persistence |
| qBittorrent    | 8001  | Torrent client WebUI           |
| FileBrowser    | 8002  | File management WebUI          |
| Jackett        | 8003  | Torrent indexer proxy          |
| Plex           | 32400 | Media server                   |
| MCP Library    | 6001  | Library MCP server             |
| MCP Text Tools | 6002  | Text/Markdown MCP server       |
| MCP WebSearch  | 6003  | Web search MCP server          |
| MCP Memory     | 6004  | Memory storage MCP server      |
| MCP Idealista  | 6005  | Idealista property MCP server  |

## Usage

### WebChat Interface

Access the browser-based chat at `http://localhost:5001` after starting the Docker Compose stack.

Features:
- **Real-time streaming** - Messages stream as they're generated with automatic reconnection
- **Topic management** - Organize conversations into topics with server-side persistence
- **Multi-agent selection** - Switch between available agents from the UI
- **User identity selection** - Switch between configured user identities with persistent avatars
- **Stream resumption** - Reconnects automatically and resumes from where you left off

#### User Identity Configuration

WebChat supports multiple user identities configured via `WebChat/appsettings.json` or environment variables:

**appsettings.json:**
```json
{
  "Users": [
    { "Id": "Alice", "AvatarUrl": "avatars/alice.png" },
    { "Id": "Bob", "AvatarUrl": "avatars/bob.png" }
  ]
}
```

**Environment variables (Docker Compose):**
```env
USERS__0__ID=Alice
USERS__0__AVATARURL=avatars/alice.png
USERS__1__ID=Bob
USERS__1__AVATARURL=avatars/bob.png
```

Place avatar images in `WebChat.Client/wwwroot/avatars/`. Selected identity persists in browser local storage.

To run WebChat standalone (development):

```bash
dotnet run --project WebChat
```

### CLI Interface

Run the agent with the CLI interface for local terminal interaction:

```bash
dotnet run --project Agent -- --chat Cli
```

### Telegram Interface (Default)

```bash
dotnet run --project Agent -- --chat Telegram
```

Or simply:

```bash
dotnet run --project Agent
```

### Service Bus Interface

Enable Azure Service Bus integration for external system connectivity. The agent listens for prompts on a queue and writes responses to another queue.

**Prompt Message Format** (sent to prompt queue):
```json
{
  "prompt": "Your question or command here",
  "sender": "external-system-id"
}
```

Messages must include `agentId` (in application properties or message body) to route to a specific agent, or the first configured agent is used.

**Response Message Format** (written to response queue):
```json
{
  "sourceId": "conversation-thread-id",
  "agentId": "jack",
  "response": "Agent's response text",
  "completedAt": "2024-01-15T10:30:00Z"
}
```

Configure via environment variables (see above) or `appsettings.json`:
```json
{
  "serviceBus": {
    "connectionString": "Endpoint=sb://...",
    "promptQueueName": "agent-prompts",
    "responseQueueName": "agent-responses",
    "maxConcurrentCalls": 10
  }
}
```

The Service Bus processor runs automatically when configured alongside other interfaces.

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

### Chat Commands

| Command   | Description                                           |
|-----------|-------------------------------------------------------|
| `/cancel` | Cancel current operation (keeps conversation history) |
| `/clear`  | Clear conversation and wipe thread history from Redis |
| `/help`   | Show available commands (CLI only)                    |

## Persistence

The agent uses Redis to persist conversation history and memory across restarts:

- **Chat History** - All messages are stored with a 30-day expiry
- **Thread State** - Each chat thread is identified by `agent-key:{chatId}:{threadId}`
- **Download Tracking** - Use `ResubscribeDownloads` tool to resume tracking downloads after restart
- **Memory Storage** - Vector-based memories stored in Redis using RediSearch for semantic recall

The CLI interface automatically restores previous conversation on startup.

## License

[CC BY-NC-ND 4.0](LICENSE) - Attribution-NonCommercial-NoDerivatives 4.0 International