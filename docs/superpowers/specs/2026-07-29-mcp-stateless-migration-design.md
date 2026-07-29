# MCP 2026-07-28 Stateless Migration — Design

**Date:** 2026-07-29
**Branch:** `mcp-update`
**Status:** Approved, ready for implementation planning

## Problem

Upgrading `ModelContextProtocol` 1.4.1 → 2.0.0 flipped
`HttpServerTransportOptions.Stateless` to default `true`. Nothing in the repo sets it, so all
19 `WithHttpTransport()` call sites silently became stateless. The compiler emits no
diagnostic for this: `MCP9006` fires only when you *set* a stateful-only option, and
defaulting into stateless is silent.

Verified at the wire level against 2.0.0:

| | default (2.0.0) | `Stateless = false` |
|---|---|---|
| `Mcp-Session-Id` header | absent | present |
| `client.SessionId` | `null` | assigned |
| standalone `GET` stream | never opened | opened |
| `DELETE /mcp` on dispose | never sent | sent |
| `SendNotificationAsync` | throws `InvalidOperationException` | delivered |
| `SampleAsync` inside a tool call | `isError=true` | works |
| negotiated protocol | **2026-07-28** | **2025-11-25** |

### Why stateful is not the fix

`Stateless = false` does not select a mode of the current protocol — it falls back to the
legacy `initialize` handshake and renegotiates down to **2025-11-25**.

