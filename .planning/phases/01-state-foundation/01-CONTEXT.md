# Phase 1: State Foundation - Context

**Gathered:** 2026-01-19
**Status:** Ready for planning

<domain>
## Phase Boundary

Core state management infrastructure for unidirectional data flow. Includes stores, actions, reducers, selectors, and a base component class. Feature-specific state slices (Topics, Messages, etc.) are Phase 2. This phase establishes the patterns and infrastructure that all subsequent phases build upon.

</domain>

<decisions>
## Implementation Decisions

### Store API design
- State accessed via property getter: `store.State` returns current state directly
- Generic store class: Single `Store<TState>` used everywhere, no typed subclasses per store
- Dispatch through separate service: `IDispatcher.Dispatch(action)` decouples components from specific stores
- No middleware in this phase — keep stores simple, add middleware when needed in later phases

### Action patterns
- Actions defined as record types: `public record TopicsLoaded(List<Topic> Topics) : IAction`
- Past tense event naming: `TopicsLoaded`, `MessageReceived`, `MessagesCleared` (describes what occurred)
- Marker interface required: All actions implement `IAction`, dispatch is typed `Dispatch<T> where T : IAction`
- Actions grouped by feature: `TopicsActions.cs` contains `TopicsLoaded`, `TopicSelected`, etc.

### Subscription model
- IObservable-based subscriptions: `store.State.Subscribe()` using Reactive Extensions pattern
- StoreSubscriberComponent handles disposal: Base class implements IDisposable, cleans up subscriptions automatically

### Claude's Discretion
- Auto-subscribe vs explicit setup in StoreSubscriberComponent — Claude picks based on reducing boilerplate vs clarity
- StateHasChanged trigger strategy — auto-call vs selective via selector, based on performance vs simplicity
- Project location for state infrastructure — WebChat.Client/State or shared, based on where stores will be used

### File organization
- By feature: `State/Topics/`, `State/Messages/`, etc. Each feature folder has its store, actions, reducers
- Flat root structure: Infrastructure files (Store.cs, IAction.cs, StoreSubscriberComponent) at same level as feature folders
- Suffix naming: `TopicsState.cs`, `TopicsActions.cs`, `TopicsReducers.cs`, `TopicsSelectors.cs`

</decisions>

<specifics>
## Specific Ideas

- IDispatcher pattern enables centralized action logging/debugging when needed later
- IObservable allows composition with other reactive streams (useful for throttling in Phase 3)
- Past tense action naming reads naturally in reducers: "when TopicsLoaded, update topics list"

</specifics>

<deferred>
## Deferred Ideas

None — discussion stayed within phase scope

</deferred>

---

*Phase: 01-state-foundation*
*Context gathered: 2026-01-19*
