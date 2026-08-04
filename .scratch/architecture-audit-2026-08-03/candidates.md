# Architecture audit — 2026-08-03

Twelve deepening candidates from the second architecture review. This file was the
survey output, not a plan. All twelve have now been grilled; each candidate's Status
row says where it went, and the body records what survived.

Each candidate becomes its own `.scratch/<slug>/` folder once it goes through
`/grill-with-docs` → `/to-spec` → `/to-tickets`. Until then this is the only
durable record. Update the Status line of a candidate when it moves.

The seven candidates from the 2026-08-02 review all landed and are excluded.
The adapter-counting argument is excluded because `docs/adr/0001-single-adapter-interfaces-stay.md`
closed it.

**All twelve have since shipped.** On 2026-08-04 the "Noted, not carded" section was
re-verified against the shipped code and grilled: three of its seven items became
`.scratch/voice-and-channel-lifecycle/spec.md`, and the four that stayed were rewritten
with today's facts. Read those four as of 2026-08-04, not as of the survey.

## Index

| # | Candidate | Strength | Area | Status |
|---|---|---|---|---|
| 1 | Metrics publishing has no module | Strong | cross-cutting | Grilled → `.scratch/metrics-publishing-module/spec.md` |
| 2 | The rebuild loses its event handlers | Strong | WebChat.Client | Grilled → `.scratch/chat-live-connection/spec.md` |
| 3 | The satellite connection has no module | Strong | McpChannelVoice | Grilled → `.scratch/satellite-connection-module/spec.md` |
| 4 | Playback has no outcome | Strong | McpChannelVoice | Grilled → `.scratch/playback-outcome/spec.md` + `docs/adr/0003-playback-settles-by-outcome.md` |
| 5 | The hub call surface leaks the connection | Strong | WebChat.Client | Grilled → `.scratch/hub-call-surface/spec.md` + `docs/adr/0004-hub-calls-answer-or-say-not-live.md` |
| 6 | `AddToolServer`, twin of `AddChannelServer` | Strong | McpServer* | Grilled → `.scratch/mcp-server-hosting/spec.md` + `docs/adr/0005-user-secrets-outrank-environment-variables.md` |
| 7 | Two copies of "how to build an agent" | Strong | Infrastructure/Agents | Grilled → `.scratch/agent-spec/spec.md` |
| 8 | The turn is not a value | Strong | Domain/Monitor | Grilled → `.scratch/conversation-group/spec.md` + `docs/adr/0006-a-group-is-anchored-and-built-by-its-first-turn.md` |
| 9 | One breakdown descriptor, not seven pipelines | Worth exploring | Dashboard + Observability | Grilled → `.scratch/metric-family/spec.md` + `docs/adr/0007-a-metric-family-is-named-not-typed.md` |
| 10 | Timers and schedules are the same backend | Rejected | Domain/Tools | Grilled → closed, no change |
| 11 | Dashboard re-implements WebChat's client | Reframed | Dashboard.Client | Grilled → sharing rejected, `docs/adr/0008-the-two-browser-clients-stay-separate.md`; reframed → `.scratch/dashboard-live-connection/spec.md` |
| 12 | The memory turn has no owner | Worth exploring | Domain/Memory | Grilled → `.scratch/turn-rendering/spec.md`; reframed, one claim withdrawn |

Candidates 1 and 2 are live defects, verified against the code. The rest are
friction.

## Ordering

Take 1 first: it is the only candidate that is both a live defect and a
cross-cutting deepening.

Take 2 before 11, decided during 11's grilling. The original reason — extract the
shared Blazor seam from the deepened connection in 2 — died with the sharing premise.
The reason now is weaker but still holds: nothing is shared, but 2 is the worked
example 11 mirrors in naming, module shape and test structure, and doing them in the
other order means writing the second one twice.

Take 5 after 2, decided during its grilling: 2 renames the module, adds the receive
verb to `IChatHubConnection` and gives its fake a handler registry, and keeps the raw
accessor deliberately for 5 to remove. Running 5 first would write the send verbs
onto an interface 2 then renames.

Candidate 6 touches no file another candidate touches and can run at any point.
Candidate 10 was closed during grilling and is not scheduled.

Candidate 9 is sequenced BEFORE candidate 11, decided during 9's grilling and
confirmed during 11's. Two of the four original contact points were the retyping of
`Store<TState>` and `LocalStorageService`, which the reframing removed. The other two
stand and are enough: both rewrite `MetricsHubEffect.StartAsync` and
`MetricsHubEffectTests`. A third appeared during 11's grilling — 11's catch-up reloads
through `DataLoadEffect`, which candidate 9 rewrites from 133 lines and eleven injected
stores down to a walk of the family table. Written before 9, catch-up gets written
twice. Candidate 9 has no dependency on candidate 2 and can start now; candidate 11
waits on both.

