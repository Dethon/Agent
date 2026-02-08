# Architecture

## Layered Architecture

```
+------------------+
|      Agent       |  Entry point, DI, SignalR Hub, Background services
+------------------+
         |
         v
+------------------+
|  Infrastructure  |  Implementations, external clients, CLI GUI
+------------------+
         |
         v
+------------------+
|      Domain      |  Contracts, DTOs, pure business logic
+------------------+
```

**Dependency Rule**: Dependencies flow inward only. Agent depends on Infrastructure, Infrastructure depends on Domain. Domain has no project dependencies.

## Key Abstractions

### Agent System

```
IAgentFactory / IScheduleAgentFactory
     |
     v
MultiAgentFactory --> AgentDefinition (config from appsettings.json)
     |                    |
     |                    +-- EnabledFeatures --> IDomainToolRegistry
     |                                               |
     |                                               v
     |                                       DomainToolRegistry
     |                                           |
     |                                           +-- IDomainToolFeature[]
     |                                               (e.g. SchedulingToolFeature)
     |
     v
DisposableAgent (base, extends AIAgent) <-- McpAgent (implementation)
     |
     +-- ChatClientAgent (inner, Microsoft.Agents.AI)
     |       |
     |       +-- RedisChatMessageStore (ChatHistoryProvider, persists to Redis)
     |
     +-- ConcurrentDictionary<AgentSession, ThreadSession>
              |
              +-- McpClientManager (MCP server connections, tools, prompts)
              +-- McpResourceManager (resource subscriptions, update notifications)
```

### Agent Definition

Configured in `appsettings.json`:
```json
{
  "agents": [{
    "id": "media-agent",
    "name": "Media Library",
    "model": "anthropic/claude-3.5-sonnet",
    "mcpServerEndpoints": ["http://library:5100/mcp", "http://text:5101/mcp"],
    "whitelistPatterns": ["library/*", "text/*"],
    "enabledFeatures": ["scheduling"],
    "customInstructions": "Optional per-agent system prompt text",
    "telegramBotToken": "optional-bot-token"
  }]
}
```

### Chat Interface Modes

The application supports four interface modes, selected via `--chat` CLI option (or `--prompt`/`-p` for one-shot):

| Mode | Flag | Description |
|------|------|-------------|
| Web | `--chat web` (default) | SignalR hub + WebChat Blazor client |
| CLI | `--chat cli` | Terminal.Gui interactive TUI |
| Telegram | `--chat telegram` | Telegram Bot API |
| OneShot | `--prompt "..."` | Single prompt, stdout response, then exit |

Each mode registers a different `IChatMessengerClient` and `IToolApprovalHandlerFactory` implementation.

### Message Pipeline

```
ChatPrompt (from Telegram/WebChat/CLI/ServiceBus)
     |
     v
IChatMessengerClient.ReadPrompts()
     |  (CompositeChatMessengerClient merges streams when multiple clients active)
     |
     v
ChatMonitor (Domain/Monitor/ChatMonitor.cs)
     |  Groups prompts by thread via GroupByStreaming + CreateTopicIfNeededAsync
     |  Parses chat commands (/clear, /cancel) via ChatCommandParser
     |
     v
IAgentFactory.Create(agentKey, userId, agentId)
     |
     v
McpAgent.RunStreamingAsync()
     |
     +-- ThreadSession (per-thread, lazy-created)
     |       +-- McpClientManager (HTTP SSE connections to MCP servers)
     |       +-- McpResourceManager (subscriptions + update channel)
     |       +-- McpSamplingHandler (handles MCP sampling requests)
     |
     +-- ToolApprovalChatClient (FunctionInvokingChatClient wrapper)
     |       +-- ToolPatternMatcher (whitelist check)
     |       +-- IToolApprovalHandler (approval UI)
     |
     +-- OpenRouterChatClient (LLM calls via OpenAI SDK + OpenRouter)
     |       +-- ReasoningHandler (intercepts SSE stream for reasoning tokens)
     |
     +-- RedisChatMessageStore (ChatHistoryProvider, persists conversation)
     |
     v
AgentResponseUpdate stream (merged with resource notification updates)
     |
     v
IChatMessengerClient.ProcessResponseStreamAsync()
     |  (CompositeChatMessengerClient routes via IMessageSourceRouter)
     |  WebUI always receives; source-matching client also receives
```

### Message Source Routing

```
MessageSource enum: WebUi | ServiceBus | Telegram | Cli

IMessageSourceRouter.GetClientsForSource()
     |
     +-- Always includes WebUI client (dashboard visibility)
     +-- Also includes client matching the message source
```

### Tool Approval Flow

