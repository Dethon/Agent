# Codebase Structure

**Analysis Date:** 2026-01-19

## Directory Layout

```
agent/
├── Agent/                    # Application layer - composition root, entry point
│   ├── App/                  # Hosted services (ChatMonitoring, CleanupMonitoring)
│   ├── Hubs/                 # SignalR hubs (ChatHub, Notifier)
│   ├── Modules/              # DI configuration (ConfigModule, InjectorModule)
│   ├── Settings/             # Configuration DTOs (AgentSettings)
│   └── Program.cs            # Application entry point
├── Domain/                   # Pure business logic, no external dependencies
│   ├── Agents/               # Agent abstractions, thread context management
│   ├── Contracts/            # Interfaces consumed by Infrastructure
│   ├── DTOs/                 # Data transfer objects, value types
│   ├── Extensions/           # Extension methods for domain types
│   ├── Monitor/              # Chat monitoring orchestration
│   ├── Prompts/              # LLM prompt templates
│   ├── Resources/            # MCP resource definitions
│   └── Tools/                # Domain tool implementations
│       ├── Commands/         # CLI command tools
│       ├── Downloads/        # Download management tools
│       ├── Files/            # File system tools
│       ├── Memory/           # Memory storage tools
│       ├── RealEstate/       # Property search tools
│       ├── Text/             # Text file manipulation tools
│       └── Web/              # Web browsing tools
├── Infrastructure/           # External integrations, implementations
│   ├── Agents/               # Agent implementations
│   │   ├── ChatClients/      # LLM chat clients (OpenRouter, ToolApproval)
│   │   └── Mcp/              # MCP client management, resources, subscriptions
│   ├── CliGui/               # Terminal UI components
│   │   ├── Abstractions/     # CLI interface contracts
│   │   ├── Rendering/        # Message formatting, display
│   │   ├── Routing/          # CLI command handling
│   │   └── Ui/               # Terminal.Gui widgets
│   ├── Clients/              # External service clients
│   │   ├── Browser/          # Playwright web browser
│   │   ├── Messaging/        # Chat adapters (Telegram, CLI, WebChat)
│   │   ├── ToolApproval/     # Approval handlers per interface
│   │   └── Torrent/          # Jackett, qBittorrent clients
│   ├── CommandRunners/       # Platform CLI runners (Bash, PowerShell, etc.)
│   ├── Extensions/           # HttpClient, Task extensions
│   ├── HtmlProcessing/       # HTML parsing, conversion
│   ├── Memory/               # Vector store, embedding service
│   ├── StateManagers/        # Redis state persistence
│   └── Utils/                # Tool helpers, pattern matching
├── McpServerLibrary/         # MCP server for media library management
│   ├── Extensions/           # MCP server extensions
│   ├── McpPrompts/           # Server-side prompts
│   ├── McpResources/         # MCP resource handlers
│   ├── McpTools/             # MCP tool wrappers
│   ├── Modules/              # DI configuration
│   ├── ResourceSubscriptions/# Real-time resource updates
│   └── Settings/             # Server configuration
├── McpServerText/            # MCP server for text file operations
├── McpServerWebSearch/       # MCP server for web search
├── McpServerMemory/          # MCP server for vector memory
├── McpServerIdealista/       # MCP server for real estate search
├── McpServerCommandRunner/   # MCP server for CLI execution
├── WebChat/                  # Blazor WebAssembly host server
├── WebChat.Client/           # Blazor WebAssembly client
│   ├── Components/           # Razor components
│   │   └── Layout/           # Layout components
│   ├── Contracts/            # Client-side interfaces
│   ├── Models/               # Client-side models
│   ├── Pages/                # Razor pages
│   └── Services/             # Client services
│       ├── Handlers/         # Notification handlers
│       ├── State/            # State management
│       ├── Streaming/        # Stream coordination
│       └── Utilities/        # Helper utilities
├── Tests/                    # Test projects
│   ├── Integration/          # Integration tests
│   │   ├── Agents/           # Agent integration tests
│   │   ├── Clients/          # Client integration tests
│   │   ├── Domain/           # Domain integration tests
│   │   ├── Fixtures/         # Shared test fixtures
│   │   ├── Jack/             # DI tests
│   │   ├── McpServerTests/   # MCP server tests
│   │   ├── McpTools/         # MCP tool tests
│   │   ├── Memory/           # Memory service tests
│   │   └── WebChat/          # WebChat integration tests
│   └── Unit/                 # Unit tests
│       ├── Domain/           # Domain unit tests
│       ├── Infrastructure/   # Infrastructure unit tests
│       ├── McpServerLibrary/ # MCP server unit tests
│       └── WebChat/          # WebChat unit tests
├── DockerCompose/            # Docker deployment configuration
│   └── volumes/              # Docker volume mounts
└── .claude/                  # Claude Code configuration
    ├── agents/               # Agent definitions
    ├── rules/                # Layer-specific coding rules
    └── skills/               # Custom skills
```

