# Moving the recall embedding from OpenRouter to the local Lemonade Server

Type: research
Status: ready-for-human

Question: would moving the memory-recall embedding call from OpenRouter to the Lemonade
Server we already run reduce latency?

All measurements below were taken on the live prod host `ai370` (`192.168.5.45`) on
2026-08-08 between 03:40 and 04:05 CEST. Prod state was restored afterwards: the two
embedding models pulled for the benchmark were unloaded and deleted, and Lemonade's
`/api/v1/health` now reports the same two models (`Whisper-Large-v3-Turbo`, `kokoro-v1`)
and zero pinned models it reported before. No code was changed.

---

## Verdict

**Yes. Do it.** The embedding call is on the blocking path of every user turn, it is
measured at **361 ms median** through OpenRouter, and the same work runs in **6 ms median**
on the Lemonade box we already operate. That is roughly a **350 ms saving on every turn**,
including every voice turn.

But two cheaper things should be done first or alongside, because they are close to free:

1. **Skip the embedding call when the user has no stored memories.** 97 of 273 measured
   recalls (36%) returned zero memories, because those users (`household`, `scheduler`)
   have no memories stored at all. Each still paid a full ~575 ms round trip to search an
   empty set. `household` is the voice satellite identity — the most latency-sensitive
   path in the product.

   **Correction, see section 7.** Calling all 97 "pure waste" was wrong. `household` and
   `GameDirector` have personality profiles, and the hook attaches a `MemoryContext` when
   `memories.Count > 0 || profile is not null` (`MemoryRecallHook.cs:83`), so those recalls
   were returning something. Only `scheduler` is genuinely empty: 7 recalls, not 97. What
   is wasted is the embedding call and the vector search, never the profile fetch. The skip
   must therefore be scoped to those two steps — skipping the whole hook would also stop
   the extraction enqueue at `MemoryRecallHook.cs:106`, so a user with no memories would
   never get any, and the skip would latch on permanently.
2. **Keep the HTTP connection warm.** A cold-connection OpenRouter call measured ~620 ms
   against ~361 ms warm. The gap is TCP + TLS setup, and with only ~35 turns a day the
   connection is almost never warm.

   **This is wider than the embedding call.** `OpenRouterChatClient` already shares one
   `SocketsHttpHandler` process-wide, and the comment at `OpenRouterChatClient.cs:249`
   says it exists precisely to avoid a fresh handshake per conversation. But it sets
   `PooledConnectionLifetime = TimeSpan.FromMinutes(2)` (line 251), and
   `SocketsHttpHandler` defaults `PooledConnectionIdleTimeout` to 1 minute. At ~35 turns a
   day every connection is dead before the next turn, so the LLM calls pay the same ~230 ms
   handshake. `LlmFirstToken` is p50 2070 ms.

Recommended target model if we go local: **`Qwen3-Embedding-0.6B-GGUF`** (1024 dims,
measured 17.5 ms median) for quality, or `nomic-embed-text-v1-GGUF` (768 dims, measured
5.9 ms median) for minimum footprint. Both are already in the shipped Lemonade catalog.

Cost of the move is small **right now** and grows over time: the entire memory corpus in
prod is **12 memories**. Re-embedding 12 items is nothing. In a year it will not be.

---

## 1. Does Lemonade expose an OpenAI-compatible embeddings endpoint?

Yes, and I verified it on the running server, not just in docs.

- Documented as `POST /v1/embeddings`; all four prefixes (`/api/v0/`, `/api/v1/`, `/v0/`,
  `/v1/`) are live —
  <https://lemonade-server.ai/docs/api/openai/#post-v1embeddings>, route registration in
  <https://github.com/lemonade-sdk/lemonade/blob/main/src/cpp/server/server.cpp> (L1004-1010,
  L1085).
- Introduced in **v8.0.4, 2025-07-09**: "Added `reranking` and `embeddings` support to
  Lemonade Server" — <https://github.com/lemonade-sdk/lemonade/releases/tag/v8.0.4>.
  We pin **v11.0.0** (`DockerCompose/lemonade/Dockerfile:5`), which is well past that.
