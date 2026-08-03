# Architecture audit — 2026-08-03

Twelve deepening candidates from the second architecture review. This file is the
survey output, not a plan. Nothing here has been grilled yet.

Each candidate becomes its own `.scratch/<slug>/` folder once it goes through
`/grill-with-docs` → `/to-spec` → `/to-tickets`. Until then this is the only
durable record. Update the Status line of a candidate when it moves.

The seven candidates from the 2026-08-02 review all landed and are excluded.
The adapter-counting argument is excluded because `docs/adr/0001-single-adapter-interfaces-stay.md`
closed it.

## Index

| # | Candidate | Strength | Area | Status |
|---|---|---|---|---|
| 1 | Metrics publishing has no module | Strong | cross-cutting | Grilled → `.scratch/metrics-publishing-module/spec.md` |
| 2 | The rebuild loses its event handlers | Strong | WebChat.Client | Grilled → `.scratch/chat-live-connection/spec.md` |
| 3 | The satellite connection has no module | Strong | McpChannelVoice | Grilled → `.scratch/satellite-connection-module/spec.md` |
| 4 | Playback has no outcome | Strong | McpChannelVoice | Grilled → `.scratch/playback-outcome/spec.md` + `docs/adr/0003-playback-settles-by-outcome.md` |
| 5 | The hub call surface leaks the connection | Strong | WebChat.Client | Not grilled |
| 6 | `AddToolServer`, twin of `AddChannelServer` | Strong | McpServer* | Not grilled |
| 7 | Two copies of "how to build an agent" | Strong | Infrastructure/Agents | Not grilled |
| 8 | The turn is not a value | Strong | Domain/Monitor | Not grilled |
| 9 | One breakdown descriptor, not seven pipelines | Worth exploring | Dashboard + Observability | Not grilled |
| 10 | Timers and schedules are the same backend | Worth exploring | Domain/Tools | Not grilled |
| 11 | Dashboard re-implements WebChat's client | Worth exploring | Blazor clients | Not grilled |
| 12 | The memory turn has no owner | Worth exploring | Domain/Memory | Not grilled |

Candidates 1 and 2 are live defects, verified against the code. The rest are
friction.

## Ordering

Take 1 first: it is the only candidate that is both a live defect and a
cross-cutting deepening.

Take 2 before 11. The shared Blazor seam in 11 should be extracted from the
deepened connection in 2, or Dashboard inherits the rebind hole.

Candidates 6 and 10 touch no file another candidate touches and can run at any
point.

Candidate 3 unblocks the voice half of candidate 1: the spans that candidate 1
wants under test are only reachable through the hosted service today.

Candidate 4 is sequenced AFTER candidate 3, decided during its grilling: the
`Discarded` outcome is settled by the satellite connection's drain phase, which
candidate 3's spec is what creates.

Candidates 4 and the noted `SendReplyTool` item overlapped; 4 has been grilled and
took the smaller half. Claimed by 4: the three segment-release paths, the three
prefetch-disposal paths, and the per-satellite voice fallback duplicated at four
sites. Left for the noted item: the eight service-locator lookups at `:37-52` and the
private statics threading nine or ten parameters, whose fix is a reply-speaker module
holding them as fields.

Rerun the cross-candidate contact check before adding a candidate, the same way
`.scratch/README.md` records for the previous batch.

## Vocabulary

Findings use the `/codebase-design` terms: module, interface, implementation,
depth, seam, adapter, leverage, locality. "Interface" means everything a caller
must know, including ordering rules and error modes, not just the signature.

---

## 1 — Metrics publishing has no module

**Strength:** Strong. **Live defect.** **Grilled**, spec at
`.scratch/metrics-publishing-module/spec.md`. The open design question below was
settled: `IMetricsPublisher` becomes `void Publish(MetricEvent)` and transports
implement a separate sink, rather than a `BestEffort` decorator over the async
signature. Recorded as `docs/adr/0002-metrics-publishing-is-fire-and-forget.md`.

**Files**

- `Infrastructure/Metrics/BufferedMetricsPublisher.cs:32`
- `Infrastructure/Metrics/RedisMetricsPublisher.cs:19`
- `Agent/Modules/InjectorModule.cs:36` — registers the buffered publisher
- `McpChannelVoice/Modules/ConfigModule.cs:38` — registers the Redis publisher raw
- `McpChannelVoice/Services/WyomingSatelliteHost.cs:507`, `:545`, `:548`
- 27 `new VoiceEvent` sites in `McpChannelVoice`
- `Domain/Monitor/ChatMonitor.cs:67`, `:104`, `:321`
- `Domain/Monitor/ReplyDispatcher.cs:25`, `:55`
- `Infrastructure/Agents/ChatClients/ToolApprovalChatClient.cs:94`, `:124`
- `Infrastructure/Agents/McpAgent.cs:139`
- `Infrastructure/Storage/RedisChatMessageStore.cs:87`

**Friction**

Every one of about 35 call sites restates "metrics must never fail a turn" in a
comment. Four of them implement it, each differently, and the rest do not. The
guarantee actually depends on which publisher the host registered, which no
caller can see.

**Verified defect**

