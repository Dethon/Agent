# Agent Memory MCP Server Specification

> MCP server enabling long-term and mid-term memory for AI agents with per-user scoping

## Problem Statement

AI agents lose context between conversations:
1. Each session starts fresh—no recall of previous interactions
2. User preferences, facts, and relationship history are lost
3. Agent personality cannot evolve based on accumulated experience
4. Multiple users sharing an agent get no personalization
5. Agents cannot proactively recall relevant information from past conversations

## Solution Overview

An MCP server providing structured memory storage with automatic relevance retrieval, user scoping, and memory lifecycle management.

### Memory Tiers

| Tier | Duration | Purpose | Examples |
|------|----------|---------|----------|
| **Long-term** | Permanent (until explicit delete) | Core facts, preferences, identity | "User prefers concise answers", "User is a Python developer" |
| **Mid-term** | Session-spanning, decays over time | Recent context, ongoing topics | "Working on project X", "Asked about Redis yesterday" |
| **Working** | Current session only | Immediate context | Handled by conversation history, not this MCP |

### Core Capabilities

| Tool | Purpose |
|------|---------|
| **MemoryStore** | Save a memory with metadata and importance |
| **MemoryRecall** | Retrieve relevant memories by semantic search or filter |
| **MemoryForget** | Delete or decay specific memories |
| **MemoryReflect** | Synthesize patterns from memories into personality/preferences |
| **MemoryList** | Browse memories by category, date, or importance |

---

## Data Model

### Memory Entity

```json
{
  "id": "mem_01HXYZ...",
  "userId": "user_123",
  "tier": "long-term",
  "category": "preference",
  "content": "User prefers detailed code examples over pseudocode",
  "context": "Learned when user asked for a sorting algorithm",
  "importance": 0.8,
  "confidence": 0.9,
  "embedding": [0.1, 0.2, ...],
  "tags": ["coding", "communication-style"],
  "createdAt": "2024-01-15T10:30:00Z",
  "lastAccessedAt": "2024-01-20T14:00:00Z",
  "accessCount": 5,
  "decayFactor": 1.0,
  "source": {
    "conversationId": "conv_abc123",
    "messageId": "msg_xyz789"
  }
}
```

### Memory Categories

| Category | Description | Examples |
|----------|-------------|----------|
| `preference` | How user likes things done | "Prefers bullet points", "Likes verbose explanations" |
| `fact` | Factual information about user | "Works at Acme Corp", "Uses macOS" |
| `relationship` | Interaction history and rapport | "We joke about tabs vs spaces", "User appreciates humor" |
| `skill` | User's capabilities and knowledge | "Expert in Rust", "Learning Kubernetes" |
| `project` | Ongoing work and context | "Building a media library app", "Migrating to microservices" |
| `personality` | Agent's evolved traits for this user | "Be more technical with this user", "Use analogies" |
| `instruction` | Explicit user directives | "Always respond in Spanish", "Don't suggest pip, use poetry" |

### User Personality Profile

Synthesized from memories, stored separately:

```json
{
  "userId": "user_123",
  "traits": {
    "communicationStyle": "technical, concise",
    "humorLevel": "occasional, dry",
    "formality": "casual",
    "detailLevel": "high for code, medium for explanations"
  },
  "knownFacts": [
    "Senior backend developer",
    "Works on distributed systems",
    "Prefers Go and Rust"
  ],
  "interactionGuidelines": [
    "Skip basic explanations unless asked",
    "Include error handling in code examples",
    "Mention performance implications"
  ],
  "lastUpdated": "2024-01-20T14:00:00Z",
  "memoryCount": 47
}
```

---

## Tool 1: MemoryStore

### Purpose
Saves a new memory or updates an existing one.

### Parameters

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `userId` | string | Yes | User identifier for scoping |
| `content` | string | Yes | The memory content to store |
| `category` | string | Yes | One of: `preference`, `fact`, `relationship`, `skill`, `project`, `personality`, `instruction` |
| `tier` | string | No | `long-term` or `mid-term` (default: inferred from category) |
| `importance` | number | No | 0.0-1.0 importance score (default: 0.5) |
| `confidence` | number | No | 0.0-1.0 confidence in accuracy (default: 0.7) |
| `tags` | string[] | No | Searchable tags |
| `context` | string | No | How/when this was learned |
| `supersedes` | string | No | Memory ID this replaces (for updates) |

