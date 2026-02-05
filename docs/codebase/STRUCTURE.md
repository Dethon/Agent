# Project Structure

## Directory Layout

```
agent/
+-- Agent/                    # Composition root (entry point)
|   +-- App/                  # Background services
|   |   +-- ChatMonitoring.cs
|   |   +-- ScheduleMonitoring.cs
|   +-- Hubs/                 # SignalR hubs
|   |   +-- ChatHub.cs
|   +-- Modules/              # DI configuration
|   |   +-- ConfigModule.cs
|   |   +-- InjectorModule.cs
|   +-- Settings/             # Configuration classes
|   +-- Program.cs
|
+-- Domain/                   # Pure business logic (no external deps)
|   +-- Agents/               # Agent abstractions
|   |   +-- AgentKey.cs
|   |   +-- ChatThreadContext.cs
|   |   +-- DisposableAgent.cs
|   +-- Contracts/            # Interfaces consumed by Domain
|   |   +-- IAgentFactory.cs
|   |   +-- IChatMessengerClient.cs
|   |   +-- IMemoryStore.cs
|   |   +-- IMessageSourceRouter.cs
|   |   +-- IToolApprovalHandler.cs
|   +-- DTOs/                 # Data transfer objects
|   |   +-- AgentDefinition.cs
|   |   +-- Memory.cs
|   |   +-- MessageSource.cs
|   |   +-- ParseResult.cs
|   |   +-- ParsedServiceBusMessage.cs
|   |   +-- ServiceBusPromptMessage.cs
|   |   +-- ServiceBusResponseMessage.cs
|   |   +-- WebChat/          # WebChat-specific DTOs
|   +-- Extensions/           # Extension methods
|   +-- Monitor/              # Chat monitoring and scheduling
|   |   +-- ChatMonitor.cs
|   |   +-- ScheduleExecutor.cs
|   +-- Prompts/              # System prompts
|   +-- Resources/            # Resource definitions
|   +-- Routers/              # Message routing logic
|   |   +-- MessageSourceRouter.cs
|   +-- Tools/                # Business logic tools
|       +-- Commands/
|       +-- Downloads/
|       +-- Files/
|       +-- Memory/
|       +-- Scheduling/
|       +-- Text/
|       +-- Web/
|
+-- Infrastructure/           # Implementations
|   +-- Agents/               # Agent implementations
|   |   +-- ChatClients/      # LLM clients
|   |   +-- Mcp/              # MCP integration
|   |   +-- MultiAgentFactory.cs
|   |   +-- McpAgent.cs
|   +-- CliGui/               # Terminal UI
|   |   +-- Abstractions/
|   |   +-- Rendering/
|   |   +-- Routing/
|   |   +-- Ui/
|   +-- Clients/              # External service clients
|   |   +-- Browser/          # Playwright
|   |   +-- Messaging/        # Telegram, WebChat, ServiceBus, CLI
|   |   |   +-- CompositeChatMessengerClient.cs  # Multiplexes multiple clients
|   |   |   +-- Cli/          # CLI messenger clients
|   |   |   |   +-- CliChatMessengerClient.cs
|   |   |   |   +-- OneShotChatMessengerClient.cs
|   |   |   +-- ServiceBus/   # Azure Service Bus integration
|   |   |   |   +-- ServiceBusChatMessengerClient.cs
|   |   |   |   +-- ServiceBusProcessorHost.cs
|   |   |   |   +-- ServiceBusMessageParser.cs
|   |   |   |   +-- ServiceBusPromptReceiver.cs
|   |   |   |   +-- ServiceBusResponseHandler.cs
|   |   |   |   +-- ServiceBusResponseWriter.cs
|   |   |   |   +-- ServiceBusConversationMapper.cs
|   |   |   +-- Telegram/
|   |   |   +-- WebChat/
|   |   +-- ToolApproval/     # Approval handlers
|   |   +-- Torrent/          # qBittorrent, Jackett
|   +-- Extensions/           # Infrastructure extensions
|   +-- Memory/               # Vector memory
|   +-- StateManagers/        # State persistence
|   +-- Utils/                # Utilities
|
+-- McpServer*/               # MCP servers (6 total)
|   +-- McpServerLibrary/     # Downloads, files
|   +-- McpServerText/        # Text operations
|   +-- McpServerWebSearch/   # Web browsing
|   +-- McpServerMemory/      # Vector memory
|   +-- McpServerIdealista/   # Real estate
|   +-- McpServerCommandRunner/ # Shell commands
|   Each contains:
|   +-- McpTools/             # Tool implementations
|   +-- McpPrompts/           # Server prompts
|   +-- McpResources/         # Resources (if any)
|   +-- Modules/              # DI configuration
|   +-- Settings/             # Server settings
|   +-- Program.cs
|
+-- WebChat/                  # Blazor host
|   +-- Program.cs
|
+-- WebChat.Client/           # Blazor WebAssembly
|   +-- Contracts/            # Service interfaces
|   +-- Extensions/           # Service extensions
|   +-- Helpers/              # UI helpers
|   +-- Models/               # Client models
|   +-- Services/             # Service implementations
|   |   +-- Streaming/        # Stream services
|   |   +-- Utilities/
|   +-- State/                # Redux-like state
|       +-- Approval/
|       +-- Connection/
|       +-- Effects/
|       +-- Hub/
|       +-- Messages/
|       +-- Pipeline/
|       +-- Streaming/
|       +-- Toast/
|       +-- Topics/
|       +-- UserIdentity/
|
+-- Tests/                    # Test project
|   +-- Integration/          # Integration tests
|   |   +-- Agents/
|   |   +-- Clients/
|   |   +-- Domain/
|   |   +-- Fixtures/         # Test fixtures (Redis, MCP, ServiceBus)
|   |   +-- McpServerTests/
|   |   +-- McpTools/
|   |   +-- Memory/
|   |   +-- Messaging/        # Service Bus integration tests
|   |   +-- StateManagers/
|   |   +-- WebChat/
|   +-- Unit/                 # Unit tests
|       +-- Domain/
|       +-- Infrastructure/
|       |   +-- Messaging/    # ServiceBus, Composite, WebChat client tests
|       +-- McpServerLibrary/
|       +-- WebChat/
|       +-- WebChat.Client/
|           +-- State/        # Store, pipeline, effect tests
|
+-- DockerCompose/            # Docker configuration
+-- docs/                     # Documentation
    +-- codebase/             # Generated codebase docs
    +-- designs/              # Design documents
    +-- plans/                # Implementation plans
```

