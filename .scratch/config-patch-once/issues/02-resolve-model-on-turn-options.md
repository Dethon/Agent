# 02 — Resolve the patched model on the turn's options, through to the wire

**What to build:** A WebChat user who picks a model for one message gets that model, even when a second message arrives while the first is still being prepared. Today the resolved model is parked on a field belonging to the chat client, which every turn of that agent shares; a second turn can overwrite it between the first turn's write and the outgoing request's read, so the request goes out on the configured model while the metrics stamp reports whichever write won.

The model half of the config patch stops being resolved in the chat client and is resolved where the agent builds its run options — the same place that already resolves the reasoning half. The resolved value travels with that turn's options to the outgoing request, so no per-request value lives on a shared object and the race has nowhere to happen.

Resolution keeps its current semantics exactly. A patch naming the configured model is not an override. A patch naming a model outside the whitelist is refused and the configured model is used. A whitelisted model matched case-insensitively is returned in the whitelist's own casing, because provider model ids are lowercase slugs and echoing the caller's casing turns a valid override into a model-not-found error.

The whitelist of patchable model ids currently reaches the chat client through its constructor. It becomes an agent-side input, which means rewiring it from where the application composes its agents.

The preferred carrier is the chat options' own model-id field. **Whether it survives the inner provider client down to request preparation must be verified against a real captured request body, not assumed.** If it does not survive, carry the model on the options' additional-properties bag and read it per-request in the delegating handler. Under no circumstances store it on client-level state again.

An options-level assertion is not sufficient evidence. It would pass while the wire stayed wrong, which is the exact failure this work exists to end.

Provider routing is untouched. It is enforced on every turn including model-override turns, and a conflict between routing and an override is a configuration error, never a silent drop.

**Blocked by:** None — can start immediately. Independent of 01; this is the ticket that fixes the reported race.

**Status:** done

- [x] A user message carrying a whitelisted model override produces that model in the outgoing request body, asserted against parsed JSON captured from a real transport handler, with a real agent over a real chat client.
- [x] Deleting the line that applies the patch to the turn's options fails that test.
- [x] The whitelist rules are asserted through the options the agent produces: whitelisted override applied, non-whitelisted refused, patch matching the configured model treated as no override, absent patch, case-insensitive match returning the whitelist's canonical casing.
- [x] The standalone test file for the resolution helper is deleted once its cases are re-expressed at the agent seam.
- [x] The mutable override holder and its volatile field are gone.
- [x] The effective-model report derives from the turn's options rather than ambient state.
- [x] Latency and token-usage events stamp the model the measured request actually used; the existing tests covering that attribution still pass.
- [x] The whitelist reaches the agent from application composition, and no chat-client constructor still takes it.
- [x] A turn with no patch is unchanged: the configured model governs and no model is stamped that was not stamped before.
- [x] Nothing in the request body's provider-routing node changes.