### Returns

```json
{
  "status": "created",
  "memoryId": "mem_01HXYZ...",
  "userId": "user_123",
  "category": "preference",
  "tier": "long-term",
  "similarMemories": [
    {
      "id": "mem_01HABC...",
      "content": "User likes concise responses",
      "similarity": 0.85,
      "suggestion": "Consider merging or superseding"
    }
  ]
}
```

### Behavior

1. Generate semantic embedding for content
2. Check for similar existing memories (>0.8 similarity)
3. If `supersedes` provided, mark old memory as replaced
4. Store memory with metadata
5. Trigger async personality re-synthesis if category is `preference`, `relationship`, or `personality`

### Description for LLM

```
Stores a memory about the user for future recall. Use this when you learn something
worth remembering about the user—their preferences, facts about them, ongoing projects,
or how they like to interact.

Categories:
- preference: How user likes things (communication style, format preferences)
- fact: Factual info (job, location, tech stack)
- relationship: Interaction patterns (inside jokes, rapport)
- skill: User's expertise and learning areas
- project: Current work and context
- personality: How YOU should behave with this user
- instruction: Explicit directives from user

Guidelines:
- Set higher importance (0.7-1.0) for explicit user statements
- Set lower importance (0.3-0.5) for inferred preferences
- Use context to record WHY you're storing this
- Check returned similarMemories to avoid duplicates
- Use supersedes to update outdated memories

Examples:
- User says "I prefer TypeScript": category="preference", content="User prefers TypeScript over JavaScript", importance=0.9
- User mentions job: category="fact", content="User works as a DevOps engineer at StartupCo", importance=0.8
- You notice user likes jokes: category="relationship", content="User appreciates occasional humor", confidence=0.6
```

---

## Tool 2: MemoryRecall

### Purpose
Retrieves relevant memories using semantic search and/or filters.

### Parameters

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `userId` | string | Yes | User identifier for scoping |
| `query` | string | No | Semantic search query |
| `categories` | string[] | No | Filter by categories |
| `tags` | string[] | No | Filter by tags (OR logic) |
| `tier` | string | No | Filter by tier |
| `minImportance` | number | No | Minimum importance threshold |
| `limit` | number | No | Max memories to return (default: 10) |
| `includeContext` | boolean | No | Include storage context (default: false) |

### Returns

```json
{
  "userId": "user_123",
  "query": "coding preferences",
  "memories": [
    {
      "id": "mem_01HXYZ...",
      "category": "preference",
      "tier": "long-term",
      "content": "User prefers detailed code examples over pseudocode",
      "importance": 0.8,
      "relevance": 0.92,
      "lastAccessed": "2024-01-20T14:00:00Z",
      "context": "Learned when user asked for sorting algorithm"
    },
    {
      "id": "mem_01HABC...",
      "category": "skill",
      "tier": "long-term",
      "content": "User is an expert Python developer",
      "importance": 0.9,
      "relevance": 0.78
    }
  ],
  "totalMatches": 12,
  "personalitySummary": "Technical user who prefers detailed, production-ready code examples"
}
```

### Behavior

1. If `query` provided, perform semantic search using embeddings
2. Apply filters (categories, tags, tier, importance)
3. Rank by relevance * importance * recency
4. Update `lastAccessedAt` for returned memories
5. Apply decay to mid-term memories not accessed recently
6. Include synthesized personality summary if available

### Description for LLM

```
Retrieves memories about the user. Use this at the START of conversations and when
you need context about the user's preferences, background, or ongoing work.

IMPORTANT: Call this proactively! Don't wait for the user to remind you of things.

Search modes:
- Semantic: Provide query to find conceptually related memories
- Filtered: Use categories/tags to browse specific memory types
- Combined: Both query and filters for precise retrieval

Best practices:
1. At conversation start: recall with categories=["preference", "personality", "instruction"]
2. When user mentions a topic: recall with query about that topic
3. Before giving advice: recall with categories=["skill", "fact"] to tailor response
4. For ongoing work: recall with categories=["project"]

Examples:
- Start of conversation: categories=["preference", "personality"], limit=5
- User asks about databases: query="database experience and preferences"
- Before code example: query="coding style preferences", categories=["preference", "skill"]
```

---

## Tool 3: MemoryForget

### Purpose
Deletes memories or adjusts their decay/importance.