## File Naming Conventions

| Pattern | Location | Example |
|---------|----------|---------|
| `I*.cs` | Domain/Contracts | `IMemoryStore.cs` |
| `*Tool.cs` | Domain/Tools | `FileDownloadTool.cs` |
| `Mcp*Tool.cs` | McpServer*/McpTools | `McpFileDownloadTool.cs` |
| `*Store.cs` | Infrastructure/StateManagers | `RedisThreadStateStore.cs` |
| `*Client.cs` | Infrastructure/Clients | `TelegramChatClient.cs` |
| `*Handler.cs` | Infrastructure/Clients | `WebToolApprovalHandler.cs` |
| `*Writer.cs` | Infrastructure/Clients | `ServiceBusResponseWriter.cs` |
| `*Mapper.cs` | Infrastructure/Clients | `ServiceBusConversationMapper.cs` |
| `*Parser.cs` | Infrastructure/Clients | `ServiceBusMessageParser.cs` |
| `*Receiver.cs` | Infrastructure/Clients | `ServiceBusPromptReceiver.cs` |
| `*Host.cs` | Infrastructure/Clients | `ServiceBusProcessorHost.cs` |
| `*Router.cs` | Domain/Routers | `MessageSourceRouter.cs` |
| `*Monitor.cs` | Domain/Monitor | `ChatMonitor.cs` |
| `*Effect.cs` | WebChat.Client/State/Effects | `TopicDeleteEffect.cs` |
| `*Pipeline.cs` | WebChat.Client/State/Pipeline | `MessagePipeline.cs` |
| `*Reducers.cs` | WebChat.Client/State/Messages | `MessagesReducers.cs` |
| `*Tests.cs` | Tests | `McpAgentIntegrationTests.cs` |

## Module Boundaries

### Domain (Pure)
- No external dependencies
- Interfaces for services Domain needs
- DTOs as records
- Business logic in Tool classes

### Infrastructure (Implementations)
- Implements Domain interfaces
- External client wrappers
- State persistence
- Cannot import from Agent

### Agent (Composition)
- DI registration
- Entry point
- Background services
- Imports from all layers

### MCP Servers (Independent)
- Each server is a separate process
- Wraps Domain tools with MCP attributes
- Shares Infrastructure for clients
