# Architecture

**Analysis Date:** 2026-01-19

## Pattern Overview

**Overall:** Multi-Agent Streaming Architecture with MCP Tool Integration

**Key Characteristics:**
- Clean Architecture with Domain, Infrastructure, and Application layers
- Multi-agent system where agents are configuration-driven, not code-driven
- Reactive streaming pipeline using `IAsyncEnumerable<T>` throughout
- Model Context Protocol (MCP) for tool integration and resource subscriptions
- Multiple chat interface adapters (Telegram, CLI, WebChat) behind common abstraction

## Layers

**Domain Layer:**
- Purpose: Pure business logic, contracts, DTOs, and domain services
- Location: `Domain/`
- Contains: Interfaces (`Contracts/`), DTOs (`DTOs/`), Tools (`Tools/`), Agents (`Agents/`), Prompts (`Prompts/`), Monitors (`Monitor/`)
- Depends on: `Microsoft.Agents.AI.Abstractions`, `FluentResults` (no external infrastructure)
- Used by: Infrastructure, Agent layers

**Infrastructure Layer:**
- Purpose: Implementations of Domain interfaces, external service clients, state persistence
- Location: `Infrastructure/`
- Contains: Agent implementations (`Agents/`), MCP integration (`Agents/Mcp/`), External clients (`Clients/`), State managers (`StateManagers/`), CLI components (`CliGui/`), Memory services (`Memory/`)
- Depends on: Domain layer, external packages (Redis, Telegram.Bot, Playwright, MCP SDK)
- Used by: Agent layer

**Application Layer (Agent):**
- Purpose: Composition root, DI configuration, entry points, SignalR hubs
- Location: `Agent/`
- Contains: DI modules (`Modules/`), Settings (`Settings/`), SignalR Hubs (`Hubs/`), Hosted services (`App/`)
- Depends on: Infrastructure, Domain layers
- Used by: External callers (runtime entry point)

**MCP Server Modules:**
- Purpose: Standalone MCP servers exposing domain tools via HTTP/SSE
- Locations: `McpServerLibrary/`, `McpServerText/`, `McpServerWebSearch/`, `McpServerMemory/`, `McpServerIdealista/`, `McpServerCommandRunner/`
- Contains: MCP tool wrappers (`McpTools/`), MCP prompts (`McpPrompts/`), Resource subscriptions
- Depends on: Domain layer (tools), Infrastructure layer (clients)
- Used by: Agent via MCP protocol over HTTP

**WebChat Client:**
- Purpose: Blazor WebAssembly browser-based chat interface
- Location: `WebChat.Client/`
- Contains: Services (`Services/`), Contracts (`Contracts/`), Pages (`Pages/`), Components (`Components/`)
- Depends on: SignalR client, Domain DTOs (shared via WebChat)
- Used by: End users via browser

## Data Flow

**Chat Message Processing Pipeline:**

1. `IChatMessengerClient.ReadPrompts()` yields incoming `ChatPrompt` from adapter (Telegram/CLI/WebChat)
2. `ChatMonitor.Monitor()` groups prompts by `AgentKey` using `GroupByStreaming()`
3. For each group, `ProcessChatThread()` creates/retrieves agent via `IAgentFactory.Create()`
4. Agent runs prompt through `McpAgent.RunStreamingAsync()` yielding `AgentRunResponseUpdate`
5. Updates merged back and processed via `IChatMessengerClient.ProcessResponseStreamAsync()`
6. Responses sent to user through respective adapter

```
ChatPrompt → GroupByStreaming(AgentKey) → ProcessChatThread → Agent.RunStreamingAsync
                                                                    ↓
User ← ProcessResponseStreamAsync ← Merge ← AgentRunResponseUpdate stream
```

**MCP Tool Execution Flow:**

1. `McpAgent` creates `ThreadSession` with `McpClientManager` connecting to MCP server endpoints
2. `McpClientManager.CreateAsync()` loads tools and prompts from each MCP server
3. During agent run, LLM requests tool calls which are executed via MCP protocol
4. `ToolApprovalChatClient` intercepts tool calls for user approval (if not whitelisted)
5. Tool results stream back to LLM for continuation

