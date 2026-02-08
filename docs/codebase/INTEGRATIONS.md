# External Integrations

## LLM Provider

### OpenRouter
- **Client**: `Infrastructure/Agents/ChatClients/OpenRouterChatClient.cs`
- **Config**: `OpenRouterConfig` record (ApiUrl, ApiKey, Model)
- **Features**:
  - Streaming responses via SSE with OpenAI-compatible protocol
  - Reasoning/thinking tokens support (custom `ReasoningHandler` intercepts SSE stream)
  - Tool calling with function definitions
  - Sender and timestamp injection into user messages
- **Adapter**: Uses `Microsoft.Extensions.AI.OpenAI` to bridge OpenRouter's OpenAI-compatible API to `IChatClient`
- **Embedding**: `OpenRouterEmbeddingService` for vector embeddings via `/embeddings` endpoint
  - Default model: `openai/text-embedding-3-small`
  - Supports single and batch embedding generation

## Messaging Platforms

### Telegram Bot API
- **Client**: `Infrastructure/Clients/Messaging/Telegram/TelegramChatClient.cs`
- **Features**:
  - Forum topics (threads)
  - Message polling
  - Inline keyboard for tool approval
  - Multi-bot support (one per agent)
- **Config**: Per-agent `TelegramBotToken` in agent definition
- **Allowed Users**: Configurable `AllowedUserNames` whitelist

### Azure Service Bus
- **Client**: `Infrastructure/Clients/Messaging/ServiceBus/ServiceBusChatMessengerClient.cs`
- **Components**:
  - `ServiceBusProcessorHost` - Background service receiving messages from prompt queue
  - `ServiceBusMessageParser` - Validates required fields and agent IDs, returns `ParseResult` discriminated union
  - `ServiceBusPromptReceiver` - Maps correlationId via `ServiceBusConversationMapper`, notifies WebUI, queues to channel
  - `ServiceBusResponseHandler` - Collects completed responses and routes to response writer
  - `ServiceBusResponseWriter` - Queue writes with Polly exponential backoff retry (3 retries, transient + timeout)
  - `ServiceBusConversationMapper` - Redis-backed correlationId to chatId/threadId/topicId mapping with 30-day TTL
- **Composite Client**: `CompositeChatMessengerClient` wraps WebChat + ServiceBus clients when Service Bus is enabled
  - Uses `IMessageSourceRouter` to route responses to correct clients based on `MessageSource`
  - Merges prompt streams from all child clients
  - Broadcasts response updates through per-client channels
- **Message Contract**:
  - Prompt: `{ correlationId, agentId, prompt, sender }` (all required, sender optional defaults to empty)
  - Response: `{ correlationId, agentId, response, completedAt }`
- **Validation**: Strict agent ID validation against configured agents; invalid messages dead-lettered
- **Purpose**: External system integration for chat prompts with request/response correlation
- **WebUI Integration**: Service Bus prompts appear as topics in WebChat UI via INotifier

### SignalR (WebChat)
- **Hub**: `Agent/Hubs/ChatHub.cs`
- **Client**: `Infrastructure/Clients/Messaging/WebChat/WebChatMessengerClient.cs`
- **Features**:
  - Streaming message delivery
  - Tool approval UI
  - Topic management
  - Session persistence
- **Config**: KeepAlive 15s, ClientTimeout 5min

### CLI
- **Client**: `Infrastructure/Clients/Messaging/Cli/CliChatMessengerClient.cs`
- **OneShot**: `Infrastructure/Clients/Messaging/Cli/OneShotChatMessengerClient.cs`
- **Features**:
  - Terminal.Gui-based interactive TUI
  - One-shot mode for single prompt/response via `--prompt` flag
  - Reasoning output display via `--reasoning` flag

## Data Storage

### Redis Stack
- **Connection**: `StackExchange.Redis.IConnectionMultiplexer`
- **Default**: `redis:6379` (Docker) / configurable via `RedisConnectionString`
- **Uses**:
  - **Thread State**: `RedisThreadStateStore` - Chat history persistence with configurable TTL (default 30 days)
  - **Schedules**: `RedisScheduleStore` - Scheduled task storage with sorted sets for due-time queries
  - **Topics**: Topic metadata persistence for WebChat conversations
  - **Service Bus Mappings**: `ServiceBusConversationMapper` - correlationId to topic mapping with 30-day TTL
  - **Memory**: `RedisStackMemoryStore` - Vector memory with semantic search
    - HNSW index for cosine similarity search
    - 1536-dimension embeddings (OpenAI text-embedding-3-small compatible)
    - Category and tag filtering
    - 365-day default expiry per memory entry
