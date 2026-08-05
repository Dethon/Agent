# Architecture review — deepening candidates (2026-08-05)

Scoped by git churn: the worktree-architecture-audit branch's fix stream points at the
seams that are wrong or missing. Vocabulary is the deep-module glossary (module,
interface, depth, seam, adapter, leverage, locality); domain terms are from
`CONTEXT.md`. An HTML version with diagrams was generated alongside this file.

## 1. One refusal rule for the downloads overlay — **Strong**

**Files:** `Domain/Tools/Downloads/Vfs/MediaLibraryDiskFileSystem.cs:101-221`,
`DownloadsOverlay.cs:28-58`, `DownloadsPath.cs:21-62`

**Problem.** "This path belongs to a live download" is one predicate, but it exists as
three predicates (`IsVirtualPath`, `IsLiveVirtualPathAsync`, `TouchesActiveDownloadAsync`)
crossed with two error shapes (tool-error envelope, exception) across seven hand-chosen
refusal sites. The two shapes disagree today: `ReadBlobAsync` serves a leftover
`downloads/<id>/status.json` (liveness check, commit `ead0b827`), while `ReadChunksAsync`
throws on the same path (spelling check). Which one runs depends on which side of the MCP
seam the caller is on. Seven commits (`2a655f4e`, `ffa0cccb`, `f8cb3cbc`, `7f14527f`,
`ead0b827`, `2b57322d`, `ab637436`) each patched one more site.

**Solution.** The downloads overlay owns one
`Task<ToolErrorResult?> RefuseAsync(DownloadsIntent intent, string path, ct)` with
intent ∈ {read, write, land, move-out, move-in, delete}. Every operation in
`MediaLibraryDiskFileSystem` becomes `await RefuseAsync(...) ?? await base.X(...)`; the
chunk methods call the same thing and wrap the envelope.

**Wins:** locality (the rule is one table) · the chunk/blob divergence becomes
unrepresentable · deletion test passes (seven helpers concentrate, not relocate) · tests
assert one interface per intent.

## 2. The cross-mount move guard sits on the wrong side of the MCP seam — **Strong**

**Files:** `Domain/Contracts/ICrossMountMoveGuard.cs`,
`Domain/Tools/FileSystem/VfsMoveTool.cs:64-71`,
`Infrastructure/Agents/Mcp/McpFileSystemDiscovery.cs:73`

**Problem.** `VfsMoveTool` type-tests `end.Backend is ICrossMountMoveGuard`, but the only
backend the registry ever mounts in production is the `McpFileSystemBackend` proxy — the
sole implementer (`MediaLibraryDiskFileSystem`) lives in the `McpServerLibrary` process
behind it. The type test can never succeed, so the live-download move refusal
(`2b57322d`, `f8cb3cbc`) never fires on the deployed path. The tests that cover it mount
a topology (`Mock<IVirtualFileSystemRegistry>` returning the concrete domain backend)
that production never builds. The interface is also shallow: one method, a two-line
delegation, existing to smuggle one predicate past a seam.

**Solution.** Carry the question across the wire like every other capability. Either
(a) lean on the destination/source-side refusals that already exist — the dry-run-then-
stream order in `TransferFileAsync` probes before streaming; or (b) add `fs_move_check`
as a thirteenth entry in `FileSystemOperations.All`, so the proxy gets it through
capability-by-overriding like everything else.

**Wins:** the refusal actually fires in production · capability-by-overriding stays the
one mechanism · the shallow interface deletes · an integration test through the real
topology becomes possible. Note: no integration fixture today mounts media plus a second
filesystem, which is exactly the topology every cross-mount bug lives in.

## 3. WebChat: one module for "this topic is streaming" — **Strong**

**Files:** `WebChat.Client/Services/Streaming/{StreamingService,ActiveStreams,StreamResumeService}.cs`,
`State/Streaming/StreamingStore.cs:74-108`, `State/Hub/HubEventDispatcher.cs:44-121`,
`State/Pipeline/MessagePipeline.cs:44-60`

**Problem.** One state machine — a topic has a stream in flight — is stored four times
(task map in `ActiveStreams`, streaming set and buffer dict in `StreamingState`,
resuming set) and written from five files. Each recent fix is a guard at a seam between
the copies: the phantom-buffer fix (`48cfd730`) is a reducer defending itself against
dispatchers that don't consult the task set; forget-by-`KeyValuePair` (`df8323f9`)
exists because two writers can flip the store's copy independently;
`StreamResumeService` asks the same question three times in twenty lines against three
staleness windows. Testability: one service needs seven stores plus a dispatcher to
construct, and 49 call sites poll with a 5-second `Eventually` loop because no
transition is observably complete.

**Solution.** A `TopicStreams` module owning one record per topic
(`None | Resuming | Streaming(task, buffer, currentMessageId)`) with a transition
interface that cannot be called out of order: `TryBegin(topicId) -> StreamLease?`,
`lease.Append(chunk)`, `lease.Complete()`, `Snapshot(topicId)`. `StreamingStore`
becomes a projection for rendering, not a parallel truth.

