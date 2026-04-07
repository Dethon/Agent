# Agent - AI Media Library Agent

An AI-powered agent that manages a personal media library through Telegram chat, web interface, or Azure Service Bus,
using OpenRouter LLMs and the Model Context Protocol (MCP).

## Features

- **Multi-Agent Support** - Run multiple agents from a single container, each with unique configurations
- **Channel Architecture** - Transports (WebChat, Telegram, ServiceBus) run as independent MCP channel servers
- **WebChat** - Browser-based chat with real-time streaming, topic management, and multi-agent selection
- **Telegram Multi-Bot** - Each agent gets its own Telegram bot with inline keyboard tool approvals
- **Azure Service Bus** - Queue-based integration for external systems
- **Conversation Persistence** - Redis-backed chat history survives application restarts
- **Tool Approval System** - Approve, reject, or auto-approve AI tool calls with whitelist patterns
- **MCP Resource Subscriptions** - Real-time updates from MCP servers via resource subscriptions
- **Download Resubscription** - Resume tracking in-progress downloads after restart
- **Web Search & Browsing** - Search the web via Brave Search API; browse pages with persistent sessions using accessibility tree snapshots and element-ref interactions via Camoufox (anti-detect browser)
- **Virtual Filesystem** - Unified filesystem across MCP servers via `filesystem://` resource discovery, with domain tools for read, create, edit, glob, search, move, and delete
- **Memory System** - Built-in proactive memory with LLM-based extraction from windowed conversation context, vector recall, and periodic consolidation (dreaming)
- **OpenRouter LLMs** - Supports multiple models (Gemini, GPT-4, etc.) via OpenRouter API
- **MCP Architecture** - Modular tool servers and channel servers for extensibility
- **Streaming Pipeline** - Concurrent message processing with GroupByStreaming and Merge operators
- **Observability Dashboard** - PWA dashboard showing token costs, tool analytics, error rates, schedule history, memory analytics, and live service health
- **Subagent Delegation** - Parent agents can spawn ephemeral subagents for parallel or heavy tasks
- **Docker Compose Stack** - Full media server setup with qBittorrent, Jackett, FileBrowser, and Camoufox

## Architecture

```
┌─────────────────┐     ┌─────────────────┐     ┌─────────────────┐
│    WebChat      │     │    Telegram     │     │  Azure Service  │
│ (Blazor WASM)   │     │    (Bots)       │     │       Bus       │
└────────┬────────┘     └────────┬────────┘     └────────┬────────┘
         │                       │                       │
         ▼                       ▼                       ▼
┌─────────────────┐     ┌─────────────────┐     ┌─────────────────┐
│ McpChannel      │     │ McpChannel      │     │ McpChannel      │
│ SignalR         │     │ Telegram        │     │ ServiceBus      │
│ (MCP Server)    │     │ (MCP Server)    │     │ (MCP Server)    │
└────────┬────────┘     └────────┬────────┘     └────────┬────────┘
         │    channel/message    │                       │
         │    send_reply         │                       │
         │    request_approval   │                       │
         └────────────┬──────────┘───────────────────────┘
                      │
                      ▼
              ┌───────────────┐
              │  Agent Core   │
              │ (MCP Client)  │
              └───────┬───────┘
                      │
      ┌───────────────┼──────────────┬──────────────┐
      ▼               ▼              ▼              ▼
┌────────────┐ ┌────────────┐ ┌─────────────┐ ┌─────────────┐
│MCP Library │ │ MCP Vault  │ │MCP WebSearch│ │MCP Idealista│
│filesystem: │ │filesystem: │ │             │ │             │
│  //media   │ │  //vault   │ │             │ │             │
└─────┬──────┘ └────────────┘ └──────┬──────┘ └──────┬──────┘
      │                              │               │
┌─────┴──────┐                 ┌─────┴──────┐  ┌─────┴──────┐
│ qBittorrent│                 │  Camoufox  │  │ Idealista  │
│  Jackett   │                 │ (anti-det  │  │    API     │
│ FileBrowser│                 │  browser)  │  └────────────┘
└────────────┘                 └────────────┘

              ┌───────────────────────────────────┐
              │  Virtual Filesystem (domain)      │
              │  Discovers filesystem:// resources │
              │  Mounts → Registry → Domain tools │
              └───────────────────────────────────┘

              ┌───────────────────────────────────┐
              │       Memory (built-in)           │
              │  Extract → Store → Recall → Dream │
              │         Redis Vector Store        │
              └───────────────────────────────────┘

                     ┌─────────────────────────────────┐
  metrics:events     │       Observability             │
  (Redis Pub/Sub)───▶│  Collector → Redis Aggregation  │──▶ Dashboard (PWA)
                     │  REST API + SignalR Hub         │
                     │  Serves Dashboard.Client        │
                     └─────────────────────────────────┘
```

