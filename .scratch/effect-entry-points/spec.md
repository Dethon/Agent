# Spec — Effect Entry Points

Status: done

## Problem Statement

Seven of the ten WebChat effects have no test at all: `InitializationEffect`, `TopicSelectionEffect`, `AgentSelectionEffect`, `SpaceEffect`, `AgentActivityEffect`, `TopicDeleteEffect` and `UserIdentityEffect`. The three that are tested — `SendMessageEffect`, `AgentSettingsEffect`, `ReconnectionEffect` — are tested because their work happens to complete synchronously under fakes, not because anything in their shape supports it.

An effect's whole contract is a constructor side effect. It registers a handler and returns. The handler assigns the real work to a discard and returns void, so there is no value a caller can hold and nothing to await. A test can dispatch the action, but it cannot know when the effect is finished, so it either asserts too early or relies on a fake resolving inline. That reliance is the reason the three existing effect tests pass and is not a property any of them states.

`InitializationEffect` is where this costs the most. It is 214 lines and owns the entire first-load sequence: connect, subscribe to hub events, register the user, validate the space, join it, load the agent list, load agent settings, select an agent, load that agent's topics, then load history and resume streams for each topic. Nothing pins any of that order. It takes 14 constructor dependencies, two of which — `ConfigService` and `PushNotificationService` — are concrete classes, and `PushNotificationService` reaches the browser through `IJSRuntime`.

The same effect drops errors on the floor. `_ = HandleInitializeAsync()` starts the whole first-load sequence and discards the task. If the space lookup throws, or the agent list request fails, the task faults and nobody observes it. There is no log line, no toast, and no retry. The app sits half-initialized with a connected hub and an empty agent list, and the only evidence is in the browser's network tab. Four other sites in the same file discard tasks the same way, and eight more across the other effects do too.

`AgentSelectionEffect` has no addressable input whatsoever. It subscribes to `TopicsStore.StateObservable` and compares each emission's `SelectedAgentId` against a private `_previousAgentId` field. Reaching its behaviour from a test means building a `TopicsStore`, dispatching enough actions to move that one field twice, and then guessing when the resulting work finished.

## Solution

Give each effect a public awaitable method that does the work, and leave the constructor registration as a thin wrapper around it.

```csharp
public Task HandleInitializeAsync(CancellationToken ct = default);

// ctor
dispatcher.RegisterHandler<Initialize>(_ => HandleInitializeAsync().LogFaults(logger));
```

`Dispatch` stays `void` at all of its call sites. Flux dispatch is fire-and-forget by design and a button dispatching `SelectTopic` has no business blocking on history loading. The two things missing are an addressable input and a completion signal, and a public awaitable member supplies both without changing anything outside the effect.

Registration stays in the constructor next to the handler, where it cannot drift. A composition-root wiring table was rejected for the same reason `webchat-slice-shape` deletes ten of them: a forgotten entry fails silently.

`LogFaults` is a `Task` extension that attaches a faulted continuation logging the exception. It replaces the bare discard at every site, so a first-load failure produces a log line instead of nothing. This closes a live bug as a side effect of the refactor.

`ConfigService` and `PushNotificationService` gain interfaces. They are the only concrete dependencies among `InitializationEffect`'s 14, and they are also dependencies of `SpaceEffect` and `UserIdentityEffect`, so extracting them unblocks all three. The browser-side one is named `IPushSubscriptionService` because `Domain.Contracts.IPushNotificationService` already exists and does something different — it sends a notification to a space from the server, where this one manages the browser's own push subscription.

`AgentSelectionEffect` stops subscribing to the store observable and handles actions instead. It registers for `SelectAgent` and for `SetAgents`, both routing to one public `HandleAgentChangedAsync`, and keeps its `_previousAgentId` field as the guard that skips the first selection.

The deliverable is a test that drives `InitializationEffect`'s first-load sequence to completion and asserts its order, with no browser involved.

## User Stories