```
Tool Call Request (via FunctionInvokingChatClient)
     |
     v
ToolPatternMatcher.IsMatch() or dynamically approved?
     |
     +-- Yes --> NotifyAutoApproved + Execute directly
     |
     +-- No --> IToolApprovalHandler.RequestApprovalAsync()
                     |
                     +-- ToolApprovalResult.Approved --> Execute
                     +-- ToolApprovalResult.ApprovedAndRemember --> Add to dynamic set + Execute
                     +-- ToolApprovalResult.Rejected --> Terminate tool invocation
```

**Approval Handler Implementations:**

| Handler | Interface | Behavior |
|---------|-----------|----------|
| `TelegramToolApprovalHandler` | Telegram inline keyboard | Per-agent bot clients |
| `WebToolApprovalHandler` | SignalR notification | Via WebChatApprovalManager |
| `CliToolApprovalHandler` | Terminal.Gui dialog | Via IToolApprovalUi |
| `AutoToolApprovalHandler` | Always approve | Used in OneShot mode |

### Domain Tool Registry

Domain-level tools (not MCP) are registered through a feature-flag system:

```
IDomainToolFeature (interface)
     |
     +-- SchedulingToolFeature
     |       +-- ScheduleCreateTool
     |       +-- ScheduleListTool
     |       +-- ScheduleDeleteTool
     |
     v
IDomainToolRegistry.GetToolsForFeatures(enabledFeatures)
     |
     v
AIFunction[] injected into McpAgent alongside MCP tools
```

Tools are named with a `domain:{feature}:{tool}` convention (e.g. `domain:scheduling:schedule-create`).

### Thread Management

```
ChatThreadResolver (singleton, Domain)
     |
     +-- ConcurrentDictionary<AgentKey, ChatThreadContext>
     |
     +-- Resolve(key) --> get or create context
     +-- Cancel(key) --> dispose context (cancel running agent)
     +-- ClearAsync(key) --> dispose + delete persisted state
```

`ChatThreadContext` wraps a `CancellationTokenSource` and a completion callback, allowing the ChatMonitor to cancel running agent work or clear conversation history.

## MCP Integration

### Client Side (Agent connects to MCP servers)

```
McpAgent
   |
   v
ThreadSession (created per agent thread)
   |
   +-- McpClientManager
   |        |
   |        +-- McpClient[] (HTTP SSE connections to each server, with Polly retry)
   |        +-- AITool[] (QualifiedMcpTool wrappers, named mcp:{server}:{tool})
   |        +-- string[] (loaded prompts from all servers)
   |
   +-- McpResourceManager
   |        |
   |        +-- McpSubscriptionManager (subscribe/unsubscribe to resources)
   |        +-- ResourceUpdateProcessor (reads resource, runs agent, writes to channel)
   |        +-- Channel<AgentResponseUpdate> (merged into main response stream)
   |
   +-- McpSamplingHandler
            |
            +-- Handles MCP CreateMessage sampling requests
            +-- Routes through the same ChatClientAgent
            +-- Tracks conversations via metadata "tracker" field
```

### Server Side (MCP servers expose tools)

Each MCP server is a standalone ASP.NET process (`WebApplication.CreateBuilder` + `app.MapMcp()`):

```
McpServer (e.g., McpServerLibrary)
   |
   +-- [McpServerTool] McpFileDownloadTool : wraps FileDownloadTool
   +-- [McpServerTool] McpFileSearchTool : wraps FileSearchTool
   +-- [McpResource] McpDownloadResource
   +-- [McpPrompt] McpSystemPrompt
   +-- ResourceSubscriptions/ (monitors downloads, sends notifications)
```

**MCP Servers:**

| Server | Port | Tools | Purpose |
|--------|------|-------|---------|
| McpServerLibrary | 5100 | Download, search, glob, move, cleanup, status, resubscribe, recommendations | Media library management |
| McpServerText | 5101 | Read, create, edit, search, glob, move, remove | Text/file operations |
| McpServerWebSearch | 5102 | Search, browse, click, inspect | Web browsing via Playwright + Brave |
| McpServerMemory | 5103 | Store, recall, list, forget, reflect | Vector memory (Redis + embeddings) |
| McpServerIdealista | 5104 | Property search | Real estate search |
| McpServerCommandRunner | 5105 | Run command, get platform | Shell command execution |

## WebChat Architecture

### Server Side (Agent project)

