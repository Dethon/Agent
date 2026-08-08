# Cut memory recall latency: local embeddings, warm connections, no search for unremembered users

Type: spec
Status: ready-for-agent

## Problem Statement

Every turn waits on memory recall before anything else happens. Measured over eight days of
production traffic, that wait is a median 575 ms, against a median 3276 ms to first reply.
It is roughly 18% of the time a person waits to hear anything back.

The wait is almost entirely one HTTP round trip to a hosted embedding API. Redis accounts
for about 15 ms of it. The hosted call itself measures a median 361 ms on a warm connection
and about 620 ms on a cold one, while the network round trip to the provider's edge is only
13 ms. The remaining ~350 ms is upstream compute and queueing, so moving to a nearer or
cheaper hosted provider cannot recover it.

On the voice path this is worse than a percentage suggests. Memory recall sits inside the
agent round trip, which lands one-for-one in the time between someone finishing speaking
and hearing the first audio back. Half a second of silence is the difference between an
assistant that feels immediate and one that feels like it is thinking.

Two smaller problems compound it. First, connections to the hosted provider are essentially
never warm: the shared connection pool caps connection lifetime at two minutes and the idle
timeout defaults to one, while real traffic is about 35 turns a day. Every turn therefore
pays a TCP and TLS handshake measured at ~230 ms, and it pays it on the LLM call too, not
only on the embedding call. Second, users with nothing stored still pay the full round trip
to search an empty set.

## Solution

Run the recall embedding on the Lemonade server that is already part of the stack, one hop
away on the same Docker network. The same work measures 18 ms there against 361 ms hosted.

Keep connections to the hosted provider warm, so the turns that still cross the network
stop paying a handshake. This helps the LLM call more than the embedding call in absolute
terms, because the LLM call happens on every turn regardless.

Stop embedding and searching at all for an **unremembered user**, meaning one with no
memory entries stored. Recall still runs for them: it still fetches the personality profile
and it still enqueues the turn for extraction. It simply has nothing to search.

A person should notice only that replies start sooner. Nothing about what the agent
remembers, or how it behaves when it remembers nothing, should change.

## User Stories

1. As someone talking to the voice satellite, I want the assistant to start answering
   sooner after I stop speaking, so that the conversation feels like a conversation rather
   than a request and a wait.
2. As someone typing in WebChat, I want the first token of a reply to arrive sooner, so
   that I can tell my message was received without watching a spinner.
3. As someone whose memories are stored, I want recall to keep returning the same facts it
   returned before, so that a change made for speed does not quietly make the assistant
   forget things.
4. As someone speaking Spanish, I want recalled memories to be as relevant as they were on
   the hosted model, so that a change of embedding model does not degrade what the
   assistant knows about me.
5. As a user of the voice satellite identity, which has no memories stored, I want turns
   not to spend half a second searching an empty set, so that the least personalised path
   is not also the slowest.
6. As someone who has never spoken to the agent before, I want my first turns to be fast,
   so that having no history is not a penalty.
7. As someone who has just had their first memory stored, I want recall to start searching
   immediately on the next turn, so that the optimisation for unremembered users does not
   latch on and stop me ever getting memories back.
8. As someone whose memories were all forgotten, I want recall to go back to the fast
   unremembered path, so that the state is derived from what is stored rather than from
   what was once true.
9. As someone using the agent, I want a personality profile to keep being applied even when
   I have no individual memories, so that speeding up recall does not strip the agent's
   sense of how I like to be spoken to.
10. As someone using the agent, I want every turn to still be considered for memory
    extraction, so that an optimisation on the read path does not disable the write path.
11. As an operator, I want the agent to refuse to start when its configured vector
    dimension disagrees with the live index, so that a mismatch is a loud failure at boot
    rather than memory silently returning nothing forever.
12. As an operator, I want the embedding model loaded and pinned before any user turn
    arrives, so that nobody pays the several-second cost of a cold model load.
13. As an operator, I want a Lemonade outage to degrade to a turn without memory rather
    than a failed turn, so that the assistant keeps working when its memory does not.
14. As an operator, I want a Lemonade outage to be visible in metrics, so that memory
    quietly returning nothing is something I find out about rather than discover months
    later.
15. As an operator, I want the embedding call timed separately from the rest of the recall
    stage, so that I can prove the change did what it claimed rather than infer it.
16. As an operator, I want connections to the hosted provider to stay warm, so that the
    LLM call stops paying a handshake on every turn at our traffic volume.
17. As an operator, I want a failing keep-alive to publish a metric, so that the connection
    pool going cold again is detectable rather than a silent regression.
18. As an operator, I want the keep-alive to cost nothing, so that keeping a connection
    open does not show up on the bill.
19. As an operator, I want the migration to leave the stored memory hashes untouched, so
    that a failed migration is recoverable by re-running it rather than by restoring a
    backup.
20. As an operator, I want to re-run the re-embedding step after deploying, so that any
    memory written during the migration window is not left unsearchable.
21. As a developer, I want the embedding client to carry a provider-neutral name, so that
    the code does not claim to talk to a service it no longer talks to.
