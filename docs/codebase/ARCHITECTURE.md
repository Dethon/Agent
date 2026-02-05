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
     |
     v
ChatMonitoring (background service)
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
AgentResponseUpdate stream
     |
     v
IChatMessengerClient.ProcessResponseStreamAsync()
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
ServiceBusProcessor (message reception)
     |
     v
ServiceBusMessageParser
     +-- Validates required fields (correlationId, agentId, prompt)
     +-- Validates agentId against configured agents
     +-- Returns ParsedServiceBusMessage or ParseFailure
     |
     v
ServiceBusPromptReceiver
     +-- Maps correlationId to internal chatId via ServiceBusConversationMapper
     +-- Persists mapping in Redis (sb-correlation:{agentId}:{correlationId})
     +-- Queues ChatPrompt to Channel
     |
     v
ChatMonitor picks up prompt
     |
     v
Agent processes prompt
     |
     v
ServiceBusResponseHandler
     +-- Retrieves correlationId from chatId mapping
     +-- Writes response to response queue via ServiceBusResponseWriter
     |
     v
Response Message to External System
```

**Message Contract**:
- Incoming: `{ correlationId, agentId, prompt, sender }` - all required
- Outgoing: `{ correlationId, agentId, response, completedAt }`
