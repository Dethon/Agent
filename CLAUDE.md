# Agent - AI Media Library Agent

## Overview

This repository contains **Agent**, an AI-powered agent that manages a personal media library through Telegram chat or web interface,
using OpenRouter LLMs and the Model Context Protocol (MCP).

## Technology Stack

- **.NET 10** - Target framework
- **Model Context Protocol (MCP)** - Tool integration architecture
- **OpenRouter LLMs** - AI model provider (Gemini, GPT-4, etc.)
- **Microsoft.Extensions.AI** - LLM abstraction layer
- **Microsoft.Agents.AI** - Agent framework
- **Redis** - Conversation state persistence
- **Docker Compose** - Deployment stack

## Project Structure

| Project                  | Layer          | Purpose                                                     |
|--------------------------|----------------|-------------------------------------------------------------|
| `Agent`                  | Application    | Composition root, Telegram bot, DI configuration            |
| `Domain`                 | Domain         | Pure business logic, contracts, DTOs, exceptions            |
| `Infrastructure`         | Infrastructure | External service clients, agent implementations             |
| `McpServerLibrary`       | Module         | MCP server for torrent search, downloads, file organization |
| `McpServerText`          | Module         | MCP server for text/markdown file inspection and editing    |
| `McpServerWebSearch`     | Module         | MCP server for web search and content fetching              |
| `McpServerMemory`        | Module         | MCP server for vector-based memory storage and recall       |
| `McpServerIdealista`     | Module         | MCP server for Idealista real estate property search        |
| `McpServerCommandRunner` | Module         | MCP server for CLI command execution                        |
| `WebChat`                | Application    | Blazor WebAssembly host server                              |
| `WebChat.Client`         | Application    | Blazor WebAssembly client for web-based chat                |
| `Tests`                  | Testing        | Unit and integration tests                                  |
| `DockerCompose`          | Deployment     | Docker Compose configuration                                |

## File Patterns

Quick reference for finding code files:

| What                     | Where                                     |
|--------------------------|-------------------------------------------|
| Contracts/Interfaces     | `Domain/Contracts/*.cs`                   |
| DTOs                     | `Domain/DTOs/*.cs`                        |
| Domain tools             | `Domain/Tools/**/*.cs`                    |
| Domain agents            | `Domain/Agents/*.cs`                      |
| Domain prompts           | `Domain/Prompts/*.cs`                     |
| Domain monitors          | `Domain/Monitor/*.cs`                     |
| Agent implementations    | `Infrastructure/Agents/*.cs`              |
| MCP integration          | `Infrastructure/Agents/Mcp/*.cs`          |
| Chat clients             | `Infrastructure/Agents/ChatClients/*.cs`  |
| External service clients | `Infrastructure/Clients/**/*.cs`          |
| Tool approval handlers   | `Infrastructure/Clients/ToolApproval/*.cs`|
| Messaging clients        | `Infrastructure/Clients/Messaging/*.cs`   |
| Torrent clients          | `Infrastructure/Clients/Torrent/*.cs`     |
| Browser clients          | `Infrastructure/Clients/Browser/*.cs`     |
| CLI UI components        | `Infrastructure/CliGui/**/*.cs`           |
| CLI abstractions         | `Infrastructure/CliGui/Abstractions/*.cs` |
| CLI routing              | `Infrastructure/CliGui/Routing/*.cs`      |
| CLI rendering            | `Infrastructure/CliGui/Rendering/*.cs`    |
| Command runners          | `Infrastructure/CommandRunners/*.cs`      |
| Memory services          | `Infrastructure/Memory/*.cs`              |
| State persistence        | `Infrastructure/StateManagers/*.cs`       |
| MCP server tools         | `McpServer*/McpTools/*.cs`                |
| MCP server prompts       | `McpServer*/McpPrompts/*.cs`              |
| WebChat pages            | `WebChat.Client/Pages/*.razor`            |
| WebChat components       | `WebChat.Client/Components/**/*.razor`    |
| WebChat contracts        | `WebChat.Client/Contracts/*.cs`           |
| WebChat services         | `WebChat.Client/Services/**/*.cs`         |
| WebChat hub              | `Agent/Hubs/*.cs`                         |
| Unit tests               | `Tests/Unit/**/*Tests.cs`                 |
| Integration tests        | `Tests/Integration/**/*Tests.cs`          |
| Test fixtures            | `Tests/Integration/Fixtures/*.cs`         |

## Architecture

Dependencies flow inward: `Agent` → `Infrastructure` → `Domain`

