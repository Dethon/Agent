# 0019 — Recall embeds locally, with no cross-provider fallback

Status: accepted
Date: 2026-08-08

## Context

Every turn waits on memory recall before anything else happens. Over eight days of production
traffic that wait was a median 575 ms against a median 3276 ms to first reply — roughly 18% of
the time a person waits to hear anything back. On the voice path it is worse than a percentage
suggests: recall sits inside the agent round trip, so it lands one-for-one in the silence
between someone finishing speaking and hearing the first audio.

Almost all of that wait was one HTTP round trip to a hosted embedding API. Redis accounted for
about 15 ms. The hosted call itself measured a median 361 ms warm and about 620 ms cold, while
the network round trip to the provider's edge measured 13 ms. The remaining ~350 ms is upstream
compute and queueing, so a nearer or cheaper hosted provider could not recover it.

The stack already runs a Lemonade server for speech recognition and synthesis, one hop away on
the same Docker network. The same embedding measured 18 ms there.

## Decision

**Recall embeds against the local Lemonade server, and there is no fallback to a hosted
provider.**

The model is `Qwen3-Embedding-0.6B`, which produces 1024-dimension vectors. It was chosen over
the smaller `nomic-embed-text-v1` on language coverage, not speed: production voice traffic is
Spanish, and of the models in Lemonade's registry only the Qwen and nomic-v2-moe models claim
multilingual coverage. The measured 12 ms between the two candidates is immaterial against a
multi-second time to first reply, so retrieval quality decided and speed did not.

The index dimension follows the model rather than the reverse. None of Lemonade's embedding
models produces 1536-wide vectors, so the index had to be rebuilt whichever one was picked. The
dimension therefore became configuration rather than a constant in the store, and a startup
check refuses to boot when it disagrees with the live index.

**The absence of a fallback is the decision, not an omission.** Falling back to the hosted
provider on a local failure is not available at any price: its vectors are 1536 wide against a
1024-wide index, so a query built from one is invalid rather than merely slower, and every
search would error. A local failure degrades to a turn with no recall block — which is exactly
what already happened when the hosted provider failed — published under its own
`memory-embedding` error metric so an outage is distinguishable from a recall that simply found
nothing.

## Considered options

**A different hosted provider.** OpenRouter lists cheaper and newer embedding models. Rejected
on measurement: the round trip to the provider's edge is 13 ms out of a 361 ms call, so ~96% of
the cost is upstream compute and queueing. No hosted provider gets near 18 ms.

**Keep the hosted provider and only warm the connection.** Connection setup measured ~230 ms of
the gap between the 575 ms stage and the 361 ms call. Worth doing, and done, but it leaves the
361 ms. It helps the LLM call more than the embedding call in absolute terms, because the LLM
call happens on every turn regardless.

**`nomic-embed-text-v1`, the smaller local model.** 768 dimensions, 6 ms against Qwen's 18 ms,
and a tenth of the disk. Rejected because it is English-first, and the traffic that this work
most affects — voice — is Spanish.

**Run a quality A/B before committing.** Rejected. Published benchmark numbers for the
candidates are on different MTEB revisions and none covers Spanish retrieval of short personal
facts, so the comparison would not have been decisive. The production corpus is 12 memories, so
a bad outcome is a script that runs in seconds.

## Consequences

- Memory now depends on one container on one box rather than a service with redundancy. The
  blast radius does not grow for the voice path: if Lemonade is down there is no speech
  recognition and no speech synthesis, so there is no voice turn left to degrade.
- A first request would pay a model load measured at several seconds. The Lemonade entrypoint
  pre-pulls the embedding model and loads it pinned — pinned specifically because the eviction
  score favours dropping fast-loading models, which is what a small embedding model is. Its
  loaded-model limit is per model type, so this displaces neither speech model.
- The index was rebuilt in place: dropped without deleting documents, vectors regenerated, index
  recreated at 1024. Every stored memory hash survived, so a failed migration was recoverable by
  re-running it.
- Changing the embedding model from here is a configuration change plus a migration, not an edit
  inside the store. That gets more expensive as the corpus grows, which is part of why the
  dimension became configuration now rather than later.
- Nobody should add a hosted fallback later. It would return vectors of the wrong width, and the
  failure it produces — every search erroring, swallowed by recall's catch-all — is silent.