### Parameters

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `userId` | string | Yes | User identifier for scoping |
| `memoryId` | string | No* | Specific memory to forget |
| `query` | string | No* | Forget memories matching query |
| `categories` | string[] | No | Filter scope to categories |
| `olderThan` | string | No | Forget memories older than ISO date |
| `mode` | string | No | `delete`, `decay`, `archive` (default: delete) |
| `reason` | string | No | Why forgetting (for audit) |

\* At least one of `memoryId` or `query` required

### Returns

```json
{
  "status": "success",
  "action": "deleted",
  "affectedCount": 3,
  "affectedMemories": [
    { "id": "mem_01HXYZ...", "content": "User works at OldCo" }
  ],
  "reason": "User changed jobs"
}
```

### Behavior

1. Validate user owns the memories
2. If mode is `delete`, permanently remove
3. If mode is `decay`, reduce importance by 50%
4. If mode is `archive`, move to cold storage (queryable but not auto-recalled)
5. Log deletion for audit purposes
6. Trigger personality re-synthesis

### Description for LLM

```
Removes or diminishes memories. Use when information is outdated, wrong, or user
explicitly asks you to forget something.

Modes:
- delete: Permanent removal
- decay: Reduce importance (memory fades but isn't gone)
- archive: Keep for history but exclude from normal recall

When to forget:
- User corrects previous information ("Actually, I use vim not emacs")
- User explicitly requests ("Forget that I work at X")
- Information is clearly outdated
- Duplicate or redundant memories

IMPORTANT: When user provides corrected info, use supersedes in MemoryStore instead
of MemoryForget—this preserves history while updating the active memory.

Examples:
- User changed jobs: memoryId="mem_old", mode="delete", reason="User changed employers"
- Outdated project: query="Project Alpha", mode="archive"
- User request: query="my location", mode="delete", reason="User requested deletion"
```

---

## Tool 4: MemoryReflect

### Purpose
Synthesizes memories into personality traits and guidelines for interacting with the user.

### Parameters

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `userId` | string | Yes | User identifier |
| `focus` | string | No | Area to reflect on (e.g., "communication", "technical") |
| `includeMemories` | boolean | No | Include source memories in response (default: false) |

### Returns

```json
{
  "userId": "user_123",
  "profile": {
    "summary": "Senior backend developer who values efficiency and technical depth",
    "communicationStyle": {
      "preference": "Direct and technical",
      "avoidances": ["Over-explanation of basics", "Excessive caveats"],
      "appreciated": ["Code examples", "Performance considerations", "Trade-off analysis"]
    },
    "technicalContext": {
      "expertise": ["Go", "Distributed systems", "Kubernetes"],
      "learning": ["Rust", "WebAssembly"],
      "stack": ["Linux", "Docker", "PostgreSQL"]
    },
    "interactionGuidelines": [
      "Skip introductory explanations unless asked",
      "Include error handling in code snippets",
      "Mention scalability implications",
      "Occasional dry humor is appreciated"
    ],
    "activeProjects": [
      "Migrating monolith to microservices",
      "Building internal CLI tools"
    ]
  },
  "confidence": 0.85,
  "basedOnMemories": 34,
  "lastReflection": "2024-01-20T14:00:00Z"
}
```

### Behavior

1. Aggregate all memories for user
2. Use LLM to synthesize patterns and insights
3. Generate actionable interaction guidelines
4. Store/update personality profile
5. Return synthesized profile

### Description for LLM

```
Synthesizes a personality profile from accumulated memories. Use this to understand
HOW to interact with a user, not just WHAT you know about them.

Call this:
- After storing several new memories
- When starting a new conversation (get the latest synthesis)
- When you're unsure how to tailor your response

The returned profile includes:
- Communication style preferences
- Technical context and expertise
- Concrete interaction guidelines
- Active projects and context

Use the interactionGuidelines to adjust your behavior. These are patterns YOU
should follow when talking to this user, derived from past interactions.

Example workflow:
1. MemoryRecall at conversation start
2. MemoryReflect if many memories but no recent profile
3. Use profile.interactionGuidelines throughout conversation
4. MemoryStore new learnings
5. MemoryReflect periodically to update profile
```

---

## Tool 5: MemoryList

### Purpose
Browse and manage memories with pagination and sorting.

