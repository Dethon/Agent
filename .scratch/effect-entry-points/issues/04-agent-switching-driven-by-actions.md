# 04 — Drive agent switching from actions

**What to build:** switching agents becomes something a test can trigger by dispatching an action. Today the effect subscribes to the topics store's observable and compares each emission's selected agent against a private field, so reaching its behaviour means building the store, dispatching enough actions to move that field twice, and guessing when the resulting work finished.

Replace the subscription with two action registrations, both routing to one public awaitable method that takes the new agent id. The method keeps the previous-agent field as its guard, so the first selection during startup is still skipped and first-load topic loading stays with the initialization effect. Without that guard, first load fetches every topic twice.

Register for the set-agents action as well as the select-agent one. The topics reducer clears the selected agent when a refreshed catalog no longer contains it, and the hub dispatches that action whenever the agent re-registers its catalog. The observable subscription sees that clearing today; an effect registered only for select-agent would silently stop reacting when an agent is removed mid-session. Both registrations read the selected agent from the store after it has reduced, which is safe because stores are constructed before effects.

**Blocked by:** 01 (fault logging).

**Status:** done

- [x] Agent-change work is reachable by calling a public method with an agent id and awaiting it.
- [x] The first agent selection after construction loads nothing.
- [x] A second selection for a different agent clears the chat session, persists the choice, and reloads that agent's topics.
- [x] Selecting the agent that is already selected does nothing.
- [x] A refreshed agent catalog that drops the selected agent reaches the same path and empties the topic list.
- [x] Topics that are mid-stream keep their local messages rather than reloading history.
- [x] The store subscription and the disposable it produced are gone. (The effect still implements `IDisposable`: it now disposes its two handler registrations instead, which is what `SpaceEffect` and `AgentActivityEffect` already do.)
- [x] A fault in the agent-change work is logged rather than discarded.