- **Docker Image**: `redis/redis-stack-server:latest`

## Download Services

### qBittorrent
- **Client**: `Infrastructure/Clients/Torrent/QBittorrentDownloadClient.cs`
- **Features**:
  - Torrent download management
  - Progress tracking
  - Category-based organization
  - Cookie-based authentication
- **Config**: ApiUrl, UserName, Password
- **Resilience**: Polly retry (3 attempts, 2s exponential backoff, 10s timeout)
- **Docker Image**: `qbittorrentofficial/qbittorrent-nox:5.1.2-2`

### Jackett
- **Client**: `Infrastructure/Clients/Torrent/JackettSearchClient.cs`
- **Purpose**: Unified torrent search across multiple trackers
- **Config**: ApiUrl, ApiKey
- **Resilience**: Polly retry (3 attempts, 1s exponential backoff, 20s timeout)
- **Docker Image**: `lscr.io/linuxserver/jackett:0.24.306`

## Web Services

### Brave Search API
- **Client**: `Infrastructure/Clients/BraveSearchClient.cs`
- **Purpose**: Web search functionality for agents
- **Config**: ApiUrl (`https://api.search.brave.com/res/v1/`), ApiKey
- **Resilience**: Polly retry (2 attempts, 1s exponential backoff, 15s timeout)

### Playwright Browser
- **Client**: `Infrastructure/Clients/Browser/PlaywrightWebBrowser.cs`
- **Features**:
  - Page navigation and content extraction
  - Element interaction and clicking
  - Page inspection
  - Modal dismissal (`ModalDismisser`)
  - CAPTCHA solving integration (`CapSolverClient`)
  - Session management (`BrowserSessionManager`)
- **Runtime**: Chromium installed in Docker image via `npx playwright install chromium`
- **Config**: `PLAYWRIGHT_BROWSERS_PATH=/ms-playwright` in container

### CapSolver
- **Client**: `Infrastructure/Clients/Browser/CapSolverClient.cs`
- **Purpose**: Automated CAPTCHA solving for browser automation
- **Config**: ApiKey (optional - browser works without it)

### Idealista
- **Client**: `Infrastructure/Clients/IdealistaClient.cs`
- **Purpose**: Real estate property search (Spain)
- **Config**: ApiUrl (`https://api.idealista.com/`), ApiKey, ApiSecret
- **Resilience**: Polly retry (2 attempts, 1s exponential backoff, 15s timeout)

## MCP Servers

All MCP servers use HTTP/SSE transport via `ModelContextProtocol.AspNetCore` and expose tools, prompts, and optionally resources. Each server includes a global `CallToolFilter` for error handling.

### McpServerLibrary (port 6001)
- **Purpose**: Media library management - downloads, file organization, search
- **Tools**:
  - `McpFileSearchTool` - Search for content across trackers via Jackett
  - `McpFileDownloadTool` - Download torrents via qBittorrent
  - `McpGetDownloadStatusTool` - Check download progress
  - `McpCleanupDownloadTool` - Clean up completed downloads
  - `McpContentRecommendationTool` - Content recommendations
  - `McpResubscribeDownloadsTool` - Re-subscribe to download monitoring
  - `McpGlobFilesTool` - Glob file pattern matching in library
  - `McpMoveTool` - Move/rename files in library
- **Resources**: `McpDownloadResource` with subscription support for download status updates
- **Prompts**: `McpSystemPrompt` - System prompt for library agent behavior
- **Dependencies**: Jackett, qBittorrent, local filesystem

### McpServerText (port 6002)
- **Purpose**: Text file operations on a vault directory
- **Tools**:
  - `McpTextGlobFilesTool` - Discover files by glob pattern
  - `McpTextSearchTool` - Search content within text files
  - `McpTextReadTool` - Read file contents
  - `McpTextEditTool` - Edit file contents
  - `McpTextCreateTool` - Create new text files
  - `McpMoveTool` - Move/rename files
  - `McpRemoveTool` - Move files/directories to trash
- **Prompts**: `McpSystemPrompt`
- **Config**: VaultPath, AllowedExtensions (.md, .txt, .json, .yaml, .yml, .toml, .ini, .conf, .cfg)

