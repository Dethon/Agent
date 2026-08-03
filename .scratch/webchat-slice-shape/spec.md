# Spec — WebChat Slice Shape

Status: done

## Problem Statement

Adding one action to a WebChat state slice means editing three files: the actions file, the reducers file, and the store's registration table. Forget the third and nothing happens. `Dispatcher.Dispatch` looks the action type up in a dictionary and returns quietly when it finds nothing. There is no error, no log line, and no failing test. The action is dispatched, the reducer that would have handled it exists, and the state does not change.

The same lookup has a second hole. `Dispatch<TAction>` keys on the static type of its argument. Dispatch through a variable declared as `IAction` and the key is `IAction`, which nothing is registered under, so nothing runs. The code compiles and does nothing.

The registration tables that create this exposure carry no logic. Ten stores hold 53 registrations between them, each one two lines that hand the action straight to the slice's reducer. Every reducer already ends in a `_ => state` arm, so each store is willing to be shown any action at all. The tables exist only to name, one type at a time, the actions the reducer would have ignored anyway.

The cost of the shape shows up in the file count. `WebChat.Client/State` is 65 files and 2,913 lines, a mean of 44 lines per file, with 16 files under 12 lines. `Toast` in the same project does the same job in three files with its reducers inline in the store, and it is easier to read than any of the other nine.

Some of those files are dead. `Selector.cs` is 60 lines with no call site anywhere in the solution. `StreamingSelectors.cs` is two lambdas with one consumer. Three actions — `ConnectionStatusChanged`, `ConnectionError`, `ApprovalResponding` — are declared, reduced and registered, and never dispatched by anything.

Two slices have no tests at all. `ConnectionReducers` is the transition table that `ReconnectionEffect` and `ChatConnectionService` both depend on. `ToastStore` dedupes by message text, caps the list at three, truncates at 150 characters and substitutes a fallback for whitespace. Nothing pins any of it.

Underneath all of that sits a worse version of the same problem. A caller who dispatches `AddMessage` cannot tell whether the message will appear. Three separate registries decide, and they disagree. `MessagesState.FinalizedMessageIdsByTopic` lives in the reducer. `MessagePipeline` keeps a private `_finalizedByTopic` dictionary under a lock. `StreamingService.ProcessStreamAsync` keeps a local `HashSet` that lives for one stream. A comment in `StreamingService` says outright that it routes late chunks through `UpdateMessage` because a fresh `AddMessage` is one that "`AddMessageWithDedup` would drop". The rule for which of the three wins is spread across three files and is not written down anywhere.

The local set is the one that misleads. It is scoped to a single `ProcessStreamAsync` call, so a message id that was committed by an earlier stream is unknown to the next one. The next stream dispatches `AddMessage` for it, the reducer's dedup recognises the id and drops it, and the message silently does not appear.

## Solution

Collapse each slice to two files and let the reducer see everything.

`*State.cs` keeps the state record. `*Store.cs` holds the actions, the reducers and the store together, the way `Toast` and the Dashboard slices already do. The actions and reducers files go away.

`Dispatcher` gains `RegisterCatchAll`. A store registers once and is shown every action. The reducer's existing `_ => state` arm does the filtering that the registration table used to do, so a reducer arm added without a matching registration now works, and there is no third file to forget. The catch-all also closes the static-type hole, because a catch-all handler does not key on the action's type at all.

`Store.Dispatch` stops emitting when the state did not change. This is what makes the catch-all affordable: without it, one action would notify every subscriber of all ten slices. A reducer that falls through to `_ => state` returns the same instance it was given, and `with` expressions always allocate, so a reference comparison separates the two cases exactly.

```csharp
var newState = reducer(State, action);
if (ReferenceEquals(newState, State)) { return; }
_subject.OnNext(newState);
```

Then delete what is not used: `Selector.cs`, `StreamingSelectors.cs`, and the three actions nobody dispatches.

