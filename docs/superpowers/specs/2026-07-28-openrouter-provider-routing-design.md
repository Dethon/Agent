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
a process-wide default for everything that does not. That default ships **unset**, meaning
OpenRouter's balanced load balancing — so an agent only leaves the default behaviour by
asking to.

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
    "apiUrl": "https://openrouter.ai/api/v1/"
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

Here Nabu routes by latency across a preferred provider order, while Jack — with no
`providerRouting` and no global default — gets OpenRouter's balanced routing. That absence
is the intended global default; see below.

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

### The global default is balanced routing, expressed by omission

OpenRouter's default strategy — the one that applies when no `provider` object is sent — is
load balancing: providers with an outage in the last 30 seconds are filtered out, then one
is chosen with inverse-square price weighting, so a provider at $1/M tokens receives nine
times the traffic of one at $3/M. Remaining providers become fallbacks.

**There is no `sort` value that requests this.** Per OpenRouter's documentation, *"if you
have `sort` or `order` set in your provider preferences, load balancing will be disabled"* —
balanced routing is available only by omitting both. Setting `sort: "price"` is not the
same thing: it deterministically picks the cheapest provider rather than weighting across
several.

The process-wide default is therefore **no `providerRouting` key in the `openRouter`
section at all**. `OpenRouterConfiguration.ProviderRouting` stays null, agents that do not
override it send no `provider` object, and they get balanced routing. The setting remains
available for a future deployment that wants to move every agent at once, but ships unset.

A consequence for per-agent configuration: any agent that sets `sort` or `order` opts out
of load balancing for itself. An agent that sets only `ignore` or `only` is applying a
filter, and OpenRouter's documentation does not state that these disable load balancing —
treat that combination as unverified rather than assuming either behaviour.

### Interaction with sticky routing and the prompt cache

Every request already carries a `session_id` (`OpenRouterHttpHelpers.cs:37`) so that
OpenRouter pins the conversation to one provider and its prompt cache stays warm. The
measured effect is a 79–96% cache-hit rate after the first cold call in a conversation.
Provider routing interacts with this, and the two fields behave differently:

- **`sort` coexists with sticky routing.** Sticky routing activates on the first successful
  request and pins whichever provider the sort preference chose; later turns in the session
  keep hitting it. This is already the shipping arrangement — Jonas and Nabu send both
  `:nitro` and a `session_id` today.
- **`order` disables sticky routing.** Per OpenRouter's prompt-caching documentation,
  *"sticky routing is not used when you specify a manual provider order via
  `provider.order` — in that case, your explicit ordering takes priority."* The
  `session_id` is ignored and the ~17k-token static prefix is re-sent uncached every turn.

`only` and `ignore` are not mentioned in either the provider-routing or prompt-caching
documentation; their interaction with sticky routing is unverified and should not be
assumed either way.

This is a sharp edge precisely because `order` is the field someone reaches for when they
want to pin a provider — and the cost is invisible in the response. See Configuration
warnings for the guard.

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
the fact that their models are configured as bare strings under `Memory:`. With the default
unset they route balanced, exactly as they do today — the configured extraction and
dreaming models (`openai/gpt-5.4-nano`) carry no suffix.

`MultiAgentFactory`'s `chatClientFactory` delegate gains a `ProviderRouting?` argument. The
delegate is currently unreferenced, but it is the natural seam for asserting which routing
an agent resolves to without an HTTP round trip.

## Configuration warnings

Two routing configurations are legal, silent, and almost certainly not what the author
meant. A single pure Domain helper detects both, declared in
`Domain/DTOs/ProviderRouting.cs` beside the record it inspects:

```csharp
ProviderRoutingAdvisories.For(model, routing) -> IReadOnlyList<string>
```