`WyomingSatelliteHost.cs:507` publishes `SttLatencyMs` inside the try whose catch
at `:545` logs "Transcription failed" and returns false. Voice registers
`RedisMetricsPublisher` directly, and its `PublishAsync` awaits a real Redis
publish, so a Redis blip discards a good transcript. The `SttError` publish at
`:548` then escapes the catch into `FollowUpConversation.RunConversationAsync`,
which handles only `OperationCanceledException`. The conversation task dies while
the TCP connection stays up.

The Agent host does not have this problem because `BufferedMetricsPublisher.PublishAsync`
only does a `TryWrite` and cannot throw. So the same guards are dead code in one
process and missing in another.

**Proposed deepening**

State the never-throwing guarantee on `IMetricsPublisher` itself and enforce it
with one decorator every host registers, then delete the site-local guards. The
open design question for grilling: a `BestEffort` decorator versus documenting the
guarantee and requiring every adapter to honour it. Also fold in the
`Stopwatch.StartNew` / `Stop` / `PublishAsync(new LatencyEvent{...})` triple that
appears at five sites.

**How tests improve**

No test today covers "a throwing publisher does not kill a turn". One decorator
test replaces 35 conventions. `ToolApprovalChatClient` loses about 28 duplicated
lines.

---

## 2 — The rebuild loses its event handlers

**Strength:** Strong. **Live defect.**

**Files**

- `WebChat.Client/Services/ChatConnectionService.cs:70-96` (ConnectAsync), `:170-205` (RebuildAsync), `:207-227` (TearDownAsync)
- `WebChat.Client/Services/SignalREventSubscriber.cs:18-21`
- `WebChat.Client/Services/SignalRHubConnectionFactory.cs`
- `WebChat.Client/Services/ForegroundReconnectPolicy.cs:14-21`
- `WebChat.Client/State/Effects/InitializationEffect.cs:73`, `:99-112`
- `WebChat.Client/State/Hub/{ConnectionEventDispatcher,HubEventDispatcher,ReconnectionEffect}.cs`
- `WebChat.Client/Layout/MainLayout.razor:95-113`, `wwwroot/app.js:60-85`

**Friction**

The live-connection story spans eight files and its ordering rules exist only as
comments. Two examples, both load-bearing: `TearDownAsync` must dispatch
`HandleClosed` before reconnecting or `ReconnectionEffect` skips its reload
(`:222-226`), and a rebuild does not raise SignalR's `Reconnected` so
`OnReconnected` is fired by hand after the first connect (`:86-96`).

**Verified defect**

`Subscribe()` is called exactly once, from `InitializationEffect.cs:73`, and
early-returns when `IsSubscribed`. `TearDownAsync` disposes the connection
without calling `Unsubscribe()`, so `IsSubscribed` stays true. The
`OnReconnected` handler at `InitializationEffect.cs:99-112` re-registers the
user, rejoins the space and resubscribes push, but never rebinds hub events.

After any `RebuildAsync` — the mobile-resume path this whole machine exists for —
`OnTopicChanged`, `OnStreamChanged`, `OnUserMessage`, `OnToolCalls`,
`OnApprovalResolved` and `OnAgentsUpdated` are registered on a disposed
connection and never re-registered. The client reads as Connected and receives
nothing.

**Proposed deepening**

One `ChatLiveConnection` module owning build → bind handlers → publish status →
recover session, so re-subscription is part of connecting rather than a caller
obligation. `ForegroundReconnectPolicy` and `AggressiveRetryPolicy` stay outside
as the pure decision functions they already are, and `HubEventDispatcher` stays
as the pure action mapping. Callers get `ConnectAsync`, `ReconnectIfNeededAsync`
and a status observable.

**How tests improve**

`Tests/Unit/WebChat.Client/ChatConnectionServiceTests.cs` (208 lines) already
drives rebuild scenarios through `IChatHubConnection`. The missing assertion —
after a rebuild a server push still reaches the store — is unwritable today
because subscription lives in a different object reachable only through a raw
`HubConnection`. `SignalREventSubscriber` and `SignalRHubConnectionFactory` have
no tests at all.

**Note**

The fix itself is small and the root cause is confirmed, so this does not need
`/diagnosing-bugs`. A red `/tdd` test for the missing behaviour is enough. The
module extraction is the larger, separate piece.

---

## 3 — The satellite connection has no module

**Strength:** Strong.

**Files**

- `McpChannelVoice/Services/WyomingSatelliteHost.cs:128-301` — `RunConnectionAsync`, 174 lines
- `McpChannelVoice/Services/WyomingSatelliteHost.cs:303-352` — `BuildCoordinator`
- `Tests/Integration/McpChannelVoice/WyomingSatelliteHostTests.cs` — 2,233 lines, 15 tests, each standing a real `TcpListener` and a hand-written fake satellite

**Friction**

`RunConnectionAsync` does six jobs: dial, wire the `ControlWriter`, register with
two registries and the arbiter, launch two background tasks, decode and route
five Wyoming frame types, and unwind in an ordered `finally`. The ordering that
makes it correct exists only as prose at `:223`, `:225-227`, `:230-236` and
`:275-281`. Nothing in any signature says `RecordRoomLevel` → `NoteWakeSignal` →
`Claim` → `OnWake` → `TryConsumeWakeSignal` must happen in that order. The wake
stash has two consumers in two files, `WyomingSatelliteHost.cs:236` and
`:318` via `CaptureSession.onOpened`.

