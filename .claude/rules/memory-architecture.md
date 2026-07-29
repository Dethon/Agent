---
paths:
  - "Infrastructure/Memory/**"
  - "Domain/Tools/Memory/**"
  - "Domain/Memory/**"
  - "Domain/Contracts/IMemory*.cs"
  - "Domain/Prompts/MemoryPrompts.cs"
  - "Agent/Modules/MemoryModule.cs"
---

# Memory Architecture

Built into the Agent process, not an MCP server:
- **Extraction** — `ChatMonitor` queues turns → `MemoryExtractionWorker` fetches the persisted thread and slices a window anchored at the recall point → `IMemoryExtractor` (LLM) reads it (rendered by `ConversationWindowRenderer` with `[CURRENT]`/`[context -N]` markers) → `IMemoryStore` (Redis Stack, vector search) persists. Falls back to raw message content when the thread is unavailable.
- **Recall** — `MemoryRecallHook` runs before each turn: builds a user-only window, sets the extraction anchor, semantic-searches, injects into the system prompt.
- **Dreaming** — `MemoryDreamingService` periodically consolidates/prunes via `IMemoryConsolidator` (LLM).
- All three publish `MetricEvent`s.
