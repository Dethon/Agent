# Expose available voice satellites in Mycroft's system prompt

**Date:** 2026-06-06
**Status:** Approved (design)
**Scope:** Informational list now; structured so a future "announce" tool reuses the same identifiers ("list now, announce later").

## Problem

The voice-optimized agent (`mycroft`) has no awareness of which voice satellites
exist. The satellite list lives in the `McpChannelVoice` process
(`SatelliteRegistry`, populated from config), but Mycroft's system prompt is built
in the Agent process and today receives nothing from the voice channel — voice is
wired as a *channel* only (`channelEndpoints`), not as a tool/prompt server in
Mycroft's `mcpServerEndpoints`.

We want Mycroft's system prompt to list the available satellites (id + room) so it
can reason about where it can be heard and answer location questions.

## Decisions (from brainstorming)

- **Purpose:** Informational list now. No new action capability in this change.
  Prompt content uses stable identifiers (id + room) so a future announce tool can
  target the same handles without rework.
- **Liveness:** Configured set only (from `SatelliteRegistry`). No online/offline
  state — a future announce tool checks liveness at call time anyway, and the
  prompt is fetched once per session so live state would go stale mid-conversation.
- **Fields per satellite:** Satellite **id** + **room**. (Speaker identity and wake
  word excluded as noise.)

## Approach (chosen: A)

**A — Voice channel becomes dual-role + a `voice_prompt` MCP prompt.** Add
`mcp-channel-voice` to Mycroft's `mcpServerEndpoints`; the voice server exposes a
`voice_prompt` rendered from its own `SatelliteRegistry`. The agent picks it up
automatically via `McpClientManager.LoadPrompts()`. Mirrors the existing
scheduling/printing prompt pattern exactly.

Rejected alternatives:
- **B — Duplicate satellite list in agent config + static fragment.** Violates
  single-source-of-truth (CLAUDE.md warns against duplicating channel config
  agent-side; the agent-catalog mechanism exists to avoid exactly this). Config
  drifts.
- **C — New channel→agent prompt-contribution protocol.** YAGNI: a large general
  mechanism for a single current need.

## Why Approach A is safe (verified)

- `Infrastructure/Agents/ThreadSession.cs:87-96` keeps a static
  `_channelProtocolToolNames` set (`send_reply`, `request_approval`,
  `create_conversation`, `register_agents`) that is **always** stripped from the
  agent-visible tool list (`FilterMcpTools`), specifically so dual-role servers
  (e.g. `mcp-scheduling`) don't leak channel tools. Adding `mcp-channel-voice` to
  `mcpServerEndpoints` therefore exposes **only** the new prompt; its channel tools
  remain hidden from the LLM.
- Mycroft's `whitelistPatterns` filter **tools**, not prompts — so they do not
  block the voice prompt.
- `McpClientManager.LoadPrompts()` fetches prompts from every connected client whose
  `ServerCapabilities.Prompts is not null`, and drops empty/whitespace results.
- Only Mycroft is wired to voice, so only Mycroft receives this prompt.

## Architecture & data flow

1. `mcp-channel-voice` declares the MCP *Prompts* capability and exposes a single
   prompt `voice_prompt`.
2. Mycroft's `mcpServerEndpoints` gains `http://mcp-channel-voice:8080/mcp`. At
   session start, `McpClientManager.LoadPrompts()` fetches `voice_prompt`;
   `ThreadSession.FilterMcpTools` strips the voice channel's protocol tools. The
   rendered text lands in the client-prompts section of
   `McpAgent.BuildInstructions`.
3. The voice server renders the prompt from `SatelliteRegistry` (configured set),
   so the list stays in sync with voice config without an agent redeploy.

## Components

- **`Domain/Prompts/VoicePrompt.cs`** (new) — static, mirrors `SchedulingPrompt`:
  - `Name = "voice_prompt"`
  - `Description` (one line)
  - `Build(IReadOnlyList<(string Id, string Room)> satellites)` → markdown, or `""`
    when the list is empty (so `LoadPrompts`' whitespace filter drops the fragment).
- **`McpChannelVoice/McpPrompts/VoiceSystemPrompt.cs`** (new) — `[McpServerPromptType]`
  class injecting `SatelliteRegistry`; method decorated with
  `[McpServerPrompt(Name = VoicePrompt.Name)]` + `[Description(VoicePrompt.Description)]`
  that enumerates `GetAllIds()` → `GetById(id)` → `(id, Room)` and returns
  `VoicePrompt.Build(...)`. Skips ids with a missing config entry.
- **`McpChannelVoice/Modules/ConfigModule.cs`** — add `.WithPrompts<VoiceSystemPrompt>()`
  to the existing `.AddMcpServer()...` chain.
- **`Agent/appsettings.json`** — add `"http://mcp-channel-voice:8080/mcp"` to the
  `mycroft` agent's `mcpServerEndpoints`. (Mycroft is defined only in this file.)
  No new env vars; no docker-compose change (the agent already `depends_on` and
  reaches this endpoint as a channel).

## Prompt content (id + room, configured set)

```
## Voice satellites

These are the voice satellites you can be heard on — the spoken devices placed
around the home. Each entry is a stable satellite id and the room it's in:

- fran-office-01 — Fran's office
- laura-office-01 — Laura's office

Each incoming message tells you which satellite and room it came from, so you can
tailor answers to where the person is.
```

No announce instructions yet (no tool exists). The `id` + `room` are exactly the
handles a future announce tool will target.

## Edge cases

- **No satellites configured** → `Build` returns `""` → fragment omitted entirely
  by `LoadPrompts`' whitespace filter.
- **An id present in `GetAllIds()` whose `GetById` returns null** (shouldn't happen)
  → skipped during enumeration in `VoiceSystemPrompt`.

## Testing (TDD, RED first)

- **`Tests/Unit/Domain/Prompts/VoicePromptTests.cs`**
  - Two satellites → output contains the `## Voice satellites` heading and both
    `- <id> — <room>` lines, in input order.
  - Empty list → returns `""`.
  - Single satellite → single bullet line.
- **`Tests/Unit/McpChannelVoice/VoiceSystemPromptTests.cs`**
  - Registry with satellites → `GetVoicePrompt()` equals `VoicePrompt.Build` over the
    registry's `(id, room)` pairs.
  - Empty registry → returns `""`.

## Out of scope

- An LLM-facing announce/route tool (deferred; this change only lays the
  identifier groundwork).
- Online/offline liveness in the prompt.
- Exposing satellites to any agent other than Mycroft.

## File-convention reminders

- No trailing newline in any `.cs` file (including tests).
- Prefer LINQ over loops (see `.claude/rules/dotnet-style.md`).