Because the only entry point is `StartAsync` over TCP, exercising any of it costs
roughly 130 lines of socket plumbing per test.

**Proposed deepening**

A `SatelliteConnection` (or `SatelliteEventRouter`) module with an interface of
roughly `HandleAsync(WyomingEvent)` plus `DisposeAsync()`, constructed over an
injected writer delegate rather than a `WyomingClient`. `WyomingSatelliteHost`
keeps dial, reconnect and teardown. Fold the wake stash in as a single
`OnRunPipeline(WakeAnnouncement)` call so the five-step order becomes an
implementation detail and the drop-an-unconsumed-stash rule at `:230-236` becomes
an internal invariant.

Interface shape is genuinely open here. Worth running `/codebase-design`'s
design-it-twice inside the grilling.

**How tests improve**

13 of the 15 integration tests become unit tests that push `WyomingEvent`s into a
router and assert on a recorded writer. Keep two real-socket tests for framing
and reconnect. This also unblocks the voice half of candidate 1.

---

## 4 — Playback has no outcome

**Strength:** Strong.

**Files**

- `McpChannelVoice/Services/SatelliteSession.cs:8-20` — the `PlaybackJob` record
- `McpChannelVoice/Services/SatelliteSession.cs:248-452` — the playback loop, 205 lines
- Producers: `WyomingSatelliteHost.cs:564`, `AnnouncementService.cs:63`, `RequestApprovalTool.cs:126` and `:141`, `SendReplyTool.cs:332`
- Segment release paths: `SendReplyTool.cs:352`, `:402`, `:509`
- Prefetch disposal paths: `SendReplyTool.cs:361`, `:408`, `:516`

**Friction**

The record exposes five callbacks — `OnStarted`, `OnPreempted`, `OnDrained`,
`OnFirstAudio`, `OnFailed` — whose mutual-exclusion rules are the whole contract
and are documented nowhere in the type. Two producers hand-roll the identical
settle-a-TCS-from-three-of-five idiom at `WyomingSatelliteHost.cs:563-571` and
`RequestApprovalTool.cs:139-148`. `SendReplyTool` must release its segment on
three separate paths and dispose the prefetch on the same three; the comments at
`:345-347` and `:354-358` say what happens when one is missed. The refused-enqueue
case is not a callback at all: `EnqueuePlaybackAsync` returns false and the
caller synthesises the terminal outcome itself.

**Proposed deepening**

Give the queue a module that guarantees exactly one terminal outcome:
`Task<PlaybackOutcome> SpeakAsync(job)` completing as `Drained`, `Preempted`,
`Failed` or `Refused`, with the queue owning the `IAsyncDisposable` audio source
so disposal stops being a producer duty. The chime and the approval prompt stop
hand-rolling TCS handshakes and `SendReplyTool` binds the segment token to the
outcome once.

Interface shape is open. Worth design-it-twice.

**How tests improve**

`Tests/Unit/McpChannelVoice/SatelliteSessionPlaybackTests.cs` (791 lines) proves
preemption by counting label strings today. With one outcome, "every enqueued job
produces exactly one terminal outcome, including refused ones" becomes a single
parameterised test rather than being re-proved per producer. The spin-waits in
`RequestApprovalToolTests.cs:279`, `:427`, `:451` can await an outcome.

---

## 5 — The hub call surface leaks the connection

**Strength:** Strong.

**Files**

- `WebChat.Client/Contracts/IChatConnectionService.cs:8` — `HubConnection? HubConnection { get; }`
- `WebChat.Client/Services/TopicService.cs` (63 lines, 5 methods, 5 copies of the guard)
- `WebChat.Client/Services/ChatMessagingService.cs` (77 lines, 5 methods, 5 copies)
- `WebChat.Client/Services/{ApprovalService,AgentService,ChatSessionService}.cs`
- `WebChat.Client/Services/PushNotificationService.cs:27`, `:50`, `:62`
- 24 uses of `connectionService.HubConnection` in total
- `Tests/Integration/WebChat/Client/Adapters/Hub{Topic,Messaging,Approval}*.cs` — the same three interfaces written a second time against a bare `HubConnection`

**Friction**

Every method is the same three lines: fetch `HubConnection`, null-check, return
an empty list or false, else `InvokeAsync`. The interface is as wide as the
implementation. The interesting part — that "not connected" is silently
indistinguishable from "no topics" at `TopicService.cs:11-17` — is stated
nowhere. Because these take the concrete `ChatConnectionService` and go through a
raw `HubConnection`, none of the five has a unit test.

**Proposed deepening**

Move the invoke and stream verbs onto `IChatHubConnection` (`InvokeAsync<T>`,
`SendAsync`, `StreamAsync<T>`), let one gateway own the disconnected decision
once — queue, throw, or a documented default — and remove `HubConnection` from
`IChatConnectionService`. The five services become typed method lists with no
null handling.

**ADR note**

Adjacent to ADR-0001 but not covered by it. The ADR fixes `Domain/Contracts/` and
rejects adapter-counting as grounds for deletion. This is a different argument in
a different project: the interface is as wide as the implementation and the
disconnected rule is unstated. The seams stay, they get narrower.

**How tests improve**

Five untestable modules become unit-testable against a fake `IChatHubConnection`,
which already exists at `ChatConnectionServiceTests.cs:166`. The 15 disconnected-path
guards become one test of the gateway. The three integration adapters delete.

