# Architecture

## Layered Architecture

```
+------------------+
|      Agent       |  Entry point, DI, SignalR Hub
+------------------+
         |
         v
+------------------+
|  Infrastructure  |  Implementations, external clients
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
IAgentFactory
     |
     v
MultiAgentFactory --> AgentDefinition (config)
     |
     v
DisposableAgent (base) <-- McpAgent (implementation)
     |
     +-- ThreadSession --> McpClientManager (MCP connections)
                      --> McpResourceManager (subscriptions)
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
    "enabledFeatures": ["scheduling"]
  }]
}
```

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
     |  Groups prompts by thread via CreateTopicIfNeededAsync
     |
     v
IAgentFactory.Create(agentKey)
     |
     v
McpAgent.RunStreamingAsync()
     |
     +-- ToolApprovalChatClient (approval wrapper)
     +-- OpenRouterChatClient (LLM calls)
     +-- MCP Tool execution
     |
     v
AgentResponseUpdate stream (tagged with MessageSource)
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
Tool Call Request
     |
     v
ToolPatternMatcher.IsWhitelisted()
     |
     +-- Yes --> Execute directly
     |
     +-- No --> IToolApprovalHandler
                     |
                     +-- TelegramToolApprovalHandler (inline keyboard)
                     +-- WebToolApprovalHandler (SignalR notification)
                     +-- CliToolApprovalHandler (Terminal.Gui dialog)
                     +-- AutoToolApprovalHandler (always approve)
```

## MCP Integration

### Client Side (Agent connects to MCP servers)

```
McpAgent
   |
   v
ThreadSession
   |
   +-- McpClientManager
   |        |
   |        +-- McpClient[] (connections to each server)
   |        +-- AITool[] (discovered tools)
   |        +-- string[] (loaded prompts)
   |
   +-- McpResourceManager
            |
            +-- Resource subscriptions
            +-- Update notifications
```

### Server Side (MCP servers expose tools)

```
McpServer (e.g., McpServerLibrary)
   |
   +-- [McpServerTool] McpFileDownloadTool : FileDownloadTool
   +-- [McpServerTool] McpFileSearchTool : FileSearchTool
   +-- [McpResource] McpDownloadResource
   +-- [McpPrompt] McpSystemPrompt
```

## WebChat State Management (Redux-like)

```
Store<TState>
   |
   +-- State (BehaviorSubject)
   +-- StateObservable
   +-- Dispatch(action, reducer)

Stores:
   +-- ConnectionStore (hub connection state)
   +-- MessagesStore (chat messages)
   +-- TopicsStore (chat topics)
   +-- StreamingStore (active streams)
   +-- ApprovalStore (pending approvals)

Effects (side effects):
   +-- HubEventDispatcher (SignalR events)
   +-- ReconnectionEffect
   +-- SendMessageEffect
   +-- TopicSelectionEffect
   +-- AgentSelectionEffect (reloads topics on agent change)
   +-- TopicDeleteEffect (handles topic removal with pipeline cleanup)

Pipeline:
   +-- IMessagePipeline / MessagePipeline
        +-- SubmitUserMessage (correlationId tracking)
        +-- AccumulateChunk (dedup via finalization)
        +-- FinalizeMessage
        +-- ClearTopic (cleanup on delete)
        +-- WasSentByThisClient (multi-browser dedup)
```

## Data Flow Patterns

### Chat Session

1. User sends message via ChatHub.SendMessage()
2. WebChatMessengerClient queues prompt
3. ChatMonitoring picks up queued message
4. Agent processes with MCP tools
5. Streaming updates sent via SignalR
6. Client stores update MessagesStore

### Scheduled Tasks

1. ScheduleCreateTool creates schedule (cron expression)
2. RedisScheduleStore persists schedule
3. ScheduleMonitoring checks due schedules
4. IScheduleAgentFactory creates agent
5. Agent executes scheduled prompt
6. Response sent to configured destination

### Memory Operations

1. MemoryStoreTool receives content
2. OpenRouterEmbeddingService generates embedding
3. RedisStackMemoryStore stores with HNSW index
4. MemoryRecallTool searches via vector similarity
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
     +-- Retrieves correlationId from chatId via TryGetCorrelationId (virtual, in-memory reverse lookup)
     +-- Filters to only completed responses with valid correlationId
     +-- Writes response to response queue via ServiceBusResponseWriter (Polly retry)
     |
     v
Response Message to External System
```

**Message Contract**:
- Incoming: `{ correlationId, agentId, prompt, sender }` - correlationId/agentId/prompt required, sender optional
- Outgoing: `{ correlationId, agentId, response, completedAt }`