Sessions were removed from the protocol itself:
[SEP-2575](https://modelcontextprotocol.io/seps/2575-stateless-mcp) removes the `initialize`
handshake; [SEP-2567](https://modelcontextprotocol.io/seps/2567-sessionless-mcp) removes
`Mcp-Session-Id` and protocol-level sessions. SEP-2575 explicitly rejected keeping stateful as
an option ("Supporting two parallel interaction models would have dramatically increased the
complexity of the protocol… By making a clean break, we ensure the entire ecosystem can move
forward"). The legacy transport carries a *"year-long offramp"* under a twelve-month minimum
deprecation policy.

So `Stateless = false` buys ~12 months and must be undone anyway. We migrate instead.

### What actually broke

Only one test failed (`McpWebSearchSessionCleanupTests`), because it is the only test that
stands up a real MCP server *and* a real client. The real blast radius is wider and mostly
untested:

- **Six emitters** push `channel/message` / `channel/cancel` outside request scope
  (SignalR, Telegram, ServiceBus, Voice, Scheduling, Library). `SendNotificationAsync` throws
  and **every emitter catches and logs at Warning** — so no inbound message from any channel
  reaches the agent, silently. The `server.SessionId ?? Guid.NewGuid()` fallback, present in all
  six `RunSessionHandler` sites, also registers a fresh key per HTTP request and unregisters it
  before the request ends.
- **WebSearch** — `web_browse`/`web_snapshot`/`web_action` call `RequireSessionId()`, which
  throws by design when `SessionId` is null. Every call becomes an error envelope.
- **Library** — `StateKey` falls back to `ClientInfo.Name`, which is the **agent name**
  (`McpClientManager.cs:75`; `userId` never reaches client identity). Today's granularity is
  per-conversation (`McpAgent` holds one `ThreadSession`, and therefore one MCP session, per
  `AgentSession`). So isolation silently collapses to one global bucket shared across every
  user of an agent. Not a *resolution* break — `SearchResult.Id = link.GetHashCode()` is
  content-derived, so an id can never resolve to the wrong torrent — which is exactly why
  nothing would visibly fail.
- **Sampling** — `McpContentRecommendationTool.SampleAsync` fails. Sampling is separately
  deprecated (`MCP9005`, 62 warnings).

## Decisions

| # | Decision | Rationale |
|---|---|---|
| D1 | Clean break to 2026-07-28 | One code path. Everything deploys from one compose stack; the Rust satellite speaks Wyoming, not MCP, so nothing external pins the old protocol. |
| D2 | Replace push with a **long-poll tool** | Delivers in one hop with the payload inline — identical latency to today's push. Resource subscriptions cost a second hop (`resources/updated` carries only a URI). |
| D3 | **In-memory bounded queue** per subscriber | Closes the poll gap, and survives agent restart (better than today). Channel-server restart still loses, same as today. No new infrastructure. |
| D4 | Conversation identity travels in **`_meta`**, always present | Prescribed by the spec (below). |
| D5 | **Delete sampling entirely** | Experimental, never really used; its only consumer costs a whole extra LLM turn for no added capability. |

### Why `_meta` (D4)

The [2026-07-28 base spec](https://modelcontextprotocol.io/specification/2026-07-28/basic/index),
§Statelessness, prescribes it and condemns the current approach:

> Servers **MUST NOT** rely on prior requests over the same connection to establish context…
> Every request supplies this metadata in its `_meta` field.
>
> Servers **SHOULD** be prepared to handle requests associated with multiple tasks, threads, or
> conversations.
>
> **Note:** an open connection… is not a conversation or session… a server **must not treat
> connection or process identity as a proxy for conversation or session continuity**.

That final line describes the `StateKey` bug exactly.

Two alternatives were checked and rejected:

- **`RequestParams.RequestState`** — per the shipped XML docs, *"opaque request state echoed
  back from a previous `InputRequiredResult`… The client must echo back the exact value without
  modification."* It is the MRTR continuation token: server-minted, single-request-scoped.
- **SEP-2567 explicit state handles** — ordinary *tool arguments* for **server**-minted state
  (e.g. a browser session id). Right mechanism for a different problem.

**Key naming.** The spec requires `_meta` prefixes be reverse-DNS labels ending in `/`, and
reserves any prefix whose second label is `mcp` or `modelcontextprotocol`. The current key is
bare `"conversationContext"`, sitting in the unprefixed namespace alongside `progressToken` and
`traceparent`. It moves to **`com.herfluffness/conversationContext`**.

## Architecture

### Unchanged

The wire *payloads* survive: `ChannelMessageNotification` and `ChannelCancelNotification` are
untouched. Only the transport mechanism carrying them changes.

Also untouched: `ChatMonitor`, `DeliveryTargetResolver`, `ReplyDispatcher`, `FirstReplyTracker`
(they consume `IAsyncEnumerable<ChannelMessage>`, and `McpChannelConnection` keeps filling the
same `Channel<ChannelMessage>`); the four outbound tools (`send_reply`, `request_approval`,
`create_conversation`, `register_agents`), which are already plain tool calls; and the six pure
tool servers (Vault, Sandbox, HomeAssistant, Idealista, Printer, Timers), which are already
correct on the new default.

### `channel_receive`

A fifth channel-protocol tool, registered in `ChannelProtocol` and added to
`ThreadSession._channelProtocolToolNames` so the LLM never sees it.

```
channel_receive(subscriberId: string, maxWaitMs: int) -> { items: ChannelInboxItem[] }
```

```csharp
public sealed record ChannelInboxItem
{
    public required ChannelInboxItemKind Kind { get; init; }   // Message | Cancel
    public ChannelMessageNotification? Message { get; init; }
    public ChannelCancelNotification? Cancel { get; init; }
}
```

A **single ordered list with a discriminator**, not two parallel lists: today both notifications
share one transport stream, so a cancel can never overtake the message it cancels. Two lists
would lose that and make cancel racy.

`subscriberId` is the stable `channel-<channelId>` the agent already uses as its client name —
**not** a handle minted at connect. A fresh handle per reconnect would orphan the queue and lose
exactly the messages buffered during an agent restart, the case D3 exists to protect. Passing
the reference on every request is also SEP-2575's "Prefer State References" principle.

**`maxWaitMs` = 30 000.** A 45s hold was verified to complete on the default client timeout, and
client cancellation propagates to the server's `CancellationToken`, so reconnect and shutdown
release held requests rather than leaking them.

No reverse proxy sits in this path: `ChannelEndpoints` are container-to-container
(`http://mcp-channel-signalr:8080/mcp`, `Agent/appsettings.json:145`). Caddy does proxy
`/hubs/*` to McpChannelSignalR, but that is the browser-facing SignalR hub, not the `/mcp`
endpoint the agent dials — so no proxy idle timeout applies to the held call.

### `ChannelInbox`

Lives in `Domain/Channel/`. Pure logic, `TimeProvider`-injected, no external dependencies, so it
unit-tests without a server.

- `ConcurrentDictionary<subscriberId, SubscriberQueue>`; a subscriber registers on first poll.
- `Enqueue` broadcasts to every registered queue, preserving today's fan-out.
- Bounded at 256, oldest dropped on overflow — the only backpressure that cannot deadlock a
  channel.
- `ReceiveAsync` returns a drained batch immediately if anything is pending; otherwise waits on
  a `TaskCompletionSource` until an item arrives or `maxWaitMs` elapses, then drains.
- **One in-flight poll per subscriber.** A second poll for the same id completes the previous
  one with an empty batch; otherwise two waiters split the stream.
- Queues are evicted after a few minutes without a poll, so a vanished agent cannot leak.

The six emitters replace `SendNotificationAsync` with `inbox.Enqueue(...)`. `RunSessionHandler`
disappears from all **six** servers that use it (SignalR, Telegram, ServiceBus, Voice,
Scheduling, Library — verified), taking the `MCPEXP002` suppressions with it and removing a
standing upgrade risk.

### Agent pump

`McpChannelConnection.ConnectAsync` drops both `RegisterNotificationHandler` calls and starts:

```csharp
while (!ct.IsCancellationRequested)
{
    try
    {
        var result = await _client.CallToolAsync(ChannelProtocol.ReceiveTool, args, ct);
        foreach (var item in Parse(result))
            Dispatch(item);            // -> existing HandleChannelMessage/CancelNotification
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
    catch (Exception ex) { log; await Task.Delay(backoff, ct); }
}
```

`HandleChannelMessageNotification` and `HandleChannelCancelNotification` survive **unchanged** —
they are public, take `JsonElement`, and are directly unit-tested. Only their feed changes, so
that suite carries over intact. Backoff stops a down server from spinning; existing
`ReconnectAsync` is unaffected.

### Conversation context

- `ChannelProtocol.ConversationContextMetaKey` → `com.herfluffness/conversationContext`.
- `McpAgent.cs:283` drops the `conversationContext is null ? null : …` conditional.
- `SubAgentRunTool.cs:64` stamps the **parent's context verbatim** onto its user message,
  following the path `SetSenderId(featureConfig.UserId)` already takes. Verbatim rather than
  substituting the subagent's own id: otherwise a `file_search` in the parent and a
  `file_download` in a subagent would namespace differently and silently break the flow. The
  cost — a subagent's tool calls attributed to the parent agent — is correct, since it acts on
  the parent's behalf.
- Memory extraction and dreaming are unaffected: they use plain `IChatClient`s, not `McpAgent`,
  so they never issue `tools/call`.

Namespace for server-side state is `{AgentId}:{ConversationId}`, which reproduces today's
per-conversation granularity exactly.

**No fallback.** Tools return a structured `ToolError` when the context is missing. A
shared-bucket fallback is a privacy leak; a per-request fallback silently severs
search→download. Both fail invisibly, and with the always-present guarantee a fallback would
only ever mask a bug.

### Library

`McpFileSearchTool` and `McpFileDownloadTool` both key on the conversation namespace.
**Both** must change or the pair desynchronizes — search namespaced one way and download the
other breaks the flow outright. `McpFileSearchTool` currently does not parse `_meta` at all.
`ParseConversationContext` lifts out of `McpFileDownloadTool` into a shared helper.

### WebSearch

`web_browse`/`web_snapshot`/`web_action` key the browser session on the conversation namespace,
matching today's granularity (MCP session id ≙ one `ThreadSession` ≙ one conversation). No new
tool argument, no reliance on the model threading a handle correctly.

`RequireSessionId()` exists only to match the `Mcp-Session-Id` header for the DELETE hook — its
own comment says so. With the hook gone its reason evaporates, and with `StateKey` moving to
`_meta` and `SessionIdHeader` losing its only consumer, all of
`Infrastructure/Extensions/McpServerExtensions.cs` is deleted along with the
`UseBrowserSessionCleanupOnMcpDelete` middleware.

**Cleanup already works.** `BrowserSessionManager` has idle eviction: `_idleTimeout` (default
30 min), a `_pruneTimer`, `PruneIdleAsync()`, and `LastAccessedAt` maintained on every access.
The DELETE hook was a promptness optimization on top of a working timer, never the sole cleanup
path. Removing it costs latency-to-reclaim, not correctness.

### Sampling removal

Delete: `Infrastructure/Agents/Mcp/McpSamplingHandler.cs`;
`ToCreateMessageResult` in `Infrastructure/Agents/Mappers/ChatResponseUpdateExtensions.cs`
(sole consumer); the `McpClientHandlers`/`SamplingHandler` wiring in `ThreadSession.BuildAsync`
(and the `_tools` field feeding it, if unused after);
`Domain/Tools/ContentRecommendationTool.cs`;
`McpServerLibrary/McpTools/McpContentRecommendationTool.cs`; its
`.WithTools<McpContentRecommendationTool>()` registration at `McpServerLibrary/Modules/ConfigModule.cs:85`;
`Tests/Integration/Agents/McpSamplingHandlerTests.cs`.

This clears all 62 `MCP9005` warnings.

## Testing

### The gap that caused this

Six emitters are unit-tested against a **mocked** `McpServer` — a mock accepts a call the real
transport refuses, so those tests stayed green while every channel was dead.
`testing.md` already says *"Prefer integration tests over mocks."* The migration closes this
rather than reproducing it.

### New tests

1. **`ChannelInbox` units** (`FakeTimeProvider`) — message/cancel interleaving preserves order;
   bounded overflow drops oldest; pending returns immediately; empty waits then wakes on
   enqueue; timeout returns empty; a second poll displaces the first; idle subscribers evicted.
2. **Channel contract test — real Kestrel, real `McpClient`**, parameterized across all six
   servers: enqueue on the server, assert it surfaces on `McpChannelConnection.Messages`. *This
   is the test that would have caught the original regression.*
3. **Protocol guard** — assert `NegotiatedProtocolVersion == "2026-07-28"` and
   `SessionId is null`. Pins the clean break: reintroducing `Stateless = false` fails loudly
   instead of silently dropping to 2025-11-25.
4. **Pump behavior** — reconnect resumes the same `subscriberId` and drains what buffered during
   the outage; a failing server backs off; cancellation exits cleanly.
5. **`_meta` contract** — key is vendor-prefixed; context present on every `tools/call`; a
   subagent inherits the parent's verbatim.
6. **Library isolation** — search and download share a conversation namespace; two conversations
   do not see each other's results.
7. **WebSearch** — conversation-keyed sessions reused across calls, reclaimed by
   `PruneIdleAsync`.

### Deleted

`McpSamplingHandlerTests` and `McpWebSearchSessionCleanupTests` — both assert mechanisms that
cease to exist. Note the second is the test that started this investigation; it is deleted
rather than fixed, replaced by (7).

### Surviving unchanged

`HandleChannelMessage/CancelNotification` tests, `ChannelProtocolTests`,
`ThreadSessionToolFilterTests` (plus one tool name).

## Sequencing

Red-Green-Refactor per triplet, commit after each. The repo is *currently* stateless-by-default
and therefore runtime-broken on every channel — this is a fix-forward, not a rollback.

1. Revert the `Stateless = false` line on `McpServerWebSearch/Modules/ConfigModule.cs`
2. `ChannelInbox`
3. **Vertical slice: SignalR end-to-end** — proves the shape before replication
4. Replicate to Telegram, ServiceBus, Voice, Scheduling, Library
5. `_meta` vendor key, always-present, subagent inheritance
6. Library namespace migration
7. WebSearch keying; delete `McpServerExtensions` + middleware
8. Delete sampling
9. Full suite, then E2E against the compose stack

Step 3 is a full vertical slice rather than "all inboxes, then all pumps": if the shape is
wrong, find out on one server instead of six.

## Known limitations

- **Response lost in flight loses that batch.** We drain on return with no ack cursor. This is
  the one new-in-kind loss window, and it is strictly narrower than the accepted
  channel-restart loss. Closing it requires an ack cursor — deliberately not built.
- **Channel-server restart loses queued messages** (in-memory), same as today.
- **A message arriving before the agent's first poll is dropped**, same as today (no active
  session → no-op). The window is small since the pump starts immediately on connect.
- **Browser sessions reclaim up to 30 minutes later** than the old DELETE hook managed.

## Non-goals

- Durable/at-least-once delivery (rejected: needs Redis in servers that have none, plus dedupe).
- Down-level 2025-11-25 compatibility (rejected under D1).
- Migrating sampling to MRTR (rejected under D5 — deleted instead).
- Any change to `ChatMonitor` and the reply fan-out path.