---

## 6 — `AddToolServer`, twin of `AddChannelServer`

**Strength:** Strong.

**Files**

Settings binding, 13 hand-copies of the same eight lines:

- `McpServerHomeAssistant/Modules/ConfigModule.cs:17-26`
- `McpServerScheduling/Modules/ConfigModule.cs:23-32`
- `McpServerLibrary/Modules/ConfigModule.cs:22-31`
- `McpServerPrinter/Modules/ConfigModule.cs:20-29`
- `McpServerTimers/Modules/ConfigModule.cs:18-27`
- `McpServerVault/Modules/ConfigModule.cs:17-26`
- `McpServerSandbox/Modules/ConfigModule.cs:18-27`
- `McpServerIdealista/Modules/ConfigModule.cs:16-25`
- `McpServerWebSearch/Modules/ConfigModule.cs:17-38`

Error filter, seven copies of the same 12-line lambda:
`McpServerHomeAssistant:54-66`, `Idealista:36-48`, `Sandbox:52-64`,
`Timers:63-75`, `Printer:61-73`, `Vault:41-53`, `WebSearch:49-61`.

Nine `Program.cs` files are byte-identical apart from the `Configure*` name.

Four files whose whole content is a `=> "ok"` protocol stub:
`McpServer{Scheduling,Library}/McpTools/{SendReplyTool,RequestApprovalTool}.cs`.

**Friction**

The copies have already diverged in a way that matters.
`McpServerWebSearch/Modules/ConfigModule.cs:30-35` re-binds `CapSolver` and
`Camoufox` explicitly with the comment "Bind nested sections explicitly for
environment variable support", while `McpServerHomeAssistant/Settings/McpSettings.cs:9`
has a structurally identical optional nested record bound only by the plain
`config.Get<McpSettings>()`. Two copies disagree about whether nested env-var
binding works and nothing decides which is right.

`.claude/rules/mcp-tools.md` has promoted the duplication to a documented
convention: "Non-channel servers still register `AddCallToolFilter` in their own
`ConfigModule.cs`". A repeated shape became a rule instead of a module.

**Proposed deepening**

`Infrastructure.Utils.AddToolServer<TSettings>(this WebApplicationBuilder)` as
the non-channel twin of `Channels.Hosting.AddChannelServer`: bind env and user
secrets with one documented answer on nested sections, register the settings
singleton, add the `ToolResponse.Create(ex)` call-tool filter, return
`IMcpServerBuilder`. Each `ConfigModule` then contributes only its own DI. Fold
the two no-op channel stubs into `AddChannelServer` as overridable defaults.

Roll in the related finding below while you are here.

**Related, same area**

Each virtual filesystem mount's name is written three times: in the backend
(`Domain/Tools/Timers/Vfs/TimerFileSystem.cs:20` → `"timers"`), in the
`[McpServerResource(UriTemplate = "filesystem://timers")]` attribute
(`McpServerTimers/McpResources/FileSystemResource.cs:10`), and in the JSON body's
`name` and `mountPoint` (`:14-15`). Same for `ha`, `schedules`, `print-queue`,
`vault`, `sandbox`. `Domain/Tools/Downloads/Vfs/MediaFilesystem.cs` exists purely
as a hand-rolled fix for this on one server.

`AddFileSystemResource<TBackend>()` next to the existing
`AddFileSystemTools<TBackend>` in `Infrastructure/Utils/FileSystemServerTools.cs:86`,
plus a `DescribeMount` hook on `FileSystemBackendBase` alongside `DescribeRead`
and friends, deletes seven resource classes.

**How tests improve**

Only one of nine servers has a `ConfigModuleTests`
(`Tests/Unit/McpServerTimers/`). One `AddToolServerTests` pins nested-section env
binding and error-filter behaviour for all nine. `FileSystemResourceTests` exist
for only two of seven servers; one test over `AddFileSystemResource` covers all
seven and makes "resource name does not match `FilesystemName`" unrepresentable.

---

## 7 — Two copies of "how to build an agent"

**Strength:** Strong.

**Files**

- `Infrastructure/Agents/MultiAgentFactory.cs:43-98` — `CreateSubAgent`
- `Infrastructure/Agents/MultiAgentFactory.cs:100-148` — `CreateFromDefinition`
- `Infrastructure/Agents/McpAgent.cs:55-100` — constructor, 18 parameters, 12 optional
- `Infrastructure/Agents/McpAgent.cs:139-144` — `SafePublishLatencyAsync`
- `Domain/DTOs/AgentDefinition.cs`, `Domain/DTOs/SubAgentDefinition.cs`

**Friction**

The two methods are about 45 lines each and roughly 80% identical: publisher,
chat client, `ToolApprovalChatClient`, `FeatureConfig`, domain tools, domain
prompts, `McpAgent`. Because the difference is expressed as which optional
arguments you omit, real behaviour hides in the omissions.

`CreateSubAgent` builds `agentPublisher` at `:50-52` and passes it to
`CreateChatClient` (`:54`) and `ToolApprovalChatClient` (`:65`), but not to
`McpAgent`. So subagents emit no `SessionWarmup`, `LlmFirstToken` or `LlmTotal`
latency at all, because `McpAgent.SafePublishLatencyAsync` returns early. Omitting
`model`, `conversationId` and `patchableModelIds` means a subagent silently
rejects every config patch and files its history-store latency under a null
conversation id. None of that is visible at either call site.

