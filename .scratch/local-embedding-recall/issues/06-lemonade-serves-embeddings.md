# 06 — Lemonade serves embeddings, in the stack and in tests

**What to build:** The Lemonade server already in the stack can serve embeddings but has no
embedding model loaded. A first request would pay a model load measured at several seconds,
which is worse than the hosted call this work is replacing.

Add the embedding model to the entrypoint's existing pre-pull step and pin it so it cannot
be evicted, so no user turn ever pays a cold load. Lemonade applies its loaded-model limit
per model type, so an embedding model does not displace speech recognition or synthesis,
but pinning matters because the eviction score specifically favours dropping fast-loading
models, which is exactly what a small embedding model is.

Then extend the integration test fixture's precondition to require the embedding model
alongside the speech models it already requires, so a test can prove the real server
answers the shape the client sends. The fixture forces the CPU backend and needs no GPU
passthrough, so this runs anywhere Docker does.

**Blocked by:** None — can start immediately.

**Status:** ready-for-agent

- [ ] The embedding model is pulled during container start, never on first request
- [ ] The model is pinned and survives eviction pressure
- [ ] Pinning it does not displace speech recognition or synthesis
- [ ] The test fixture requires the embedding model in its provisioned cache and skips with
      a clear reason when it is absent
- [ ] A contract test proves the real server answers the request and response shape the
      client sends, including the vector width
- [ ] That test runs without GPU passthrough
