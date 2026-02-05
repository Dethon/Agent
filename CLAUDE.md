# Agent

AI agent via Telegram/WebChat/CLI using .NET 10 LTS, MCP, and OpenRouter LLMs.

## Architecture

**Layers** (dependencies flow inward): `Agent` → `Infrastructure` → `Domain`

- **Domain**: Contracts, DTOs, pure business logic (no external deps)
- **Infrastructure**: Implementations, external clients, state management
- **Agent**: DI, config, entry point

**Interface policy**: Only create interfaces when Domain needs to consume them.

See `.claude/rules/` for layer-specific coding rules.

## Codebase Documentation

Detailed documentation in `docs/codebase/`:

| File | Content |
|------|---------|
| `ARCHITECTURE.md` | Agent system, message pipeline, tool approval, MCP integration |
| `STRUCTURE.md` | Directory layout, module boundaries |
| `STACK.md` | Tech stack, packages, dependencies |
| `INTEGRATIONS.md` | External services (OpenRouter, Telegram, Redis, etc.) |
| `CONVENTIONS.md` | Coding style, patterns, rules |
| `TESTING.md` | Test framework, TDD workflow, organization |
| `CONCERNS.md` | Technical debt, risks, security considerations |
| `maps/code-map-*.json` | Structural code maps |

## Projects

| Project | Purpose |
|---------|---------|
| `Agent` | Composition root, Telegram bot, DI |
| `Domain` | Contracts, DTOs, business logic |
| `Infrastructure` | External clients, agent implementations |
| `McpServer*` | MCP servers (Library, Text, WebSearch, Memory, Idealista, CommandRunner) |
| `WebChat`/`.Client` | Blazor WebAssembly chat interface |
| `Tests` | Unit and integration tests |

## Key File Locations

| What | Where |
|------|-------|
| Contracts | `Domain/Contracts/*.cs` |
| DTOs | `Domain/DTOs/*.cs` |
| Agent implementations | `Infrastructure/Agents/*.cs` |
| External clients | `Infrastructure/Clients/**/*.cs` |
| MCP tools | `McpServer*/McpTools/*.cs` |
| WebChat state | `WebChat.Client/State/**/*.cs` |
| Tests | `Tests/{Unit,Integration}/**/*Tests.cs` |

## Key Types

- **AgentDefinition** - Agent config (model, MCP endpoints, instructions)
- **MultiAgentFactory** - Creates/routes agents by config
- **ChatMonitor** - Streaming message pipeline
- **IToolApprovalHandler** - User approval for tool execution
- **IMemoryStore** - Vector memory (Redis-backed)

## Multi-Agent Config

Agents configured in `appsettings.json` under `"agents"` array. Each has id, name, model, MCP endpoints, whitelist patterns, custom instructions, and optional telegram token.

## WebChat State

Redux-like pattern in `WebChat.Client/State/`: Stores + Effects + HubEventDispatcher

## NuGet

The NuGet package cache may be in a non-standard location. Check the `NUGET_PACKAGES` environment variable to find the actual path before assuming `~/.nuget/packages`.