Candidate 7 is sequenced AFTER candidate 1, decided during its grilling: both
rewrite the same lines inside `McpAgent`. Candidate 1 deletes `SafePublishLatencyAsync`,
makes publishing void and non-null and replaces both turn stopwatches with a latency
scope; running it first means candidate 7 folds an already-correct class into the
spec, and candidate 1's accounting of untouched test construction sites still holds.

Candidate 8 is sequenced AFTER candidate 1, decided during its grilling, on the same
argument as candidate 7: candidate 1's ticket 03 rewrites the monitor's publish sites
against today's layout, and candidate 8 moves those lines into a new module. Candidate
8 also contacts candidate 12, but not for the reason first recorded here: the recall
call at `ChatMonitor.cs:282` does not move, and what changes is the signature of the
private method holding it. Candidate 8's ticket 02 makes the monitor's private per-turn
methods take a turn record, and `BuildUserMessageAsync` is one of them — which is where
candidate 12 hangs its anchor-ordering test.

Candidate 12 is sequenced AFTER candidates 1 and 8, decided during its grilling.
Candidate 1's ticket 05 rewrites `MemoryRecallHook`'s stopwatch and publish structure
and drops `Async` suffixes from methods that lose their awaits; candidate 12 edits the
same file for the feature gate and the anchor. Candidate 8 settles the signature the
ordering test attaches to. Neither contact is deep, but going first in either case
means writing the same lines twice. Candidate 8 already waits on 1, so this adds no
new dependency edge.

Candidate 3 unblocks the voice half of candidate 1: the spans that candidate 1
wants under test are only reachable through the hosted service today.

Candidate 4 is sequenced AFTER candidate 3, decided during its grilling: the
`Discarded` outcome is settled by the satellite connection's drain phase, which
candidate 3's spec is what creates.

Candidates 4 and the noted `SendReplyTool` item overlapped; 4 has been grilled and
took the smaller half. Claimed by 4: the three segment-release paths, the three
prefetch-disposal paths, and the per-satellite voice fallback duplicated at four
sites. Left for the noted item: the service-locator lookups and the private statics
threading nine or ten parameters, whose fix is a reply-speaker module holding them as
fields. Both halves have now landed — 4 shipped, and the remainder is
`.scratch/voice-and-channel-lifecycle/issues/02-the-reply-speaker-leaves-the-tool.md`.

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

**Strength:** Strong. **Grilled**, spec at `.scratch/hub-call-surface/spec.md`. The
open question below — queue, throw, or a documented default — was settled: every hub
call returns either the server's answer or `NotLive`, and the result travels to the
effects. Recorded as `docs/adr/0004-hub-calls-answer-or-say-not-live.md`. The
grilling found the friction to be three live defects, not friction: the sidebar wipe
at `AgentSelectionEffect.cs:84`, the vanished message at `SendMessageEffect.cs:93`,
and the stream that starts and says nothing at `StreamingService.cs:43`. It also
found the guard incomplete — it covers the null window but not the connecting or
reconnecting states — and the three integration adapters to be unreferenced dead
code. Sequenced after candidate 2.

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

**Strength:** Strong. **Grilled**, spec at `.scratch/mcp-server-hosting/spec.md`. The
open question below — one documented answer on nested sections — was settled by probe:
the plain `config.Get<T>()` binds nested sections from environment variables exactly as
the explicit re-bind does, so `McpServerWebSearch:30-35` is redundant code to delete,
not a disagreement to resolve. The grilling found three more things the survey missed:
the `?? throw new InvalidOperationException("Settings not found")` guard is unreachable
in all 13 copies; `AddUserSecrets<Program>()` is a silent no-op on the 5 servers with no
`UserSecretsId`; and the source order is load-bearing and unrecorded — user secrets are
added last so they outrank the empty `.env` placeholders, recorded as
`docs/adr/0005-user-secrets-outrank-environment-variables.md`. It also found the count
wrong in the other direction: the `AddSingleton(settings).AddMcpServer().WithHttpTransport()`
prologue is 13 copies, not 9, because every channel server has it too. The spec therefore
splits `AddMcpHost` (all 13) from `AddToolServer` (the 9), renames `Channels.Hosting` to
`Mcp.Hosting`, and shares one call-tool filter between both calls.

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

Grilled. Three claims above were checked against the code and are wrong: subagents
already emit token, tool-call and tool-exec events; `SessionWarmup` is unreachable
for a subagent and `HistoryStore` times a null store, so the real gap is
`LlmFirstToken` and `LlmTotal`; and the coverage-gap note below is wrong, because
`Tests/Unit/Infrastructure/McpAgentLatencyTests.cs` and two sibling unit test files
cover `McpAgent`'s turn behaviour against a mocked `IChatClient`. See
`.scratch/agent-spec/spec.md`.

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

