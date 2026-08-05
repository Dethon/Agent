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
- **Extraction** — `ChatMonitor` queues turns → `MemoryExtractionWorker` fetches the persisted thread and hands it to `ExtractionWindow.Build`, which cuts the window at the `MemoryAnchor` → `IMemoryExtractor` (LLM) reads it, rendered by `ExtractionWindow.Render` with `[CURRENT]`/`[context -N]` markers → `IMemoryStore` (Redis Stack, vector search) persists. Falls back to raw message content when the thread is unavailable.
- **Recall** — `MemoryRecallHook` runs before each turn: builds a user-only window, takes the extraction anchor, semantic-searches, attaches a `MemoryContext` to the message.

`Domain/Memory` owns both halves of what the model reads. `ExtractionWindow` cuts and renders the extraction window; the anchor it cuts at is only correct because recall runs before the turn is persisted, which `MemoryAnchor`'s factory names and `ChatMonitorMemoryAnchorTests` pins. Each rendered marker is cross-checked against the prompt constant that names it — renaming one side alone goes red.
- **Dreaming** — `MemoryDreamingService` periodically consolidates/prunes via `IMemoryConsolidator` (LLM).
- All three publish `MetricEvent`s.
