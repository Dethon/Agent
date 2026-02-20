# Agent

AI agent via Telegram/WebChat/MessageBus/CLI using .NET 10 LTS, MCP, and OpenRouter LLMs.

## Verify Before Assuming

Before proposing any architectural change or debugging hypothesis, first verify your assumptions by checking the actual current state (read the file, run the command, check the config). Never assume something is missing or broken without evidence.

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

## Environment Variables

When adding code that reads new environment variables or configuration values, you **must** update all relevant infrastructure files in the same change:

- `DockerCompose/docker-compose.yml` — add the variable to the appropriate service's `environment` section (use placeholder values like `${VAR_NAME}` or `changeme`).
- `DockerCompose/.env` — add a placeholder entry for the new variable.
- `appsettings.json` / `appsettings.Development.json` — add the corresponding configuration key with a placeholder value.

Do not defer these updates to a later step. The skeleton must exist at the same time the code that maps the variable is created.

## Multi-Agent Patterns

### Handling Agent Failures

- If a worker agent appears stuck (no progress after a reasonable period), **replace it** — spawn a fresh agent for the same task rather than retrying the stuck one indefinitely.
- Do not retry the same failing action more than twice. After two failures, reassess the approach or escalate to the user.
- When a worker reports an error, the orchestrator should decide whether to reassign, adjust the task, or abort — not blindly re-dispatch.

### Layer Completion Verification

Before marking a layer of work as done, **verify every agent in that layer has completed**. Do not assume completion from partial signals. Check `TaskList` to confirm all tasks in the layer are `completed` before proceeding to dependent work or reporting success.

### Auto-Commit After Triplets

When executing TDD plans with triplet tasks (RED → GREEN → REVIEW), **commit after each triplet completes successfully**. This keeps the history granular and makes rollbacks cheap. The commit message should reference the triplet's feature or task name.

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

When automating the WebChat with Playwright, use `ignoreHTTPSErrors: true` for the browser context when testing locally (the Let's Encrypt certificate is valid for `assistants.herfluffness.com`, not `localhost`). You must select a user identity from the avatar picker in the header before sending messages, otherwise sends are silently rejected with a toast error.