```
ChatHub (SignalR Hub)
   |
   +-- RegisterUser / GetAgents / ValidateAgent
   +-- StartSession / EndSession / DeleteTopic / SaveTopic
   +-- SendMessage (streaming) / EnqueueMessage (fire-and-forget)
   +-- ResumeStream (reconnection support)
   +-- GetHistory / GetAllTopics / IsProcessing / GetStreamState
   +-- RespondToApprovalAsync / IsApprovalPending / GetPendingApprovalForTopic
   +-- CancelTopic
   |
   v
WebChatMessengerClient (IChatMessengerClient)
   |
   +-- WebChatSessionManager (topic-to-session mapping)
   +-- WebChatStreamManager (streaming buffers per topic)
   +-- WebChatApprovalManager (pending approvals per topic)
   +-- Channel<ChatPrompt> (prompt queue from hub to monitor)
   |
   v
HubNotifier (INotifier) --> HubNotificationAdapter --> IHubContext<ChatHub>
   |
   +-- Broadcasts: TopicChanged, StreamChanged, UserMessage, ToolCalls, ApprovalResolved
```

### Client Side (WebChat.Client, Blazor WebAssembly)

**Redux-like State Management:**

```
Store<TState> (generic, backed by BehaviorSubject<TState>)
   |
   +-- State (current value)
   +-- StateObservable (Rx observable for subscriptions)
   +-- Dispatch(action, reducer) --> produces new state

Dispatcher (central action bus)
   |
   +-- RegisterHandler<TAction>(handler) --> per-action-type handler list
   +-- Dispatch<TAction>(action) --> invokes all registered handlers

Selector<TState, TResult> (memoized state projection)
   |
   +-- Select(state) --> cached result with reference equality check
   +-- Compose() --> chain selectors

RenderCoordinator
   |
   +-- Creates throttled observables (50ms sample interval)
   +-- Used by streaming components for efficient re-rendering

StoreSubscriberComponent (base Blazor component)
   |
   +-- Subscribe<TState, TSelected>() --> auto-dispose Rx subscriptions
   +-- Calls StateHasChanged() on observable emissions
```

**Stores:**

| Store | State | Purpose |
|-------|-------|---------|
| ConnectionStore | Connection status | SignalR hub connection state |
| MessagesStore | Messages by topic | Chat message history |
| TopicsStore | Topics + selected agent/topic | Topic management |
| StreamingStore | Streaming content by topic | Active streaming state |
| ApprovalStore | Pending approvals | Tool approval requests |
| ToastStore | Toast notifications | UI notifications |
| UserIdentityStore | User identity | Selected user ID, available users |

**Effects (side effects triggered by dispatched actions):**

| Effect | Triggers On | Behavior |
|--------|-------------|----------|
| InitializationEffect | Initialize, SelectUser | Connect SignalR, load agents/topics/history, register user |
| AgentSelectionEffect | SelectAgent | Reload topics for new agent, persist selection |
| TopicSelectionEffect | SelectTopic | Load history if needed, resume streams |
| TopicDeleteEffect | DeleteTopic | Cancel processing, remove via hub, cleanup pipeline |
| SendMessageEffect | SendChatMessage | Submit to pipeline, send via hub, handle streaming |
| UserIdentityEffect | LoadUsers | Load user config from JSON, restore saved selection |
| ReconnectionEffect | ConnectionReconnected | Reload topics and resume active streams |
| ConnectionEventDispatcher | Hub lifecycle events | Dispatch connection state changes |
| HubEventDispatcher | Hub push notifications | Dispatch topic/stream/approval/user-message changes |

**Message Pipeline (`IMessagePipeline`):**

```
SubmitUserMessage(topicId, content, senderId)
     --> Generates correlationId, adds user message to store

AccumulateChunk(topicId, messageId, content, reasoning, toolCalls)
     --> Dispatches StreamChunk (skips already-finalized messages)

FinalizeMessage(topicId, messageId)
     --> Moves streaming content into persisted message
     --> Deduplicates via finalization tracking

LoadHistory(topicId, messages)
     --> Bulk-loads history from server

ResumeFromBuffer(result, topicId, currentMessageId)
     --> Rebuilds state from stream buffer after reconnection

ClearTopic(topicId)
     --> Cleans up finalization tracking

WasSentByThisClient(correlationId)
     --> Multi-browser deduplication check
```

## CLI Architecture

```
Agent --chat cli
   |
   v
TerminalGuiAdapter (Terminal.Gui TUI)
   |
   +-- ChatListDataSource (scrollable message list)
   +-- ApprovalDialog (tool approval modal)
   +-- ThinkingIndicator (animated spinner)
   +-- CollapseStateManager (collapsible sections)
   |
   v
CliChatMessageRouter
   |
   +-- Derives ChatId/ThreadId from agent name hash (XxHash32)
   +-- Input queue (BlockingCollection<string>)
   +-- ChatCommandHandler (/clear, /cancel commands)
   +-- ChatHistoryMapper + ChatMessageFormatter (display formatting)
   |
   v
CliChatMessengerClient (IChatMessengerClient)
   |
   +-- RestoreHistory on first ReadPrompts call (from Redis)
   +-- Routes AgentResponseUpdate stream to terminal display
```

