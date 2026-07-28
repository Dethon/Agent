# Per-agent OpenRouter provider routing

**Date:** 2026-07-28
**Status:** Approved, ready for implementation planning

## Problem

The only provider-routing control the agent has today is the `:nitro` suffix baked into a
model string — `z-ai/glm-5.2:nitro` on Jonas, Nabu and `jonas-worker`. That suffix is
OpenRouter shorthand for `provider.sort = "throughput"`, so the current configuration can
express exactly one routing policy, applied to whichever agents happen to carry the suffix.

There is no way to prefer price or latency for one agent, to pin an agent to a provider
whose behaviour is known-good, or to exclude a provider that has been serving bad
completions. OpenRouter exposes all of this through the request body's `provider` object;
the agent never sends one.

## Goal

Let each agent and subagent declare its own provider-routing policy in configuration, with
a process-wide default for everything that does not.

## Non-goals

- Exposing OpenRouter's compliance and pricing filters (`requireParameters`,
  `dataCollection`, `quantizations`, `maxPrice`, `zdr`, `preferredMinThroughput`,
  `preferredMaxLatency`). They can be added later as additional properties on the same
  record without changing any of the plumbing below.
- The object form of `sort`. Only the string form (`price` / `throughput` / `latency`) is
  supported.
- Routing for embeddings. `OpenRouterEmbeddingService` posts to `/embeddings` on its own
  `HttpClient`, outside the chat-client path, and the configured embedding model
  (`openai/text-embedding-3-small`) realistically has one provider.
- Per-field merging of an agent's routing with the global default. See Precedence.

## Configuration

A new record in `Domain/DTOs/ProviderRouting.cs`:

```csharp
public record ProviderRouting
{
    public ProviderSort? Sort { get; init; }
    public string[]? Order { get; init; }
    public string[]? Only { get; init; }
    public string[]? Ignore { get; init; }
    public bool? AllowFallbacks { get; init; }
}

public enum ProviderSort { Price, Throughput, Latency }
```

`Sort` is an enum rather than a string so that .NET configuration binding rejects an
unknown value at bind time on its own, naming the offending path
(`agents:1:providerRouting:sort`). This is startup validation for free: no hand-written
validator, and an invalid `sort` can never reach OpenRouter.

Every property is nullable so that "unset" is distinguishable from "empty". A null
property is **omitted** from the serialized object, never sent as JSON `null`.

The record is added as `ProviderRouting? ProviderRouting { get; init; }` to three types:

| Type | Project | Role |
|---|---|---|
| `AgentDefinition` | `Domain/DTOs` | per-agent override |
| `SubAgentDefinition` | `Domain/DTOs` | per-subagent override |
| `OpenRouterConfiguration` / `OpenRouterConfig` | `Agent/Settings`, `Infrastructure/Agents` | process-wide default |

Example:

```json
"openRouter": {
    "apiUrl": "https://openrouter.ai/api/v1/",
    "providerRouting": { "sort": "throughput" }
},
"agents": [
    {
        "id": "nabu",
        "model": "z-ai/glm-5.2",
        "providerRouting": {
            "sort": "latency",
            "order": ["deepinfra", "novita"],
            "ignore": ["chutes"],
            "allowFallbacks": true
        }
    },
    { "id": "jack", "model": "z-ai/glm-5.2" }
]
```

Per the repository's environment-variable rule, `providerRouting` is a generic tunable and
belongs in `appsettings.json` alone. It gets no `docker-compose.yml` entry and no
`DockerCompose/.env` placeholder.

### Precedence

Resolution is wholesale replacement:

```
effectiveRouting = definition.ProviderRouting ?? globalConfig.ProviderRouting
```

An agent that sets `providerRouting` owns the entire object; it does not inherit
individual fields from the global default. This keeps resolution predictable and prevents
an agent from silently inheriting an `ignore` list that is not visible at its own
configuration site.

When neither is set, no `provider` key is added to the request body at all — byte-identical
to what the agent sends today.

### Field semantics worth recording