22. As a developer, I want the embedding base address and model to be ordinary
    configuration, so that pointing recall at a different server does not need a code
    change.
23. As a developer, I want the vector dimension to come from configuration rather than a
    constant, so that changing the embedding model is a configuration change plus a
    migration rather than an edit in the middle of a store.
24. As a developer, I want a test that runs the real embedding wire format against a real
    Lemonade, so that a change in the server's request or response shape is caught by the
    test suite rather than in production.
25. As a developer, I want a test that proves recall skips the embedding call for an
    unremembered user while still attaching a profile and still enqueueing extraction, so
    that the three behaviours cannot drift apart.
26. As a developer, I want a test that points the store at a wrong-dimension index and
    asserts a loud failure, so that the guard we are adding is itself guarded.
27. As a developer, I want the tests to run on a machine without a GPU, so that memory work
    is not gated on having the right hardware.
28. As someone reading the repository later, I want a record of why embeddings are local
    and why there is no cross-provider fallback, so that the absence of a fallback reads as
    a decision rather than an oversight.

## Implementation Decisions

**Embedding provider and model.** Recall embeddings are generated by the Lemonade server
already running in the stack, using `Qwen3-Embedding-0.6B`, which produces 1024-dimension
vectors. It was chosen over the smaller `nomic-embed-text-v1` because production voice
traffic is Spanish and only the Qwen and nomic-v2-moe models claim multilingual coverage.
The measured 12 ms difference between the two candidates is immaterial against a
multi-second time to first reply, so retrieval quality decides and speed does not.

No quality A/B is run before committing. Published benchmark numbers for these models are
on different MTEB revisions and none covers Spanish retrieval of short personal facts, so a
comparison would not be decisive, and the corpus is small enough that a bad outcome is
cheap to reverse.

**The embedding client does not change shape.** It already speaks plain OpenAI-compatible
JSON, which is exactly what Lemonade serves, so only its configuration changes. It is
renamed to something provider-neutral, takes its base address from the memory embedding
configuration rather than from the hosted provider's section, and sends no authorization
header when pointed at the local server.

**No cross-provider fallback.** Falling back to the hosted provider on a local failure is
not available: it returns 1536-wide vectors, which are invalid against a 1024-wide index
rather than merely slower. A local failure therefore degrades to a turn with no recall
block, which is the behaviour that already exists when the hosted provider fails. That
degradation is made observable with its own metric rather than only a swallowed log line.

The blast radius does not grow for the voice path. If Lemonade is down there is no speech
recognition and no speech synthesis, so there is no voice turn left to degrade.

**Vector dimension becomes configuration.** It is currently a constant in the Redis memory
store, and the store only creates an index when reading its definition fails, so a live
index of the wrong width is kept and never noticed. The dimension moves to configuration,
and a startup check compares the configured value against the live index's vector field.
A mismatch refuses to start.

The check compares the **vector field's dimension only**, not the whole schema. The live
production index carries a tag field that the current code no longer creates, left behind
when a superseding feature was removed, so a full schema comparison would fail on day one
against a perfectly healthy index.

The check runs at startup rather than lazily on first recall, because a lazy check would be
swallowed by the recall hook's catch-all and produce exactly the silent failure the check
exists to prevent.

**Cold starts are removed rather than tolerated.** A first-request model load measures
several seconds. The embedding model is added to the Lemonade entrypoint's existing
pre-pull step and pinned so it cannot be evicted. Lemonade applies its loaded-model limit
per model type, so an embedding model does not displace speech recognition or synthesis.

**Connection warmth covers both paths.** The pooled connection lifetime is raised and an
explicit idle timeout is set, both on the shared handler used by every hosted chat client
and on the embedding client. In addition, a keep-alive fires on an interval below the idle
timeout against a zero-cost endpoint on the hosted provider, so a connection survives the
long gaps between turns. It publishes a metric on failure, so the pool going cold again is
visible rather than silent. The keep-alive must not be a completion request: it would cost
money on every fire for no user-visible work.

The local path needs no keep-alive. It is plain HTTP on a Docker bridge with no TLS
handshake to amortise.

**Unremembered users.** Recall skips the embedding call and the vector search when the user
has no memory entries. It does not skip the hook. The profile fetch still runs, because a
user can have a personality profile and no memories, and the extraction enqueue still runs,
because skipping it would mean a user with no memories could never acquire any and the
optimisation would latch on permanently. The emptiness check is a Redis query rather than
cached state, so a first stored memory takes effect on the very next turn with nothing to
invalidate.

This ships after the migration, not before. While recall is hosted it saves ~575 ms on
those turns; once recall is local the same skip saves under 20 ms. It is being taken
anyway, with that reduced value understood.

**Configuration placement.** The embedding base address and model name are generic
tunables and live in application settings alone. The Lemonade service is in the same
compose stack at a stable in-stack address, so it is not a per-deployment value and gets no
compose environment entry.

**Observability.** The embedding call is timed separately from the rest of the recall
stage. Today the stage is measured at 575 ms while the call measures 361 ms, and the
remaining ~214 ms is attributed to connection setup by inference rather than measurement.
Separating them makes the before-and-after provable.