**Strength:** Strong. **Grilled**, spec at `.scratch/conversation-group/spec.md`,
decision recorded as
`docs/adr/0006-a-group-is-anchored-and-built-by-its-first-turn.md`. Sequenced AFTER
candidate 1, for the same reason candidate 7 is: candidate 1's
`issues/03-migrate-the-chat-monitor.md` rewrites the exact publish sites this
candidate relocates and asserts the existing monitor tests pass unchanged, so
landing 8 first would force a re-spec of an already-ticketed piece of work. The
grilling settled the open shape: the `int index` is deleted rather than renamed to
`IsGroupOpener`, because a group anchors on its first *turn* instead of its first
*message*, which makes "the anchor message" and "the first queued turn" the same
message by construction. It also found `DeliveryTarget.Minted` to be the real
source of the `skipMinted` correction — the flag goes stale on reused anchors, and
redefining it as per-turn truth deletes the parameter. Two things the survey below
overstates: the unawaited warmup task does not outlive the agent (warmup takes
`McpAgent._syncLock` before the dispatch loop is reached, so `DisposeAsync` always
waits behind it), and the eager order's documented reason survives the change —
warmup still overlaps the announce and memory recall, losing only its overlap with
a string switch. One constraint the survey missed: the thread context and its
`group.Complete` callback must stay eager, because `ChatThreadResolver.ClearAsync`
only deletes persisted state when it finds a live context. Contact with candidate
12: `ChatMonitor.cs:282` moves into the new module, so 12's line references go
stale if 8 lands first.

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

**Strength:** Worth exploring. **Grilled**, spec at `.scratch/metric-family/spec.md`,
decision recorded as `docs/adr/0007-a-metric-family-is-named-not-typed.md`.

**Files**

- `Dashboard.Client/Effects/MetricsHubEffect.cs:31-139` — 280 lines, 7 `CancellationTokenSource` fields, 7 near-identical `Refresh*BreakdownAsync`
- `Dashboard.Client/Effects/DataLoadEffect.cs` — 133 lines, an eighth copy of the same fan-out, missed by the survey. 11 injected stores, 19 parallel calls, and the page-load half of the aggregation-default bug
- `Dashboard.Client/Services/MetricsApiService.cs:43-107` — 7 `Get*GroupedAsync`
- `Observability/MetricsApiEndpoints.cs:94-237` — 238 lines, 7 `/x/by/{dimension}` maps, 22 copies of `from ?? today`, all calling `DateTime.UtcNow` directly even though `MetricsQueryService:26` already takes a `TimeProvider`
- `Observability/Services/MetricsQueryService.cs:171-445` — 452 lines, 7 `Get*GroupedAsync`. **Out of scope**, see below
- `Dashboard.Client/Pages/{Tokens,Tools,Errors,Schedules,Memory,Latency,Voice}.razor` — 41 `Storage.Get*/SetAsync` sites across these seven plus `Overview.razor`
- `Domain/DTOs/Metrics/Enums/LatencyMetric.cs` — a reduction over durations, used as the "Metric" pill on latency and the "Aggregate" pill on voice

**Friction**

Adding one metric family means editing five files in lockstep with no compiler
help. Voice was the last one added. The comment at `MetricsApiService.cs:100-102`
documents exactly the bug this fan-out produces: a default parameter in one layer
silently reverting the user's selection in another. Every page repeats the same
sequence: load saved prefs, subscribe, `SetDateRange`, `LoadAsync`, the three
`On*Changed` handlers, persist, `ReloadBreakdown`.

**Proposed deepening (as surveyed)**

One `MetricBreakdown<TDimension, TMetric>` descriptor per family carrying route
segment, Redis key prefix, store and localStorage key prefix, with a single
implementation owning the CTS and debounce, the query-string build, the
null-to-empty mapping and preference persistence. The 7 effect methods become a
table lookup, the 7 endpoint maps become one loop, the 7 pages become one
`<BreakdownPage Descriptor=... />`.

**Settled by grilling**

*Scope narrowed.* Client-side, plus the endpoint date defaulting.
`MetricsQueryService`'s grouping stays per-family: `GetTokenGroupedAsync` switches
between two event streams by metric, `GetMemoryGroupedAsync` merges three and
type-switches, `GetVoiceGroupedAsync` pre-filters by event kind. There is no shared
shape to extract. `DataLoadEffect` comes in, and the family absorbs its raw-event
load as well as the breakdown, taking it from 133 lines to roughly 35.

*One descriptor, not one generic.* `BreakdownFamily` carries a name, a localStorage
prefix and two delegates; `BreakdownFamily<TState>` adds the store handle. It is not
generic over dimension and metric, because the seven families have four call shapes
and the generic version fits three. The ADR records the rejected alternative and its
price.