**Wins:** chunk-for-idle-topic becomes unrepresentable · leverage (phantom-buffer,
wrong-stream-forgotten and resume-race classes collapse into one invariant) · tests
drop the seven-store constructor and the polling loops · resume asks once.

## 4. A module that owns the satellite connection's generation — **Strong**

**Files:** `McpChannelVoice/Services/{SatelliteConnection,PlaybackQueue,ReplySpeaker,VoiceTurn,FollowUpConversation}.cs`,
`Modules/ConfigModule.cs:138-139`

**Problem.** Five recent fixes (`f7d84180`, `f3238513`, `bc2ff1bc`, `f679ea24`,
`b828f545`) are one bug class: something outlives the satellite connection generation
that created it and wedges or latches the next one. No module's interface is "one
connection generation". `PlaybackQueue` exposes four closing verbs (`Complete`,
`CompleteAndDiscardQueued`, `DiscardUnplayed`, `Dispose`) whose ordering rule is a prose
comment inside `SatelliteConnection.DrainAsync:163-183` — a seam in the wrong place.
Three process-scoped stores each invented their own expiry mechanism to answer "is this
from a dead generation": `VoiceConversationManager` (generation + timer),
`VoiceDeliveryRegistry` (generation + timer), `ReplySpeaker._streams` (reference
identity via `BelongsTo`). `WyomingSatelliteHost.CreateConnection` is `internal` for
tests — an admission the seam sits one layer too high.

**Solution.** A `SatelliteRun` (or make `SatelliteConnection` own it) whose interface is
`RunAsync(...)` plus one terminal `EndAsync(EndReason)` with
`EndReason ∈ {LinkDropped, Shutdown}` replacing the four playback verbs. Inside:
arbitration release, queue close mode, drain, discard sweep, disposal, and one
`OnGenerationEnded(satelliteId, turn)` seam that `ReplySpeaker`,
`ReplyTextAccumulator` and `VoiceDeliveryRegistry` implement as adapters.

**Wins:** "wedged after a redial" untestable-by-construction · four queue verbs shrink
to one reason enum · three expiry schemes become one seam · compatible with ADR-0003
(outcome semantics) and ADR-0013 (mic vs turn).

## 5. Dashboard: becoming live owns its own hold and catch-up debt — **Worth exploring**

**Files:** `Dashboard.Client/Services/MetricsLiveConnection.cs:22-28,76-203`,
`Effects/MetricsHubBinder.cs:20-21,219-225`, `Effects/DataLoadEffect.cs:10-15,44-60`

**Problem.** Ten fixes hit one become-live state machine spread over four files. The
push-hold counter (`_holdDepth`) has three mutators in two modules, and the live
connection decides whether catch-up is owed by reading the page loader's failure flag
plus a raw connection-epoch counter
(`connectionStore.State.Epoch <= 1 && !dataLoad.LastLoadFailed`). The WebChat client
hides the same rule behind `BecameLiveAgain` and its three subscribers never see a
number; the dashboard's callers juggle epochs themselves. Tests assert on the raw
`Epoch` counter in five places because the module has no vocabulary for "did it catch
up".

**Solution.** The connection hands the binder a push-gate it owns
(`binder.Bind(hub, gate)`), so hold depth has one owner and unbind cannot orphan a
queue. `IMetricsCatchUp` also answers whether one is owed; `DataLoadEffect` reports its
outcome to it instead of exposing `LastLoadFailed` + `LoadCompleted` for the connection
to correlate.

**Wins:** hold depth gets one owner · epoch stops leaking to callers, as in WebChat ·
tests stop asserting on a raw counter · two fields and one event delete from the
connection.

## 6. A resolution that translates paths, so no tool can forget — **Worth exploring**

**Files:** `Domain/Contracts/IVirtualFileSystemRegistry.cs:6`,
`Domain/Tools/FileSystem/Vfs{GlobFiles,TextRead,FileInfo,TextSearch,Copy,Remove}Tool.cs`

**Problem.** Backends answer in mount-relative coordinates with varying leading-slash
conventions; every tool that cares re-derives the translation itself. Four
near-identical private `Normalize` methods; five tools (`remove`, `create`, `edit`,
`copy`, `move`) skip it and leak backend-local (sometimes container-absolute) paths to
the model. The same fix has been applied twice, months apart (`3ce84b3d`, `1c02dae3`),
to the two tools missed the first time.

**Solution.** `FileSystemResolution` — today a behaviourless 3-tuple — gains
`ToVirtual(...)`, or better, the registry returns a resolution that wraps the backend
and translates on the way out, so the interface every tool sees already speaks full
virtual paths.

**Wins:** leverage (one implementation, eleven call sites) · `remove`/`create`/`edit`
become correct without anyone noticing · four duplicate normalizers delete · the next
convention change is one edit.

## 7. BindSettings: settings declare their shape instead of being guessed at — **Worth exploring**