1. As a developer, I want each effect to expose a method I can call, so that reaching its behaviour does not mean reconstructing the state that triggers it.
2. As a developer, I want that method to return a `Task` I can await, so that my assertion runs after the effect finished rather than after a fake happened to resolve inline.
3. As a developer, I want the constructor registration to stay next to the handler, so that adding an effect cannot mean forgetting a line in a wiring table somewhere else.
4. As a developer, I want `Dispatch` to stay `void`, so that no UI component is given the option to await a state change.
5. As a developer, I want a test for the first-load sequence, so that reordering its ten steps fails a test instead of failing in a browser.
6. As a developer, I want that test to assert the order of the steps, not just that each one happened, so that a subtle reorder is caught.
7. As a developer, I want `InitializationEffect` constructible without a JS runtime, so that a unit test does not need a Blazor host.
8. As a developer, I want `ConfigService` reached through an interface, so that a test can supply a space config without an `HttpClient`.
9. As a developer, I want the browser push service reached through an interface, so that its name says it manages a subscription and does not collide with the server-side sender.
10. As a developer, I want `SpaceEffect` and `UserIdentityEffect` to benefit from the same two interfaces, so that the extraction is not made for one call site.
11. As a user, I want a failed first load to leave evidence, so that a support conversation can start from something other than "it was blank".
12. As a developer, I want a faulted effect task to be logged rather than discarded, so that a bug in an effect is not invisible by construction.
13. As a developer, I want one helper doing that logging, so that thirteen discard sites do not become thirteen slightly different try/catch blocks.
14. As a developer, I want the helper itself under test, so that the mechanism the other twelve sites rely on is pinned.
15. As a developer, I want `AgentSelectionEffect` triggered by an action, so that its input is something I can dispatch rather than a field I have to move indirectly.
16. As a developer, I want the first agent selection during startup to keep being skipped, so that first load does not fetch every topic twice.
17. As a user, I want my agent choice to survive a reload, so that switching agents is a decision I make once.
18. As a user, I want the topic list to change when I switch agents, so that I am not looking at another agent's conversations.
19. As a user, I want the app to react when the selected agent disappears from the catalog, so that a removed agent does not leave a dead topic list on screen.
20. As a developer, I want `AgentActivityEffect`'s two action handlers to get entry points even though its streaming subscription cannot, so that partial testability is not blocked on total testability.
21. As a developer, I want each of the seven conversions to be a no-op apart from the signature and the fault log, so that a review can check the diff rather than the behaviour.
22. As a developer, I want the conversions to land one at a time, so that a bisect points at one effect.
23. As a developer, I want the effects to keep working through `RegisterHandler<TAction>` after `webchat-slice-shape` lands, so that the two changes do not have to be merged into one.
24. As a developer, I want awaiting first-load init to mean topic history has loaded, so that a test asserting on loaded messages does not race.
25. As a developer, I want push subscription to stay off the awaited path, so that a slow `pushManager.subscribe()` cannot stall init again.

## Implementation Decisions

**Public `HandleAsync`, constructor wraps it.** Every effect gains a public method carrying the work and keeps its `RegisterHandler<TAction>` registration in the constructor, now delegating to that method and attaching `LogFaults`. The method takes the action's payload as parameters rather than the action record, so a test does not have to construct an action to call it. `Dispatch` is unchanged at every call site.

**Effects get no interfaces of their own.** They are resolved eagerly in `Program.cs` and injected into nothing, so there is no consumer for a seam to sit between. ADR-0001's uniformity rule is about injected dependencies; an effect is not one. This is consistent with the ADR, not an exception to it.

**`IConfigService` and `IPushSubscriptionService` are added.** `ConfigService` keeps `GetConfigAsync` and `GetSpaceAsync`. The browser push service keeps `RequestAndSubscribeAsync`, `ResubscribeAsync`, `UnsubscribeAsync` and `IsSubscribedAsync`. Both interfaces live in `WebChat.Client.Contracts` alongside the project's other client-side contracts, and both concrete classes are registered against them in the WebChat service-collection extension. Every consumer switches to the interface: `InitializationEffect`, `SpaceEffect`, `UserIdentityEffect` and `SignalRHubConnectionFactory` for config, `InitializationEffect` and `SpaceEffect` for push.

**The name `IPushSubscriptionService` is deliberate.** `Domain.Contracts.IPushNotificationService` exists and has one method, `SendToSpaceAsync`. The two are unrelated and belong to different sides of the system. Naming the browser-side one the same thing would make every mention ambiguous to a reader and to a grep.

**`LogFaults` is a `Task` extension in `WebChat.Client/Extensions`.** It takes an `ILogger` and an optional context string, attaches a continuation that runs only on faulted, and logs the exception. It returns nothing, because its callers are discarding the task by construction. It replaces the bare `_ =` at every effect site.