For message identity, `MessagesState.FinalizedMessageIdsByTopic` becomes the only registry. `MessagePipeline` reads it instead of keeping its own dictionary. `StreamingService` drops its local set and reads the same state. A message id that was committed by any earlier path is then known to every later one, so the stream that would have silently dropped a repeat now updates the existing bubble instead.

Deleting the pipeline's dictionary also empties `MessagePipeline.ClearTopic`. Its only caller dispatches `ClearMessages` on the line above, and the messages reducer already drops that topic's finalized ids when it handles `ClearMessages`. Once the pipeline reads from state, `ClearTopic` has nothing left to clear, so it leaves `IMessagePipeline`.

## User Stories

1. As a developer, I want a reducer arm I add to take effect without editing a second file, so that I cannot half-add an action.
2. As a developer, I want a dispatched action that no reducer handles to be harmless rather than silently meaningful, so that forgetting a registration is not a class of bug.
3. As a developer, I want an action dispatched through an `IAction`-typed variable to reach its reducer, so that the declared type of a local does not decide whether my code runs.
4. As a developer, I want a slice to be two files, so that reading one slice does not mean opening five tabs.
5. As a developer, I want a slice's actions to sit next to the reducer that handles them, so that I can see the whole vocabulary of a slice at once.
6. As a developer, I want WebChat slices to look like the Dashboard slices in the same solution, so that moving between the two projects does not require holding two shapes in my head.
7. As a developer, I want `Toast` to stay recognisable after the change, so that the slice that already had the target shape is evidence the shape works.
8. As a developer, I want the store to skip notifying subscribers when the state is unchanged, so that a catch-all registration does not cost ten times the notifications.
9. As a developer, I want that skip to be exact rather than a value comparison, so that a genuine change to a large state record is never mistaken for a no-op.
10. As a developer, I want handlers to run in the order they were registered after the change, so that a store that updates state before an effect reads it keeps doing so.
11. As a developer, I want `Selector.cs` gone, so that a search for how selectors work does not turn up 60 lines nobody calls.
12. As a developer, I want `StreamingSelectors` inlined at its only consumer, so that two lambdas do not need a file and a namespace.
13. As a developer, I want `ConnectionStatusChanged`, `ConnectionError` and `ApprovalResponding` removed, so that the action list describes what the app actually dispatches.
14. As a developer, I want the connection transition table under test before it is moved, so that a refactor cannot quietly change what happens when the hub drops.
15. As a developer, I want the toast rules under test before they are moved, so that the dedupe, the cap, the truncation and the whitespace fallback survive the collapse.
16. As a developer, I want the catch-all itself under test, so that a later change to the dispatcher cannot reintroduce silent no-op dispatch.
17. As a user, I want an assistant message that arrives on a second stream with an id seen before to update the bubble I am looking at, so that the reply does not vanish.
18. As a user, I want a message that the client already knows about not to appear twice, so that a reconnect does not duplicate the conversation.
19. As a user, I want a topic I reopen after a resume to show the same messages the server has, so that the foreground-resume path stays correct.
20. As a developer, I want one place that answers whether a message id has been committed, so that I can predict what `AddMessage` will do without reading three files.
21. As a developer, I want `StreamingService` to stop keeping its own per-stream set, so that the rule stops depending on how long a single stream lived.
22. As a developer, I want `MessagePipeline` to stop holding message identity behind a lock, so that identity has one owner and one lifetime.
23. As a developer, I want `ClearTopic` off `IMessagePipeline` once it does nothing, so that the interface does not advertise a step callers still think they need.
24. As a developer, I want deleting a topic to clear its finalized ids through the same action that clears its messages, so that the two cannot drift apart.

## Implementation Decisions

**Two files per slice.** `*State.cs` holds the state record. `*Store.cs` holds the action records, the reducers and the store class. `Toast` is the in-project template and needs the least change. This applies to all ten slices: AgentActivity, AgentSettings, Approval, Connection, Messages, Space, Streaming, Toast, Topics, UserIdentity.