- **Domain**: Interfaces, DTOs, domain services, pure business logic (no external dependencies)
- **Infrastructure**: Implementations, external clients, state management
- **Agent**: Bootstrapping, DI, configuration, application entry point

**Interface policy**: Services with a single expected implementation do not require an interface unless the Domain layer needs to consume them. The Agent layer may directly depend on Infrastructure concrete types when no abstraction is needed.

See `.claude/rules/` for layer-specific coding rules that apply automatically based on file paths.

## Key Components

### Multi-Agent Architecture

Agents are defined as configuration data, allowing a single container to run multiple agents:

- **AgentDefinition** (`Domain/DTOs/AgentDefinition.cs`) - Defines an agent with name, model, MCP endpoints, whitelist patterns, and custom instructions
- **MultiAgentFactory** (`Infrastructure/Agents/MultiAgentFactory.cs`) - Creates agents based on definitions, resolves agent from bot token hash
- **TelegramChatClient** (`Infrastructure/Clients/Messaging/TelegramChatClient.cs`) - Polls multiple Telegram bots, routes messages by bot token hash

Agent routing:
- **Telegram**: Each bot token maps to one agent via SHA256 hash matching
- **CLI**: Uses the first configured agent
- **WebChat**: User selects agent from available list

Configuration in `appsettings.json`:
```json
{
  "agents": [
    {
      "id": "jack",
      "name": "Jack",
      "model": "google/gemini-2.0-flash-001",
      "mcpServerEndpoints": ["http://mcp-library:8080/sse"],
      "whitelistPatterns": ["mcp:mcp-library:*"],
      "customInstructions": "You are Jack...",
      "telegramBotToken": "123456:ABC..."
    }
  ]
}
```

### WebChat

Browser-based chat interface using Blazor WebAssembly and SignalR:

- **WebChat** - Static file host for the Blazor WebAssembly client
- **WebChat.Client** - Blazor WebAssembly application with chat UI
- **ChatHub** (`Agent/Hubs/ChatHub.cs`) - SignalR hub for real-time communication
- **WebChatMessengerClient** (`Infrastructure/Clients/Messaging/WebChatMessengerClient.cs`) - Server-side message routing
- **ChatConnectionService** (`WebChat.Client/Services/ChatConnectionService.cs`) - Client-side SignalR connection management

Features:
- Real-time message streaming with reconnection support
- Topic-based conversations with server-side persistence
- Stream resumption after disconnection (buffered messages + sequence tracking)
- Multi-agent selection

### Message Streaming Pipeline

The `ChatMonitor` uses a streaming pipeline to handle concurrent conversations:

```
Prompts → GroupByStreaming(AgentKey) → ProcessChatThread → Merge → SendResponse
```

### MCP Resource Subscriptions

Real-time resource updates from MCP servers:

- **SubscriptionTracker** (McpServerLibrary) - Server-side subscription tracking
- **McpSubscriptionManager** (Infrastructure) - Client-side subscription lifecycle
- Flow: MCP Server → `notifications/resources/updated` → Client → Agent

### Conversation Persistence

Redis-backed chat history with key format: `agent-key:{chatId}:{threadId}` (30-day expiry)

### Chat Commands

| Command   | Action                                          |
|-----------|-------------------------------------------------|
| `/cancel` | Cancels current operation, keeps history        |
| `/clear`  | Clears conversation and deletes persisted state |

### Tool Approval System

Tool execution requires user approval via `IToolApprovalHandler`:

- `TelegramToolApprovalHandler` - Inline keyboard in Telegram
- `CliToolApprovalHandler` - Modal dialog in CLI
- `WebToolApprovalHandler` - SignalR-based approval for WebChat

Results: `Approved`, `ApprovedAndRemember`, `Rejected`, `AutoApproved`

### Memory System

Vector-based storage via `IMemoryStore` → `RedisStackMemoryStore` with `IEmbeddingService` →
`OpenRouterEmbeddingService`

Tools: `MemoryStoreTool`, `MemoryRecallTool`, `MemoryForgetTool`, `MemoryListTool`, `MemoryReflectTool`

### Web Search

`IWebSearchClient` → `BraveSearchClient` + `IWebFetcher` → `WebContentFetcher`

WebFetch supports CSS selectors for targeting content, multiple output formats (text/markdown/html), and link extraction.

### Real Estate Search

`IIdealistaClient` → `IdealistaClient` for property search in Spain, Italy, and Portugal via Idealista API

### Command Runners

Platform-specific via `ICommandRunner`: `BashRunner`, `CmdRunner`, `PowerShellRunner`, `ShRunner`
