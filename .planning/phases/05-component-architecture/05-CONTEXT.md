# Phase 5: Component Architecture - Context

**Gathered:** 2026-01-20
**Status:** Ready for planning

<domain>
## Phase Boundary

Break ChatContainer.razor (~305 lines) into thin render layers that dispatch actions and consume store state. Components dispatch actions only, render from stores, and stay under 100 lines each. This phase removes ChatStateManager usage from components.

</domain>

<decisions>
## Implementation Decisions

### Component boundaries
- Split ChatContainer by UI region: Sidebar, MessageArea, InputArea, ConnectionStatus
- Extract TopicItem component within TopicList for individual topic rendering
- Create StreamingMessageDisplay as separate component (isolates high-frequency updates)
- Move ApprovalModal inside message area (relates to current conversation)

### Store subscriptions
- Components subscribe directly to store slices they need (truly isolated, unidirectional)
- Use StoreSubscriberComponent base class for automatic subscribe/unsubscribe lifecycle
- When component needs multiple stores, create combined selector (view model pattern)
- Remove ChatStateManager usage entirely — components subscribe to stores only
- Store reads localStorage during initialization (selectedAgentId)

### Action dispatch location
- Complex operations (send message) handled by Effect classes triggered by actions
- Component dispatches SendMessageAction — effect coordinates: topic creation, session start, streaming
- Components inject IDispatcher only, never access stores directly for dispatch
- Effects subscribe to action types and execute side effects

### Claude's Discretion
- Topic selection: whether to use store actions or service calls (based on side effects needed)
- Simple vs complex actions: when to dispatch directly vs go through effects
- LocalStorage persistence: effect-based or component-based saving on agent change

</decisions>

<specifics>
## Specific Ideas

- ChatContainer remains composition root — coordinates initial data load (agents, topics), then delegates to child components
- Extraction order: leaf components first (ConnectionStatus → ChatInput → ApprovalModal → MessageArea → TopicList)
- Each extraction creates new .razor file immediately — clear boundaries from the start
- Tests updated with each component extraction — always green, never accumulate debt

</specifics>

<deferred>
## Deferred Ideas

None — discussion stayed within phase scope

</deferred>

---

*Phase: 05-component-architecture*
*Context gathered: 2026-01-20*