### McpServerWebSearch (port 6003)
- **Purpose**: Web search and browser-based browsing
- **Tools**:
  - `McpWebSearchTool` - Web search via Brave Search API
  - `McpWebBrowseTool` - Navigate and extract page content
  - `McpWebClickTool` - Click elements on web pages
  - `McpWebInspectTool` - Inspect page structure and elements
- **Prompts**: `McpSystemPrompt`
- **Dependencies**: Brave Search API, Playwright (Chromium), CapSolver (optional)
- **Docker**: Custom multi-stage build with Playwright browser pre-installed

### McpServerMemory (port 6004)
- **Purpose**: Semantic vector memory store and recall
- **Tools**:
  - `McpMemoryStoreTool` - Store a memory with embedding
  - `McpMemoryRecallTool` - Semantic search for relevant memories
  - `McpMemoryForgetTool` - Delete a memory
  - `McpMemoryReflectTool` - Reflect on and update existing memories
  - `McpMemoryListTool` - List stored memories
- **Prompts**: `McpSystemPrompt`
- **Dependencies**: Redis Stack (vector search), OpenRouter (embeddings)

### McpServerIdealista (port 6005)
- **Purpose**: Real estate property search on Idealista
- **Tools**:
  - `McpPropertySearchTool` - Search properties with filters
- **Prompts**: `McpSystemPrompt`
- **Dependencies**: Idealista API

### McpServerCommandRunner (no docker-compose entry)
- **Purpose**: Shell command execution
- **Tools**:
  - `McpRunCommandTool` - Execute shell commands
  - `McpGetCliPlatformTool` - Detect available shell platform
- **Shell Support**: Bash, Sh, PowerShell, Cmd (auto-detected via `AvailableShell`)
- **Config**: WorkingDirectory

## MCP Client Integration

### Transport
- HTTP/SSE client transport to MCP server endpoints
- Polly retry policies for connection resilience

### Features
- Tool discovery and invocation via `McpClientManager`
- Prompt loading from servers (system prompts)
- Resource subscription and real-time updates via `McpResourceManager` / `McpSubscriptionManager`
- Sampling handler for nested LLM calls (`McpSamplingHandler`)
- Per-thread session management with MCP clients (`ThreadSession`)

### Server Endpoints (Configurable)
Each agent definition specifies `McpServerEndpoints[]` - array of SSE endpoint URLs:
```json
{
  "mcpServerEndpoints": [
    "http://mcp-library:8080/sse",
    "http://mcp-websearch:8080/sse"
  ]
}
```

## Docker/Container Configuration

### docker-compose.yml Services

| Service | Image | Port | Purpose |
|---------|-------|------|---------|
| redis | redis/redis-stack-server:latest | 6379 | State storage and vector search |
| qbittorrent | qbittorrentofficial/qbittorrent-nox:5.1.2-2 | 8001 | Torrent download client |
| filebrowser | filebrowser/filebrowser:v2 | 8002 | Web-based file management UI |
| jackett | lscr.io/linuxserver/jackett:0.24.306 | 8003 | Torrent tracker search aggregator |
| plex | lscr.io/linuxserver/plex:1.42.1 | host network | Media server |
| mcp-library | mcp-library:latest (built) | 6001 | Library MCP server |
| mcp-text | mcp-text:latest (built) | 6002 | Text MCP server |
| mcp-websearch | mcp-websearch:latest (built) | 6003 | WebSearch MCP server |
| mcp-memory | mcp-memory:latest (built) | 6004 | Memory MCP server |
| mcp-idealista | mcp-idealista:latest (built) | 6005 | Idealista MCP server |
| agent | agent:latest (built) | 5000 | Main agent application |
| webui | webui:latest (built) | 5001 | Blazor WebAssembly chat UI |
| caddy | caddy:2 | 443 | Reverse proxy with auto TLS |
| cloudflared | cloudflare/cloudflared:latest | - | Cloudflare Tunnel for remote access |

### Networking
- Custom Docker network `jackbot` for inter-container communication
- Caddy reverse proxy routes `/hubs/*` to agent, everything else to webui
- Cloudflare Tunnel provides secure external access without port forwarding
- DDNS IP allowlist middleware for access control

### Dev Container
- `.devcontainer/` with Docker-in-Docker (dind) setup
- .NET 10 SDK with Blazor WebAssembly workloads
- Docker CLI + Compose plugin for container management
- Claude Code pre-installed for AI-assisted development
- Shared NuGet cache and User Secrets volumes