**Migration.** The index is rebuilt live and the new build deployed afterwards. The index
is dropped without deleting documents, so all stored memory hashes survive; the vectors are
regenerated against Lemonade by a one-off script; the index is recreated at the new
dimension. The script is throwaway rather than committed.

During the window between the index being recreated and the new build landing, the old
build is still running. Its recall queries fail and are swallowed, which is harmless, but
its extraction worker can still write a vector at the old width that the new build would
never be able to search. The re-embedding step is therefore run once more after deploying,
to catch any stray.

## Testing Decisions

A good test here asserts what an outside observer can see: whether the model received a
recall block, whether an embedding was requested at all, whether the turn was enqueued for
extraction, and whether the process started. It does not assert call ordering inside the
hook, which methods were invoked, or how the vector was serialised. The repository's
testing rules prefer real dependencies over mocks, and both a Redis fixture and a Lemonade
fixture already exist, so the default is an integration test.

**Primary seam: the memory recall hook, driven against a real Redis and a real Lemonade.**
One seam covers most of the feature: the embedding swap, a genuine 1024-wide vector making
a round trip through the index, and the unremembered-user behaviour. The cases worth
pinning are a user with memories getting them back, an unremembered user getting no
embedding request and no search while still getting a profile and still being enqueued for
extraction, a user acquiring their first memory and being searched on the following turn,
and a Lemonade outage producing a turn with no recall block rather than a failed turn.

Prior art: the existing recall hook integration tests, and the Redis memory store
integration tests, both of which already drive real storage through the Redis fixture.

**Narrow seam: index verification.** The startup check needs an index deliberately created
at the wrong width, which the primary seam cannot produce. Tested against the Redis
fixture: a matching index starts, a mismatched one fails loudly, and an index carrying an
extra field the code no longer creates still starts, since that is the live production
shape.

**Narrow seam: the keep-alive.** Tested with a fake clock and a fake message handler,
asserting it fires on its interval, that it targets a non-billable endpoint, and that a
failure publishes a metric. The repository already uses an injectable time provider for
time-dependent code. Connection pool warmth itself is not directly assertable; the
configured lifetimes are asserted instead, and the shared handler is already exposed
internally for exactly that kind of check.

**Narrow seam: the embedding client wire format.** Unit tests against a fake handler cover
request and response shape, extending the existing mock-based embedding tests. A contract
test against the real Lemonade fixture proves the server actually answers that shape.

The Lemonade fixture currently treats the speech recognition and synthesis model caches as
a hard precondition and skips when they are absent. It needs the embedding model added to
that precondition. This falls out of the entrypoint pre-pull change, which puts the model
in the same cache directory the fixture mounts. The fixture forces the CPU backend and
needs no GPU passthrough, so these tests run anywhere Docker does.

Prior art for a service-contract test that self-skips when the service is unavailable: the
existing hosted embedding integration tests, and the Lemonade quality signal tests.

## Out of Scope

- **The extraction prompt's yield.** Across all retained history, 6 candidates from 1128
  extractions. That is the prompt behaving as written, and whether the bar sits in the
  right place is a product judgement about what is worth remembering. Changing it during
  this work would make the before-and-after measurement unreadable.
- **The orphaned personality profile bug.** Tracked separately under
  `memory-profile-outlives-memories`. It changes what recall returns and should land first,
  but it is a correctness problem with no latency angle.
- **Overlapping recall with session warmup.** Already implemented. Warmup is started
  unawaited when the conversation group is established, specifically so it overlaps recall,
  and it runs once per group rather than once per turn.
- **Reranking.** Lemonade serves a reranking endpoint and ships rerankers. That is a recall
  precision feature, not a latency one.
- **NPU inference.** Only available through a backend with no embedding models in the
  shipped registry, and the integrated GPU numbers are already fast enough that it would
  not matter.
- **Switching hosted embedding providers.** Measured round trip to the provider's edge is
  13 ms out of a 361 ms call, so nearly all of the cost is upstream. No hosted provider
  gets near the local numbers.
- **Batching and caching embeddings.** There is exactly one embedding call per turn on the
  blocking path, and the recall input is a three-turn window, so exact repeats are rare.
- **Running recall concurrently with the LLM call.** The recall block is attached to the
  message handed to the model, so this would require memory to arrive as a separate turn or
  a tool result. A different feature.

## Further Notes

All latency figures come from measurements taken on the production host on 2026-08-08, plus
eight days of production metrics. They are recorded with their method in the research note
alongside this spec, including what is measured, what is vendor-published, and what is
inferred.

The migration is cheap right now and will not stay that way. The entire production corpus
is 12 memories across two users, so re-embedding is a script that runs in seconds. The same
migration in a year is a different piece of work, which is part of why the dimension is
becoming configuration rather than staying a constant.

One number remains unexplained. The recall stage measures 575 ms while the embedding call
measures 361 ms and Redis accounts for about 15 ms. Connection setup measured at ~230 ms
fits the gap, but nobody has instrumented the call separately inside the hook to prove it.
The observability decision above closes this.
