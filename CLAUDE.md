# Agent

AI agent via Telegram/WebChat/CLI using .NET 10 LTS, MCP, and OpenRouter LLMs.

## Architecture

**Layers** (dependencies flow inward): `Agent` → `Infrastructure` → `Domain`

- **Domain**: Contracts, DTOs, pure business logic (no external deps)
- **Infrastructure**: Implementations, external clients, state management
- **Agent**: DI, config, entry point

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

## Multi-Agent Config

Agents configured in `appsettings.json` under `"agents"` array. Each has id, name, model, MCP endpoints, whitelist patterns, custom instructions, and optional telegram token.

## WebChat State

Redux-like pattern in `WebChat.Client/State/`: Stores + Effects + HubEventDispatcher

## TDD

Follow Red-Green-Refactor for all features and bug fixes. Write a failing test first, then implement. See `.claude/rules/tdd.md` for full workflow.

## LSP

Prefer using the LSP tool over Grep/Glob for code navigation when possible:

- **goToDefinition** to find where a type/method is defined instead of grepping for `class Foo`
- **findReferences** to find all usages of a symbol instead of grepping for its name
- **goToImplementation** to find concrete implementations of interfaces
- **hover** to check type info and signatures without reading entire files
- **incomingCalls/outgoingCalls** to trace call chains instead of manual searching

Fall back to Grep/Glob when LSP is unavailable or for pattern-based searches (e.g. finding all TODO comments, searching config files, or matching across non-code files).

## NuGet

The NuGet package cache may be in a non-standard location. Check the `NUGET_PACKAGES` environment variable to find the actual path before assuming `~/.nuget/packages`.

## Devcontainer

A Docker-in-Docker devcontainer is available for isolated Claude Code sessions:

```bash
# Start
cd .devcontainer && docker compose up -d

# Enter
docker exec -it dev bash

# First run: install Claude Code and configure
./setup.sh

# Start Claude Code
cd /workspace
claude --dangerously-skip-permissions

# Run the agent stack (inside DinD)
cd /workspace/DockerCompose
docker compose up -d
```

The devcontainer mounts the repo at `/workspace`, `.claude` config at `/home/devuser/.claude`, and .NET User Secrets as read-only. Docker commands inside the container talk to a DinD sidecar, fully isolated from the host Docker daemon.
