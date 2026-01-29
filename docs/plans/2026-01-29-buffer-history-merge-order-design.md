# Buffer-History Merge Ordering

## Problem

When `ResumeFromBuffer` merges buffered messages with history, buffer messages whose IDs are not found in history get appended at the end via `AddMessage`. This loses their original ordering — they should be interleaved with history messages based on their position relative to anchor messages (buffer messages that DO match history by ID).

## Design

### Merge Algorithm

Replace the per-message dispatch loop in `ResumeFromBuffer` (MessagePipeline.cs:217-230) with a single merged list built upfront.

**Steps:**

1. Walk `completedTurns` in order. Classify each as **anchor** (has MessageId present in `historyById`) or **new**. Group into segments of consecutive new messages followed by an optional anchor. Track:
   - `leadingNew`: new messages before the first anchor
   - `anchorPrecedingNew`: `Dictionary<string, List<ChatMessageModel>>` mapping each anchor's MessageId to the new messages that precede it in buffer order
   - `trailingNew`: new messages after the last anchor

2. Walk history in order. For each history message:
   - If it equals the first anchor and there are `leadingNew` messages, insert them before it
   - If it's an anchor, apply merge logic inline (enrich with reasoning/toolcalls from buffer turn), then insert its `anchorPrecedingNew` messages after it
   - Otherwise, emit the history message as-is

3. Append `trailingNew` messages at the end.

4. Dispatch the merged list as a single `MessagesLoaded` action with updated `FinalizedMessageIdsByTopic`.

### Prompt Handling

The current prompt (user message that triggered the buffer) is currently dispatched separately before the turns loop. In the new approach, if the prompt isn't in history, prepend it to `leadingNew` (or to the appropriate segment based on its position relative to the first anchor).

### Streaming Message

Unchanged. The streaming message is always the latest in-progress content, dispatched as `StreamChunk` at the end.

### TryMergeIntoHistory

Moves inline into the merge loop. Instead of dispatching `UpdateMessage`, we create the enriched model directly when building the merged list.

## Scope

- **Modified**: `MessagePipeline.ResumeFromBuffer` — replace turns loop with merge algorithm
- **Unchanged**: `MessagesReducers`, `BufferRebuildUtility`, `StreamResumeService`, streaming chunk logic
- **No new actions or reducer changes needed** — reuses `MessagesLoaded`

## File

`WebChat.Client/State/Pipeline/MessagePipeline.cs`