**The Dashboard shape is the goal, not the literal target.** Dashboard stores inherit `Store<TState>` and expose typed methods, and Dashboard has no dispatcher at all. WebChat stores must keep composing `Store<TState>` and must keep taking a `Dispatcher`, because WebChat effects subscribe to actions through it. What transfers is the file count and the inline reducers, not the inheritance.

**`Dispatcher.RegisterCatchAll(Action<IAction>)`** replaces the ten registration tables. It returns an `IDisposable` registration like `RegisterHandler<TAction>` does. `RegisterHandler<TAction>` stays, because effects use it to subscribe to specific actions and gain nothing from seeing all of them.

**Catch-all handlers and typed handlers run in one registration order.** A catch-all registered before a typed handler runs before it, for every action. This is what preserves the current behaviour where a store updates state before an effect reads it.

**`Store.Dispatch` returns without calling `OnNext` when `ReferenceEquals(newState, State)`.** This lands before the catch-all. In the other order, every action briefly notifies every subscriber of every slice.

**Two selector files are deleted, two are kept.** `Selector.cs` and `Streaming/StreamingSelectors.cs` go; the two streaming lambdas move into their only consumer. `AgentSettings/AgentSettingsSelectors.cs` stays because it encodes the patchable-model whitelist and the default-diffing rules. `Topics/UnreadSelectors.cs` stays. Both have real logic and both have tests.

**`MessagesState.FinalizedMessageIdsByTopic` becomes the only message-identity registry.** `MessagePipeline._finalizedByTopic` is deleted and `ShouldProcess` and `GetSnapshot` read the messages state instead. `StreamingService.ProcessStreamAsync`'s local `committed` set is deleted and the same state is read in its place.

**`MessagePipeline.ClearTopic` is removed from `IMessagePipeline` and from the class.** `TopicDeleteEffect` already dispatches `ClearMessages(topicId)` immediately before calling it, and `MessagesReducers` drops the topic's finalized ids while handling that action. The call in `TopicDeleteEffect` goes with it.

**`PipelineSnapshot.FinalizedCount` becomes a projection of `MessagesState`.** The record shape does not change. What changes is where the number is read from.

**Widening the committed set is accepted and intended.** Reading from state means ids finalized by history load or by `SendMessageEffect` are now visible to a stream that did not commit them itself. Such a stream routes through `UpdateMessage` rather than dispatching an `AddMessage` the reducer would drop. This is the behaviour the `StreamingService` comment describes wanting.

**No change to the Blazor DI registrations.** `AddWebChatStores` and `AddWebChatEffects` keep their current contents and their current call order in `Program.cs`.

## Testing Decisions

A good test here dispatches an action and asserts what a subscriber or a state read observes. It does not assert that a particular handler was registered, or count registrations, or reach into a store's private fields. The registration tables are the implementation detail being removed; tests that name them would have to be rewritten by the change that is supposed to prove they were unnecessary.

Three new files, everything else absorbed by files that already exist and already have the fixture.

**`Tests/Unit/WebChat.Client/State/DispatcherTests.cs`** (new) covers the two core mechanics together. A reducer that returns its input emits nothing to a `StateObservable` subscriber. An action dispatched through a variable typed `IAction` reaches a catch-all handler. A catch-all registered before a typed handler for the same action runs first. That last one is the guard for the ordering the stores depend on; it is a dispatcher-level test with two recording handlers and needs nothing widened in production code to reach.

**`Tests/Unit/WebChat.Client/State/ConnectionStoreTests.cs`** (new) pins the connection transition table before the slice is collapsed. `ConnectionStore` is already constructed directly in `ReconnectionEffectTests:28`, so the construction pattern is settled.

**`Tests/Unit/WebChat.Client/State/ToastStoreTests.cs`** (new) pins dedupe by message text, the cap of three, truncation at 150 characters, and the whitespace fallback. `ToastStore` is already constructed directly in `StreamingServiceTests:37` and `StreamResumeServiceTests:38`.

Both characterization files are written and passing before the slices they cover are touched, not after.

