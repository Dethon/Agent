# External Integrations

## LLM Provider

### OpenRouter
- **Client**: `Infrastructure/Agents/ChatClients/OpenRouterChatClient.cs`
- **Config**: `OpenRouterConfig` record (ApiUrl, ApiKey, Model)
- **Features**:
  - Streaming responses
  - Reasoning/thinking tokens support
  - Tool calling with function definitions
- **Embedding**: `OpenRouterEmbeddingService` for vector embeddings

## Messaging Platforms

### Telegram Bot API
- **Client**: `Infrastructure/Clients/Messaging/Telegram/TelegramChatClient.cs`
- **Features**:
  - Forum topics (threads)
  - Message polling
  - Inline keyboard for tool approval
  - Multi-bot support (one per agent)
- **Config**: Per-agent `TelegramBotToken` in agent definition

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

## Data Storage

### Redis Stack
- **Connection**: `StackExchange.Redis.IConnectionMultiplexer`
- **Uses**:
  - **Thread State**: `RedisThreadStateStore` - Chat history persistence
  - **Schedules**: `RedisScheduleStore` - Scheduled task storage
  - **Memory**: `RedisStackMemoryStore` - Vector memory with search
    - HNSW index for semantic search
    - 1536-dimension embeddings (OpenAI compatible)
    - Category and tag filtering

## Download Services

### qBittorrent
- **Client**: `Infrastructure/Clients/Torrent/QBittorrentDownloadClient.cs`
- **Features**:
  - Torrent download management
  - Progress tracking
  - Category-based organization

### Jackett
- **Client**: `Infrastructure/Clients/Torrent/JackettSearchClient.cs`
- **Purpose**: Unified torrent search across multiple trackers

## Web Services

### Brave Search API
- **Client**: `Infrastructure/Clients/BraveSearchClient.cs`
- **Purpose**: Web search functionality for agents

### Playwright Browser
- **Client**: `Infrastructure/Clients/Browser/PlaywrightWebBrowser.cs`
- **Features**:
  - Page navigation
  - Element interaction
  - Content extraction
  - Modal dismissal (`ModalDismisser`)
  - CAPTCHA solving integration (`CapSolverClient`)

### Idealista
- **Client**: `Infrastructure/Clients/IdealistaClient.cs`
- **Purpose**: Real estate property search (Spain)

## MCP Server Communication

### Transport
- HTTP client transport to MCP server endpoints
- Polly retry policies for resilience

### Features
- Tool discovery and invocation
- Prompt loading from servers
- Resource subscription and updates
- Sampling handler for nested LLM calls

### Server Endpoints (Configurable)
Each agent definition specifies `McpServerEndpoints[]`:
- Library server (downloads, files)
- Text server (file operations)
- WebSearch server (browser, search)
- Memory server (vector store)
- Idealista server (real estate)
- CommandRunner server (shell commands)
