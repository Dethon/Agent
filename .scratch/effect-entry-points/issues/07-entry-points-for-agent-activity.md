# 07 — Entry points for agent activity

**What to build:** the two action-driven parts of the agent-activity effect become awaitable and testable. Loading the catalog maps every agent's topics so unseen-activity badges can be attributed; selecting an agent clears that agent's unseen activity. Neither is pinned today.

This effect is a hybrid and only converts partially. Its streaming subscription stays as it is — the activity mapping it drives is genuinely derived from streaming state and there is no action that means it. The effect goes from testable through nothing to testable through its two action entry points, which is the whole gain available here.

**Blocked by:** 01 (fault logging).

**Status:** done

- [x] The topic-mapping work is reachable by calling a public method with an agent list and awaiting it.
- [x] The clear-unseen-activity work is reachable by calling a public method with an agent id.
- [x] Dispatching either action still runs the same work.
- [x] A fault in the topic-mapping work is logged rather than discarded.
- [x] Awaiting the mapping method means every agent's topics are attributed.
- [x] The streaming subscription and its disposal are unchanged.
- [x] The effect has a test file that dispatches each action in at least one case.