### Channel Protocol

Each channel MCP server exposes a standard protocol:
- **Inbound**: `channel/message` notification — pushes user messages to the agent
- **Outbound**: `send_reply` tool — agent streams response chunks back to the user
- **Outbound**: `request_approval` tool — interactive tool approval or auto-approval notification
- **Outbound**: `create_conversation` tool — agent-initiated conversations (scheduling)

New transports can be added by deploying a new channel MCP server — zero agent code changes needed.

### MCP Tool Servers

| Server            | Tools                                                                                                                   | Resources             | Purpose                                                                                 |
|-------------------|-------------------------------------------------------------------------------------------------------------------------|-----------------------|-----------------------------------------------------------------------------------------|
| **mcp-library**   | FileSearch, FileDownload, GetDownloadStatus, CleanupDownload, ResubscribeDownloads, ContentRecommendation, FsGlob, FsMove | `filesystem://media`  | Search and download content via Jackett/qBittorrent, organize media files                |
| **mcp-vault**     | FsGlob, FsRead, FsSearch, FsCreate, FsEdit, FsMove, FsDelete                                                          | `filesystem://vault`  | Manage a knowledge vault of markdown notes and text files                                |
| **mcp-websearch** | WebSearch, WebBrowse, WebSnapshot, WebAction                                                                            |                       | Search the web and browse pages via Camoufox with accessibility tree snapshots            |
| **mcp-idealista** | IdealistaPropertySearch                                                                                                 |                       | Search real estate properties on Idealista (Spain, Italy, Portugal)                      |

### MCP Channel Servers

| Server                    | Purpose                                                                                    |
|---------------------------|--------------------------------------------------------------------------------------------|
| **mcp-channel-signalr**   | WebChat transport — hosts SignalR hub, manages streams/sessions/approvals, push notifications |
| **mcp-channel-telegram**  | Telegram transport — multi-bot polling (one per agent), inline keyboard approvals            |
| **mcp-channel-servicebus**| Azure Service Bus transport — queue processor, auto-approval, response sender                |

### Agents

| Agent     | MCP Servers                                         | Features                                    | Purpose                                                                   |
|-----------|-----------------------------------------------------|---------------------------------------------|---------------------------------------------------------------------------|
| **Jack**  | mcp-library, mcp-websearch                          | filesystem (glob, move)                     | Media acquisition and library management ("Captain Jack" pirate persona)  |
| **Jonas** | mcp-vault, mcp-websearch, mcp-idealista             | filesystem, scheduling, subagents, memory   | Knowledge base management ("Scribe" persona) with subagent delegation     |

### Multi-Agent Configuration

Agents are defined as configuration data, each with:
- Custom LLM model selection
- Specific MCP server endpoints
- Tool whitelist patterns (e.g., `mcp:mcp-library:*`, `domain:subagents:*`)
- Custom system instructions
- Enabled features (e.g., `filesystem`, `scheduling`, `subagents`, `memory`)

#### Subagents

Agents with the `subagents` feature enabled can delegate work to ephemeral subagents via the `run_subagent` tool. Subagents are configured per-agent in `appsettings.json` under the `subAgents` array, each with its own model, MCP server endpoints, and execution timeout. Subagents use ephemeral state (no Redis persistence) and cannot spawn further subagents.

Agent routing:
- **Telegram**: Each bot token maps to one agent (configured per channel)
- **WebChat**: User selects agent from available list in the UI
- **Service Bus**: Agent specified in message `agentId` field (falls back to default)

## Projects