The `chatClientFactory` delegate at `MultiAgentFactory.cs:22`, used at `:201-204`,
is a testability escape hatch cut straight through the seam.

**Proposed deepening**

One `AgentSpec` record that both entry points project onto — both definition
records already carry the same fields — and one `Build(spec)` that assembles the
client stack, tools and prompts, and the `McpAgent`. `McpAgent` takes the spec
plus `chatClient` and `stateStore` instead of 18 positional arguments, so
"subagents get no metrics" becomes a visible field rather than a missing
argument.

Decide during grilling whether the metrics asymmetry is intended. If it is, it
should be a named field. If it is not, this candidate fixes a second defect.

**How tests improve**

`Tests/Unit/Infrastructure/MultiAgentFactoryTests.cs` (342 lines) can only reach
routing, and only through the `chatClientFactory` hook. With a spec, `Create`
versus `CreateSubAgent` becomes one table-driven test over the projection.

Note the coverage gap: `McpAgent`'s turn behaviour is covered only by
`Tests/Integration/Agents/*`, and all nine files are
`[Trait("Category","Llm")]` plus `SkippableFact` gated on a live OpenRouter key,
so they do not run by default.

---

## 8 — The turn is not a value

**Strength:** Strong.

**Files**

- `Domain/Monitor/ChatMonitor.cs` (336 lines) — `PendingTurn` `:43-44`, `ProcessChatThread` `:81-99`, `DispatchCommandsAndQueueTurnsAsync` `:150-190`, `RunTurnAsync` `:219-245`, `ResolveTurnTargetsAsync` `:255-264`, `BuildUserMessageAsync` `:266-287`, `StreamAgentTurn` `:289-319`
- `Domain/Monitor/DeliveryTargetResolver.cs:74-106`
- `Domain/Monitor/ReplyDispatcher.cs:12-34`
- `Domain/Monitor/FirstReplyTracker.cs` (11 lines)

**Friction**

The only thing tying a message to "is this the group opener?" is an `int index`
carried in `PendingTurn` and read in two unrelated places: `ResolveTurnTargetsAsync`
(`index == 0` reuses group targets, else re-resolves) and
`AnnounceTurnStartAsync(skipMinted: index == 0)`. The comment at `:150-152` is the
whole specification, and it exists precisely because the invariant has no type.

The index counts commands too, so its correctness depends on a second unstated
invariant: a `/clear` or `/cancel` tears the group down via
`ChatThreadContext.Dispose` → `group.Complete`, so an index above zero always
implies a preceding real turn. Nothing in `ChatCommandParser` or
`ChatThreadResolver` says so.

Separately, `ProcessChatThread:81-99` builds the agent, restores the thread and
kicks off MCP warmup before any message content is parsed. A `/clear` arriving as
the first message of a group — routine after an agent restart — constructs a full
`McpAgent`, connects every MCP endpoint, lists tools and fetches prompts, then
discards all of it at `:167`.

**Proposed deepening**

Have the queuing loop emit a fully-resolved `Turn` record — channel message,
resolved delivery targets, `IsGroupOpener`, `FirstReplyTracker` — so target
resolution and announce stop reading an int and the group-opener rule is decided
in one place. Move command parsing ahead of agent construction so a group
carrying only commands never builds an agent or a session. `ChatMonitor` keeps
stream plumbing; a `TurnRunner` owns build-message → await warmup → stream →
metrics.

**How tests improve**

`Tests/Unit/Domain/MonitorTests.cs:390-456`, `:628-659`, `:706-750` and
`:816-861` each drive the whole monitor through channel writes plus
`TurnGate`/`OnReply`/`TaskCompletionSource` choreography to assert one routing or
ordering fact. Against a `Turn` value those become direct assertions. Two things
become testable that are inexpressible today: "a leading `/clear` constructs no
agent" (`FakeAgentFactory.Created` already exists at `:128`) and "the group-opener
rule holds when message 0 was a command".

---

## 9 — One breakdown descriptor, not seven pipelines

**Strength:** Worth exploring.

**Files**

- `Dashboard.Client/Effects/MetricsHubEffect.cs:31-139` — 280 lines, 7 `CancellationTokenSource` fields, 7 near-identical `Refresh*BreakdownAsync`
- `Dashboard.Client/Services/MetricsApiService.cs:43-107` — 7 `Get*GroupedAsync`
- `Observability/MetricsApiEndpoints.cs:94-237` — 238 lines, 7 `/x/by/{dimension}` maps, 22 copies of `from ?? today`
- `Observability/Services/MetricsQueryService.cs:171-445` — 452 lines, 7 `Get*GroupedAsync`
- `Dashboard.Client/Pages/{Tokens,Tools,Errors,Schedules,Memory,Latency,Voice}.razor`

**Friction**

Adding one metric family means editing five files in lockstep with no compiler
help. Voice was the last one added. The comment at `MetricsApiService.cs:100-102`
documents exactly the bug this fan-out produces: a default parameter in one layer
silently reverting the user's selection in another. Every page repeats the same
sequence: load saved prefs, subscribe, `SetDateRange`, `LoadAsync`, the three
`On*Changed` handlers, persist, `ReloadBreakdown`.

**Proposed deepening**

