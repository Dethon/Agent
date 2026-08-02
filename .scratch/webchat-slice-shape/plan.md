# WebChat Slice Shape Implementation Plan

**Goal:** Collapse the 5-file slice to the 2-file shape the Dashboard already uses in this solution, remove the silently-ignored-action footgun, and make the reducer the single owner of message identity.

**Why now:** `WebChat.Client/State` is 65 files / 2,913 lines — a mean of 44 lines per file, nine of them under 12 lines. `Dashboard.Client/State/Voice/VoiceStore.cs` does the same job in 38 lines with actions and reducers inline. Adding one action to a WebChat slice means editing three files, and **forgetting the third produces a silently ignored action**: `Dispatcher.Dispatch` returns quietly on an unregistered type (`Dispatcher.cs:30-33`).

**Source:** architecture review 2026-08-02, candidate 6.

## Global Constraints

- TDD per task. `dotnet test Tests/Unit --nologo -v q`.
- `.cs` files have no trailing newline; pre-commit runs `dotnet format` and re-stages whole files.
- File-scoped namespaces, primary constructors, `record` actions, no XML doc comments.
- Commit after each task.
- `Dashboard.Client/State` is the reference shape. `Store.cs` and `IAction.cs` are currently duplicated verbatim across the two projects — do not deepen that divergence.

## Locked decisions

**Two files per slice**: `*State.cs` (the record) and `*Store.cs` (actions, reducers and the store together).

**`Dispatcher.RegisterCatchAll`** replaces the 10 per-type registration tables (~65 lines of pure pass-through). All eight reducers already have a `_ => state` default arm, so a catch-all is semantically safe.

**`Store.Dispatch` skips `OnNext` when the state is unchanged.** This is required by the catch-all, not optional: without it every action would notify all ten slices across 46 `StateObservable` subscribers.

```csharp
var newState = reducer(State, action);
if (ReferenceEquals(newState, State)) { return; }
_subject.OnNext(newState);
```

`with` expressions always allocate, so `ReferenceEquals` catches exactly the `_ => state` path and nothing else.

The catch-all also closes a second footgun: `Dispatch<TAction>` keys on the **static** type, so dispatching through a variable typed `IAction` matches nothing today.

**`MessagesState.FinalizedMessageIdsByTopic` becomes authoritative for message identity.** Three independent registries exist now: a local `HashSet` inside `StreamingService.ProcessStreamAsync`, `MessagesReducers.AddMessageWithDedup`, and `MessagePipeline._finalizedByTopic`. `StreamingService.cs:140-142` says outright that it routes late chunks through `UpdateMessage` because a fresh `AddMessage` "AddMessageWithDedup would drop". A caller of `Dispatch(new AddMessage(...))` cannot today predict whether the message appears.

## Deletions

| what | why |
|---|---|
| 10 `*Store.cs` registration tables | replaced by catch-all |
| `State/Selector.cs` (60 lines) | zero production call sites |
| `State/Streaming/StreamingSelectors.cs` | two lambdas, one consumer (`RenderCoordinator.cs:23,34`) |
| `ConnectionStatusChanged`, `ConnectionError`, `ApprovalResponding` | declared and reduced, never dispatched |

Kept: `AgentSettings/AgentSettingsSelectors.cs` (`GetConfigPatch` + `Sanitize` encode the whitelist and default-diffing rules) and `Topics/UnreadSelectors.cs`. Both are genuinely deep.

## Tasks

1. **`Store.Dispatch` skips unchanged state.** Failing test first: a reducer returning `state` emits nothing. Land this before the catch-all — the ordering matters, or task 2 floods 46 subscribers.
2. **`Dispatcher.RegisterCatchAll`.** Test that an action dispatched through an `IAction`-typed variable now reaches its reducer.
3. **Collapse the slices, one commit each.** Ten slices to two files apiece. Mechanical; the tests that exist should not need edits.
4. **Delete the dead actions and the two selector files.**
5. **Write tests for `State/Connection` and `State/Toast` before touching them.** Neither has any test today. `ConnectionReducers` is the transition table that `ReconnectionEffect` and `ChatConnectionService` both depend on; `ToastStore.cs:24-42` dedupes by message text, caps at 3, truncates at 150 chars and substitutes a fallback for whitespace.
6. **Unify message identity.** `MessagesState` authoritative; `MessagePipeline._finalizedByTopic` becomes a read of that state; `StreamingService`'s local set is deleted.

## Sequencing

Task 6 is behavioural and touches the foreground-resume path that `098a5038` and `9d1db7ab` recently fixed. Tasks 1–5 are mechanical. Land 1–5 first; treat 6 as a separate reviewable change.

## Risks

- **Task 6 changes when a message appears.** The three registries have different key spaces and the interleaving rule is currently distributed across `StreamingService.cs:137-320`, `MessagesReducers.cs:25` and `MessagePipeline.cs:65-108,230-239`. Reconstruct the rule explicitly before deleting any of the three.
- **Effects depend on store registration order.** `SpaceStore.cs:7-8` states that handlers registered there run synchronously before `SpaceEffect`'s async handler so effects can read up-to-date state, and `InitializationEffect.cs:87-88` relies on it. That holds only because `Dispatcher._handlers` is a `List` iterated in insertion order and `AddWebChatStores()` precedes `AddWebChatEffects()` in `Program.cs:35-36`. **The catch-all must preserve insertion ordering**, and swapping those two `Program.cs` lines must stay a behaviour change nobody makes accidentally — pin it with a test.
- Task 1 is an observable change for any component relying on an emit when nothing changed. Grep the 46 subscription sites before landing.
