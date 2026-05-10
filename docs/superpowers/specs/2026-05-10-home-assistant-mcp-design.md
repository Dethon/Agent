# Home Assistant MCP Server — Design

**Date:** 2026-05-10
**Status:** Draft (pending user review)

## Goal

Give the agent control over the user's Home Assistant instance — and through it, every connected device (vacuum, lights, climate, media, sensors, …) — via a small, generic MCP tool surface. The original prompt was "control my Roborock vacuum cleaner"; routing through Home Assistant is the more general solution because the user's S8 already needs cloud connectivity and HA already speaks Roborock's protocol.

## Non-goals

- Direct Roborock cloud / MQTT / miio integration.
- Domain-typed tools (`vacuum_start`, `light_turn_on`, …). The agent uses generic discovery + service-call tools.
- WebSocket subscriptions or live state push to the agent.
- E2E tests that spin up Home Assistant in CI.
- Any custom approval logic. All four tools are added to the existing `ToolApprovalChatClient` whitelist; no per-call prompts.

## Architecture

A new `McpServerHomeAssistant` project, sibling to `McpServerIdealista` and the rest. Layering follows the established pattern:

```
Agent (existing)
  └─ ToolApprovalChatClient (existing)
       └─ MCP HTTP transport
            └─ McpServerHomeAssistant
                 ├─ McpHomeListEntitiesTool
                 ├─ McpHomeGetStateTool
                 ├─ McpHomeListServicesTool
                 └─ McpHomeCallServiceTool
                      └─ IHomeAssistantClient (Domain contract)
                           └─ HomeAssistantClient (Infrastructure, HttpClient + Bearer)
                                └─ HTTPS → homeassistant container :8123
```

`homeassistant` runs as a container in the same `docker-compose` stack with a persistent bind-mounted config directory. The MCP server reaches it over the compose network at `http://homeassistant:8123`. The user reaches it from any LAN machine for browser-based configuration via published port `8123`.

## Components

### Domain layer

- `Domain/Contracts/IHomeAssistantClient.cs` — minimal interface:
  - `Task<IReadOnlyList<HaEntityState>> ListStatesAsync(CancellationToken)`
  - `Task<HaEntityState?> GetStateAsync(string entityId, CancellationToken)` (returns null on 404)
  - `Task<IReadOnlyList<HaServiceDefinition>> ListServicesAsync(CancellationToken)`
  - `Task<HaServiceCallResult> CallServiceAsync(string domain, string service, string? entityId, IReadOnlyDictionary<string, object?>? data, CancellationToken)`
- `Domain/DTOs/HomeAssistant/*.cs` — records: `HaEntityState`, `HaEntitySummary`, `HaServiceDefinition`, `HaServiceField`, `HaServiceCallResult`.
- `Domain/Tools/HomeAssistant/*.cs` — pure Domain tool classes (`HomeListEntitiesTool`, `HomeGetStateTool`, `HomeListServicesTool`, `HomeCallServiceTool`) holding `Name`/`Description` constants and a `RunAsync` that delegates to `IHomeAssistantClient`.
- `Domain/Prompts/HomeAssistantPrompt.cs` — `Name`, `Description`, `SystemPrompt` constants matching the `IdealistaPrompt` convention. The `SystemPrompt` teaches: discover before acting (`home_list_entities` → `home_list_services` → `home_call_service`); prefer the `entity_id` parameter over inlining it in `data`; common service patterns per domain (e.g. `vacuum.start` / `vacuum.return_to_base`, `light.turn_on` with `brightness_pct`, `climate.set_temperature`); and that `home_get_state` is the way to confirm an action took effect.
- `Domain/Exceptions/HomeAssistant*.cs` — typed exceptions: `HomeAssistantUnauthorizedException`, `HomeAssistantNotFoundException`, generic `HomeAssistantException`.

### Infrastructure layer

- `Infrastructure/Clients/HomeAssistant/HomeAssistantClient.cs` — `HttpClient`-based, Bearer auth from settings, returns Domain DTOs.
- `Infrastructure/Extensions/HomeAssistantClientExtensions.cs` — `services.AddHomeAssistantClient(settings)` matching the `AddIdealistaClient` style (HttpClient registration + 2-attempt 1s-backoff retry policy on transient 5xx/network errors).