**Awaiting `HandleInitializeAsync` means first load finished, except for push.** The per-topic history loads are gathered with `Task.WhenAll` before the method returns, so a caller that awaits it knows history is in the store. `SubscribePushAsync` stays detached — it is network- and browser-dependent, and awaiting it once stalled the agent list by roughly 30 seconds. `TryResumeStreamAsync` inside `LoadTopicHistoryAsync` also stays detached, because a resumed stream is long-lived and awaiting it would mean awaiting the conversation.

**`AgentSelectionEffect` is driven by two actions, not by the store observable.** `HandleAgentChangedAsync(string? agentId)` is public and registered against both `SelectAgent` and `SetAgents`, each registration passing `TopicsStore.State.SelectedAgentId` read after the store has reduced. The `_previousAgentId` field stays as the guard, so the first selection during startup is still skipped and `InitializationEffect` remains the one that loads topics on first load. The `StateObservable` subscription and the `IDisposable` it produced are deleted.

**Registering for `SetAgents` preserves a path the plan does not mention.** `TopicsReducers` clears `SelectedAgentId` when a `SetAgents` payload no longer contains the selected agent. `HubEventDispatcher` dispatches `SetAgents` from the hub's `OnAgentsUpdated` broadcast, which fires whenever the agent re-registers its catalog. Today the observable subscription sees that clearing and reacts to it; an effect registered only for `SelectAgent` would not. Handling both actions keeps the behaviour identical. There is no follow-up ticket for this — it is part of the conversion.

**Reading store state inside the registration is safe.** Stores register their handlers before effects do, because the eager `GetRequiredService` block in `Program.cs` resolves effects that take stores as constructor parameters, so every store is constructed first. `InitializationEffect` already relies on this when it reads `_spaceStore.State.CurrentSlug` immediately after dispatching `InvalidSpace`.

**`AgentActivityEffect` is converted partially.** Its `SetAgents` and `SelectAgent` handlers get public awaitable entry points. Its `StreamingStore.StateObservable` subscription stays as it is — the streaming activity mapping is genuinely state-derived and has no action that means it. The effect is testable through its two action entry points after the change and was testable through neither before.

**No change to DI registration shape.** `AddWebChatStores`, `AddWebChatEffects` and the eager resolution block keep their current contents and order. The only additions are the two interface registrations.

**Expand–contract across the conversions.** `RegisterHandler<TAction>` keeps working throughout, each effect converts independently, and no ticket depends on another effect having converted first.

## Testing Decisions

A good test here dispatches an action or calls the public entry point, awaits it, and asserts on store state or on calls recorded by a fake service. It does not assert that a handler was registered, does not reach into `_previousAgentId`, and does not assert on the `LogFaults` continuation from inside an effect test. The constructor registration is the implementation detail being wrapped; a test naming it would have to change every time an effect converts.

Existing effect tests build the real `Dispatcher`, the real stores, and fakes for the services — `SendMessageEffectTests` is the template and it constructs eleven collaborators that way. New tests follow it. Where a fake already exists in `Tests/Unit/WebChat.Client/Fixtures` (`FakeTopicService`, `FakeChatMessagingService`, `FakeApprovalService`) it is used rather than mocked.

**`Tests/Unit/WebChat.Client/State/InitializationEffectTests.cs`** (new) is the deliverable. It awaits `HandleInitializeAsync` and asserts the sequence: connect, subscribe, register user, resolve and join the space, load agents, load agent settings, select an agent, load topics, load each topic's history. Order is asserted by recording call order in the fakes, not by asserting each step in isolation. It also covers the branch where the space lookup returns null and `InvalidSpace` is dispatched, the branch where the agent list is empty and the method returns early, and the saved-agent-id path where local storage holds an agent that is no longer in the catalog.

**`Tests/Unit/WebChat.Client/Extensions/TaskExtensionsTests.cs`** (new) covers `LogFaults`: a faulting task produces a log entry, a completing task produces none. This is the one test that pins the mechanism the other twelve sites depend on.

**`Tests/Unit/WebChat.Client/State/AgentSelectionEffectTests.cs`** (new) covers the trigger change: the first `SelectAgent` after construction loads nothing, a second one for a different agent clears the session, writes local storage and reloads topics, a repeat of the same agent id does nothing, and a `SetAgents` payload that drops the selected agent reaches the same path.