- **Live proof from our own prod container**: `POST http://127.0.0.1:13305/api/v1/embeddings`
  with no model returns `{"error":{"message":"Invalid request: No model specified in
  request","type":"invalid_request"}}` — a structured handler response, not a 404. The
  server's `/api/v1/health` also reports `"max_models":{"embedding":1,...,"reranking":1,...}`
  and `"pinned_models":{"embedding":0,...}`, so embeddings are a first-class model type in
  the build we run.

Request and response shape match what `OpenRouterEmbeddingService` already sends and parses
(`Infrastructure/Memory/OpenRouterEmbeddingService.cs:42-59`): `model` + `input` (string or
array of strings), response `{"object":"list","data":[{"index":..,"embedding":[...]}],
"model":..,"usage":..}` — <https://lemonade-server.ai/docs/api/openai/#post-v1embeddings>.
Verified live: our benchmark got back `{'data','model','object','usage'}` and a float array.

Two limits worth knowing:

- **Backends**: only the `llamacpp` and `flm` recipes implement embeddings. ONNX/OGA does
  not. "This endpoint is only available for models using the `llamacpp` or `flm` recipes"
  — same docs page. Within `llamacpp`, every device works (cpu, vulkan, rocm, cuda,
  metal) — <https://github.com/lemonade-sdk/lemonade/blob/main/docs/dev/backends-reference.md>.
  On our box the model auto-loaded on **`device: gpu`** (Vulkan on the Radeon 890M) with no
  configuration; confirmed in `/api/v1/health` during the benchmark.