### MCP server project

`McpServerHomeAssistant/` mirrors `McpServerIdealista`:

- `Program.cs` — `WebApplication.CreateBuilder` + `MapMcp("/mcp")`.
- `Settings/McpSettings.cs` — `HomeAssistantConfiguration { BaseUrl, Token }`.
- `Modules/ConfigModule.cs` — registers settings, HA client, MCP server with `AddCallToolFilter` (global error → `ToolResponse.Create(ex)`), the four `McpHome*Tool` types via `.WithTools<...>()`, and `McpSystemPrompt` via `.WithPrompts<McpSystemPrompt>()`.
- `McpTools/McpHomeListEntitiesTool.cs`, `McpHomeGetStateTool.cs`, `McpHomeListServicesTool.cs`, `McpHomeCallServiceTool.cs` — each inherits the corresponding Domain tool, applies `[McpServerToolType]` / `[McpServerTool]` / `[Description]` attributes, returns `CallToolResult` via `ToolResponse.Create()`.
- `McpPrompts/McpSystemPrompt.cs` — `[McpServerPromptType]` class with a `[McpServerPrompt]` static method returning `HomeAssistantPrompt.SystemPrompt`. Mirrors `McpServerIdealista/McpPrompts/McpSystemPrompt.cs`.
- `appsettings.json` — placeholder `HomeAssistant.BaseUrl`.
- `Dockerfile` — same shape as other MCP servers.

## Tool surface

All tools prefixed `home_`. The generic-tools choice means new HA capabilities surface automatically without code changes.

### `home_list_entities`

- **Inputs:** `domain?: string`, `area?: string`, `limit?: int = 100`.
- **Output:** `[{ entity_id, state, friendly_name, domain, last_changed }]` — projection of `/api/states` to keep context cost low. Sorted by `entity_id`.
- **Behavior:** GET `/api/states`, filter where the `entity_id`'s domain (the part before the first dot) equals `domain`, optionally substring-match `area` against `friendly_name`, project, take `limit`.

### `home_get_state`

- **Inputs:** `entity_id: string` (required).
- **Output:** `{ entity_id, state, attributes, last_changed, last_updated }` — full attributes for one entity.
- **Behavior:** GET `/api/states/{entity_id}`. 404 → `{ ok: false, message: "entity not found" }`.

### `home_list_services`

- **Inputs:** `domain?: string`.
- **Output:** `[{ domain, service, description, fields: { name: { description, required, example? } } }]` — flattened list (HA returns nested `{ domain: { service: meta } }`).
- **Behavior:** GET `/api/services`, flatten, filter by domain if provided.

### `home_call_service`

- **Inputs:** `domain: string`, `service: string`, `entity_id?: string`, `data?: object`.
- **Output:** `{ ok: true, changed_entities: [{ entity_id, state }] }` — HA returns the touched entities; we surface that as confirmation. Non-2xx → `{ ok: false, message }`.
- **Behavior:** POST `/api/services/{domain}/{service}`. `entity_id`, when provided, is hoisted into `target.entity_id` (modern HA API); `data` keys go in the body root. If both `entity_id` parameter and `data.entity_id` are present, the explicit `entity_id` parameter wins.

## Data flow — typical "clean the kitchen" turn

1. Agent calls `home_list_entities(domain="vacuum")`.
2. `ToolApprovalChatClient` matches the name against the whitelist — auto-approved → invoked.
3. MCP server hits `GET /api/states`, projects vacuums, returns the list.
4. Agent sees `vacuum.roborock_s8` is `docked`, calls `home_call_service(domain="vacuum", service="start", entity_id="vacuum.roborock_s8")`.
5. `home_call_service` is also whitelisted → auto-approved → invoked.
6. MCP server POSTs `/api/services/vacuum/start` with body `{"target":{"entity_id":"vacuum.roborock_s8"}}` → HA returns the changed-entities list.
7. Tool returns `{ ok: true, changed_entities: [...] }` → agent reports back to user.

All four tool names go into the agent's existing whitelist; no per-call approval prompt.

## Hosting & configuration

### docker-compose

Two new services in `DockerCompose/docker-compose.yml`:

