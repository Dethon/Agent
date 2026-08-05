# 0012 — A server's tool set is fixed for a connection generation

Status: accepted
Date: 2026-08-04

## Context

`McpChannelConnection` asks the channel server what tools it has before two of its
calls, every time. `CreateConversationAsync` runs `ListToolsAsync` to check for
`create_conversation`, and `RegisterAgentsAsync` runs it again to check for
`register_agents`. (Members rather than line numbers throughout: the file moves often.)

The probe is not free. `DeliveryTargetResolver` calls `CreateConversationAsync` per
delivery target on agent-initiated turns — once in `ResolveAsync` to mint a missing
conversation, once in `AnnounceTurnStartAsync` to announce the turn start — so a
scheduled announcement to three targets pays three round trips before any of them is
asked to do anything.

The question the probe asks is "does this server offer this tool". Nothing in this repo
can change that answer while a connection is up. All thirteen servers register their
tools during `ConfigModule` construction, before the transport starts, and
`Tests/Integration/McpServers/McpServerRegistrations.cs` drives exactly that path for
every one of them.

## Decision

The tool set is fetched once per **connection generation** — one successful connect or
reconnect — and both capability questions go through one private `OffersToolAsync`, which
reads the cached set. A reconnect discards the cache (`ConnectAsync` clears it), so a
server that restarted with different tools is seen correctly the moment the connection is
rebuilt.

The generation is the unit rather than the process, the connection object, or a timed
expiry, because a reconnect is the only event that can put a different server process on
the other end.

## Considered options

**Keep probing per call.** Always correct, no assumption. Rejected: it pays a round trip
per target per turn to ask a question whose answer is fixed, and the cost falls on the
delivery path, where latency is user-visible.

**Cache for the process lifetime.** Simpler still, and wrong in the one case that
matters: a channel server redeployed with a new tool would never be seen by a running
agent, because reconnecting would not clear anything.

**Cache with a time-to-live.** Bounds the staleness without needing to know what
invalidates it. Rejected as a worse version of the same thing — it adds a duration
nobody can pick correctly, and it still serves a stale answer for that duration after the
event that actually invalidates the cache.

**Declare capability in configuration instead of probing.** `ChannelEndpoints` already
carries per-channel facts such as `attachOnly`. Rejected: it moves a fact the server
knows about itself into a file that has to be kept in sync with it, which is the failure
this repo removed from filesystem backends by deriving tools from overrides.

## Consequences

- A channel server that registers tools lazily, after its transport starts, would be seen
  as not having them for the life of the connection. No server does this today, and this
  record is where to look when one first tries.
- A connection must discard its cache on reconnect. That is one line, and forgetting it
  reintroduces the process-lifetime cache rejected above, so it is worth a test.
- The saving is per target per turn on agent-initiated turns, which is the path timers,
  alarms and scheduled tasks all take.