| Project                  | Description                                                     |
|--------------------------|-----------------------------------------------------------------|
| `Agent`                  | Composition root, connects to channel and tool MCP servers      |
| `Domain`                 | Core domain logic, agent contracts, channel protocol DTOs       |
| `Infrastructure`         | External service clients (MCP, OpenRouter, push notifications)  |
| `McpServerLibrary`       | MCP server for torrent search, downloads, and file organization |
| `McpServerVault`          | MCP server for text/markdown file inspection and editing        |
| `McpServerWebSearch`     | MCP server for web search and browsing via Camoufox             |
| `McpServerIdealista`     | MCP server for Idealista real estate property search            |
| `McpChannelSignalR`      | MCP channel server for WebChat (SignalR hub, streaming, push)   |
| `McpChannelTelegram`     | MCP channel server for Telegram (multi-bot, approvals)          |
| `McpChannelServiceBus`   | MCP channel server for Azure Service Bus (queues)               |
| `WebChat`                | Blazor WebAssembly host server for browser-based chat           |
| `WebChat.Client`         | Blazor WebAssembly client with chat UI and SignalR integration  |
| `Observability`          | Metrics collector, REST API, SignalR hub — serves the Dashboard |
| `Dashboard.Client`       | Blazor WebAssembly observability dashboard (PWA)                |
| `DockerCompose`          | Docker Compose configuration for the full stack                 |
| `Tests`                  | Unit and integration tests                                      |

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Docker](https://www.docker.com/) and Docker Compose
- [OpenRouter API Key](https://openrouter.ai/)
- [Telegram Bot Token](https://core.telegram.org/bots#creating-a-new-bot) (one per agent)

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
JACKETT__APIKEY=your_jackett_api_key
QBITTORRENT__USERNAME=admin
QBITTORRENT__PASSWORD=your_password

# Agent definitions (array)
AGENTS__0__ID=jack
AGENTS__0__NAME=Jack
AGENTS__0__MODEL=z-ai/glm-5.1
AGENTS__0__MCPSERVERENDPOINTS__0=http://mcp-library:8080/mcp
AGENTS__0__MCPSERVERENDPOINTS__1=http://mcp-websearch:8080/mcp
AGENTS__0__ENABLEDFEATURES__0=filesystem.glob
AGENTS__0__ENABLEDFEATURES__1=filesystem.move
AGENTS__0__WHITELISTPATTERNS__0=domain:filesystem:*
AGENTS__0__WHITELISTPATTERNS__1=mcp:mcp-library:*
AGENTS__0__WHITELISTPATTERNS__2=mcp:mcp-websearch:*
AGENTS__0__CUSTOMINSTRUCTIONS=You are Jack, a media library assistant...

# Subagent definitions (per-agent, optional)
SUBAGENTS__0__ID=jonas-worker
SUBAGENTS__0__NAME=Jonas Worker
SUBAGENTS__0__DESCRIPTION=A worker subagent with the same toolset as Jonas
SUBAGENTS__0__MODEL=z-ai/glm-5.1
SUBAGENTS__0__MCPSERVERENDPOINTS__0=http://mcp-vault:8080/mcp
SUBAGENTS__0__MAXEXECUTIONSECONDS=600

# Channel endpoints (agent connects to these)
CHANNELENDPOINTS__0__CHANNELID=signalr
CHANNELENDPOINTS__0__ENDPOINT=http://mcp-channel-signalr:8080/mcp
CHANNELENDPOINTS__1__CHANNELID=telegram
CHANNELENDPOINTS__1__ENDPOINT=http://mcp-channel-telegram:8080/mcp
CHANNELENDPOINTS__2__CHANNELID=servicebus
CHANNELENDPOINTS__2__ENDPOINT=http://mcp-channel-servicebus:8080/mcp

# Telegram channel (one bot per agent)
BOTS__0__AGENTID=jack
BOTS__0__BOTTOKEN=your_telegram_bot_token_for_jack
ALLOWEDUSERNAMES__0=your_telegram_username

# SignalR channel
AGENTS__0__ID=jack
AGENTS__0__NAME=Jack
AGENTS__0__DESCRIPTION=General assistant

# Service Bus channel (optional)
SERVICEBUSCONNECTIONSTRING=Endpoint=sb://yournamespace.servicebus.windows.net/;SharedAccessKeyName=...
```

### 3. Run with Docker Compose

```bash
cd DockerCompose

# Linux / WSL
docker compose -f docker-compose.yml -f docker-compose.override.linux.yml -p jackbot up -d --build

# Windows
docker compose -f docker-compose.yml -f docker-compose.override.windows.yml -p jackbot up -d --build
```

## Services & Ports

| Service                  | Port | Description                    |
|--------------------------|------|--------------------------------|
| WebChat                  | 5001 | Browser-based chat interface   |
| Dashboard                | 5003 | Observability dashboard (PWA)  |
| Redis                    | 6379 | Conversation state persistence |
| qBittorrent              | 8001 | Torrent client WebUI           |
| FileBrowser              | 8002 | File management WebUI          |
| Jackett                  | 8003 | Torrent indexer proxy          |
| MCP Library              | 6001 | Library MCP server             |
| MCP Vault                | 6002 | Document vault MCP server      |
| MCP WebSearch            | 6003 | Web search MCP server          |
| MCP Idealista            | 6005 | Idealista property MCP server  |
| MCP Channel SignalR      | 6010 | WebChat channel server         |
| MCP Channel Telegram     | 6011 | Telegram channel server        |
| MCP Channel ServiceBus   | 6012 | ServiceBus channel server      |
| Camoufox                 | 9377 | Anti-detect browser (WebSocket)|

## Usage

### WebChat Interface

Access the browser-based chat at `http://localhost:5001` after starting the Docker Compose stack. In production, connect through Caddy (port 443) which routes `/hubs/*` to the SignalR channel server.

Features:
- **Real-time streaming** - Messages stream as they're generated with automatic reconnection
- **Topic management** - Organize conversations into topics with server-side persistence
- **Multi-agent selection** - Switch between available agents from the UI
- **User identity selection** - Switch between configured user identities with persistent avatars
- **Stream resumption** - Reconnects automatically and resumes from where you left off
- **Push notifications** - Browser push notifications when responses complete (VAPID-based)

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

### Observability Dashboard

Access at `http://localhost:5003/dashboard/` (direct) or `https://yourdomain/dashboard/` (via Caddy). Installable as a PWA.

The dashboard provides operational visibility into agent behavior:

- **Overview** — KPI cards (tokens, cost, tool calls, errors), service health grid, recent activity feed
- **Tokens** — Token usage time-series, cost breakdown, per-user and per-model tables
- **Tools** — Tool call frequency, success/failure rates, average duration
- **Errors** — Error list with type, service, and message details
- **Schedules** — Schedule execution history with duration and success/failure status
- **Memory** — Memory extraction, recall, and dreaming analytics

Data flows via Redis Pub/Sub: all services emit metric events through `IMetricsPublisher`, the Observability collector aggregates them into Redis, and the dashboard reads via REST API with live updates via SignalR.

### Telegram Interface

Each agent gets its own Telegram bot. Configure bot tokens in the Telegram channel's environment:

```env
BOTS__0__AGENTID=jack
BOTS__0__BOTTOKEN=your_jack_bot_token
BOTS__1__AGENTID=jonas
BOTS__1__BOTTOKEN=your_jonas_bot_token
ALLOWEDUSERNAMES__0=your_telegram_username
```

### Service Bus Interface

Azure Service Bus integration for external system connectivity. The channel listens for prompts on a queue and writes responses to another queue.

**Prompt Message Format** (sent to prompt queue):
```json
{
  "correlationId": "unique-request-id",
  "agentId": "jack",
  "prompt": "Your question or command here",
  "sender": "external-system-id"
}
```

**Response Message Format** (written to response queue):
```json
{
  "correlationId": "unique-request-id",
  "content": "Agent's response text"
}
```

### Tool Approval

When the agent wants to execute a tool:
- **Approve** - Allow the tool to run once
- **Always** - Auto-approve this tool for the session
- **Reject** - Block the tool execution

Configure permanent auto-approvals using glob patterns:

```json
{
  "whitelistPatterns": [
    "mcp:mcp-library:*",
    "domain:filesystem:*",
    "domain:memory:*"
  ]
}
```

Pattern format: `mcp:<server>:<tool>` or `domain:<feature>:<tool>` with `*` wildcard support.

### Chat Commands

| Command   | Description                                           |
|-----------|-------------------------------------------------------|
| `/cancel` | Cancel current operation (keeps conversation history) |
| `/clear`  | Clear conversation and wipe thread history from Redis |

## Persistence

The agent uses Redis to persist conversation history and memory across restarts:

- **Chat History** - All messages are stored with a 30-day expiry
- **Thread State** - Each chat thread is identified by `agent-key:{agentId}:{conversationId}`
- **Download Tracking** - Use `ResubscribeDownloads` tool to resume tracking downloads after restart
- **Memory Storage** - Proactively extracted memories from windowed conversation context, stored in Redis with vector search for semantic recall; periodic dreaming consolidates and prunes
- **Push Subscriptions** - Browser push notification subscriptions stored in Redis per space
- **Metrics** - Token usage, tool calls, errors, and schedule executions stored as Redis sorted sets and hashes with 30-day TTL
- **Service Health** - Heartbeat-based health tracking with 60-second TTL keys in Redis

## License

[CC BY-NC-ND 4.0](LICENSE) - Attribution-NonCommercial-NoDerivatives 4.0 International
