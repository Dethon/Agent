# Proactive Memory System Design

## Summary

Replace the current MCP-server-based memory system (McpServerMemory) with a domain-level feature that operates non-agentically. Memory recall is injected automatically into user messages via a hook in the agent pipeline. Memory extraction runs asynchronously via a channel-based background worker. A nightly "dreaming" service consolidates memories using LLM-driven merge, importance decay, and profile synthesis.

The agent no longer needs to call memory tools — context is provided transparently. The only remaining agent-callable tool is `memory_forget` for explicit user deletion requests.

## Goals

- Memories are stored proactively without relying on the agent to remember to call tools
- Relevant memories are injected into every user message automatically — no empty first recalls
- Recall is based on what the user actually wrote, not generic category filters
- Memory maintenance (dedup, decay, profile synthesis) happens automatically overnight
- The feature follows existing domain patterns (`IDomainToolFeature`, `IHostedService`, `Channel<T>`)

## Architecture

Three independent components wired through the existing DI module pattern:

```
User Message
     |
     +---> [ChatMonitor] --> MemoryRecallHook (sync)
     |          |                |
     |          |           Embed query -> search Redis -> attach memories as AdditionalProperties
     |          |                |
     |          |           Enqueue message to Channel<MemoryExtractionRequest>
     |          |
     |          v
     |     [Agent runs with memory context injected in user message]
     |          |
     |          +-- can call domain:memory:memory_forget
     |          |
     |          v
     |     [Response sent to user]
     |
     +---> [MemoryExtractionWorker] (IHostedService, reads Channel)
     |          |
     |          +-- IMemoryExtractor (LLM call) -> candidate memories
     |          +-- IEmbeddingService -> generate embeddings
     |          +-- IMemoryStore -> store new memories (with dedup)
     |
     +---> [MemoryDreamingService] (IHostedService, nightly cron)
               |
               +-- LLM-driven merge of similar memories
               +-- Importance decay of unaccessed memories
               +-- LLM-driven personality profile synthesis
```

## Component Details

### 1. Memory Recall Hook

Synchronous component that runs in `ChatMonitor` before the agent processes each message.

**Contract**: `IMemoryRecallHook` (Domain) — defines `EnrichAsync(ChatMessage, CancellationToken)` which attaches memory context and returns
**Implementation**: `MemoryRecallHook` (Infrastructure) — implements recall logic and also enqueues the extraction request to the channel

**Flow**:

1. `ChatMonitor.ProcessChatThread()` calls `IMemoryRecallHook.EnrichAsync(chatMessage, ct)` before passing the message to the agent
2. Extracts the user's text content, generates an embedding via `IEmbeddingService`
3. Searches `IMemoryStore.SearchAsync()` with the embedding — returns top N relevant memories + personality profile
4. Attaches results as `AdditionalProperties` on the `ChatMessage` using a new extension method (`SetMemoryContext` / `GetMemoryContext`)
5. Enqueues a `MemoryExtractionRequest` (userId, message content, conversationId) to `Channel<MemoryExtractionRequest>` (channel injected into the Infrastructure implementation, not part of the domain contract)
6. Returns — agent runs immediately

**Injection in OpenRouterChatClient**: Same transform block that handles userId/timestamp. Reads `GetMemoryContext()` from `AdditionalProperties` and prepends a formatted block to the user message content:

```
[Memory context]
- User prefers concise responses (preference, importance: 0.9)
- User works at Contoso on a .NET microservices project (fact, importance: 0.8)
[End memory context]
```

**New DTOs**:

- `MemoryContext` — record holding `IReadOnlyList<MemorySearchResult>` and `PersonalityProfile?`
- `MemoryExtractionRequest` — record holding `string UserId`, `string MessageContent`, `string? ConversationId`

### 2. Memory Extraction Worker

Background `IHostedService` that consumes `Channel<MemoryExtractionRequest>` and stores extracted memories.

**Contract**: `IMemoryExtractor` (Domain)
**Implementation**: `OpenRouterMemoryExtractor` (Infrastructure)

**IMemoryExtractor contract**:

```csharp
public interface IMemoryExtractor
{
    Task<IReadOnlyList<ExtractionCandidate>> ExtractAsync(
        string messageContent, string userId, CancellationToken ct);
}
```

**ExtractionCandidate DTO**:

```csharp
public record ExtractionCandidate(
    string Content,
    MemoryCategory Category,
    double Importance,
    double Confidence,
    IReadOnlyList<string> Tags,
    string? Context);
```

**Worker flow**:

1. Reads `MemoryExtractionRequest` from channel
2. Fetches existing personality profile for the user (for dedup context)
3. Calls `IMemoryExtractor.ExtractAsync()` — LLM analyzes the message
4. For each candidate:
   - Generate embedding via `IEmbeddingService`
   - Check for similar existing memories (>0.85 threshold from config)
   - If similar memory exists with same meaning: skip
   - If similar memory exists but new info updates it: supersede
   - Otherwise: store via `IMemoryStore.StoreAsync()`

**LLM extraction prompt**: System prompt instructs the model to extract storable facts, preferences, instructions, skills, and projects from the message. Returns structured JSON array of candidates. Includes the user's existing personality profile as context to avoid storing duplicates. Follows the importance/confidence guidelines from the current `MemoryPrompt`.

### 3. Memory Dreaming Service

Nightly `IHostedService` that consolidates memories. Runs three operations sequentially per user in this order: **merge -> decay -> reflect**.

**Contracts**:

- `IMemoryConsolidator` (Domain) — LLM-driven merge and profile synthesis
- `OpenRouterMemoryConsolidator` (Infrastructure)

#### 3a. LLM-driven memory merging

- Fetch all active (non-superseded) memories for a user, grouped by category
- Send each category cluster to `IMemoryConsolidator` with a consolidation prompt
- The LLM decides:
  - Which memories are redundant and should be merged (returns a single consolidated memory)
  - Which memories contradict each other (keeps the newer one, supersedes the older)
  - Which memories are still distinct and should remain separate
- Apply decisions: create merged memories, supersede the originals

#### 3b. Importance decay

- Fetch all active memories
- For memories not accessed in the last N days (configurable, default 30): reduce importance by a configurable factor (default 0.9, multiplicative)
- Floor at configurable minimum (default 0.1)
- Skip exempt categories (default: `Instruction`)

#### 3c. LLM-driven personality profile synthesis

- Fetch all active memories for the user (post-merge, post-decay)
- Send to `IMemoryConsolidator` with a profile synthesis prompt
- LLM generates a structured `PersonalityProfile` (summary, communication style, technical context, interaction guidelines, active projects)
- Store via `IMemoryStore.SaveProfileAsync()`

**Scheduling**: `BackgroundService` with a polling loop. Uses `ICronValidator` (already exists) for next-occurrence calculation. Configurable cron expression, defaults to `0 3 * * *` (3 AM daily).

### 4. Memory Forget Tool

The only remaining agent-callable tool. Exposed as a domain tool via `IDomainToolFeature`.

Reuses existing `MemoryForgetTool` logic with minimal changes:

- Registered as `domain:memory:memory_forget`
- Supports forget by ID, query, category, or age
- Supports delete and archive modes
- Agent can call it when user explicitly says "forget X"

### 5. Simplified Memory Prompt

`MemoryPrompt.cs` is drastically simplified. No longer instructs the agent to call recall or manage storage. Content:

- Tells the agent that memory context is provided automatically in messages
- Documents the `memory_forget` tool for explicit deletion requests
- No mandatory first calls, no storage triggers, no memory hygiene instructions

## DI Wiring

New module `MemoryModule` in `Agent/Modules/`:

```csharp
public static IServiceCollection AddMemory(this IServiceCollection services)
{
    // Channel for async extraction
    services.AddSingleton(Channel.CreateUnbounded<MemoryExtractionRequest>(
        new UnboundedChannelOptions { SingleReader = true }));

    // Infrastructure
    services.AddSingleton<IMemoryStore, RedisStackMemoryStore>();
    services.AddSingleton<IEmbeddingService, OpenRouterEmbeddingService>();
    services.AddSingleton<IMemoryExtractor, OpenRouterMemoryExtractor>();
    services.AddSingleton<IMemoryConsolidator, OpenRouterMemoryConsolidator>();

    // Hook (sync recall + enqueue extraction)
    services.AddSingleton<IMemoryRecallHook, MemoryRecallHook>();

    // Domain tool (only memory_forget)
    services.AddTransient<MemoryForgetTool>();
    services.AddTransient<IDomainToolFeature, MemoryToolFeature>();

    // Background workers
    services.AddHostedService<MemoryExtractionWorker>();
    services.AddHostedService<MemoryDreamingService>();

    return services;
}
```

**Integration in ConfigModule.cs**:

```csharp
services.ConfigureAgents(settings, cmdParams)
    .AddAgent(settings)
    .AddScheduling()
    .AddSubAgents(settings.SubAgents)
    .AddMemory()
    .AddChatMonitoring(settings, cmdParams);
```

**ChatMonitor change**: Inject `IMemoryRecallHook` via constructor. Call `EnrichAsync` before passing messages to the agent.