## Directory Purposes

**Agent/:**
- Purpose: Composition root and application entry point
- Contains: DI configuration, hosted services, SignalR hubs, settings DTOs
- Key files: `Program.cs`, `Modules/InjectorModule.cs`, `Modules/ConfigModule.cs`, `Hubs/ChatHub.cs`

**Domain/:**
- Purpose: Pure business logic with no external dependencies
- Contains: Interfaces, DTOs, domain tools, agent abstractions, prompts
- Key files: `Contracts/IChatMessengerClient.cs`, `Contracts/IAgentFactory.cs`, `Monitor/ChatMonitor.cs`, `Agents/DisposableAgent.cs`

**Domain/Tools/:**
- Purpose: Business logic for tool implementations
- Contains: Tools organized by capability (Files, Downloads, Memory, Web, etc.)
- Key files: `Files/ListFilesTool.cs`, `Memory/MemoryStoreTool.cs`, `Web/WebBrowseTool.cs`

**Infrastructure/Agents/:**
- Purpose: Agent implementations and LLM integration
- Contains: MCP agent, chat clients, thread session management
- Key files: `McpAgent.cs`, `MultiAgentFactory.cs`, `ThreadSession.cs`, `ChatClients/OpenRouterChatClient.cs`

**Infrastructure/Agents/Mcp/:**
- Purpose: MCP protocol client integration
- Contains: Client management, resource subscriptions, sampling handler
- Key files: `McpClientManager.cs`, `McpResourceManager.cs`, `McpSubscriptionManager.cs`

**Infrastructure/Clients/Messaging/:**
- Purpose: Chat interface adapters
- Contains: Telegram, CLI, WebChat messenger clients
- Key files: `TelegramChatClient.cs`, `CliChatMessengerClient.cs`, `WebChatMessengerClient.cs`

**Infrastructure/Clients/ToolApproval/:**
- Purpose: User approval for tool execution
- Contains: Interface-specific approval handlers
- Key files: `TelegramToolApprovalHandler.cs`, `CliToolApprovalHandler.cs`, `WebToolApprovalHandler.cs`

**McpServer*/ (all MCP servers):**
- Purpose: Expose domain tools via MCP protocol
- Contains: MCP tool wrappers, prompts, resource handlers
- Key files: `McpTools/*.cs`, `McpPrompts/*.cs`, `Program.cs`

**WebChat.Client/:**
- Purpose: Browser-based Blazor WebAssembly chat UI
- Contains: Services, pages, components for SignalR-based chat
- Key files: `Services/ChatConnectionService.cs`, `Services/Streaming/StreamingCoordinator.cs`, `Pages/Chat.razor`

**Tests/:**
- Purpose: Unit and integration tests
- Contains: Tests organized by layer and test type
- Key files: `Integration/Fixtures/*.cs`, `Unit/Domain/*.cs`, `Unit/Infrastructure/*.cs`

## Key File Locations

**Entry Points:**
- `Agent/Program.cs`: Main application entry, hosts ChatMonitor
- `WebChat/Program.cs`: Static file server for Blazor WebAssembly
- `WebChat.Client/Program.cs`: Blazor WebAssembly bootstrap
- `McpServerLibrary/Program.cs`: MCP server for media library (pattern for all MCP servers)

**Configuration:**
- `Agent/appsettings.json`: Application configuration (agents, Redis, OpenRouter)
- `Agent/Settings/AgentSettings.cs`: Settings DTOs
- `Agent/Modules/ConfigModule.cs`: Configuration loading
- `Agent/Modules/InjectorModule.cs`: DI registration