```yaml
homeassistant:
  image: ghcr.io/home-assistant/home-assistant:stable
  container_name: homeassistant
  restart: unless-stopped
  volumes:
    - ./volumes/homeassistant_config:/config
  environment:
    - TZ=Europe/Madrid
  ports:
    - "8123:8123"

mcp-server-homeassistant:
  build:
    context: ..
    dockerfile: McpServerHomeAssistant/Dockerfile
  environment:
    - HomeAssistant__BaseUrl=http://homeassistant:8123
    - HomeAssistant__Token=${HA_TOKEN}
  depends_on:
    - homeassistant
  volumes:
    # user-secrets mount as in other MCP services (per OS override)
```

Notes:
- Bind mount `./volumes/homeassistant_config:/config` matches the `qbittorrent_config` convention.
- `8123:8123` binds to all interfaces so the user can reach the HA web UI from any LAN machine.
- HA's official image runs as root and ignores `PUID`/`PGID`; intentionally omitted.
- Default bridge networking (not `network_mode: host`) — preserves service-name DNS for the MCP server. LAN auto-discovery features in HA are sacrificed; cloud-based integrations like Roborock work fine on bridge.
- The MCP server is added to the `up` command lists in `CLAUDE.md` after implementation.

### Secrets & configuration

- `DockerCompose/.env` — add `HA_TOKEN=` placeholder. Token is a secret.
- User Secrets (local, non-compose dev) — `HomeAssistant:Token`.
- `appsettings.json` / `appsettings.Development.json` — `HomeAssistant.BaseUrl` placeholder. Non-secret.
- Agent config — register the new MCP endpoint at `http://mcp-server-homeassistant:<port>/mcp` under the existing MCP servers list.
- Agent whitelist — add patterns covering all four tools (e.g. `home_*` or each name explicitly). The agent calls them without per-tool approval.

### One-time setup (documented for the user)

1. `docker compose -p jackbot up -d homeassistant`.
2. Browse to `http://<host>:8123` from any LAN machine, complete onboarding (create owner account).
3. Profile menu → Security → **Long-Lived Access Tokens** → create one → copy.
4. Set `HA_TOKEN=...` in `DockerCompose/.env`.
5. In HA → Settings → Devices & Services → Add Integration → **Roborock** → log in with the Roborock account. The Roborock S8 appears as `vacuum.<name>`.
6. `docker compose -p jackbot up -d mcp-server-homeassistant`.

## Error handling

- Global `AddCallToolFilter` in `ConfigModule` catches all exceptions and returns `ToolResponse.Create(ex)`. No per-tool try/catch.
- `HomeAssistantClient` throws typed exceptions:
  - 401 → `HomeAssistantUnauthorizedException` ("token invalid or expired").
  - 404 → for `GetState`, returns `null` (tool maps to `{ok:false}`); for service-call, throws `HomeAssistantNotFoundException` ("domain or service not found").
  - Other non-2xx → `HomeAssistantException` with status code and response body snippet.
- Retry policy: 2 attempts, 1s backoff, on transient 5xx/network only — same as Idealista.

## Testing

- **Unit (`Tests/Unit/HomeAssistant/*`)** — Domain tools tested with a fake `IHomeAssistantClient`. Coverage:
  - `home_list_entities` projection and domain/area filtering.
  - `home_get_state` 404 → `{ok:false}` mapping.
  - `home_call_service` `entity_id` hoisting into `target` and precedence over `data.entity_id`.
- **Integration (`Tests/Integration/HomeAssistant/*`)** — `HomeAssistantClient` against a `WireMock`-stubbed HA endpoint. Coverage:
  - Bearer header presence.
  - Request body shape for service calls (`target.entity_id` placement).
  - 401 → `HomeAssistantUnauthorizedException`, 404 → null/exception per route, transient 5xx triggers retry.
- **No E2E.** Manual smoke test against the docker-compose stack covers end-to-end.

## Out of scope (future work)

- WebSocket transport for live state subscriptions.
- Area / device / label registry surfacing.
- Recorder/history queries.
- Domain-typed convenience tools (e.g. `home_vacuum_start`) layered on top of the generic core.
- Caching `list_services`/`list_entities` to reduce token cost.
