# Phase 2: State Slices - Context

**Gathered:** 2026-01-20
**Status:** Ready for planning

<domain>
## Phase Boundary

Create feature-specific state slices with clear ownership boundaries: TopicsState, MessagesState, StreamingState, ConnectionState, and ApprovalState. Each slice has its own store, actions, and reducers. This phase builds on the Phase 1 infrastructure (Store, Dispatcher, StoreSubscriberComponent, Selector).

</domain>

<decisions>
## Implementation Decisions

### State Shape Design
- Messages normalized by topic: `Dictionary<TopicId, List<Message>>` — fast topic switching, clear ownership
- Topic selection stores ID only: `SelectedTopicId: string?` — derive full topic via selector
- Streaming tracks per-message: `Dictionary<MessageId, StreamingContent>` — supports potential concurrent streams

### Action Granularity
- Fine-grained actions for messages: separate `AddMessage`, `UpdateMessage`, `RemoveMessage` actions
- Separate streaming actions: `StreamStarted`, `StreamChunk`, `StreamCompleted`, `StreamCancelled` — explicit types
- Verb + Noun naming convention: `AddMessage`, `SelectTopic`, `UpdateConnection` — imperative style
- Rich records allowed: actions can have computed properties for convenience

### Cross-slice Coordination
- Component responsibility for topic→messages: component dispatches `SelectTopic`, then separately dispatches `LoadMessages`
- Approval triggers execution: when approval is granted, the reducer or effect directly invokes tool execution

### Error and Loading States
- Errors auto-clear on success: next successful action clears previous error — reduces manual work

### Claude's Discretion
- ConnectionState metadata: whether to include lastConnected, reconnectAttempts, or just status enum
- Stream→Messages transfer: whether StreamCompleted automatically adds to MessagesState or dispatches separately
- Store isolation: whether stores can dispatch to other stores or must go through effects/components
- Loading state location: per-slice IsLoading vs centralized LoadingState
- Error representation: string message vs structured error type
- Connection error display: global banner vs component-handled

</decisions>

<specifics>
## Specific Ideas

- Messages dictionary keyed by TopicId enables instant topic switching without re-filtering
- Per-message streaming tracking future-proofs for scenarios like parallel tool executions
- Fine-grained actions make debugging easier — clear what changed and why
- Component-driven coordination keeps stores pure and predictable

</specifics>

<deferred>
## Deferred Ideas

None — discussion stayed within phase scope

</deferred>

---

*Phase: 02-state-slices*
*Context gathered: 2026-01-20*