**Files:** `Mcp.Hosting/SettingsBinder.cs:75-164`,
`Tests/Unit/Mcp.Hosting/{SystemXProbe,MicrosoftXProbe}Settings.cs`

**Problem.** The public interface says `where TSettings : class`, but the real contract
is a reflection predicate over C# shapes — setters, record structs, initializer
defaults, BCL-namespace heuristics — that grows one carve-out per production startup
failure. Five commits (`fb61ac51`, `5d657867`, `73b02bb5`, plus the
required-with-initializer and collection-element clauses), one of which fixed a startup
`StackOverflowException` no catch can convert. Each new shape needs a new probe type
file, so the test surface grows with the language rather than the domain. `BindSettings`
also silently reorders the host's whole configuration (`9ad00609`) — a second
undeclared effect of the same too-thin interface.

**Solution.** Narrow the seam: a settings section declares itself one (marker interface
or attribute), so "is this a section" stops being namespace archaeology; a record
struct, a near-miss namespace and a cross-assembly record are all answered by one rule,
and a computed property cannot be a section by construction. The shipped-appsettings
conformance test over all thirteen roots becomes the leverage point; the per-shape
probes delete.

**Wins:** the fix stream stops · `IsFrameworkType` and both probe files delete · test
surface grows with the domain.

## 8. The conversation group gets one ending — **Worth exploring**

**Files:** `Domain/Monitor/ConversationGroup.cs:101-119,177-233`

**Problem.** "End this group because something went wrong" is spelled out at four sites
as an ad-hoc pair of statements, each under a 6-10-line comment re-deriving the same
invariant (an escaped exception is swallowed by the monitor's stream merge and the group
wedges until restart). The four endings differ — one skips cancel, one skips observe —
and a `_warmupSurfaced` flag exists only to keep two of them from double-logging. Five
commits (`32066581`, `b0bd5685`, `da448f2f`, `9cdd9a54`, `66588ba4`) each added or
repaired one site. Every test must drive a whole grouping plus fakes to reach one catch
block.

**Solution.** One private `EndGroup(GroupEnding reason, Exception? ex)` owns logging
with the reason's own message, context cancellation (or `onGroupComplete` for the
pre-context ending), and abandoned-warmup observation. The four sites collapse to
`EndGroup(reason, ex); break;`. Depth without a new type; compatible with ADR-0006.

**Wins:** four comments become one · `_warmupSurfaced` becomes internal to the ending ·
tests ask "how does this group end" at one seam · cheapest candidate on the list.

## Also assessed

- **Cross-mount transfer engine** (*Worth exploring*) — 260 lines of `internal static`
  inside `VfsCopyTool` parameterised by `deleteSource: bool`; move rules
  (`ab637436`'s "streamed nothing" rule, the duplicated source-delete-failed rule) live
  in the copy tool, and the directory path flattens the error codes the file path
  preserves. Falls out naturally if candidate 1 or 2 is taken up.
- **Glob-root arithmetic leak** (*Worth exploring*) — `MediaLibraryDiskFileSystem.GlobAsync`
  re-runs the disk backend's path arithmetic via `MatcherRoot`/`ToMatcherRelative`,
  made public for this one caller. A protected extra-candidates seam on the disk
  backend deletes ~40 lines and re-privatises three members; four fixes (`af62f154`,
  `04a70577`, `814c6658`, `7f14527f`) were coordinate/policy mismatches between the two.
- **Dashboard family stores** (*Worth exploring*) — seven stores are the same nine lines
  with types swapped (~250 deletable lines), and the metric family's date range is
  cached in three places (`972f1024` changed which families a page stamps). A generic
  family store is intra-project, so it passes the deletion test in a way the
  cross-client sharing ADR-0008 rejects does not; it extends ADR-0007's "named, not
  typed" one layer down.
- **WebChat first-connect suppression** (*Speculative*) — `BecameLiveAgain` is deep but
  correct only if a caller in another file performs an arm/disarm protocol around an
  unrelated await; letting the connection store learn "first connect" itself deletes
  two verbs and three suppression fields.
- **Voice turn timestamps** (*Speculative*) — `PlaybackQueue` carries turn anchors
  (`ITurnAnchor`) with a different lifetime than the turn they describe, forcing a
  dispatch-stamp gate in `ReplySpeaker`; moving them onto `VoiceTurn`'s epoch deletes
  the interface. Subsumed by candidate 4 if taken.
- **Left alone: the five one-line WebChat hub services** — the shallowest modules in
  either client (103 lines of code, 64 of interface, 448 of fakes), but ADR-0001 and
  ADR-0004 keep them deliberately: their fakes are the only injection point for
  scripted not-live answers, and the numbers still favour keeping the seam.

## Top recommendation

**Candidate 1 — one refusal rule for the downloads overlay.** Seven commits have each
patched one of seven refusal sites, and today the two shapes of the same operation
disagree on the same path — a live inconsistency, not a hypothetical. The deepening is
well-scoped (three files, one subsystem), the deletion test passes cleanly, and it sets
up candidate 2, which fixes a refusal that currently never fires in production.