**State Management:**
- `ChatThreadResolver` maintains in-memory `ChatThreadContext` per `AgentKey`
- `RedisThreadStateStore` persists chat messages to Redis with 30-day expiry
- `RedisChatMessageStore` integrates with `Microsoft.Agents.AI` for automatic persistence
- Key format: `agent-key:{chatId}:{threadId}`

## Key Abstractions

**DisposableAgent:**
- Purpose: Base class for agents that manage async resources
- Examples: `Infrastructure/Agents/McpAgent.cs`
- Pattern: Extends `AIAgent` with `IAsyncDisposable`, manages `ThreadSession` per conversation

**IChatMessengerClient:**
- Purpose: Abstracts different chat interfaces (Telegram, CLI, WebChat)
- Examples: `Infrastructure/Clients/Messaging/TelegramChatClient.cs`, `Infrastructure/Clients/Messaging/CliChatMessengerClient.cs`, `Infrastructure/Clients/Messaging/WebChatMessengerClient.cs`
- Pattern: Yields prompts via `IAsyncEnumerable<ChatPrompt>`, processes responses via streaming

**IToolApprovalHandler:**
- Purpose: User approval for tool execution before running
- Examples: `Infrastructure/Clients/ToolApproval/TelegramToolApprovalHandler.cs`, `Infrastructure/Clients/ToolApproval/CliToolApprovalHandler.cs`, `Infrastructure/Clients/ToolApproval/WebToolApprovalHandler.cs`
- Pattern: Factory pattern via `IToolApprovalHandlerFactory`, results include `Approved`, `ApprovedAndRemember`, `Rejected`, `AutoApproved`

**AgentDefinition:**
- Purpose: Configuration-driven agent specification (name, model, MCP endpoints, whitelist)
- Examples: `Domain/DTOs/AgentDefinition.cs`
- Pattern: Record type loaded from `appsettings.json`, enables multi-agent in single container

**AgentKey:**
- Purpose: Unique identifier for a conversation thread
- Examples: `Domain/Agents/AgentKey.cs`
- Pattern: Composite key of `ChatId`, `ThreadId`, and optional `BotTokenHash`

## Entry Points

**Agent Application (Main):**
- Location: `Agent/Program.cs`
- Triggers: Command line execution with `--chat` option (Telegram/CLI/Web/OneShot)
- Responsibilities: Build host, configure DI, start `ChatMonitor` hosted service, optionally map SignalR hub

**WebChat Host:**
- Location: `WebChat/Program.cs`
- Triggers: HTTP requests for Blazor WebAssembly static files
- Responsibilities: Serve static files, provide `/api/config` endpoint, fallback to `index.html`

**MCP Servers:**
- Location: `McpServer*/Program.cs`
- Triggers: HTTP SSE connections from MCP clients
- Responsibilities: Register MCP tools/prompts, handle MCP protocol requests

**ChatHub (SignalR):**
- Location: `Agent/Hubs/ChatHub.cs`
- Triggers: SignalR connections from WebChat client
- Responsibilities: Session management, message streaming, topic CRUD, tool approval handling

## Error Handling

**Strategy:** Graceful degradation with logging, streaming errors to client

**Patterns:**
- `WithErrorHandling()` extension wraps agent runs to catch and convert exceptions to `ErrorContent`
- `ChatMonitor` catches exceptions in inner and outer loops, logs errors, continues processing
- Tool execution failures wrapped in `ErrorContent` and streamed back to LLM
- WebChat streams `ChatStreamMessage.Error` field to client for display

## Cross-Cutting Concerns

**Logging:** `Microsoft.Extensions.Logging` via DI, injected via primary constructors

**Validation:** Guard clauses with `ArgumentNullException.ThrowIfNull()`, `ObjectDisposedException.ThrowIf()`

**Authentication:**
- Telegram: Username whitelist in settings, checked per message
- WebChat: Agent ID validation, no user auth (designed for local/trusted use)

**Cancellation:** `CancellationToken` propagated throughout, `ChatThreadContext` provides linked tokens with callback support for cooperative cancellation

---

*Architecture analysis: 2026-01-19*