### Parameters

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `userId` | string | Yes | User identifier |
| `category` | string | No | Filter by single category |
| `tier` | string | No | Filter by tier |
| `sortBy` | string | No | `created`, `accessed`, `importance` (default: created) |
| `order` | string | No | `asc` or `desc` (default: desc) |
| `page` | number | No | Page number (default: 1) |
| `pageSize` | number | No | Items per page (default: 20, max: 100) |

### Returns

```json
{
  "userId": "user_123",
  "memories": [
    {
      "id": "mem_01HXYZ...",
      "category": "preference",
      "tier": "long-term",
      "content": "User prefers vim keybindings",
      "importance": 0.7,
      "createdAt": "2024-01-15T10:30:00Z",
      "lastAccessedAt": "2024-01-20T14:00:00Z",
      "accessCount": 3
    }
  ],
  "pagination": {
    "page": 1,
    "pageSize": 20,
    "totalItems": 47,
    "totalPages": 3
  },
  "stats": {
    "byCategory": {
      "preference": 15,
      "fact": 12,
      "skill": 8,
      "project": 7,
      "relationship": 5
    },
    "byTier": {
      "long-term": 35,
      "mid-term": 12
    }
  }
}
```

### Description for LLM

```
Lists memories with filtering, sorting, and pagination. Use this to review what
you know about a user or find specific memories to update/delete.

Use cases:
- Audit what you've stored: no filters, sortBy="created"
- Find old memories: sortBy="accessed", order="asc"
- Review category: category="project" to see all projects
- Find important memories: sortBy="importance", order="desc"

The stats field shows distribution of memories—useful for understanding what
kinds of things you've remembered about this user.
```

---

## Automatic Memory Behaviors

### Proactive Recall

The agent should automatically call `MemoryRecall` at:
1. **Conversation start**: Retrieve personality profile and key preferences
2. **Topic change**: When conversation shifts to new subject, recall related memories
3. **Before recommendations**: Recall relevant preferences and context

### Automatic Storage Triggers

The agent should call `MemoryStore` when:
1. User explicitly states a preference ("I prefer X")
2. User shares factual information ("I work at X", "I'm learning Y")
3. User corrects the agent (store correction, supersede wrong memory)
4. User gives explicit instruction ("Always do X", "Never suggest Y")
5. Pattern detected in multiple interactions (inferred preference)

### Memory Decay

Mid-term memories automatically decay:
- Decay applied on each access check
- Importance reduced by 10% per week without access
- Memories below 0.1 importance auto-archived
- Long-term memories don't decay but can be manually adjusted

### Deduplication

Before storing:
1. Generate embedding for new content
2. Find memories with >0.85 similarity
3. If found, either:
   - Merge (combine information)
   - Supersede (if new info is update)
   - Skip (if duplicate)

---

## Storage Implementation

### Recommended: Vector Database + Redis

```
┌─────────────────┐     ┌──────────────────┐
│   Memory MCP    │────▶│   Redis          │
│   Server        │     │   - Metadata     │
│                 │     │   - Fast lookup  │
└────────┬────────┘     │   - Profiles     │
         │              └──────────────────┘
         │
         ▼
┌─────────────────┐
│   Vector DB     │
│   - Embeddings  │
│   - Semantic    │
│     search      │
│   (Qdrant/      │
│    Pinecone)    │
└─────────────────┘
```

### Key Schema (Redis)

```
memory:{userId}:{memoryId}     → Memory JSON
memories:{userId}:index        → Sorted set by created date
memories:{userId}:category:{c} → Set of memory IDs in category
profile:{userId}               → Personality profile JSON
```

### Alternative: SQLite + Local Embeddings

For simpler deployments:
- SQLite for storage
- Local embedding model (e.g., all-MiniLM-L6-v2)
- In-memory vector index

---

## Embedding Generation

### Recommended: OpenRouter Embeddings API

Since Jack already uses OpenRouter for LLM inference, use the same API for embeddings—single API key, unified billing.

**Endpoint**: `POST https://openrouter.ai/api/v1/embeddings`

**Available Models**:

| Model | Dimensions | Cost (per 1M tokens) | Notes |
|-------|------------|----------------------|-------|
| `openai/text-embedding-3-small` | 1536 | ~$0.02 | Recommended for most use cases |
| `openai/text-embedding-3-large` | 3072 | ~$0.13 | Higher quality, larger storage |

**Cost Estimate**: Storing 10,000 memories ≈ $0.01-0.02

### Implementation