*Redis key prefix dropped from the descriptor.* `Dashboard.Client` references Domain
only and `Observability` references both, so a descriptor holding the Redis prefix and
the localStorage prefix could only live in Domain, shipping the server's key layout
into the WASM download.

*Refresh coalesces rather than cancels.* Awaiting a refresh means the breakdown
reflects state at or after the call; concurrent callers share the run and it re-runs
once if state moved. There is no debounce in `Dashboard.Client` today, so the surveyed
"CTS and debounce" would have been new behaviour with new lag. Cancelling a WASM
`HttpClient` request does not stop the server, so today's cancel-stale makes a burst of
twenty events cost twenty full Redis aggregations and read one. This is the candidate's
only behaviour change, and it rewrites
`MetricsHubEffectTests.cs:115 RapidEvents_CancelsStaleApiCallAndUsesFreshData`.

*Refresh throws; callers apply the policy.* `MetricsHubEffect` wraps the table in one
try/catch replacing seven; `DataLoadEffect` keeps its catch and its red connection dot.
No behaviour change.

*Pages keep their markup.* The surveyed `<BreakdownPage Descriptor=... />` was
over-claimed: below the control header nothing is shared. Each page has its own KPI row,
chart type and event table, `Memory` injects `MetricsStore` too, `Latency` draws a trend
series, and the headers differ (group-by on 7, metric on 5, `DisabledValues` on 2, an
extra Aggregate pill on Voice). A `<BreakdownControls>` wrapper owns the header,
preference load/save and date derivation, with Voice's pill in an `ExtraControls`
fragment. `Overview.razor` stays as it is; it has the time pill but no breakdown.

*`DateRange` binder.* A record with `BindAsync` defaulting from `TimeProvider`, one
parameter per endpoint. 22 `DateTime.UtcNow` calls become one, and the endpoints become
time-testable like the query service already is. No OpenAPI in `Observability`, so
hiding the query parameters behind a binder costs no tooling.

*`LatencyMetric` renamed `Aggregation`.* Query-string names unchanged. It is never
persisted, so the rename is value-safe. `VoiceMetric` keeps its name: it is pinned by
integer value in Redis, and renaming it would pull 27 `new VoiceEvent` sites in
`McpChannelVoice` into a Dashboard candidate.

*Vocabulary.* `CONTEXT.md` gains a Metrics dashboard section: metric family, breakdown,
dimension, aggregation.

**How tests improve**

Only `Tests/Unit/Dashboard.Client/MetricsApiServiceLatencyTests.cs` (41 lines)
and `MetricsHubEffectTests.cs` (384 lines) touch this. The per-page preference
and reload logic is reachable only through Playwright in `Tests/E2E/Dashboard/`.
One parameterised test over the family table covers all seven families,
including the six with no coverage.

The wrapper's preference and date logic moves into a plain `BreakdownSession` class so
it unit-tests with the existing xunit, Shouldly and fake `TimeProvider`. There is no
bUnit in the repo and this candidate does not add it, so which pills render stays
E2E-covered.

---

## 10 — Timers and schedules are the same backend

**Strength:** Rejected during grilling on 2026-08-03. Closed with no change. The
verdict and its evidence are at the end of this section; the original write-up is
kept as written so the argument can be re-read if someone reopens it.

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

**Verdict — rejected, 2026-08-03**

Closed with no code change. Both arguments the candidate rests on are weaker than
the write-up claims.

The duplication is smaller than the correspondence table suggests. Byte-identical
across the two files: the `Error` factory, the `Exec` envelope, `ParseSpec`,
`ToZone` and the created envelope — roughly 40 to 50 lines out of 938. The rest of
the table is same-shape, different-content. `NodeExistsAsync`, `ScopeXAsync`, glob
and read all switch over per-backend node enums, and the two trees are not the same
depth: timers are `/<id>/{timer,status}.json` plus a root-level `dismiss.sh`, while
schedules are `/<agentId>/<scheduleId>/{schedule,status}.json` plus `run_now.sh`
plus an `agent_info.json` at the agent level. A shared class would first have to
unify those node models behind an optional owner level and two sets of extra files,
which costs about what it saves. Exec compounds it: timers exec at the mount root,
schedules exec on the record directory.

The test argument does not hold at all. `ScheduleFileSystemJourneyTests` makes 10
glob/info calls; `TimerFileSystemJourneyTests` makes 1. There is no timer traversal
suite to consolidate. The only literally duplicated tests are
`Search_UncompilablePattern` and `Search_PathologicalRegex`, and both already
exercise `FileSystemBackendBase`, not either backend.

Two facts found while grilling, worth keeping whether or not this ever reopens:

- An intermediate class is compatible with capability-by-override.
  `FileSystemServerTools.Overrides` only checks
  `DeclaringType != typeof(FileSystemBackendBase)`, and
  `DiskFileSystem → TextDiskFileSystem → SandboxFileSystem` already relies on that.
  The consequence is that whatever an intermediate overrides is advertised for every
  subclass, and a subclass cannot opt back out.