**OneShot mode** (`--prompt "..."`) uses `OneShotChatMessengerClient`:
- Sends single prompt, streams response to stdout
- Calls `lifetime.StopApplication()` on completion
- Uses `AutoToolApprovalHandler` (all tools auto-approved)

## Data Flow Patterns

### Chat Session

1. User sends message via ChatHub.SendMessage() / CLI input / Telegram message
2. Appropriate `IChatMessengerClient` queues `ChatPrompt`
3. `ChatMonitoring` BackgroundService drives `ChatMonitor.Monitor()`
4. `ChatMonitor` groups prompts by thread, creates agents, processes messages
5. `McpAgent` streams `AgentResponseUpdate` through MCP tools + LLM
6. Response stream routed back through `IChatMessengerClient.ProcessResponseStreamAsync()`

### Scheduled Tasks

1. `ScheduleCreateTool` creates schedule (cron expression + agent definition)
2. `RedisScheduleStore` persists schedule
3. `ScheduleMonitoring` BackgroundService runs `ScheduleDispatcher` (30s interval)
4. Due schedules written to `Channel<Schedule>`
5. `ScheduleExecutor` reads channel, creates agent via `IScheduleAgentFactory`
6. Agent executes prompt; response sent to WebUI (if supported) or consumed silently
7. One-time schedules deleted after execution

### Memory Operations

1. `MemoryStoreTool` receives content via MCP
2. `OpenRouterEmbeddingService` generates embedding vector
3. `RedisStackMemoryStore` stores with HNSW vector index
4. `MemoryRecallTool` searches via vector similarity (cosine)
5. Results filtered by category/tags/importance

### Service Bus Integration

```
External System
     |
     v
ServiceBusProcessorHost (BackgroundService)
     |
     v
ServiceBusMessageParser
     +-- Validates required fields (correlationId, agentId, prompt)
     +-- Validates agentId against configured agents
     +-- Returns ParseSuccess(ParsedServiceBusMessage) or ParseFailure(reason, details)
     +-- Invalid messages dead-lettered with reason
     |
     v
ServiceBusPromptReceiver
     +-- Maps correlationId to internal chatId/threadId/topicId via ServiceBusConversationMapper
     +-- Persists mapping in Redis (sb-correlation:{agentId}:{correlationId}) with 30-day TTL
     +-- Notifies WebUI via INotifier (user message appears in real-time)
     +-- Queues ChatPrompt to Channel
     |
     v
CompositeChatMessengerClient.ReadPrompts() merges ServiceBus + WebChat prompts
     |
     v
ChatMonitor groups by thread and processes
     |
     v
Agent processes prompt
     |
     v
CompositeChatMessengerClient.ProcessResponseStreamAsync()
     +-- Routes to WebChat (always) + ServiceBus (for SB-originated messages) via IMessageSourceRouter
     |
     v
ServiceBusResponseHandler
     +-- Retrieves correlationId from chatId via TryGetCorrelationId (in-memory reverse lookup)
     +-- Filters to only completed responses with valid correlationId
     +-- Writes response to response queue via ServiceBusResponseWriter (Polly retry)
     |
     v
Response Message to External System
```

**Message Contract**:
- Incoming: `{ correlationId, agentId, prompt, sender }` - correlationId/agentId/prompt required, sender optional
- Outgoing: `{ correlationId, agentId, response, completedAt }`

## DI Module Organization

```
Agent/Modules/
   |
   +-- ConfigModule
   |       +-- GetSettings() (environment vars + user secrets)
   |       +-- GetCommandLineParams() (System.CommandLine parsing)
   |       +-- ConfigureAgents() orchestrates AddAgent + AddScheduling + AddChatMonitoring
   |
   +-- InjectorModule
   |       +-- AddAgent() (Redis, ChatThreadResolver, AgentFactory, ToolRegistry)
   |       +-- AddChatMonitoring() (ChatMonitor, interface-specific clients)
   |       +-- AddCliClient() / AddTelegramClient() / AddOneShotClient() / AddWebClient()
   |       +-- AddServiceBusClient() (composite client with SB + WebChat)
   |
   +-- SchedulingModule
           +-- AddScheduling() (Channel, stores, validators, tools, feature, executor, dispatcher)
```

## Security

### DDNS IP Allowlist

When running in Web mode, the `DdnsIpAllowlistMiddleware` resolves a configured DDNS hostname and only allows requests from matching IPs. It checks `CF-Connecting-IP`, `X-Forwarded-For`, and direct connection IP.

### Tool Approval

Non-whitelisted tools require explicit user approval before execution, preventing unauthorized actions. The whitelist uses glob patterns configured per agent.
