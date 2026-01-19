# External Integrations

**Analysis Date:** 2026-01-19

## APIs & External Services

**AI/LLM:**
- OpenRouter - LLM provider for chat completions and embeddings
  - SDK/Client: `Infrastructure/Agents/ChatClients/OpenRouterChatClient.cs`
  - Embedding: `Infrastructure/Memory/OpenRouterEmbeddingService.cs`
  - Auth: `openRouter:apiKey` in appsettings
  - Endpoint: `https://openrouter.ai/api/v1/`
  - Uses OpenAI-compatible API via Microsoft.Extensions.AI.OpenAI

**Messaging:**
- Telegram Bot API - Primary chat interface
  - SDK/Client: Telegram.Bot 22.8.1 via `Infrastructure/Clients/Messaging/TelegramChatClient.cs`
  - Auth: Per-agent bot tokens in `agents[].telegramBotToken`
  - Features: Long polling, forum topic management, inline keyboard approvals

**Search:**
- Brave Search API - Web search
  - SDK/Client: `Infrastructure/Clients/BraveSearchClient.cs`
  - Auth: `BraveSearch:ApiKey` in McpServerWebSearch appsettings
  - Endpoint: `https://api.search.brave.com/res/v1/`

**Torrent:**
- Jackett - Torrent indexer aggregator
  - SDK/Client: `Infrastructure/Clients/Torrent/JackettSearchClient.cs`
  - Auth: `jackett:apiKey`
  - Endpoint: `http://jackett:9117/api/v2.0/` (Docker internal)
  - Protocol: Torznab XML API

- qBittorrent - Torrent download client
  - SDK/Client: `Infrastructure/Clients/Torrent/QBittorrentDownloadClient.cs`
  - Auth: `qBitTorrent:userName`, `qBitTorrent:password`
  - Endpoint: `http://qbittorrent:8001/api/v2/` (Docker internal)
  - Uses cookie-based session authentication

**Real Estate:**
- Idealista API - Property search in Spain, Italy, Portugal
  - SDK/Client: `Infrastructure/Clients/IdealistaClient.cs`
  - Auth: OAuth2 client credentials (`Idealista:ApiKey`, `Idealista:ApiSecret`)
  - Endpoint: `https://api.idealista.com/`

**CAPTCHA:**
- CapSolver - DataDome CAPTCHA solving service
  - SDK/Client: `Infrastructure/Clients/Browser/CapSolverClient.cs`
  - Auth: `CapSolver:ApiKey`
  - Used by: PlaywrightWebBrowser for protected sites

## Data Storage

**Databases:**
- Redis Stack (redis/redis-stack-server)
  - Connection: `redis:connectionString` (e.g., `redis:6379`)
  - Client: StackExchange.Redis + NRedisStack
  - Uses:
    - Chat history persistence (`RedisThreadStateStore.cs`)
    - Chat message caching (`RedisChatMessageStore.cs`)
    - Vector memory storage (`RedisStackMemoryStore.cs`)
    - WebChat topic metadata
  - Key patterns:
    - `agent-key:{chatId}:{threadId}` - Conversation state (30-day expiry)
    - `memory:{userId}:{memoryId}` - Memory entries (365-day expiry)
    - `memory:profile:{userId}` - Personality profiles
    - `topic:{topicId}` - WebChat topic metadata
  - Vector search: RediSearch with HNSW algorithm, COSINE distance, 1536 dimensions

**File Storage:**
- Local filesystem only
  - `IFileSystemClient` via `LocalFileSystemClient.cs`
  - Media library paths configurable via `baseLibraryPath`, `downloadLocation`
  - Docker volumes mount host paths into containers

**Caching:**
- Microsoft.Extensions.Caching.Memory for in-memory caching
- Used in MCP servers for temporary state

## Authentication & Identity

**Auth Provider:**
- Custom per-interface approach (no central identity provider)

**Telegram:**
- Username whitelist: `telegram:allowedUserNames[]`
- Bot token hash maps to agent definition

**WebChat:**
- No authentication (session-based via SignalR connection)
- Topic ownership tracked via topicId

**External APIs:**
- OpenRouter: API key in Authorization header
- Brave Search: `X-Subscription-Token` header
- Idealista: OAuth2 Bearer token (auto-refreshed)
- Jackett/qBittorrent: API key / session cookies

## Monitoring & Observability

**Error Tracking:**
- None (standard logging only)

**Logs:**
- Microsoft.Extensions.Logging
- Configurable log levels in appsettings.json
- Docker container logs with size limits (5MB max, 3 files)

## CI/CD & Deployment

**Hosting:**
- Docker Compose on self-hosted Linux server
- Services: redis, qbittorrent, filebrowser, jackett, plex, mcp-*, agent, webui

**CI Pipeline:**
- None detected in repository

**Container Images:**
- Base: `mcr.microsoft.com/dotnet/aspnet:10.0` (runtime)
- Build: `mcr.microsoft.com/dotnet/sdk:10.0` (multi-stage)
- Custom images built via `docker-compose.yml`

## Environment Configuration

**Required env vars (production):**
```
OPENROUTER_APIKEY
TELEGRAM_BOTTOKEN_{AGENT}
BRAVE_APIKEY
JACKETT_APIKEY
QBITTORRENT_USER
QBITTORRENT_PASSWORD
REDISCONNECTIONSTRING
DATA_PATH           # Host path for media/downloads
VAULT_PATH          # Host path for text files
REPOSITORY_PATH     # Path to source for Docker builds
PUID / PGID         # Container user/group IDs
```

**Secrets location:**
- Development: .NET User Secrets (UserSecretsId per project)
- Production: Docker `.env` file in DockerCompose directory

## Webhooks & Callbacks

**Incoming:**
- SignalR Hub (`Agent/Hubs/ChatHub.cs`) - WebChat real-time messaging
  - Methods: GetAgents, StartSession, SendMessage, ResumeStream, SaveTopic, etc.

**Outgoing:**
- MCP resource notifications (`notifications/resources/updated`)
- Telegram message sends (async response to prompts)
- SignalR notifications to WebChat clients (topic changes, stream changes)

## MCP Server Endpoints

**Internal network (Docker):**
- `http://mcp-library:8080/sse` - Torrent search, downloads, file organization
- `http://mcp-text:8080/sse` - Text/markdown file inspection and editing
- `http://mcp-websearch:8080/sse` - Web search and content fetching
- `http://mcp-memory:8080/sse` - Vector-based memory storage
- `http://mcp-idealista:8080/sse` - Real estate search
- `http://mcp-commandrunner:8080/sse` - CLI command execution (if deployed)

**Protocol:**
- Server-Sent Events (SSE) transport
- JSON-RPC 2.0 over SSE

## Browser Automation

**Playwright:**
- Implementation: `Infrastructure/Clients/Browser/PlaywrightWebBrowser.cs`
- Mode: Headless Chromium with stealth scripts
- Features:
  - Session management across navigations
  - Modal auto-dismissal
  - Scroll-to-load for lazy content
  - DOM stability waiting
  - CAPTCHA detection and solving (via CapSolver)
- Optional CDP endpoint for remote browser connection

---

*Integration audit: 2026-01-19*