One `MetricBreakdown<TDimension, TMetric>` descriptor per family carrying route
segment, Redis key prefix, store and localStorage key prefix, with a single
implementation owning the CTS and debounce, the query-string build, the
null-to-empty mapping and preference persistence. The 7 effect methods become a
table lookup, the 7 endpoint maps become one loop, the 7 pages become one
`<BreakdownPage Descriptor=... />`.

**How tests improve**

Only `Tests/Unit/Dashboard.Client/MetricsApiServiceLatencyTests.cs` (41 lines)
and `MetricsHubEffectTests.cs` (384 lines) touch this. The per-page preference
and reload logic is reachable only through Playwright in `Tests/E2E/Dashboard/`.
One parameterised test over the descriptor set covers all seven families,
including the six with no coverage.

---

## 10 — Timers and schedules are the same backend

**Strength:** Worth exploring.

**Files**

- `Domain/Tools/Timers/Vfs/TimerFileSystem.cs` (429 lines)
- `Domain/Tools/Scheduling/Vfs/ScheduleFileSystem.cs` (509 lines)
- `Domain/Tools/Downloads/Vfs/DownloadsOverlay.cs:207` — a third copy of `Error`

**Friction**

Both are "a store of records rendered as `/<id>/spec.json` plus a read-only
`status.json` plus an action `.sh`", and the correspondence holds member for
member:

| Member | Timer | Schedule |
|---|---|---|
| `_json` / `_parseOptions` statics | `:56-63` | `:56-62` |
| `ParseSpec(content, out error)` | `:286-303` | `:418-436` |
| `Exec(...)` (byte-identical) | `:417-422` | `:402-407` |
| `Error(...)` | `:428-429` | `:508-509` |
| `ToZone` | `:385-386` | `:484-487` |
| `ScopeXAsync` | `:399-415` | `:160-179` |
| `NodeExistsAsync` | `:391-397` | `:208-215` |
| exit-127 text | `:260` | `:393` |
| create envelope | `:205` | `:266` |

Even the `DescribeGlob` blurbs differ only in the words "timer" and "schedule".
`FileSystemBackendBase` already absorbed `NotFound`, `Invalid`, `ReadOnly`,
`Fail`, `GlobPrologue`, `SearchNodesAsync` and `Glob`, but stopped short of the
record-directory layer and the success envelopes.

**Proposed deepening**

A `RecordDirectoryFileSystem<TRecord, TSpec>` between `FileSystemBackendBase` and
these two, owning glob, info, read, search, exists and scope over the
`(id → spec.json, status.json, action.sh)` shape plus the create, exec and delete
envelopes. Each backend supplies only `ListAsync`, `GetAsync`, `RenderSpec`,
`RenderStatus`, `ValidateSpec` and its action handler. Also lift `Error(...)` and
the `Exec(...)` and `Created(...)` envelope builders onto `FileSystemBackendBase`,
where `Fail<T>` already lives.

**How tests improve**

`TimerFileSystemJourneyTests` and `ScheduleFileSystemJourneyTests` re-prove the
same traversal semantics against two engines today. The glob, info, read, search
and exists half moves to one shared test over a fake record store, leaving the
per-backend suites to test only what actually differs: arming and validation
versus cron, DST and reassign.

---

## 11 — Dashboard re-implements WebChat's client

**Strength:** Worth exploring. Take candidate 2 first.

**Files**

- `Dashboard.Client/State/Store.cs` (28 lines) vs `WebChat.Client/State/Store.cs` (39) — WebChat's has the reference-equality no-op guard at `:28-35`, Dashboard's `Dispatch` at `:20-25` always calls `OnNext`
- `Dashboard.Client/State/IAction.cs` vs `WebChat.Client/State/IAction.cs` — identical
- `Dashboard.Client/Services/LocalStorageService.cs` (27, no interface) vs `WebChat.Client/Services/LocalStorageService.cs` plus `Contracts/ILocalStorageService.cs`
- `Dashboard.Client/Services/MetricsHubService.cs` (69) — concrete class, 14 `virtual` members and a `protected` parameterless constructor existing purely so `MetricsHubEffectTests` can subclass it; plain `.WithAutomaticReconnect()` at `:16`
- `Dashboard.Client/Layout/MainLayout.razor:41-46` — `try { StartAsync() } catch { }`, never retried
- `WebChat.Client/Services/ForegroundReconnectPolicy.cs:14-21` and `WebChat.Client/wwwroot/app.js:60-85` — no Dashboard counterpart

**Friction**

The deletion test says yes: delete Dashboard's `Store`, `IAction` and
`LocalStorageService` and reference WebChat's, and complexity vanishes rather
than moving. The divergence is not cosmetic. A backgrounded dashboard tab gets no
probe or rebuild, which is the exact Android-zombie case `ForegroundReconnectPolicy`
was written for. A failed initial `StartAsync` leaves `ConnectionStore` at its
default with no user-visible signal. The missing dispatch guard means every Redis
event re-renders every subscriber.

**Proposed deepening**

Extract store, dispatcher, local storage and the connection seam
(`IHubConnectionFactory`, `IHubConnection`, the foreground policy) into a shared
Blazor class library and have Dashboard adapt to it. `MetricsHubService` becomes
a typed handler registration over the shared connection and its `virtual` and
`protected`-constructor test hooks disappear.