- The third `Error` copy could not have been fixed by lifting onto
  `FileSystemBackendBase` as proposed: `DownloadsOverlay` is a plain class, not a
  backend. Separately, `FsError.AlreadyExists<T>` already exists and both backends
  bypass it by hand-rolling `Error(ToolError.Codes.AlreadyExists, …)`, because
  `FileSystemBackendBase` never exposed it.

---

## 11 — Dashboard re-implements WebChat's client

**Strength:** Reframed during grilling on 2026-08-03. The sharing half was rejected
and recorded as `docs/adr/0008-the-two-browser-clients-stay-separate.md`. What
survives is a dashboard live-connection candidate, spec at
`.scratch/dashboard-live-connection/spec.md`, summarised under **Verdict** at the end
of this section. The original survey is kept as written so the argument can be
re-read. Sequenced after candidates 9 and 2.

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

**Verdict — reframed, 2026-08-03**

The sharing argument was rejected and the reasons are in
`docs/adr/0008-the-two-browser-clients-stay-separate.md`. In short: the two
`Store<TState>` classes are used incompatibly, so the deletion test fails; the
reference-equality guard could never fire in Dashboard because every dashboard
reducer allocates; the two `LocalStorageService` classes are a union rather than a
duplicate; and the two connections need disjoint things. What is genuinely identical
is `IAction`, one line.

Three defects found while grilling, all verified against the code, all real, and none
of them about sharing.

**`MetricsHubService.cs:16` uses a bare `.WithAutomaticReconnect()`.** The ASP.NET
Core docs give that overload delays of 0, 2, 10 and 30 seconds and then it stops
permanently. Any outage past roughly 42 seconds kills the dashboard's live feed for
good — an agent container restart is enough, no mobile backgrounding required.

**A failed initial start is unrecoverable, not merely un-retried.**
`MetricsHubEffect.StartAsync` sets `_started = true` at `:149`, before registering
handlers and before `await hub.StartAsync()` at `:263`. `MainLayout.razor:42`
swallows the exception. A second call returns at the guard, so the transport is never
started. The same docs confirm `WithAutomaticReconnect` never retries an initial
start under any policy, so the retry loop has to be written by hand.

**A reconnect catches nothing up.** `OnReconnected` at `:246` only flips the
connection flag. `Observability/Hubs/MetricsHub.cs` is an empty `Hub` with no
`OnConnectedAsync`, so the server never replays a gap. Every event missed during an
outage stays missing from the stores and the breakdowns until the user changes a pill
or reloads the page, under a green Live dot. WebChat has `ReconnectionEffect` for
exactly this; Dashboard has no counterpart.

Also confirmed: only `Overview.razor:30-31` renders a connection indicator. The other
eight pages show stale numbers with no signal at all.

**Settled by grilling**

*Recovery is a retry policy that never gives up*, backing off to a steady interval —
0, 2, 10, 30, then 30 forever. No JS interop, no `visibilityHelper`, no foreground
policy, no rebuild path. The half-open-zombie case that `ForegroundReconnectPolicy`
exists for in WebChat is not covered, and that is accepted: it needs a probe verb and
a rebuild, and the dashboard's dominant failure is an agent restart rather than an
Android freeze.

*The module retries its own initial start* on the same schedule and never gives up, so
opening the dashboard during a restart works. The `_started` latch bug goes with it.

*Catch-up runs on every recovery and is skipped on the first connect*, where page load
already loads the same data. It reloads for the range held in the stores, which after
candidate 9 is the family table.

*A connection epoch on `ConnectionState`*, an int incremented on becoming live,
matching candidate 2's term. Honest note: the race the epoch closes in WebChat — a
rebuild completing before anyone observed a disconnected state — does not exist here,
because SignalR always raises `Reconnecting` before `Reconnected` and there is no
rebuild. Its value in Dashboard is shared vocabulary and a reload rule assertable
against the store rather than through the effect.

*The status indicator moves into `MainLayout`* so all nine pages show it, and the
state widens from `bool IsConnected` to Live / Reconnecting / Connecting. `Overview`
drops its local dot and reads the store. With a policy that never gives up there is no
permanent dead state, so the useful distinction is between trying and never having
been up.

*`IMetricsHubConnection` with one generic receive verb*, plus lifecycle events and
start, replacing the 14 `virtual` members and the `protected` parameterless
constructor that exist only so `MetricsHubEffectTests` can subclass the concrete
class. Same complaint as candidate 7's `chatClientFactory`: a test hook cut through
the seam. No factory: without a rebuild there is never a second connection instance.