**Existing files absorb the rest.** `MessagePipelineTests.cs` already has the `_dispatcher` and `_pipeline` fixture and already asserts `FinalizedCount`; its two `ClearTopic` tests are rewritten against `ClearMessages`. `StreamingServiceTests.cs` covers the stream paths that lose the local set. `MessagesStoreTests.cs` covers the dedup semantics. `TopicsStoreTests.cs`, `StreamingStoreTests.cs` and `AgentSettingsStoreTests.cs` are the regression surface for the slice collapse and should not need edits — if collapsing a slice forces a test change, that slice's behaviour moved and the change needs explaining.

**No test pins the DI wiring.** The plan asked for a test making a swap of the `AddWebChatStores()` and `AddWebChatEffects()` lines a visible behaviour change. Such a test cannot fail. Both are `AddScoped`, so those calls only register descriptors; construction order comes from the DI graph and the eager resolution block in `Program.cs`. Reaching that order from a test means exposing construction sequencing that production code has no reason to expose. The dispatcher-level ordering test covers the invariant that the catch-all could actually break.

**Prior art.** `StreamingStoreTests.cs` and `TopicsStoreTests.cs` are the pattern for a store test: build a `Dispatcher`, build the store on it, dispatch, assert on `State`. `RenderCoordinatorTests.cs` is the pattern for asserting on emissions from an observable.

## Out of Scope

`Dashboard.Client/State` is not touched. `Store.cs` and `IAction.cs` are duplicated verbatim across the two projects today; this spec does not deepen that duplication and does not resolve it either. Sharing them is a separate decision with a project-reference question attached.

`AgentSettingsSelectors` and `UnreadSelectors` stay as they are.

The effects are not restructured. Nine effect files and the two hub files keep their current shape and their `RegisterHandler<TAction>` registrations. `effect-entry-points` covers that and depends on this landing first.

The `_pendingUserMessages` dictionary in `MessagePipeline` is a different concern from message identity and stays where it is.

No behaviour change to what the user sees on the ordinary path: send a message, watch it stream, read the reply. Story 17 is the one visible change, and it only fires on a repeat id across streams.

## Further Notes

The plan's risk section attributes handler ordering to `AddWebChatStores()` preceding `AddWebChatEffects()` at `Program.cs:35-36`. That is wrong and anyone reading the plan will re-derive it. Both calls only register `AddScoped` descriptors. The order that actually holds comes from the eager `GetRequiredService` block: `ReconnectionEffect` is resolved first and takes `ConnectionStore`, `TopicsStore` and `SpaceStore` as constructor parameters, so those stores are built — and register their handlers — before any effect constructor body runs. `SendMessageEffect`, resolved second, pulls in `MessagesStore`, `StreamingStore` and `UserIdentityStore` the same way. Swapping lines 35 and 36 changes nothing.

The plan also describes `Dispatcher._handlers` as a `List` iterated in insertion order. It is a `Dictionary<Type, List<Action<IAction>>>`. Ordering today is per action type, within that type's list.

`InitializationEffect.cs:87-88` reads `_spaceStore.State` on the line after dispatching `InvalidSpace`. That works because `Dispatch` is synchronous, not because of registration order — `InvalidSpace` has exactly one handler. It is not evidence about ordering either way, and it keeps working unchanged.

`StoreSubscriberComponent.Subscribe` already applies `DistinctUntilChanged()` to the selected projection, so components are already immune to redundant emissions. The subscribers that see a real difference from the unchanged-state skip are the effects that subscribe to `StateObservable` directly: `AgentActivityEffect` and `ReconnectionEffect` (`AgentSelectionEffect` was on this list until `effect-entry-points` ticket 04 moved it onto actions). Each receives strictly fewer emissions than before and each reads current state in its handler, so fewer is safe. Confirm that when landing the skip rather than assuming it.

The plan counts nine files under 12 lines; there are 16. It counts 46 `StateObservable` subscribers; there are 67 references across 28 files, some of which are the property declarations on the ten stores. Neither number changes any decision.

Task 6's ordering behind the rest is not negotiable. It touches the foreground-resume path that commits `098a5038` and `9d1db7ab` recently fixed, and it is the only part of this spec that changes what a user sees.
