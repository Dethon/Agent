# 03 — Put the first-load sequence under test

**What to build:** the whole first-load ordering becomes assertable without a browser. Today it is 214 lines behind a constructor side effect — connect, subscribe to hub events, register the user, resolve and join the space, load agents, load agent settings, select an agent, load that agent's topics, then load history per topic — and nothing pins any of it. Reordering two of those steps breaks the app and no test notices.

Make the initialization method public and awaitable, and leave the constructor registration as a thin wrapper that attaches the fault logging from ticket 01. The same applies to the user-registration handler in that effect.

Awaiting the method means first load is finished, with one exception: the per-topic history loads are gathered before it returns, so a test can assert on loaded messages, but push subscription stays detached. Awaiting push once stalled the agent list by roughly thirty seconds and the comment explaining that is still in the file. Stream resume inside the per-topic load also stays detached, because a resumed stream is long-lived.

**Blocked by:** 01 (fault logging), 02 (interfaces for config and push).

**Status:** done

- [x] The initialization work is reachable by calling a public method and awaiting it.
- [x] Dispatching the initialize action still runs the same work, and a fault in it is logged.
- [x] A test asserts the order of the first-load steps, not merely that each one happened.
- [x] Awaiting the method means every topic's history is in the store.
- [x] Awaiting the method does not wait on push subscription or on stream resume.
- [x] A space slug the config service does not recognise dispatches the invalid-space action and retries with the fallback slug.
- [x] An empty agent list ends initialization without selecting an agent or loading topics.
- [x] A saved agent id that is no longer in the catalog falls back to the first agent and persists that choice.
- [x] The effect is constructed in the test with no Blazor JS runtime.