Extract from the deepened `ChatLiveConnection` of candidate 2, not from today's
eight-file version, or Dashboard inherits the rebind hole.

**How tests improve**

Dashboard has 2 test files, 425 lines. Its live and reconnect behaviour is
otherwise asserted only by `Tests/E2E/Dashboard/DashboardRealTimeE2ETests.cs` (98
lines, real Playwright and real Redis). Sharing the seam hands Dashboard the
208-line `ChatConnectionServiceTests` suite and lets the E2E test shrink to a
smoke check.

---

## 12 — The memory turn has no owner

**Strength:** Worth exploring.

**Files**

- `Domain/Monitor/ChatMonitor.cs:282`
- `Infrastructure/Memory/MemoryRecallHook.cs:59-60` (anchor), `:78`, `:99-103`, `:41-48` (feature gate)
- `Domain/Extensions/ChatMessageExtensions.cs:13`, `:137`
- `Infrastructure/Agents/ChatClients/OpenRouterChatClient.cs:126-133`, `:322-337` (`FormatMemoryContext`, private static)
- `Domain/Memory/MemoryExtractionQueue.cs`
- `Infrastructure/Memory/MemoryExtractionWorker.cs:108-130`, `:46-53` (feature gate)
- `Domain/Memory/ConversationWindowRenderer.cs:23-32`
- `Domain/Prompts/MemoryPrompts.cs:9`, `:29`, `:70` — 149 lines, zero tests

**Friction**

Three unexpressed invariants span this chain.

`anchorIndex = persistedCount` at `MemoryRecallHook.cs:59-60` is correct only
because `ChatMonitor` calls `EnrichAsync` before the turn is persisted.
`MemoryExtractionRequest.AnchorIndex` is a bare `int` that says nothing about
this.

`MemoryExtractionWorker.BuildExtractionWindowAsync` appends `FallbackContent`
last at `:124-127` purely so `ConversationWindowRenderer` labels it `[CURRENT]`
at `:26-28`, which is what `MemoryPrompts.ExtractionSystemPrompt` promises the
model at `:29` and `:70`. Three modules, no shared contract.

`MemoryPrompts.FeatureSystemPrompt:9` tells the model to look for a
`[Memory context]` block that is produced by a private static in an unrelated
adapter, `OpenRouterChatClient.FormatMemoryContext`. Memory silently vanishes
behind any other `IChatClient`.

The feature gate is copy-pasted at `MemoryRecallHook.cs:41-48` and
`MemoryExtractionWorker.cs:46-53`, and is simply absent from
`MemoryDreamingService`, which consolidates for every user regardless.

**Proposed deepening**

A `Domain/Memory/MemoryTurn` module owning the vocabulary behind a small
interface: `Anchor(long persistedCount)` returning a named type,
`BuildWindow(thread, anchor, fallback)`, `RenderWindow(window)` (today's
`ConversationWindowRenderer`) and `RenderRecallBlock(MemoryContext)`, moving
`FormatMemoryContext` out of `OpenRouterChatClient` so the block the prompt
promises and the block a client emits are the same function. Plus one
`MemoryFeatureGate` the three services share.

Interface shape is open. Worth design-it-twice.

**How tests improve**

`Domain/Prompts/MemoryPrompts.cs` is referenced by zero tests today. With
`MemoryTurn`, one test asserts the round trip: window → render → the markers the
extraction prompt names, and recall block → the marker the feature prompt names.
Today the only way to test the `[Memory context]` contract is through
`OpenRouterChatClient`, which needs an HTTP transport.

---

## Noted, not carded

Smaller, or better folded into a candidate above.

**The alert routing rule, restated in six places.** The timer/alarm/schedule
decision and the four-hour ceiling appear in `Domain/Prompts/TimerPrompt.cs:21-38`
and `:33`, `:42`, `:58`; `Domain/Prompts/SchedulingPrompt.cs:16-18`;
`Domain/Prompts/HomeAssistantPrompt.cs:98-110`;
`McpServerTimers/McpResources/FileSystemResource.cs:16`; and
`McpServerScheduling/McpResources/FileSystemResource.cs:21`. Only
`Domain/Tools/Timers/Vfs/TimerFileSystem.cs:307` (`MaxDurationSeconds`) is
machine-readable. This has already shipped a bug:
`Tests/Unit/McpServerScheduling/FileSystemResourceTests.cs:41-44` documents that
the resource blurb contradicted the engine on both halves of the timing contract.
One shared fragment sourced from the constant. Also split
`HomeAssistantPrompt.cs` (195 lines, five jobs) — lines 118-179 are Music
Assistant playback, a different subsystem, and the tool side already has the
`HaMusicActions` split.

**`SendReplyTool` is 450 lines of reply policy behind a static MCP signature.**
`McpChannelVoice/McpTools/SendReplyTool.cs` (518 lines): eight service-locator
lookups at `:37-52`, private statics threading nine or ten parameters at
`:196-205`, `:220-228`, `:261-271`. Tests must build a nine-registration
`ServiceCollection` to call a static — `SendReplyToolTests.cs:87-97`, `:470-479`,
`:526`, plus `TurnLatencyDecompositionTests.cs:87-101` and
`SendReplyToolScheduledDeliveryTests.cs:46-57`. The per-satellite voice fallback
`session.Config.Tts?.OpenAi?.Voice ?? settings.Tts.OpenAi.Voice` is duplicated at
`:273`, `RequestApprovalTool.cs:124` and `:140`, `AnnouncementService.cs:58-60`.
The static-plus-`IServiceProvider` shape is a repo-wide convention and should
stay; the module inside it should come out. Candidate 4 claims the voice fallback
(one resolver on the satellite session) and the disposal duty; what stays here is the
service-locator lookups and the parameter threading.