*A `MetricsLiveConnection` module* owns the ordered sequence — build, bind, start,
publish status, catch up. `MetricsHubEffect` keeps only the event-to-store mapping it
shares with candidate 9's family table.

*Vocabulary.* `CONTEXT.md`'s "Chat client connection" section was renamed "Client live
connection" and its six client-agnostic terms reworded to cover both clients;
session recovery, hub call and not live are marked chat-client-only. New term
**catch-up**, kept distinct from session recovery because one re-reads data and the
other re-establishes an identity.

**Out of scope, moved to Noted**

The unselective page subscriptions. See the entry below; the diagnosis in the survey
above is wrong and the corrected one is there.

---

## 12 — The memory turn has no owner

**Strength:** Worth exploring. Grilled → `.scratch/turn-rendering/spec.md`. The
proposed single module did not survive; three of the four friction claims did, and
the scope widened past memory. Body below rewritten to match what was settled.

**Files**

- `Domain/Monitor/ChatMonitor.cs:266-287` (`BuildUserMessageAsync`, the recall call at `:282`)
- `Infrastructure/Memory/MemoryRecallHook.cs:59-60` (anchor), `:78`, `:99-103`, `:41-48` (feature gate)
- `Domain/Extensions/ChatMessageExtensions.cs:13`, `:126-145`
- `Infrastructure/Agents/ChatClients/OpenRouterChatClient.cs:82-136` (the whole clone-and-prepend transform), `:296-311` (`FormatMemoryContext`, private static)
- `Infrastructure/Memory/MemoryExtractionWorker.cs:108-130`, `:46-53` (feature gate)
- `Domain/Memory/ConversationWindowRenderer.cs:23-32`
- `Domain/Prompts/MemoryPrompts.cs:9`, `:29`, `:70` — 149 lines, zero tests
- `Infrastructure/Agents/ChatClients/RedisChatMessageStore.cs:69` (what actually gets persisted)

Two references in the original card had drifted: `FormatMemoryContext` is at `:296-311`,
not `:322-337`. `Domain/Memory/MemoryExtractionQueue.cs` was listed and is not involved.

**Friction**

Three unexpressed invariants span this chain.

`anchorIndex = persistedCount` at `MemoryRecallHook.cs:59-60` is correct only
because `ChatMonitor` calls `EnrichAsync` before the turn is persisted.
`MemoryExtractionRequest.AnchorIndex` is a bare `int` that says nothing about
this. If the order ever changed, the extraction window would take the current
message out of the persisted thread *and* append `FallbackContent`, handing the
extractor the same turn twice with the real one labelled `[context -1]`. Nothing
would go red.

`MemoryExtractionWorker.BuildExtractionWindowAsync` appends `FallbackContent`
last at `:124-127` purely so `ConversationWindowRenderer` labels it `[CURRENT]`
at `:26-28`, which is what `MemoryPrompts.ExtractionSystemPrompt` promises the
model at `:29` and `:70`. Three modules, no shared contract. Both mechanical ends
do have tests — `MemoryExtractionWorkerDriftTests` and `ConversationWindowRendererTests`
— so what is unpinned is only the link to the prompt constant.

`MemoryPrompts.FeatureSystemPrompt:9` tells the model to look for a
`[Memory context]` block that is produced by a private static in an unrelated
adapter, `OpenRouterChatClient.FormatMemoryContext`. The card's "memory silently
vanishes behind any other `IChatClient`" does not apply — `OpenRouterChatClient` is
the only implementation in the repo. What stands is that the block has no test
anywhere and is reachable only by driving the chat client.

The slicing rule is a private method on a `BackgroundService`, so four of the ten
tests in `MemoryExtractionWorkerTests` (`:240`, `:288`, `:317`, `:380`) each stand up
a fake extractor, embedding service, store, thread store, metrics publisher and agent
definition provider to assert a `Take`/`TakeLast`.

The feature gate is copy-pasted at `MemoryRecallHook.cs:41-48` and
`MemoryExtractionWorker.cs:46-53`, fail-open on both a null and an unknown agent id
with neither copy saying so.

**Withdrawn: the missing gate in `MemoryDreamingService`.** The gate is per agent;
dreaming iterates users from `store.GetAllUserIdsAsync` and has no agent to check. A
user only reaches that list if memories were stored for them, which required an agent
with memory enabled, and consolidating memories that already exist is correct however
many agents can read them back. There is no global `Memory:Enabled` flag either — a
switch like that is a separate candidate, not this one. Do not re-raise.

**Deepening as settled**

Not one module. Two chains that touch only through `MemoryExtractionRequest`, which
already exists, so they split by the prompt each satisfies: an `ExtractionWindow`
(pure `Build` plus `Render`, absorbing `ConversationWindowRenderer`) paired with
`ExtractionSystemPrompt`, and a `RecallBlock` renderer paired with
`FeatureSystemPrompt`. `FormatMemoryContext` moves out of the adapter, and with it the
whole clone-and-prepend transform: one `UserMessageDecorator` owns everything
prepended to an outgoing user turn — sender, location, satellite, timestamp,
dismissed alert and the recall block. The anchor becomes a named value whose factory
states its precondition, pinned by a `ChatMonitor` ordering test. One feature-gate
extension replaces both copies.