```csharp
public class OpenRouterEmbeddingService : IEmbeddingService
{
    private readonly HttpClient _httpClient;
    private readonly string _model = "openai/text-embedding-3-small";

    public OpenRouterEmbeddingService(HttpClient httpClient, IOptions<OpenRouterSettings> settings)
    {
        _httpClient = httpClient;
        _httpClient.BaseAddress = new Uri("https://openrouter.ai/api/v1/");
        _httpClient.DefaultRequestHeaders.Authorization = 
            new AuthenticationHeaderValue("Bearer", settings.Value.ApiKey);
    }

    public async Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken ct = default)
    {
        var request = new { model = _model, input = text };
        var response = await _httpClient.PostAsJsonAsync("embeddings", request, ct);
        response.EnsureSuccessStatusCode();
        
        var result = await response.Content.ReadFromJsonAsync<EmbeddingResponse>(ct);
        return result!.Data[0].Embedding;
    }

    public async Task<float[][]> GenerateEmbeddingsAsync(IEnumerable<string> texts, CancellationToken ct = default)
    {
        var request = new { model = _model, input = texts.ToArray() };
        var response = await _httpClient.PostAsJsonAsync("embeddings", request, ct);
        response.EnsureSuccessStatusCode();
        
        var result = await response.Content.ReadFromJsonAsync<EmbeddingResponse>(ct);
        return result!.Data.OrderBy(d => d.Index).Select(d => d.Embedding).ToArray();
    }
}

public record EmbeddingResponse(EmbeddingData[] Data, string Model, EmbeddingUsage Usage);
public record EmbeddingData(int Index, float[] Embedding);
public record EmbeddingUsage(int PromptTokens, int TotalTokens);
```

### Domain Interface

```csharp
// Domain/Memory/IEmbeddingService.cs
public interface IEmbeddingService
{
    Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken ct = default);
    Task<float[][]> GenerateEmbeddingsAsync(IEnumerable<string> texts, CancellationToken ct = default);
}
```

### Similarity Calculation

```csharp
public static class VectorMath
{
    public static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length)
            throw new ArgumentException("Vectors must have same dimensions");

        float dot = 0, magA = 0, magB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            magA += a[i] * a[i];
            magB += b[i] * b[i];
        }
        
        return dot / (MathF.Sqrt(magA) * MathF.Sqrt(magB));
    }
}
```

### Alternative: No Embeddings Mode

For simpler deployments or lower costs, the memory system can operate without embeddings:

