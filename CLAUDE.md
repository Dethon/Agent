# Agent

AI agent via Telegram/WebChat/MessageBus/CLI using .NET 10 LTS, MCP, and OpenRouter LLMs.

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

## Projects

| Project | Purpose |
|---------|---------|
| `Agent` | Composition root, Telegram bot, DI |
| `Domain` | Contracts, DTOs, business logic |
| `Infrastructure` | External clients, agent implementations |
| `McpServer*` | MCP servers (Library, Text, WebSearch, Memory, Idealista, CommandRunner) |
| `WebChat`/`.Client` | Blazor WebAssembly chat interface, Redux-like state (Stores + Effects + HubEventDispatcher) |
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

## TDD

Follow Red-Green-Refactor for all features and bug fixes. Write a failing test first, then implement. See `.claude/rules/tdd.md` for full workflow.

## Local Development

### Docker Compose files

| File                                                | Purpose |
|-----------------------------------------------------|---------|
| `DockerCompose/docker-compose.yml`                  | Main service definitions |
| `DockerCompose/docker-compose.override.windows.yml` | Windows user secrets mount (`%APPDATA%/Microsoft/UserSecrets`) |
| `DockerCompose/docker-compose.override.linux.yml`   | Linux user secrets mount (`$HOME/.microsoft/usersecrets`) |

### Launching

Pick the override file matching your OS:

```bash
# Linux / WSL
docker compose -f DockerCompose/docker-compose.yml -f DockerCompose/docker-compose.override.linux.yml -p jackbot up -d --build agent webui mcp-text mcp-websearch mcp-memory mcp-idealista mcp-library qbittorrent jackett redis caddy

# Windows
docker compose -f DockerCompose/docker-compose.yml -f DockerCompose/docker-compose.override.windows.yml -p jackbot up -d --build agent webui mcp-text mcp-websearch mcp-memory mcp-idealista mcp-library qbittorrent jackett redis caddy
```

### Secrets

Services read secrets from .NET User Secrets mounted into containers at `/home/app/.microsoft/usersecrets`. The override files map the host-side path. If the agent crashes with `Value cannot be an empty string. (Parameter 'connectionString')`, user secrets are not being mounted — check you're using the correct override for your OS.

### Accessing the WebChat

Caddy (port 443, Let's Encrypt TLS) is the entry point. It routes `/hubs/*` to the agent's SignalR hub and everything else to the WebUI. **Connect through Caddy, not directly to webui:5001**, or SignalR won't reach the agent backend.

### Debugging with Playwright

When automating the WebChat with Playwright, use `ignoreHTTPSErrors: true` for the browser context when testing locally (the Let's Encrypt certificate is valid for `assistants.herfluffnes.com`, not `localhost`). You must select a user identity from the avatar picker in the header before sending messages, otherwise sends are silently rejected with a toast error.