**Examined and accepted, no change.** Memory context is persisted on the message
(`RedisChatMessageStore.cs:69` stores `RequestMessages`) while the rendered block is
not, so every request re-renders a block for each historical user turn that carries
context. `ChatMessageSerializationTests.cs:157-166` pins that as deliberate for prompt
caching. It also rules out rendering the block at the recall hook: that would put the
text into the persisted message, which the extraction worker reads back.

**How tests improve**

`Domain/Prompts/MemoryPrompts.cs` is referenced by zero tests today. Marker
cross-checks on both renderer seams give it its first coverage. The twelve prefix
tests in `OpenRouterChatClientPrefixTests` drop Moq and the client, the four window
tests drop six fakes each, and the recall block becomes testable without a transport.

---

## Noted, not carded

Smaller, or better folded into a candidate above.

**All seven were re-verified on 2026-08-04**, after the twelve candidates had shipped.
Three went into `.scratch/voice-and-channel-lifecycle/spec.md` and are stubbed below;
a fourth issue appeared during that grilling and was never a noted item. The four that
stayed are rewritten here with today's facts — four of the seven entries had aged, and
two of them lost the evidence they were built on.

**Dashboard pages re-render on every dispatch.** Found while grilling candidate 11,
and it replaces that candidate's diagnosis, which was wrong. Re-verified 2026-08-04
and much smaller than written: candidate 9 collapsed Tokens, Tools, Errors, Latency,
Schedules and Voice onto `Dashboard.Client/Components/MetricControls.razor`, so "the
same shape on the other seven" is no longer true. Four sites remain, each subscribing
to a whole store observable with no selector and no `DistinctUntilChanged`:
`MetricControls.razor:58` (which now serves six pages at once), `Overview.razor:95-110`
(seven subscriptions, four of them calling `RebuildActivity` over four full event
lists), `Memory.razor:156` and `ConnectionIndicator.razor:16`. WebChat avoids this with
`WebChat.Client/State/StoreSubscriberComponent.cs:12-34`, which selects a slice and
applies `DistinctUntilChanged`. Candidate 11 attributed the cost to the missing
reference-equality guard on `Dashboard.Client/State/Store.cs`; that guard could never
fire there, because every dashboard reducer is bound to one action and always
allocates. It would be dead code. The fix is a select-then-distinct helper, and it is
now mostly one edit in `MetricControls` plus `Overview`. No reported symptom, so this
is a finding rather than a card.

**The alert routing rule, restated in five places.** Re-verified 2026-08-04; it was
six, and both the sixth site and the entry's best evidence are gone. Candidate 6
deleted the two hand-written `McpResources/FileSystemResource.cs` files in favour of
`AddFileSystemResource<TBackend>()`, and with them
`Tests/Unit/McpServerScheduling/FileSystemResourceTests.cs`, which documented that the
resource blurb had contradicted the engine on both halves of the timing contract. That
shipped bug can no longer recur the same way — the blurb now comes off the backend.

What is left is the timer/alarm/schedule decision and the four-hour ceiling written out
in prose at `Domain/Prompts/TimerPrompt.cs:16-18`, `:33`, `:36`, `:42`;
`Domain/Prompts/SchedulingPrompt.cs:16-18`; `Domain/Prompts/HomeAssistantPrompt.cs:79`,
`:98-110`; `Domain/Tools/Timers/Vfs/TimerFileSystem.cs:24-32` (`DescribeMount`); and
`Domain/Tools/Scheduling/Vfs/ScheduleFileSystem.cs:21`. Only
`Domain/Tools/Timers/Vfs/TimerFileSystem.cs:317` (`MaxDurationSeconds`) is
machine-readable, and the ceiling is spelled "4 hours" in four of the prose sites. One
shared fragment sourced from the constant. Also split `HomeAssistantPrompt.cs` (195
lines, five jobs) — lines 118-179 are Music Assistant playback, a different subsystem,
and the tool side already has the `HaMusicActions` split.

**`SendReplyTool` is reply policy behind a static MCP signature.** **Grilled
2026-08-04 → `.scratch/voice-and-channel-lifecycle/issues/02-the-reply-speaker-leaves-the-tool.md`.**
The file is 470 lines now, not 518: candidate 4 took the voice fallback (it is
`SatelliteSession.ResolveVoice`) and the disposal duty. Corrected counts at grilling
time: seven unconditional service-locator lookups at `:36-42` plus two conditional at
`:51` and `:59`, seven private statics, four test files reaching the tool. The
static-plus-`IServiceProvider` shape is a repo-wide convention and stays.

