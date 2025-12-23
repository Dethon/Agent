# Copilot Instructions for Jack AI Media Library Agent

## Overview

This repository contains **Jack**, an AI-powered agent that manages a personal media library through Telegram chat, using OpenRouter LLMs and the Model Context Protocol (MCP).

## Technology Stack

- **.NET 10** - Target framework
- **Model Context Protocol (MCP)** - Tool integration architecture
- **OpenRouter LLMs** - AI model provider (Gemini, GPT-4, etc.)
- **Microsoft.Extensions.AI** - LLM abstraction layer
- **Microsoft.Agents.AI** - Agent framework
- **Redis** - Conversation state persistence
- **Docker Compose** - Deployment stack

## Project Structure

| Project | Layer | Purpose |
|---------|-------|---------|
| `Jack` | Application | Composition root, Telegram bot, DI configuration |
| `Domain` | Domain | Pure business logic, contracts, DTOs, exceptions |
| `Infrastructure` | Infrastructure | External service clients, agent implementations |
| `McpServerLibrary` | Module | MCP server for torrent search, downloads, file organization |
| `McpServerText` | Module | MCP server for text/markdown file inspection and editing |
| `McpServerCommandRunner` | Module | MCP server for CLI command execution |
| `Tests` | Testing | Unit and integration tests |
| `DockerCompose` | Deployment | Docker Compose configuration |

## Architecture Rules

### Dependency Direction
Dependencies flow inward: `Jack` → `Infrastructure` → `Domain`

- **Domain**: Must NOT reference Infrastructure or Jack
- **Infrastructure**: May reference Domain, must NOT reference Jack
- **Jack**: May reference both Domain and Infrastructure

### Layer Responsibilities

- **Domain**: Interfaces, DTOs, domain services, pure business logic
- **Infrastructure**: Implementations, external clients, state management
- **Jack**: Bootstrapping, DI, configuration, application entry point

## Coding Standards

### Modern .NET Patterns
- Use file-scoped namespaces
- Use primary constructors where appropriate
- Prefer `record` types for DTOs and immutable data
- Use nullable reference types and proper null handling
- Apply `async`/`await` throughout for asynchronous operations
- Use `ArgumentNullException.ThrowIfNull()` for guard clauses
- Prefer `IReadOnlyList<T>` and `IReadOnlyCollection<T>` for return types

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

Flow: Agent message → RedisChatMessageStore.AddMessagesAsync → Redis SET with expiry

### Chat Commands

The `ChatCommandParser` handles user commands in `ChatMonitor`:

| Command | Action |
|---------|--------|
| `/cancel` | Cancels current operation, keeps conversation history |
| `/clear` | Clears conversation and deletes persisted state from Redis |

### Tool Approval System

Tool execution requires user approval through the `IToolApprovalHandler` interface:

- **ToolApprovalChatClient** - Wraps the chat client to intercept tool calls
- **TelegramToolApprovalHandler** - Shows inline keyboard for approve/reject in Telegram
- **CliToolApprovalHandler** - Shows modal dialog in CLI interface

Approval results:
- `Approved` - Execute once
- `ApprovedAndRemember` - Auto-approve this tool for the session
- `Rejected` - Block execution
- `AutoApproved` - Matched whitelist pattern

### CLI Interface Components

Located in `Infrastructure/Clients/Cli/`:

- **CliChatMessengerClient** - `IChatMessengerClient` implementation for terminal, restores history on startup
- **CliChatMessageRouter** - Routes messages between UI and agent
- **CliToolApprovalHandler** - Terminal-based approval dialogs
- **CliCommandHandler** - Handles `/cancel`, `/clear`, `/help` commands
- **ChatHistoryMapper** - Maps persisted chat messages to display format
- **TerminalGuiAdapter** - Terminal.Gui wrapper for the TUI

---

## 🔥 CRITICAL: Use Subagents for Specialized Tasks

**This repository has specialized subagents available. ALWAYS delegate to the appropriate subagent when the task matches their expertise.**

### Available Subagents

| Subagent | Expertise | When to Use |
|----------|-----------|-------------|
| **@agentic-workflows** | Agentic patterns, MCP tools, conversation management | Designing agent capabilities, adding MCP tools, prompt engineering |
| **@code-simplifier** | Refactoring, readability, conciseness | Simplifying complex code, reducing redundancy, cleanup |
| **@implementation-helper** | Implementation of methods, classes, DTOs | Any "implement X" or "create method for Y" requests |
| **@layer-separation** | Architecture compliance, dependency rules | Checking/enforcing layer separation, fixing violations |
| **@dotnet-agent-framework** | Microsoft.Agents.AI framework | Creating AI agents, tool calling, agent composition |

### Delegation Guidelines

1. **Always consider subagents first** - Before implementing a task yourself, check if a specialized subagent is better suited

2. **Delegate implementation tasks** - When asked to implement, create, or write code:
   ```
   @implementation-helper implement a new extension method for string validation
   ```

3. **Delegate architecture reviews** - When code affects multiple layers:
   ```
   @layer-separation check if this change violates architecture rules
   ```

4. **Delegate agent design** - For MCP tools or agent behavior:
   ```
   @agentic-workflows design a new MCP tool for playlist management
   ```

5. **Delegate .NET agent work** - For Microsoft.Agents.AI patterns:
   ```
   @dotnet-agent-framework create a delegating agent for request routing
   ```

6. **Delegate code cleanup** - For refactoring and simplification:
   ```
   @code-simplifier simplify this complex method
   ```

### Multiple Subagents

For complex tasks, use multiple subagents in sequence:
1. First: `@agentic-workflows` to design the approach
2. Then: `@implementation-helper` to implement the code
3. Finally: `@layer-separation` to verify architecture compliance

---

## Testing

- Unit tests go in `Tests/` project
- Follow existing test patterns and naming conventions
- Agent behaviors should be testable
- Integration tests requiring Redis use `RedisFixture` for container management

## Documentation & Comments

- **Prioritize readable code over comments** - Code should be self-documenting through clear naming
- **Avoid XML docs** - Do not add XML documentation comments
- **Only comment when truly needed** - Explain "why" not "what", and only when the code alone cannot convey intent
- Update README.md for user-facing changes