**The capture is a shared mutable field.** `SatelliteSession.cs:155-178` exposes
`OpenCapture`, `CloseCapture`, `HasActiveCapture`, `RouteAudio`, `EndCapture`,
`GetCaptureActivity` and `TryAbortCapture` over one volatile field at `:47`.
`CaptureSession` was extracted to own the mic but the field stayed public, so
`RequestApprovalTool.cs:177-196` re-implements open/close/record by hand and
`WakeArbiter` reaches into four unrelated facts through the whole session
(`WakeArbiterHandle` at `WakeArbiter.cs:8-11` carries a `SatelliteSession`). The
tell: `HasActiveCapture` at `:164` has zero production callers and 12 test call
sites using it as a spin-wait synchroniser. Narrow `WakeArbiterHandle` to
`CalibratedPeakIn`, `TryAbort` and `ReArmAsync`. Related to candidate 3.

**Channel-connection lifecycle.** `Infrastructure/Clients/Channels/McpChannelConnection.cs`
(461 lines) implements two interfaces that between them describe none of its
lifecycle. "Not connected" has five behaviours: `SendReplyAsync` and
`RequestApprovalAsync` throw (`EnsureConnected` `:455-461`),
`CreateConversationAsync` returns null (`:355-358`), `RegisterAgentsAsync`
returns silently (`:398-401`), `IsHealthyAsync` returns false (`:427-430`), and
`Messages` at `:54` yields forever. The connect → register → reconnect →
re-register ordering lives entirely in `Agent/App/ChannelConnectionHost.cs:27-50`
plus two near-identical 28-line retry loops at `:67-94` and `:96-123`. Also
`CreateConversationAsync` pays a full `ListToolsAsync` round trip to probe
capability on every call (`:362-364`), and `ChatMonitor` calls it per turn per
target for agent-initiated turns (`:237-240`). Consider one
`RunAsync(endpoint, catalog, ct)` owning the whole lifecycle and caching the tool
set per connection generation.

**HTTP adapter boilerplate.** `Infrastructure/Clients/Voice/HttpSatelliteCatalog.cs:16-34`,
`HttpAlertDismisser.cs:13-18` and `HttpInsistentAnnouncer.cs:14-23` each repeat
build request → add `X-Announce-Token` → `VoiceHubHttp.SendAsync` →
`EnsureSuccessStatusCode` → `ReadFromJsonAsync ?? []`, roughly 14 of 20 lines.
`VoiceHubHttp.cs:16-32` proves the seam is wanted but only deepened transport.
Error policy differs per client with no interface stating it:
`Torrent/JackettSearchClient.cs:38-41`, `:57-60`, `:76-79` swallow everything to
`[]`; `BraveSearchClient.cs:16-24` throws raw; `HomeAssistant/HomeAssistantClient.cs:159-197`
maps to typed exceptions (the properly deep version of the same idea);
`Torrent/QBittorrentDownloadClient.cs:109-126` re-authenticates on 403. None of
the three voice adapters has a unit test.

**`ConversationContext` travels by magic string plus an AsyncLocal.**
`Infrastructure/Agents/Mcp/ConversationContextMeta.cs:10` (`OptionsKey`), `:17`
(reads `FunctionInvokingChatClient.CurrentContext`); stamped at
`McpAgent.cs:373`, `:388-399`; read at `QualifiedMcpTool.cs:27`; handed to
Domain's `FeatureConfig` at `MultiAgentFactory.cs:73` and `:119`. The contract is
enforced by nothing except an error log at `McpAgent.cs:392-394`, and
`options ??= CreateRunOptions(...)` at `:274` means a caller-supplied
`AgentRunOptions` drops the context entirely. The ambient read itself is
deliberate and correct per the comment at `ConversationContextMeta.cs:13-16`, so
this is about giving it a home, not removing it. A `TurnContextScope` owning both
ends. Speculative; overlaps candidate 7.

---

## How to work a candidate

Per `/ask-matt`, the route is the main flow entered at `/grill-with-docs`:

1. `/grill-with-docs` on one candidate, describing the friction and the proposed
   deepening in your own words. Do not point it at this file as a substitute for
   stating the claim.
2. `/to-spec` to collapse the thread.
3. `/to-tickets` to split it into `.scratch/<slug>/issues/`, blockers-first.
4. Clear context, then `/implement` per ticket path.

Keep steps 1 to 3 in one unbroken context window.

Candidate 2's defect fix does not need grilling: the root cause is confirmed, so
a red `/tdd` test for "a server push still reaches the store after a rebuild" is
enough. The module extraction is separate and does need grilling.

Candidates 3, 4 and 12 propose modules whose interface could go several ways.
Run `/codebase-design`'s design-it-twice inside the grilling for those.

After `/to-tickets` on this batch, write the cross-candidate ordering the way
`.scratch/README.md` does for the previous batch. No individual ticket can
express it.