- **No `dimensions` parameter.** It is absent from the documented parameter table and from
  `server.cpp`; the handler forwards the raw body to llama-server
  (<https://github.com/lemonade-sdk/lemonade/blob/main/src/cpp/server/backends/llamacpp/llamacpp_server.cpp>
  L643-645). OpenRouter *does* support `dimensions`
  (<https://openrouter.ai/docs/api-reference/embeddings/create-embeddings>). So we cannot ask a
  local model for a 1536-wide vector to keep the current index. Matryoshka truncation would
  have to be done in our own code.

There is also a reranking endpoint (`POST /api/v1/rerank`, llamacpp only) with two shipped
rerankers — <https://lemonade-server.ai/docs/api/llamacpp/#post-v1rerank>. Out of scope here,
but it is there if recall precision ever matters more than recall latency.

NPU note: NPU embeddings exist only through the `flm` (FastFlowLM) backend, and Lemonade's
Docker install docs document no NPU device passthrough
(<https://lemonade-server.ai/docs/guide/install/docker/>). Our compose maps only `/dev/dri`
and comments that the NPU node is an opt-in override
(`DockerCompose/docker-compose.yml:604-611`). So for us "local" means iGPU or CPU, not NPU.

## 2. Which models, which dimensions, and what does that cost in quality?

The five embedding models in the registry of the exact v11.0.0 image we run
(read out of `/opt/lemonade/resources/server_models.json` in the prod container; same file
as <https://github.com/lemonade-sdk/lemonade/blob/main/src/cpp/resources/server_models.json>):

| Lemonade model | Quant | Size GB | Dims | Recipe |
| --- | --- | --- | --- | --- |
| `nomic-embed-text-v1-GGUF` | Q4_K_S | 0.078 | 768 | llamacpp |
| `nomic-embed-text-v2-moe-GGUF` | Q8_0 | 0.51 | 768 | llamacpp |
| `Qwen3-Embedding-0.6B-GGUF` | Q8_0 | 0.64 | 1024 | llamacpp |
| `Qwen3-Embedding-4B-GGUF` | Q8_0 | 4.28 | 2560 | llamacpp |
| `Qwen3-Embedding-8B-GGUF` | Q8_0 | 8.05 | 4096 | llamacpp |

Dimensions confirmed live for the two I benchmarked: `nomic-embed-text-v1-GGUF` returned
**768** floats, `Qwen3-Embedding-0.6B-GGUF` returned **1024** floats.

**None of them is 1536.** So the Redis index has to be rebuilt whichever one we pick.

Retrieval quality, from model cards and vendor pages:

| Model | Dims | MTEB | Source |
| --- | --- | --- | --- |
| `text-embedding-3-small` (today) | 1536 | 62.3 avg | <https://developers.openai.com/api/docs/guides/embeddings> |
| `nomic-embed-text-v1.5` | 768 | 62.28 avg | <https://huggingface.co/nomic-ai/nomic-embed-text-v1.5> |
| `Qwen3-Embedding-0.6B` | up to 1024 | 64.33 MTEB-Multilingual, 70.70 MTEB-Eng v2 | <https://huggingface.co/Qwen/Qwen3-Embedding-0.6B> |

Caveats on that table, stated plainly:

- The 62.28 figure is for `nomic-embed-text-v1.5`. Lemonade ships **v1**, not v1.5. I did
  not find an MTEB average on the v1 card, so treat v1 as "around v1.5, probably a little
  below" — that is an estimate, not a source.
- Qwen3's 70.70 is on MTEB-Eng v2, a different revision from the 56/58-dataset average
  OpenAI's 62.3 refers to. The two numbers are **not directly comparable**. What is fair to
  say: Qwen3-Embedding-0.6B is a 2025-era model that reports top-of-leaderboard scores, and
  `text-embedding-3-small` is a 2024 model. There is no evidence the local option is worse.
- The live MTEB leaderboard Space is a Gradio app that could not be fetched, so all scores
  come from model cards and vendor docs.

**Language matters more here than MTEB average.** Our voice traffic is Spanish
(`DockerCompose/docker-compose.yml:591-598` sets a Castilian whisper prompt). Of the
options, `Qwen3-Embedding-0.6B` and `nomic-embed-text-v2-moe` are explicitly multilingual;
`nomic-embed-text-v1` is English-first. That pushes toward Qwen3-0.6B.

## 3. What is the actual latency delta?

All numbers in this section are **measured**, on prod, unless labelled otherwise.

### OpenRouter today (measured)

Called from the prod host with the agent container's real API key, `text-embedding-3-small`,
short Spanish command strings, 20 requests over one keep-alive HTTPS connection:

| | ms |
| --- | --- |
| mean | 419 |
| p50 | **361** |
| p90 | 540 |
| min | 217 |
| max | 1618 |

Cold connection (fresh TCP + TLS each time, 6 samples): time-to-first-byte
**585, 593, 594, 617, 619, 763 ms**. Of that, TCP connect took ~215 ms and the TLS
handshake completed at ~230 ms.

Plain ICMP RTT to `openrouter.ai` from prod: **13.3 ms avg** (5 packets, min 11.4, max 15.0).

That is the important shape: **network RTT to OpenRouter's edge is ~13 ms. The other
~350 ms is upstream.** The response body reports `"provider": "OpenAI"`, so OpenRouter is
proxying to OpenAI and we are paying OpenAI's queue plus a second hop. Moving to a
"nearer/faster provider" cannot recover most of this, because the distance is not the
problem.

### Lemonade locally (measured, same box)

`nomic-embed-text-v1-GGUF`, auto-loaded on the iGPU, 30 warm requests:

| | ms |
| --- | --- |
| mean | 6.9 |
| p50 | **5.9** |
| p90 | 6.3 |
| max | 36.1 (first request after load) |

Three-turn recall window (107 chars, the shape `BuildRecallWindowText` actually produces):
p50 **8.5 ms**, mean 12.6 ms.

`Qwen3-Embedding-0.6B-GGUF`, 30 warm requests: mean 20.7, p50 **17.5**, p90 38.1 ms.
Three-turn window: p50 11.8 ms.

**Cold start (model load on first request): 2657 ms for nomic-v1, 4275 ms for Qwen3-0.6B.**
Model pull (download) took 7.5 s and 8.5 s respectively over our connection.

### The delta

| Path | p50 |
| --- | --- |
| OpenRouter, warm connection | 361 ms |
| OpenRouter, cold connection | ~620 ms |
| Lemonade nomic-v1, warm | 6 ms |
| Lemonade Qwen3-0.6B, warm | 18 ms |

So: **~340-600 ms saved per turn**, depending on whether the OpenRouter connection was warm.

Neither vendor publishes embedding latency numbers. OpenRouter's latency page
(<https://openrouter.ai/docs/guides/best-practices/latency-and-performance>) only says it
runs on Cloudflare Workers at the edge and warns about cold edge caches in the first 1-2
minutes in a new region — no millisecond figure. Lemonade ships a `lemonade bench` CLI with
an `embed` scenario (<https://github.com/lemonade-sdk/lemonade/blob/main/docs/guide/cli.md>)
but publishes no embedding results. Every number above is ours.

## 4. Where does the call sit, and how much of the turn is it?

**It is on the blocking path, and there is exactly one call per user turn.**

The chain:

- `ConversationGroup.BuildUserMessageAsync` awaits the recall hook —
  `Domain/Monitor/ConversationGroup.cs:498`.
- That await is inside `Domain/Monitor/ConversationGroup.cs:448`, which runs **before**
  `await state.Warmup` (line 453) and before `StreamAgentTurn` (line 454). Nothing about
  the turn starts until recall returns.
- Inside the hook, `Infrastructure/Memory/MemoryRecallHook.cs:71` is a single awaited
  `GenerateEmbeddingAsync`. The Redis search and the profile fetch that follow are already
  run in parallel (lines 73-78).

The other three embedding call sites are all off the user-visible path:

- `Infrastructure/Memory/MemoryExtractionWorker.cs:136` — a hosted background service
  (`Agent/Modules/MemoryModule.cs:103`), fed by a queue the recall hook enqueues to.
- `Infrastructure/Memory/MemoryDreamingService.cs:139` — cron, default `0 3 * * *`
  (`Agent/appsettings.json`).
- `Domain/Tools/Memory/MemoryForgetTool.cs:83` — only when the model calls that tool.

### Share of the turn, measured

The repo already decomposes turn latency. `LatencyStage` (`Domain/DTOs/Metrics/Enums/LatencyStage.cs`)
names `SessionWarmup`, `MemoryRecall`, `LlmFirstToken`, `LlmTotal`, `ToolExec`,
`HistoryStore`, `FirstReply`. On the voice side, `Tests/Unit/McpChannelVoice/TurnLatencyDecompositionTests.cs`
pins that `SpeechEndToFirstAudioMs` equals the exact sum of `trailing silence + speaker
verify + STT + AgentRoundTripMs + reply queue wait + TTS`. `MemoryRecall` lives **inside**
`AgentRoundTripMs` — the hub cannot see into it, as `McpChannelVoice/Services/VoiceTurn.cs:203`
notes.

Eight days of prod metrics (2026-08-01 to 08-08, from Redis `metrics:latency:*` and
`metrics:memory-recall:*`):

| Stage | n | mean ms | p50 | p90 |
| --- | --- | --- | --- | --- |
| SessionWarmup | 130 | 90 | 15 | 497 |
| **MemoryRecall** | **273** | **622** | **575** | **800** |
| LlmFirstToken | 291 | 2532 | 2070 | 3735 |
| LlmTotal | 291 | 8678 | 5850 | 19150 |
| ToolExec | 419 | 1217 | 18 | 2209 |
| HistoryStore | 291 | 3 | 2 | 6 |
| FirstReply | 289 | 4278 | 3276 | 7640 |

**MemoryRecall is 17.6% of time-to-first-reply at p50** (575 / 3276), 14.5% at the mean.
On the voice path it is a straight ~575 ms added to `AgentRoundTripMs`, which the test above
proves lands one-for-one in `SpeechEndToFirstAudioMs`.

### What is inside those 575 ms

Measured pieces of the recall stage:

- Redis KNN search over the index: **2.4 ms p50** (FT.SEARCH KNN 10, from the dev box
  including LAN RTT; on-host it is less).
- `LRANGE -200` on the largest live thread (46 messages, 0.1 MB): **11 ms**.
- Redis PING from the dev box: 2.5 ms. `HistoryStore` (a Redis write) is 2 ms p50.

So Redis accounts for roughly 15 ms of the 575. The embedding HTTP call is measured at
361 ms warm and ~620 ms cold. **The recall stage is, essentially, the embedding call.**
The gap between 361 and 575 is best explained by connection setup — measured at ~230 ms —
which is consistent with the traffic pattern: ~35 turns a day means `IHttpClientFactory`'s
default handler lifetime almost always expires between turns. That last step is an
inference from measurements, not itself a measurement.

### The finding that matters most

Recall latency and results broken down by user, same 8 days:

| userId | recalls | zero-result | avg memories | mean ms | p50 ms |
| --- | --- | --- | --- | --- | --- |
| Dethon | 173 | 0 | 8.5 | 653 | 576 |
| **household** | **90** | **90** | **0.0** | 566 | 575 |
| **scheduler** | **7** | **7** | **0.0** | 595 | 595 |
| Tradaly | 3 | 0 | 4.0 | 662 | 540 |

**97 of 273 recalls (36%) returned nothing at all.** The whole memory corpus in prod is
**12 memories** (8 for `Dethon`, 4 for `Tradaly`) plus 4 profile keys — confirmed by
`KEYS memory:*`, which returns 16 keys. `household` — the voice satellite identity used when
speaker verification does not resolve a person — has **zero** stored memories and has paid
90 × ~575 ms for that.

## 5. What does the migration cost?

**Vector dimension and index rebuild.** `VectorDimension` is a hardcoded
`private const int` — `Infrastructure/Memory/RedisStackMemoryStore.cs:15` — used to build
the HNSW field at line 278. It is not configurable. `EnsureIndexCreatedAsync` (line 244)
only creates the index when `FT.INFO` throws, so it will happily keep an existing 1536-wide
index and never notice the mismatch. Changing dimension needs: a code change to the const
(or better, make it configurable), an explicit `FT.DROPINDEX`, and a recreate.

**Re-embedding.** Stored vectors are raw FLOAT32 blobs (`VectorSerializer.ToBytes`,
line 342). A 768- or 1024-wide query against 1536-wide stored blobs is simply wrong. Every
stored memory must be re-embedded. **Today that is 12 memories** — a one-off script, seconds
of work. This is the cheapest this migration will ever be.

**Config and DI.** `Agent/Modules/MemoryModule.cs:26-36` builds the embedding client from
the `openRouter` section: base address `openRouter:apiUrl`, a Bearer header, and
`Memory:Embedding:Model`. The class is even named `OpenRouterEmbeddingService`. The wire
format is already plain OpenAI, so the client code itself needs no change — only the base
address, the model name, and dropping the Authorization header. The clean shape is a
`Memory:Embedding:BaseUrl` setting alongside the existing `Memory:Embedding:Model`,
defaulting to the OpenRouter URL. Per `CLAUDE.md`, a generic tunable like that belongs in
`appsettings.json` alone — but the Lemonade host is a topology-dependent value on the
McpChannelVoice side already (`Stt__OpenAi__BaseUrl`), so if the Agent container needs to
reach it the same way, a compose `environment` entry is the honest place.

**Reachability is not a problem.** The `lemonade` service is in the same compose stack, on
the same `jackbot` network, on the same physical host as the agent
(`DockerCompose/docker-compose.yml:574-635`). The voice channel already reaches it at
`http://lemonade:13305/v1` (`McpChannelVoice/Settings/SttSettings.cs:11`,
`TtsSettings.cs:46`). "Local" here really does mean one hop on a Docker bridge — the 6 ms
figure above was measured over loopback on that host, so container-to-container will be
within a millisecond of it.

**New failure mode: Lemonade down or cold.**

- *Down*: `EnsureSuccessStatusCode` throws
  (`Infrastructure/Memory/OpenRouterEmbeddingService.cs:15`), the hook's catch at
  `MemoryRecallHook.cs:123` logs and publishes an `ErrorEvent`, and the turn proceeds with
  no memory context. That is the same degradation we already have when OpenRouter fails, so
  this is not a new class of failure — but it does move the dependency from a service with
  redundancy to one container on one box. Note that if Lemonade is down, voice is already
  down (no STT, no TTS), so for the voice path the blast radius does not grow.
- *Cold*: measured **2.7 s (nomic-v1) to 4.3 s (Qwen3-0.6B)** on the first request after a
  load. Mitigations, all documented:
  - Lemonade has **no idle TTL by default**; a loaded model stays until evicted or the
    server stops — <https://lemonade-server.ai/docs/guide/configuration/multi-model/>.
  - `max_loaded_models` defaults to 1 **per model type**, applied independently. An
    embedding model does not evict whisper or kokoro. Verified live: with the embedding
    model loaded, `/api/v1/health` showed all three resident, and `max_models` reported
    separate slots for `embedding`, `transcription` and `tts`.
  - `POST /api/v1/load` with `"pinned": true` exempts a model from eviction. Verified live
    on our server: `pinned_models.embedding` went to 1.
  - The container entrypoint already pre-pulls the STT model
    (`DockerCompose/docker-compose.yml:587-590`). Adding the embedding model to that warmup
    removes the cold start entirely.
  - The **opt-in** `auto_evict` (default off) has a two-stage idle policy and an eviction
    score that favours evicting fast-loading models first — that is exactly a small
    embedding model, so if we ever turn `auto_evict` on we must pin the embedding model.

**Contention.** The model auto-loaded on `device: gpu`, sharing the Radeon 890M with
whisper (`device: gpu`, Vulkan). A 6 ms embedding is not going to disturb a whisper decode
in any way we could measure, but it is worth knowing that the two now share the iGPU. Pinning
the embedding model to CPU is an option if it ever matters — the host has 24 threads.

## 6. Cheaper alternatives that get some or all of the same win

Ordered by value per unit of effort.

1. **Skip the call when there is nothing to recall.** 36% of measured recalls searched an
   empty per-user corpus and paid ~575 ms for the privilege. A cached per-user "has any
   memories" check (Redis `EXISTS` on a per-user counter, or a small cache refreshed when
   extraction stores something) turns those into ~0 ms. This is the single largest win
   available and it does not touch the vector index, the model, or the wire format. It also
   composes with the local move rather than competing with it.
2. **Keep the HTTP connection warm.** Measured gap between cold and warm OpenRouter calls
   is ~260 ms. `MemoryModule.cs:26` uses `AddHttpClient`, whose handler lifetime defaults to
   2 minutes; at ~35 turns a day the pool is essentially always cold. Setting
   `SetHandlerLifetime` / `PooledConnectionLifetime` longer, or issuing a periodic keep-alive
   request, recovers most of the difference. Cheap, low risk, and it keeps working as a
   fallback if the local path is ever disabled.
3. ~~**Overlap recall with session warmup.**~~ **Wrong, already implemented.** Warmup does
   not start at `ConversationGroup.cs:453`. It starts at `ConversationGroup.cs:414`, inside
   `EnsureEstablishedAsync`, and is deliberately not awaited there. The comment on lines
   410-413 says it is left running "so it overlaps the turn-start announce and memory
   recall", and line 453 only awaits whatever is left. Warmup also runs once per
   conversation group, so on every turn after the group's first it is an already-completed
   task. There is nothing here to recover.
4. **A different OpenRouter model.** OpenRouter's embedding collection lists cheaper and
   newer options — Perplexity Embed V1 0.6B at $0.004/M, BAAI bge-m3 and Qwen3-Embedding-8B
   at $0.01/M (<https://openrouter.ai/collections/embedding-models>). But measured ICMP RTT
   to OpenRouter is 13 ms and the call takes 361 ms, so ~96% of the time is upstream
   compute and queueing, not distance. Switching providers might shave some of it; it
   cannot get near 6 ms.
5. **Batching.** Not applicable. There is exactly one embedding call per turn on the
   blocking path (`MemoryRecallHook.cs:71`). `GenerateEmbeddingsAsync` exists but nothing on
   the critical path uses it.
6. **Caching by input text.** The recall input is a 3-turn user window
   (`MemoryRecallHook.cs:156-170`), so exact repeats are rare. Low value.
7. **Running recall concurrently with the LLM call.** Not possible as designed — the recall
   result is attached to the message that is handed to the model
   (`MemoryRecallHook.cs:85`). It would require a restructure where memory arrives as a
   second turn or a tool result.

---

## 7. Follow-up investigations, 2026-08-08

Two further read-only passes over prod, run after the design discussion. Both issued read
commands only.

### Redis state and the index

- 16 `memory:*` keys. Memory entries: 8 `Dethon`, 4 `Tradaly`, **0 `household`**,
  **0 `GameDirector`**. `scheduler` has nothing at all.
- 4 personality profiles: `Dethon` (1937 B), `Tradaly` (1857 B), `household` (701 B),
  `GameDirector` (1240 B). Profiles are plain strings, memories are hashes.
- `FT.INFO idx:memories`: HNSW, `M 16`, `ef_construction 200`, FLOAT32, **DIM 1536**,
  COSINE, `num_docs 12`, `hash_indexing_failures 0`.
- **Index drift.** The live index carries a `supersededById` TAG field that
  `CreateIndexAsync` no longer creates; it was removed in commit `4fc834af` and the index
  was never recreated, because `EnsureIndexCreatedAsync` only calls `FT.CREATE` when
  `FT.INFO` throws. So the code and the live index already disagree. A startup mismatch
  check must compare the **vector field's `DIM` only**, or it would fire on day one. No
  document carries the field (verified 12/12), so a rebuild that drops it loses nothing.
- **Blast radius of a rebuild is small.** `FT._LIST` returns exactly one index.
  `FT.DROPINDEX` *without* `DD` leaves all 12 hashes and their embeddings intact, so the
  source data survives the migration. `SpeakerProfileStore` turned out to be
  filesystem-only (`profile.json` beside the WAVs), with no Redis exposure at all.
- The index prefix is `memory:`, so it also matches the four `memory:profile:*` keys. They
  are strings and the index is `ON HASH`, so they are not indexed and cause no failures.

### Extraction is healthy, and the yield is 0.53%

274 extraction events over 2026-08-01 to 08-08, and `ZCARD metrics:memory-extraction:<d>`
equals `ZCARD metrics:memory-recall:<d>` on **all eight days**. `MemoryExtractionQueue` is
an unbounded `Channel`, so nothing was dropped. Every event carries `candidateCount: 0,
storedCount: 0` — for **every** user, not only `household`.

The decisive evidence that the LLM call really happens: token records for the extraction
model match the extraction events second-for-second, at 1414-1546 tokens in and **13-14
tokens out**, which is the length of `{"candidates":[]}`. On the one day an extraction did
produce a candidate the same record reads 1511 in, 75 out. There are zero
`Service="memory"` errors from extraction in any retained history.

The content explains it. Real `household` turns are `"¿Qué hora es?"`, `"Pon el aire
acondicionado a 25 grados."`, `"¿Qué era eso?"`. `Domain/Prompts/MemoryPrompts.cs:27` says
verbatim that a question is never a memory, that one-off requests do not qualify, and that
most messages producing zero memories is the correct outcome. Zero is the specified answer.

Across all retained history: **6 candidates from 1128 extractions, 0.53%**. Each of those
1128 turns spent ~1500 input tokens and a median 828 ms of background LLM work. Recorded as
a finding, not acted on: whether the prompt bar sits where it should is a product judgement
about what is worth remembering, and moving it mid-migration would make the before/after
measurement unreadable.

### Bug: a personality profile outlives the memories it was built from

`memory:profile:household` reads `BasedOnMemoryCount: 2`, `Confidence: 0.1`,
`LastUpdated: 2026-07-26`. A successful `memory_forget` ran at 2026-07-27T01:00:02 and
deleted those two memories.

`RedisStackMemoryStore.GetAllUserIdsAsync` (lines 143-157) derives the dreaming user list
by scanning `memory:*:*` and taking `parts[1]`, so a user whose memory entries are all gone
is never enumerated again. Dreaming events for `household` stop on 2026-07-27 and never
resume. The profile is therefore neither refreshed nor deleted, and `MemoryRecallHook` has
been injecting it into every voice turn for 13 days — live threads carry
`"Memories":[],"Profile":{…LastUpdated 2026-07-26…}`. `GameDirector` is the same orphan.

The same scan also reads `memory:profile:X` as a user named `"profile"`, which is
enumerated and then presumably errors into the swallow at `MemoryDreamingService.cs:61`.

This is worth more than the latency work. The user asked the agent to forget, and an
artifact derived from those memories survived the forget and is still being read to the
model. A fix touches `GetAllUserIdsAsync` (enumerate profile keys, stop mis-parsing them)
and `MemoryDreamingService.RunDreamingForUserAsync` (clear a profile whose user has no
active memories, rather than skipping that user).

### A Lemonade testcontainer fixture already exists

`Tests/Integration/Fixtures/LemonadeFixture.cs` runs the real Lemonade image as a
testcontainer, forcing `STT_BACKEND=cpu` so no `/dev/dri` passthrough is needed, and
exposes `BaseUrl` with the `/v1` suffix already appended. Its comment states it pins the
contract rather than GPU throughput. It gates on `LocateProvisionedVolumes`, which requires
the whisper and Kokoro snapshots under `DockerCompose/volumes/lemonade-hf-cache`. An
embedding contract test needs the Qwen3 GGUF in that same cache, which the entrypoint
pre-pull change adds anyway. So "Lemonade needs a GPU, so it cannot be tested" is false.

---

## What I could not verify

- **MTEB numbers for the exact shipped model.** Lemonade ships `nomic-embed-text-v1`; the
  62.28 MTEB average I found is for `nomic-embed-text-v1.5`
  (<https://huggingface.co/nomic-ai/nomic-embed-text-v1.5>). I did not find an MTEB average
  on the v1 card.
- **A like-for-like quality comparison.** OpenAI's 62.3 for `text-embedding-3-small`
  (<https://developers.openai.com/api/docs/guides/embeddings>) and Qwen3-Embedding-0.6B's
  70.70 (<https://huggingface.co/Qwen/Qwen3-Embedding-0.6B>) are on different MTEB
  revisions and different task sets. They are not comparable. The live MTEB leaderboard
  Space is a Gradio app and could not be fetched.
- **Spanish retrieval quality**, which is what actually matters for our voice traffic. No
  source I found benchmarks these models on Spanish semantic search of short personal facts.
  The only honest test is to re-embed the 12 memories both ways and compare what recall
  returns. That is a cheap experiment and it should happen before committing.
- **Whether the 214 ms gap between the measured warm OpenRouter call (361 ms) and the
  measured recall stage (575 ms) is really connection setup.** Redis accounts for ~15 ms of
  it, and cold-connection setup measured ~230 ms, which fits — but I did not instrument the
  embedding call separately inside the hook to prove it. If someone wants certainty, add a
  latency scope around `MemoryRecallHook.cs:71`.
- **Whether OpenRouter's embeddings endpoint is formally GA.** No beta or preview label
  appears on <https://openrouter.ai/docs/api_reference/embeddings>, and it is in all three
  official SDKs, but no page states a status either way.
- **The vector dimension `openai/text-embedding-3-small` returns through OpenRouter is
  documented** — the OpenRouter model page omits it
  (<https://openrouter.ai/openai/text-embedding-3-small>). I confirmed **1536 by calling it**
  from prod, and OpenAI documents 1536 as the default.
- **NPU embeddings on our hardware.** The `flm` backend supports them and Lemonade's Linux
  NPU guide exists, but no FLM embedding model appears in the shipped registry, and
  Lemonade's Docker docs document no NPU passthrough. Untested. The iGPU numbers above are
  fast enough that this does not matter.
- **Load under concurrency.** All Lemonade benchmarks were sequential, at 04:00, with no
  voice traffic. I did not measure an embedding request racing a whisper decode on the same
  iGPU.

## How to reproduce the measurements

- Prod metrics: Redis at `192.168.5.45:6379`, no auth. `ZRANGE metrics:latency:<yyyy-MM-dd> 0 -1`
  and `ZRANGE metrics:memory-recall:<yyyy-MM-dd> 0 -1`; both are JSON per member. Key layout
  is in `Observability/Services/MetricsCollectorService.cs:247-283`.
- OpenRouter timing: from the prod host, key via
  `docker inspect agent --format '{{range .Config.Env}}{{println .}}{{end}}' | grep OPENROUTER__APIKEY`,
  then a `http.client.HTTPSConnection` kept open across 20 POSTs to
  `/api/v1/embeddings`.
- Lemonade timing: `POST http://127.0.0.1:13305/api/v1/pull {"model_name": "..."}`, then
  timed POSTs to `/api/v1/embeddings`. Clean up with `/api/v1/unload` then `/api/v1/delete`.
  Check `/api/v1/health` before and after.
