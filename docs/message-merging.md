# Message Merging: Multi-MessageId Bubble Consolidation

## Problem

During a single assistant response, the LLM backend may produce multiple distinct MessageIds. Earlier MessageIds often carry only reasoning or tool calls, while only the final MessageId carries the actual content. Without merging, each MessageId renders as a separate chat bubble — producing empty bubbles above the real response.

## Design Rules

- **Forward accumulation only**: reasoning and tool calls from earlier messages merge into later ones, never backwards.
- **Reasoning separator**: `"\n-----\n"` between blocks from different message turns.
- **Tool calls separator**: `"\n"` between blocks from different message turns.
- **Empty trailing bubble allowed**: the last bubble in the conversation may have no content (indicates more chunks/messages are expected).

## Two Code Paths, One View-Level Safety Net

The fix lives in two layers to cover all scenarios:

### 1. Streaming path — `StreamingService.cs`

During live streaming, when a MessageId turn change occurs and the outgoing turn has **no Content** (only reasoning/toolcalls), the data is **carried forward** into the next streaming accumulator rather than finalized as a separate store entry.

**Key methods:**

- `CarryForward(message)` — resets the accumulator but preserves reasoning/toolcalls with trailing separators (`"\n-----\n"` / `"\n"`) so `AccumulateChunk` concatenates correctly across turn boundaries.
- `TrimSeparators(message)` — strips any trailing separator that was added for carry-forward but had no subsequent content (called at stream end before final `AddMessage`).

**Location**: `WebChat.Client/Services/Streaming/StreamingService.cs` — applied in both `StreamResponseAsync` and `ResumeStreamResponseAsync` at the `isNewMessageTurn` branch.

**Why here**: during streaming, finalized messages go to `MessagesStore` while the current message lives in `StreamingStore`/`StreamingMessageDisplay`. These are separate rendering systems — the view-level merge cannot reach across them. Without this fix, a content-less finalized message would sit as a visible empty bubble above the active streaming bubble.

### 2. View level — `MessageList.razor`

`MergeConsecutiveAssistantMessages` runs on every render, collapsing any consecutive run of assistant messages into a single bubble. This handles:

- **Buffer resume path**: `BufferRebuildUtility.ResumeFromBuffer` inserts "following new" messages after their history anchor, which can produce `[content, no-content]` order.
- **Edge cases**: any scenario where separate assistant messages end up adjacent in the store (race conditions, reordering, etc).

**Algorithm**:
1. Walk the message list forward.
2. Collect consecutive assistant messages into a run.
3. Accumulate reasoning (`"\n-----\n"` separator) and tool calls (`"\n"` separator).
4. Take Content from whichever message in the run has it.
5. Use the message with Content as the anchor (preserves its MessageId/Timestamp); fall back to the last message if none has Content.
6. Emit one merged bubble at the end of each run.

**Location**: `WebChat.Client/Components/Chat/MessageList.razor` — `MergeConsecutiveAssistantMessages` static method, called from `UpdateMessages`.

## Why Both Layers

| Scenario | StreamingService fix | MessageList fix |
|---|---|---|
| Live streaming, turn change with no content | Needed (streaming vs finalized are separate render systems) | Cannot help |
| Buffer resume, following-new messages after anchor | Not involved | Needed |
| Post-stream finalized messages in store | Covered (carried forward, no empty entries created) | Safety net if any slip through |

Removing either layer would leave one scenario unhandled. The view-level merge is a safety net that ensures no empty bubbles regardless of how messages arrive in the store.