**Core Logic:**
- `Domain/Monitor/ChatMonitor.cs`: Message processing pipeline orchestration
- `Infrastructure/Agents/McpAgent.cs`: MCP-integrated agent implementation
- `Infrastructure/Agents/MultiAgentFactory.cs`: Agent creation from definitions
- `Domain/Agents/ChatThreadResolver.cs`: Thread context management

**Chat Interfaces:**
- `Infrastructure/Clients/Messaging/TelegramChatClient.cs`: Telegram bot integration
- `Infrastructure/Clients/Messaging/CliChatMessengerClient.cs`: Terminal UI chat
- `Infrastructure/Clients/Messaging/WebChatMessengerClient.cs`: SignalR-based web chat

**Testing:**
- `Tests/Integration/Fixtures/*.cs`: Shared test fixtures (Redis, MCP servers)
- `Tests/Unit/Domain/*.cs`: Domain logic unit tests
- `Tests/Unit/Infrastructure/*.cs`: Infrastructure unit tests

## Naming Conventions

**Files:**
- PascalCase for all C# files: `ChatMonitor.cs`, `AgentSettings.cs`
- Interface prefix with `I`: `IChatMessengerClient.cs`, `IAgentFactory.cs`
- Test suffix `Tests`: `ChatMonitorTests.cs`, `McpAgentTests.cs`
- MCP wrappers prefix with `Mcp`: `McpListFilesTool.cs`

**Directories:**
- PascalCase for all directories: `Contracts/`, `StateManagers/`
- Plural for collections: `Agents/`, `Clients/`, `Tools/`
- Singular for specific feature: `CliGui/`, `Memory/`

**Namespaces:**
- Match directory structure: `Domain.Contracts`, `Infrastructure.Agents.Mcp`
- Root namespace per project: `Domain`, `Infrastructure`, `Agent`

## Where to Add New Code

**New Domain Tool:**
- Implementation: `Domain/Tools/{Category}/{ToolName}Tool.cs`
- MCP wrapper: `McpServer{X}/McpTools/Mcp{ToolName}Tool.cs`
- Tests: `Tests/Unit/Domain/{ToolName}ToolTests.cs`

**New External Client:**
- Implementation: `Infrastructure/Clients/{Category}/{ClientName}Client.cs`
- Interface (if Domain needs it): `Domain/Contracts/I{ClientName}Client.cs`
- Tests: `Tests/Integration/Clients/{ClientName}ClientTests.cs`

**New Chat Interface:**
- Messenger client: `Infrastructure/Clients/Messaging/{Name}ChatMessengerClient.cs`
- Tool approval handler: `Infrastructure/Clients/ToolApproval/{Name}ToolApprovalHandler.cs`
- DI registration: Add method in `Agent/Modules/InjectorModule.cs`

**New MCP Server:**
- Create project: `McpServer{Name}/`
- Entry point: `McpServer{Name}/Program.cs`
- Tools: `McpServer{Name}/McpTools/`
- Prompts: `McpServer{Name}/McpPrompts/`

**New WebChat Feature:**
- Service interface: `WebChat.Client/Contracts/I{Feature}Service.cs`
- Service implementation: `WebChat.Client/Services/{Feature}Service.cs`
- Register in: `WebChat.Client/Program.cs`

**Shared DTOs:**
- Domain DTOs: `Domain/DTOs/{Name}.cs`
- WebChat-specific: `Domain/DTOs/WebChat/{Name}.cs`

## Special Directories

**.claude/:**
- Purpose: Claude Code configuration and rules
- Generated: No (manually maintained)
- Committed: Yes

**.planning/:**
- Purpose: Planning and analysis documents
- Generated: Partially (by Claude Code commands)
- Committed: Optional (project preference)

**DockerCompose/volumes/:**
- Purpose: Docker volume data (downloads, configs)
- Generated: Yes (at runtime)
- Committed: No (in .gitignore)

**bin/, obj/:**
- Purpose: Build output directories
- Generated: Yes (by dotnet build)
- Committed: No (in .gitignore)

---

*Structure analysis: 2026-01-19*