`order` is a *preference* while `allowFallbacks` is true (OpenRouter's default), and a
*hard restriction* when it is false. Pinning an agent to a specific provider therefore
requires setting both fields.

## Plumbing

The routing value follows exactly the route `sessionId` already travels. No new mechanism
is introduced.

```
AgentDefinition.ProviderRouting
  -> MultiAgentFactory.CreateChatClient        (?? global default)
     -> OpenRouterChatClient ctor              (new optional parameter)
        -> ReasoningHandler                    (captured, as sessionId is)
           -> OpenRouterHttpHelpers.PrepareRequestBodyAsync
              -> obj["provider"] = { ... }
```

`PrepareRequestBodyAsync` gains a `ProviderRouting?` parameter and one more stamp beside
the existing `session_id` and `usage` lines. Its eleven existing call sites in
`Tests/Unit/Infrastructure/OpenRouterHttpHelpersTests.cs` are updated to pass the new
argument.

Mapping the record to the wire object — snake_case `allow_fallbacks`, lowercased `sort` —
lives in Infrastructure alongside `PrepareRequestBodyAsync`. The Domain record stays free
of JSON concerns, per `.claude/rules/domain-layer.md`.

`MultiAgentFactory` resolves the effective routing in `CreateChatClient` for both entry
points, `CreateFromDefinition` (agents) and `CreateSubAgent` (subagents).

`MemoryModule` picks up the global default with a
`config.GetSection("openRouter:providerRouting").Get<ProviderRouting>()` bind, passed into
both `OpenRouterChatClient` constructions — `IMemoryExtractor` and `IMemoryConsolidator`.
Memory models therefore inherit the default and have no per-model override, which matches
the fact that their models are configured as bare strings under `Memory:`.

`MultiAgentFactory`'s `chatClientFactory` delegate gains a `ProviderRouting?` argument. The
delegate is currently unreferenced, but it is the natural seam for asserting which routing
an agent resolves to without an HTTP round trip.

## Conflict detection

Because `:nitro` means `sort: throughput` and `:floor` means `sort: price`, a suffix
combined with a disagreeing `providerRouting.sort` sends OpenRouter two contradictory
instructions with no documented winner.

A pure Domain helper detects this, declared in `Domain/DTOs/ProviderRouting.cs` beside the
record it inspects:

```csharp
ProviderRoutingConflict.Detect(model, routing) -> string?
```

It returns a message when the model carries a routing suffix and `routing.Sort` is set to
a different value, and null otherwise. A suffix combined with `order`, `only`, `ignore` or
`allowFallbacks` but no `sort` is not a conflict and returns null.

`MultiAgentFactory` calls it where the logger already exists, so detection covers both
appsettings agents and agents minted at runtime through `CustomAgentRegistry`. The result
is logged as a warning naming the agent; the request is still sent unchanged. A warning
rather than a throw, because the same code path serves runtime-created agents where a
throw would fail a live conversation rather than a boot.

## Migration

The `:nitro` suffixes currently in the repository are rewritten to explicit configuration,
preserving today's behaviour exactly:

| Location | Before | After |
|---|---|---|
| `Agent/appsettings.json` — agent `jonas` | `z-ai/glm-5.2:nitro` | `z-ai/glm-5.2` + `providerRouting.sort = throughput` |
| `Agent/appsettings.json` — agent `nabu` | `z-ai/glm-5.2:nitro` | `z-ai/glm-5.2` + `providerRouting.sort = throughput` |
| `Agent/appsettings.json` — subagent `jonas-worker` | `z-ai/glm-5.2:nitro` | `z-ai/glm-5.2` + `providerRouting.sort = throughput` |
| `Agent/Modules/MemoryModule.cs:40` — extraction fallback | `z-ai/glm-4.7-flash:nitro` | `z-ai/glm-4.7-flash` |
| `Agent/Modules/MemoryModule.cs:56` — dreaming fallback | `z-ai/glm-4.7-flash:nitro` | `z-ai/glm-4.7-flash` |

The two `MemoryModule` strings are hard-coded fallbacks used only when `Memory:Extraction:Model`
and `Memory:Dreaming:Model` are absent from configuration. They are currently present in
`appsettings.json` as `openai/gpt-5.4-nano`, so the fallbacks are inert today; they are
migrated for consistency and pick up throughput sorting from the global default instead.

Agent `jack` carries no suffix today and gets no per-agent `providerRouting`, so it changes
from sending no routing at all to inheriting the global default. This is the one
intentional behaviour change in the migration.

The global default is set to `{ "sort": "throughput" }`, matching the majority of today's
configuration.

## Testing

Red-Green-Refactor per triplet, following `.claude/rules/testing.md`. `OpenRouterHttpHelpersTests`
is the primary surface and already establishes the request-construction pattern.

**Serialization** (`Tests/Unit/Infrastructure/OpenRouterHttpHelpersTests.cs`)

- Each field maps to its OpenRouter key; `AllowFallbacks` becomes `allow_fallbacks`.
- `Sort` serializes lowercased (`ProviderSort.Throughput` -> `"throughput"`).
- Unset fields are absent from the `provider` object rather than present as `null`.
- A null `ProviderRouting` produces no `provider` key at all — this is the regression guard
  proving existing traffic is unchanged.
- An empty `ProviderRouting` (all fields null) likewise produces no `provider` key.

**Precedence** (`Tests/Unit/Infrastructure/MultiAgentFactoryTests.cs`, via the
`chatClientFactory` seam)

- An agent with `providerRouting` resolves to its own object, not the global default.
- An agent without one resolves to the global default.
- Neither set resolves to null.
- The same three cases for `CreateSubAgent`.

**Conflict detection** (new `Tests/Unit/Domain/ProviderRoutingConflictTests.cs`)

- `:nitro` + `sort: price` returns a message.
- `:floor` + `sort: throughput` returns a message.
- `:nitro` + `sort: throughput` returns null (agreement, not a conflict).
- `:nitro` + `order` only, no `sort`, returns null.
- No suffix + any routing returns null.
- No suffix + no routing returns null.

**Binding** (new `Tests/Unit/Agent/ProviderRoutingBindingTests.cs`)

- A valid `sort` string binds to the matching `ProviderSort` member, case-insensitively.
- An invalid `sort` string fails to bind, with the exception naming the configuration path.

These build an in-memory `ConfigurationBuilder` rather than reading the repository file,
following the pattern in `Tests/Unit/McpChannelVoice/*SettingsBindingTests.cs`.

**Migration** (`Tests/Unit/Agent/AgentAppSettingsTests.cs`)

- The migrated agents still resolve to `throughput`, and no agent or subagent model string
  carries a `:nitro` or `:floor` suffix any more, so the migration cannot silently regress.
  These read the working-tree `appsettings.json` through the existing `RepoRoot()` helper,
  matching how the file's other pins work.

## Files touched

New:

- `Domain/DTOs/ProviderRouting.cs` (the record, the `ProviderSort` enum and
  `ProviderRoutingConflict`)
- `Tests/Unit/Domain/ProviderRoutingConflictTests.cs`
- `Tests/Unit/Agent/ProviderRoutingBindingTests.cs`

Modified:

- `Domain/DTOs/AgentDefinition.cs`
- `Domain/DTOs/SubAgentDefinition.cs`
- `Agent/Settings/AgentSettings.cs` (`OpenRouterConfiguration`)
- `Infrastructure/Agents/MultiAgentFactory.cs` (`OpenRouterConfig`, `CreateChatClient`,
  `CreateFromDefinition`, `CreateSubAgent`, `chatClientFactory` delegate)
- `Infrastructure/Agents/ChatClients/OpenRouterChatClient.cs` (constructors,
  `ReasoningHandler`)
- `Infrastructure/Agents/ChatClients/OpenRouterHttpHelpers.cs` (`PrepareRequestBodyAsync`,
  wire mapping)
- `Agent/Modules/InjectorModule.cs` (carry `ProviderRouting` into `OpenRouterConfig`)
- `Agent/Modules/MemoryModule.cs` (bind the default, pass to both chat clients, drop
  `:nitro` from the two fallback strings)
- `Agent/appsettings.json` (global default, migrate three model strings)
- `Tests/Unit/Infrastructure/OpenRouterHttpHelpersTests.cs` (11 call sites plus new tests)
- `Tests/Unit/Infrastructure/MultiAgentFactoryTests.cs`
- `Tests/Unit/Agent/AgentAppSettingsTests.cs`
- `CLAUDE.md` (a line on provider routing where agent configuration is described)

Note: `.cs` files carry no trailing newline (`.editorconfig` sets
`insert_final_newline = false`).