**The capture is a shared mutable field.** **Grilled 2026-08-04 →
`.scratch/voice-and-channel-lifecycle/` issues `03` and `04`, and
`docs/adr/0013-the-microphone-and-the-turn-are-separate-types.md`.** The seven
members are at `SatelliteSession.cs:72-95` over the field at `:9`; `HasActiveCapture`
has zero production callers and 16 test call sites, not 12. This entry's proposed fix
was wrong and the ticket does not follow it: narrowing `WakeArbiterHandle` to
`CalibratedPeakIn`, `TryAbort` and `ReArmAsync` misses `SatelliteId`, `Config.Room`,
`Config.Identity`, `Config.RmsOffsetDb`, `SupportsPause` and `GetCaptureActivity`,
which the arbiter reads at 17 sites.

**Channel-connection lifecycle.** **Grilled 2026-08-04 →
`.scratch/voice-and-channel-lifecycle/` issues `05` and `06`,
`docs/adr/0011-not-connected-is-five-behaviours-and-stays-that-way.md` and
`docs/adr/0012-a-servers-tool-set-is-fixed-for-a-connection-generation.md`.** Verified
intact, with one drifted reference: the per-turn per-target `CreateConversationAsync`
calls moved out of `ChatMonitor` when candidate 8 landed and are now at
`Domain/Monitor/DeliveryTargetResolver.cs:51` and `:91`. The five not-connected
behaviours are kept rather than unified — see ADR 0011 for why
`CreateConversationAsync`'s null is load-bearing.

**HTTP adapter boilerplate.** `Infrastructure/Clients/Voice/HttpSatelliteCatalog.cs:16-34`,
`HttpAlertDismisser.cs:13-18` and `HttpInsistentAnnouncer.cs:14-23` each repeat
build request → add `X-Announce-Token` → `VoiceHubHttp.SendAsync` →
`EnsureSuccessStatusCode` → `ReadFromJsonAsync ?? []`, roughly 14 of 20 lines.
`VoiceHubHttp.cs:16-32` proves the seam is wanted but only deepened transport.
Error policy differs per client with no interface stating it:
`Torrent/JackettSearchClient.cs:38-41`, `:57-60`, `:76-79` swallow everything to
`[]`; `BraveSearchClient.cs:16-24` throws raw; `HomeAssistant/HomeAssistantClient.cs:159-197`
maps to typed exceptions (the properly deep version of the same idea);
`Torrent/QBittorrentDownloadClient.cs:109-126` re-authenticates on 403.

**Re-verified 2026-08-04: this entry's evidence is gone.** "None of the three voice
adapters has a unit test" is false — `Tests/Unit/Infrastructure/Clients/Voice/`
holds `HttpSatelliteCatalogTests`, `HttpAlertDismisserTests` and
`HttpInsistentAnnouncerTests`. What is left is about five repeated lines across four
methods, in files of 19, 24 and 35 lines, where `VoiceHubHttp` already owns the part
with a rule in it. The error-policy divergence still stands and is a separate
observation about four unrelated clients, not about this boilerplate. Weak; kept only
so the divergence is written down somewhere.

**`ConversationContext` travels by magic string plus an AsyncLocal.**
`Infrastructure/Agents/Mcp/ConversationContextMeta.cs:10` (`OptionsKey`), `:17`
(reads `FunctionInvokingChatClient.CurrentContext`); stamped at
`McpAgent.cs:352-362`; read at `QualifiedMcpTool.cs:27`; handed to Domain's
`FeatureConfig` at `MultiAgentFactory.cs:83`. The contract is enforced by nothing
except a warning log at `McpAgent.cs:356-359`. The ambient read itself is deliberate
and correct per the comment at `ConversationContextMeta.cs:13-16`, so this is about
giving it a home, not removing it. A `TurnContextScope` owning both ends.

**Half withdrawn, 2026-08-04.** "`options ??= CreateRunOptions(...)` means a
caller-supplied `AgentRunOptions` drops the context entirely" was the concrete defect
here, and it is no longer one. Candidate 7 landed `ResolveTurnConfig`, whose comment at
`McpAgent.cs:251-255` states that a supplied option set replaces instructions, tools,
reasoning effort and the config patch on purpose for non-channel callers, and whose
warning at `:257-262` says so at run time. No production path passes options. What is
left is taste about the magic string. Speculative, and weaker than when written.

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

Candidates 3, 4 and 12 proposed modules whose interface could go several ways.
Design-it-twice inside the grilling earned its keep on 12: the proposed single module
turned out to be two chains that touch only through a record that already existed.

After `/to-tickets` on this batch, write the cross-candidate ordering the way
`.scratch/README.md` does for the previous batch. No individual ticket can
express it.