**Feature activation**: Controlled via `EnabledFeatures: ["memory"]` on agent definitions, same as `"subagents"` and `"scheduling"`.

## Configuration

```json
{
  "Memory": {
    "Embedding": {
      "Model": "openai/text-embedding-3-small"
    },
    "Extraction": {
      "Model": "google/gemini-2.0-flash-001",
      "MaxCandidatesPerMessage": 5,
      "SimilarityThreshold": 0.85
    },
    "Dreaming": {
      "CronSchedule": "0 3 * * *",
      "Model": "google/gemini-2.0-flash-001",
      "MergeSimilarityThreshold": 0.90,
      "DecayDays": 30,
      "DecayFactor": 0.9,
      "DecayFloor": 0.1,
      "DecayExemptCategories": ["Instruction"]
    },
    "Recall": {
      "DefaultLimit": 10,
      "IncludePersonalityProfile": true
    },
    "MemoryTtlDays": 365
  }
}
```

Redis connection uses the shared top-level config. OpenRouter API key and base URL use the shared OpenRouter config.

## What Gets Deleted

**Entire project removed**:

- `McpServerMemory/` — all files, project reference, docker-compose service, deployment config

**Domain tools deleted**:

- `MemoryStoreTool` — replaced by extraction worker
- `MemoryRecallTool` — replaced by recall hook
- `MemoryReflectTool` — replaced by LLM-driven consolidation in dreaming service
- `MemoryListTool` — maintenance is automatic, no agent browsing needed

**Domain tools kept**:

- `MemoryForgetTool` — simplified, re-registered as domain tool feature

**Infrastructure kept**:

- `RedisStackMemoryStore` — unchanged
- `OpenRouterEmbeddingService` — unchanged

**Infrastructure added**:

- `OpenRouterMemoryExtractor` — LLM-based extraction
- `OpenRouterMemoryConsolidator` — LLM-based merge + reflect

**Config removed**:

- `mcp-memory` from docker-compose services
- `mcp-memory` from agent MCP endpoint configuration
- `mcp:mcp-memory:*` from whitelist patterns

**Config added**:

- `Memory` section in appsettings.json (see Configuration above)

## Error Handling

| Failure | Behavior |
|---|---|
| Extraction worker: LLM error or parse failure | Retry up to 2 times (3 total). After exhaustion: log + publish `ErrorEvent`, drop the message. |
| Extraction worker: Redis or embedding error | Log + publish `ErrorEvent`, drop the message. No retry (infrastructure issue). |
| Recall hook: embedding or Redis error | Log + publish `ErrorEvent`. Proceed without memory context. Never blocks the agent pipeline. |
| Dreaming service: per-user failure | Log error for that user, continue processing other users. |
| Dreaming service: LLM error or parse failure | Retry up to 2 times (3 total). After exhaustion: log + publish `ErrorEvent`, skip that operation for that user. |
| Dreaming service: global failure | Log + publish `ErrorEvent`. Retry on next cron tick. |

## Metrics

Published through existing `IMetricsPublisher`:

- `MemoryExtractionEvent` — extraction duration, candidate count, stored count
- `MemoryRecallEvent` — recall duration, memory count returned
- `MemoryDreamingEvent` — merged count, decayed count, profile regenerated
- `ErrorEvent` — for all failure cases

## Testing

### Unit tests (Domain layer)

| Test target | Verification |
|---|---|
| `MemoryForgetTool` | Existing tests with minimal adaptation for domain tool registration |
| `MemoryRecallHook` | Mock IMemoryStore + IEmbeddingService. Verify: attaches MemoryContext to AdditionalProperties, enqueues MemoryExtractionRequest to channel, handles empty results gracefully |
| `MemoryDreamingWorker` | Mock IMemoryStore + IMemoryConsolidator. Verify: merge -> decay -> reflect ordering, decay math (factor, floor, exempt categories), supersede calls for merged memories |
| Extraction dedup logic | Given similar existing memories above threshold: verify skip. Given updated info: verify supersede. Given novel info: verify store. |

### Integration tests (Infrastructure)

| Test target | Verification |
|---|---|
| `RedisStackMemoryStore` | Existing tests — unchanged |
| `OpenRouterMemoryExtractor` | Given sample user messages, returns valid ExtractionCandidate[] with correct categories and importance ranges |
| `OpenRouterMemoryConsolidator` | Given sample memory sets with duplicates/contradictions, returns correct merge decisions and a well-formed PersonalityProfile |
| `MemoryRecallHook` integration | End-to-end: store memories in Redis -> recall hook enriches a ChatMessage -> verify injected content |