**Existing files absorb the smaller conversions where a fixture already exists.** The four remaining mechanical conversions — `TopicSelectionEffect`, `SpaceEffect`, `TopicDeleteEffect`, `UserIdentityEffect` — plus `AgentActivityEffect` get tests in new files following the same template, one per effect, added with the conversion that makes them reachable. `ReconnectionEffectTests`, `SendMessageEffectTests` and `AgentSettingsEffectTests` should not need edits; if converting an effect forces one, that effect's behaviour moved and the change needs explaining.

**No test asserts that an effect's constructor registered a handler.** The behaviour that matters is that dispatching the action still runs the work, which every effect test covers by dispatching rather than by calling the method directly in at least one case per effect.

**No test covers `PushNotificationService`'s JS interop beyond what exists.** `PushNotificationServiceTests` already covers it with a mocked `IJSRuntime` and is unaffected by the interface extraction.

## Out of Scope

`Dispatch` does not become async and no call site changes. The 146 `Dispatch` calls across the client are untouched.

The effects are not merged, split, or moved between files. `InitializationEffect` stays 214 lines and stays responsible for the whole first-load sequence; making that sequence smaller is a different change and this spec is what would make it safe.

`AgentActivityEffect`'s streaming subscription and `ReconnectionEffect`'s connection-state subscription stay observable-driven. `AgentSettingsEffect`'s persistence subscription does too. Only `AgentSelectionEffect` moves off the observable, because it is the one whose observable is a proxy for an action that already exists.

`Dashboard.Client/Effects` is not touched.

The retry and toast behaviour for a failed first load is not built. `LogFaults` logs; it does not surface anything to the user. Turning that log into a toast is a product decision and a separate change.

No E2E test is added. The whole point is that first-load ordering becomes assertable without a browser.

## Further Notes

The plan places the swallowed first-load throw at `InitializationEffect.cs:67`. It is at `:72`; `:67` is the `SelectUser` handler, which discards a task the same way. There are five discard sites in that file. `:102` is deliberate and already wrapped in try/catch, and the reason is written in a comment above it.

The plan says `InitializationEffect` "cannot be constructed in a unit test at all". That is too strong — `PushNotificationServiceTests` constructs `PushNotificationService` with a mocked `IJSRuntime` today, so the same could be done here. The barrier is inconvenience, and the extraction stands on ADR-0001's uniformity rule rather than on impossibility.

The plan describes task 3 as seven uniform conversions. Five are uniform: `InitializationEffect`, `TopicSelectionEffect`, `SpaceEffect`, `TopicDeleteEffect` and `UserIdentityEffect` each already have a private `Handle*Async` sitting behind a `RegisterHandler` registration, so the conversion is a visibility change plus `LogFaults`. `AgentActivityEffect` is a hybrid, with two action handlers that convert and one subscription that does not. `AgentSelectionEffect` has no action registration at all and is entirely the subject of task 5.

The plan's risk about `webchat-slice-shape`'s unchanged-state skip does not affect `AgentSelectionEffect`'s priming. `Store.StateObservable` is a `BehaviorSubject`, so the initial emission that leaves `_previousAgentId` null comes from subscribing, not from a dispatch, and the skip cannot suppress it. After this spec lands the question is moot, because the subscription is gone.

That also updates a note in the `webchat-slice-shape` spec. It lists `AgentSelectionEffect`, `AgentActivityEffect` and `ReconnectionEffect` as the raw `StateObservable` subscribers affected by the skip. Once this lands, only the latter two are.

`TopicList.razor:215` already guards its dispatch with `if (agentId != _selectedAgentId)`, so the UI never dispatches `SelectAgent` for the currently selected agent. The equality guard inside `HandleAgentChangedAsync` is therefore redundant against today's only UI caller and is kept because the method is public and a test will call it directly.

The plan counts 131 `Dispatch` call sites. A grep across `.cs` and `.razor` returns 146, including `Dashboard.Client` and tests. The number does not change the decision that `Dispatch` stays `void`.

`webchat-slice-shape` ticket 05 and ticket 06 both touch `TopicDeleteEffect` — 05 by collapsing the Messages slice it dispatches into, 06 by removing its `MessagePipeline.ClearTopic` call. This spec's `TopicDeleteEffect` conversion rewrites the same file. Whichever lands second rebases onto the first; the changes do not overlap in content, since one is the handler signature and the other is the body of the handler.