**Trade-offs**:
- ❌ No semantic search (can't find "communication preferences" when memory says "likes concise answers")
- ❌ No automatic duplicate detection
- ✅ Zero embedding costs
- ✅ No vector database needed
- ✅ Simpler infrastructure

**Mitigations**:
1. **Rich tagging**: Store memories with multiple searchable tags
2. **Hierarchical categories**: Use specific categories like `preference/communication/verbosity`
3. **Full-text search**: Use Redis Search or SQLite FTS for keyword matching
4. **LLM-assisted retrieval**: Let the agent request memories by category, then filter

**Configuration**:
```csharp
public class MemorySettings
{
    public bool EnableEmbeddings { get; set; } = true;
    public string EmbeddingModel { get; set; } = "openai/text-embedding-3-small";
    public float SimilarityThreshold { get; set; } = 0.85f;
}
```

When `EnableEmbeddings = false`:
- `MemoryStore` skips embedding generation
- `MemoryRecall` uses tag/category/keyword filtering only
- Deduplication relies on exact text matching

---

## Security & Privacy

### User Scoping

All operations MUST include `userId`:
- Memories are strictly isolated per user
- No cross-user queries allowed
- UserId validated on every operation

### Data Retention

- Users can request full memory export
- Users can request complete deletion
- Audit log tracks all memory operations
- Configurable retention policies per tier

### Sensitive Data

- Avoid storing passwords, tokens, PII
- Category `instruction` for user directives only
- Optional encryption at rest

---

## Agent System Prompt Integration

Add to agent system prompt:

```markdown
## Memory System

You have access to a memory system via MCP tools. Use it proactively:

### At Conversation Start
Call MemoryRecall with categories=["preference", "personality", "instruction"] to load
the user's profile. Apply the interaction guidelines throughout the conversation.

### During Conversation
- When user shares information about themselves → MemoryStore as "fact"
- When user states preferences → MemoryStore as "preference" with high importance
- When user corrects you → MemoryStore with supersedes pointing to wrong memory
- When user gives explicit instructions → MemoryStore as "instruction" with importance=1.0
- When topic changes → MemoryRecall with relevant query

### Building Relationship
- Notice patterns in how user communicates
- Store relationship insights (humor, formality, etc.)
- Periodically MemoryReflect to synthesize personality

### Memory Hygiene
- Don't store trivial or one-time information
- Update rather than duplicate (use supersedes)
- Forget outdated information proactively
```

---

## Workflow Examples

### Example 1: First Conversation with New User

```
1. User sends first message: "Hey, can you help with some Python code?"

2. Agent calls MemoryRecall
   userId: "user_123"
   categories: ["preference", "personality", "instruction"]
   
   → Returns: { memories: [], personalitySummary: null }
   
3. Agent responds helpfully, then calls MemoryStore
   userId: "user_123"
   category: "skill"
   content: "User works with Python"
   importance: 0.6
   confidence: 0.7
   context: "User asked for Python help"
```

### Example 2: Returning User

```
1. User sends message: "I'm back! Still working on that API"

2. Agent calls MemoryRecall
   userId: "user_123"
   query: "API project"
   categories: ["project", "preference"]
   
   → Returns:
   {
     memories: [
       { content: "Building REST API for inventory management", category: "project" },
       { content: "Prefers FastAPI over Flask", category: "preference" }
     ],
     personalitySummary: "Technical user, prefers concise responses, FastAPI enthusiast"
   }

3. Agent responds: "Welcome back! How's the inventory API coming along? 
   Still using FastAPI?"
```

### Example 3: User Correction

```
1. Previous memory: "User works at TechCorp"

2. User says: "Actually I changed jobs, I'm at StartupXYZ now"

3. Agent calls MemoryStore
   userId: "user_123"
   category: "fact"
   content: "User works at StartupXYZ"
   importance: 0.9
   supersedes: "mem_old_job_id"
   context: "User corrected previous job information"
   
   → Old memory marked as superseded, new memory active
```

### Example 4: Personality Evolution

```
After 20 conversations, agent notices patterns:
- User often asks for performance implications
- User appreciates when agent mentions edge cases
- User prefers examples over theory

Agent calls MemoryStore:
   category: "personality"
   content: "When helping this user, emphasize performance implications and edge cases"
   importance: 0.8
   context: "Inferred from conversation patterns"

Agent calls MemoryReflect:
   → Returns updated profile with new interaction guidelines
```

---

## Implementation Notes

### File Location
- `McpServerMemory/McpTools/McpMemoryStoreTool.cs`
- `McpServerMemory/McpTools/McpMemoryRecallTool.cs`
- `McpServerMemory/McpTools/McpMemoryForgetTool.cs`
- `McpServerMemory/McpTools/McpMemoryReflectTool.cs`
- `McpServerMemory/McpTools/McpMemoryListTool.cs`

### Domain Classes
- `Domain/Memory/Memory.cs` - Core entity
- `Domain/Memory/MemoryCategory.cs` - Category enum
- `Domain/Memory/MemoryTier.cs` - Tier enum
- `Domain/Memory/PersonalityProfile.cs` - Synthesized profile
- `Domain/Memory/IMemoryStore.cs` - Storage interface
- `Domain/Memory/IEmbeddingService.cs` - Embedding generation

### Infrastructure Classes
- `Infrastructure/Memory/OpenRouterEmbeddingService.cs` - OpenRouter embeddings client
- `Infrastructure/Memory/RedisMemoryStore.cs` - Redis-based memory storage
- `Infrastructure/Memory/VectorMath.cs` - Cosine similarity calculations

### Dependencies
- OpenRouter API (embeddings endpoint) - uses existing API key
- Redis with RediSearch module (for full-text + vector search)
- Alternative: Qdrant/Pinecone for dedicated vector DB

---

## Future Enhancements

1. **Memory Sharing**: Allow users to share specific memories (e.g., project context with team)
2. **Memory Import/Export**: Portable memory format for backup/migration
3. **Confidence Calibration**: Track prediction accuracy to adjust confidence scores
4. **Memory Clustering**: Auto-group related memories into topics
5. **Temporal Queries**: "What did the user mention last week?"
6. **Memory Conflicts**: Detect and resolve contradictory memories
7. **Privacy Modes**: "Incognito" conversations that don't store memories
8. **Memory Visualization**: Resource endpoint for browsing memories
