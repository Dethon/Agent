# Agent - AI Media Library Agent

## Overview

This repository contains **Agent**, an AI-powered agent that manages a personal media library through Telegram chat,
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
| `McpServerCommandRunner` | Module         | MCP server for CLI command execution                        |
| `Tests`                  | Testing        | Unit and integration tests                                  |
| `DockerCompose`          | Deployment     | Docker Compose configuration                                |

## File Patterns

Quick reference for finding code:

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
| External service clients | `Infrastructure/Clients/*.cs`             |
| CLI UI components        | `Infrastructure/CliGui/**/*.cs`           |
| CLI abstractions         | `Infrastructure/CliGui/Abstractions/*.cs` |
| CLI routing              | `Infrastructure/CliGui/Routing/*.cs`      |
| CLI rendering            | `Infrastructure/CliGui/Rendering/*.cs`    |
| Command runners          | `Infrastructure/CommandRunners/*.cs`      |
| Memory services          | `Infrastructure/Memory/*.cs`              |
| State persistence        | `Infrastructure/StateManagers/*.cs`       |
| MCP server tools         | `McpServer*/McpTools/*.cs`                |
| MCP server prompts       | `McpServer*/McpPrompts/*.cs`              |
| Unit tests               | `Tests/Unit/**/*Tests.cs`                 |
| Integration tests        | `Tests/Integration/**/*Tests.cs`          |
| Test fixtures            | `Tests/Integration/Fixtures/*.cs`         |

## Architecture Rules

### Dependency Direction

Dependencies flow inward: `Agent` → `Infrastructure` → `Domain`

- **Domain**: Must NOT reference Infrastructure or Agent
- **Infrastructure**: May reference Domain, must NOT reference Agent
- **Agent**: May reference both Domain and Infrastructure

### Layer Responsibilities

- **Domain**: Interfaces, DTOs, domain services, pure business logic
- **Infrastructure**: Implementations, external clients, state management
- **Agent**: Bootstrapping, DI, configuration, application entry point

### Layer Violation Detection

When reviewing code, watch for these violations:

```csharp
// VIOLATION: Domain importing Infrastructure
using Infrastructure.Clients;  // Never in Domain/

// VIOLATION: Infrastructure importing Agent
using Agent.App;  // Never in Infrastructure/

// VIOLATION: Domain depending on concrete framework types
public class DomainService
{
    private readonly HttpClient _client;  // Framework type in Domain
}
```

Correct pattern:

```csharp
// Domain defines interface
public interface IHttpService { }

// Infrastructure implements it
public class HttpService : IHttpService { }
```

## Coding Standards

### Modern .NET Patterns

- Use file-scoped namespaces
- Use primary constructors where appropriate
- Prefer `record` types for DTOs and immutable data
- Use nullable reference types and proper null handling
- Apply `async`/`await` throughout for asynchronous operations
- Use `ArgumentNullException.ThrowIfNull()` for guard clauses
- Prefer `IReadOnlyList<T>` and `IReadOnlyCollection<T>` for return types
- Use `TimeProvider` for testable time-dependent code

### Clean Code

- Follow SOLID principles, especially Single Responsibility
- Prefer composition over inheritance
- Keep methods small and focused (< 20 lines ideal)
- Use meaningful names that reveal intent
- Minimize mutable state and side effects

### MCP Development

- New capabilities should be exposed as MCP tools
- Follow patterns in existing `McpServer*` projects
- Tool definitions should have clear descriptions and parameters

## Key Patterns & Components

### Message Streaming Pipeline

The `ChatMonitor` uses a streaming pipeline to handle concurrent conversations:

1. **GroupByStreaming** - Groups incoming prompts by chat thread (AgentKey)
2. **Merge** - Combines multiple async streams into a single output stream
3. Each thread gets its own agent instance and cancellation context

```
Prompts → GroupByStreaming(AgentKey) → ProcessChatThread → Merge → SendResponse
```

### MCP Resource Subscriptions

The system supports real-time resource updates from MCP servers:

- **SubscriptionTracker** (McpServerLibrary) - Tracks which clients are subscribed to which resources
- **McpSubscriptionManager** (Infrastructure) - Client-side subscription lifecycle management
- **ResourceUpdateProcessor** - Processes resource updates and feeds them back to the agent

Flow: MCP Server emits `notifications/resources/updated` → Client receives → Agent processes update

### Conversation Persistence

Chat history is persisted to Redis, enabling conversations to survive application restarts:

- **IThreadStateStore** (Domain) - Interface for thread state persistence
- **RedisThreadStateStore** (Infrastructure) - Redis implementation using StackExchange.Redis
- **RedisChatMessageStore** (Infrastructure) - `ChatMessageStore` implementation that persists to Redis
- **ChatHistoryMapper** (Infrastructure) - Maps stored messages to CLI display format

Key format: `agent-key:{chatId}:{threadId}` with 30-day expiry.

### Chat Commands

The `ChatCommandParser` handles user commands in `ChatMonitor`:

| Command   | Action                                                     |
|-----------|------------------------------------------------------------|
| `/cancel` | Cancels current operation, keeps conversation history      |
| `/clear`  | Clears conversation and deletes persisted state from Redis |

### Tool Approval System

Tool execution requires user approval through the `IToolApprovalHandler` interface:

- **ToolApprovalChatClient** - Wraps the chat client to intercept tool calls
- **TelegramToolApprovalHandler** - Shows inline keyboard for approve/reject in Telegram
- **CliToolApprovalHandler** - Shows modal dialog in CLI interface

Approval results: `Approved`, `ApprovedAndRemember`, `Rejected`, `AutoApproved`

### CLI Interface Components

Located in `Infrastructure/CliGui/`:

- **Abstractions**: `ICliChatMessageRouter`, `ITerminalAdapter`, `ITerminalSession`, `IToolApprovalUi`
- **Routing**: `CliChatMessageRouter` (routes messages between UI and agent), `CliCommandHandler` (handles `/cancel`,
  `/clear`, `/help`)
- **Rendering**: `ChatHistoryMapper`, `ChatMessageFormatter`, `ChatLine`, `ChatLineType`, `ChatMessage`
- **UI**: `TerminalGuiAdapter` (Terminal.Gui wrapper), `CliUiFactory`, `ApprovalDialog`, `ChatListDataSource`,
  `CollapseStateManager`

The messenger client `CliChatMessengerClient` is located in `Infrastructure/Clients/`.

### Memory System

Vector-based memory storage for agent context persistence:

- **IMemoryStore** (Domain) - Interface for memory storage operations
- **RedisStackMemoryStore** (Infrastructure) - Redis-backed vector store using RediSearch
- **IEmbeddingService** (Domain) - Interface for text embeddings
- **OpenRouterEmbeddingService** (Infrastructure) - Generates embeddings via OpenRouter API

Memory tools: `MemoryStoreTool`, `MemoryRecallTool`, `MemoryForgetTool`, `MemoryListTool`, `MemoryReflectTool`

### Web Search

Web search and content fetching capabilities:

- **IWebSearchClient** (Domain) - Interface for web search
- **BraveSearchClient** (Infrastructure) - Brave Search API integration
- **IWebFetcher** (Domain) - Interface for fetching web content
- **WebContentFetcher** (Infrastructure) - Fetches and processes web page content

### Command Runners

Platform-specific command execution:

- **ICommandRunner** (Domain) - Interface for command execution
- **IAvailableShell** (Domain) - Interface for detecting available shells
- Implementations: `BashRunner`, `CmdRunner`, `PowerShellRunner`, `ShRunner`

## Testing

- Unit tests go in `Tests/` project
- Follow existing test patterns and naming conventions
- Agent behaviors should be testable
- Integration tests requiring Redis use `RedisFixture` for container management

## Documentation

- Prioritize readable code over comments
- Do not add XML documentation comments
- Only comment when truly needed - explain "why" not "what"