**Suffix versus sort.** `:nitro` means `sort: throughput` and `:floor` means `sort: price`,
so a suffix combined with a disagreeing `routing.Sort` sends OpenRouter two contradictory
instructions with no documented winner. Emitted when the model carries a routing suffix and
`routing.Sort` is set to a different value. Agreement is not a conflict and emits nothing;
neither does a suffix combined with `order`, `only`, `ignore` or `allowFallbacks` when
`sort` is unset.

**`order` disables the prompt cache.** Emitted whenever `routing.Order` is non-empty,
regardless of the other fields. The message states that sticky routing is off, that the
`session_id` the request still carries is ignored, and that `only` plus `sort` restricts the
provider set without the cache cost — because the point of the warning is to redirect
someone who reached for `order` meaning "restrict", not "sequence".

`MultiAgentFactory` calls the helper where the logger already exists, so both advisories
cover appsettings agents and agents minted at runtime through `CustomAgentRegistry`. Each
message is logged as a warning naming the agent; the request is sent unchanged either way.
Warnings rather than throws, because the same code path serves runtime-created agents where
a throw would fail a live conversation rather than a boot.

The helper returns a list rather than a single nullable string so a configuration that
trips both — say `:nitro` with `sort: price` and an `order` — reports both rather than
whichever check ran first.

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

Agent `jack` carries no suffix today, gets no per-agent `providerRouting`, and inherits an
unset global default — so it keeps sending no `provider` object and stays on balanced
routing.

**The migration is therefore behaviour-preserving end to end.** Every caller sends exactly
what it sends today: `throughput` for Jonas, Nabu and `jonas-worker`, balanced for Jack and
for the two memory models. The only thing that changes is where the policy is written.

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

**Configuration warnings** (new `Tests/Unit/Domain/ProviderRoutingAdvisoriesTests.cs`)

Suffix versus sort:

- `:nitro` + `sort: price` yields one advisory.
- `:floor` + `sort: throughput` yields one advisory.
- `:nitro` + `sort: throughput` yields none (agreement, not a conflict).
- `:nitro` with `only`/`ignore`/`allowFallbacks` but no `sort` yields none.
- No suffix + any routing yields none.
- No suffix + no routing yields none.

`order` disabling the prompt cache:

- Any non-empty `order` yields an advisory, with and without a suffix, with and without
  `sort`.
- An empty or null `order` yields none.
- `:nitro` + `sort: price` + `order` yields **both** advisories, proving the helper does not
  stop at the first match.

**Binding** (new `Tests/Unit/Agent/ProviderRoutingBindingTests.cs`)

- A valid `sort` string binds to the matching `ProviderSort` member, case-insensitively.
- An invalid `sort` string fails to bind, with the exception naming the configuration path.

These build an in-memory `ConfigurationBuilder` rather than reading the repository file,
following the pattern in `Tests/Unit/McpChannelVoice/*SettingsBindingTests.cs`.

**Migration** (`Tests/Unit/Agent/AgentAppSettingsTests.cs`)

- The migrated agents still resolve to `throughput`, and no agent or subagent model string
  carries a `:nitro` or `:floor` suffix any more, so the migration cannot silently regress.
- The `openRouter` section declares no `providerRouting`. Balanced routing is expressed by
  omission and cannot be asserted from the request body of an agent that overrides it, so
  this pin is the only thing standing between a one-line edit and every non-overriding
  agent — Jack and both memory models — silently leaving load balancing.
- Jack declares no `providerRouting`, so it stays balanced.

These read the working-tree `appsettings.json` through the existing `RepoRoot()` helper,
matching how the file's other pins work.

## Files touched

New:

- `Domain/DTOs/ProviderRouting.cs` (the record, the `ProviderSort` enum and
  `ProviderRoutingAdvisories`)
- `Tests/Unit/Domain/ProviderRoutingAdvisoriesTests.cs`
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
- `CLAUDE.md` (a line on provider routing where agent configuration is described,
  including that `order` costs the prompt cache)

Note: `.cs` files carry no trailing newline (`.editorconfig` sets
`insert_final_newline = false`).
