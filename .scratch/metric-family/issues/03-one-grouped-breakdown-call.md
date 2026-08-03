# 03 — One grouped breakdown call

**What to build:** There is one way for the dashboard client to ask the server for a
breakdown, instead of seven.

The client's API service has a separate method per metric family, each one a URL builder
differing only in its route segment and which query values it appends. They return two
different value types between them. One of them carries a comment explaining that a
particular parameter must never be given a default, because a default there is how a
call site silently reverted the user's aggregation choice.

Collapse them into a single grouped call taking the route segment and the query values.
Both effects and all seven pages move onto it. Delete the comment: it exists because two
call sites could disagree about a default, and the family ticket that follows makes the
family the only place a request is built.

This is a prefactor. It makes every family's request look the same before the family
table exists, so the table's entries are uniform rather than each wrapping a different
method.

Behaviour is identical. Every request that goes out today goes out unchanged.

**Blocked by:** 01 — the voice and latency call signatures name the renamed enum.

**Status:** ready-for-agent

- [ ] One grouped call on the API service replaces the seven per-family builders, handling both value types the families return.
- [ ] The live-update effect, the page-load effect and all seven pages use it.
- [ ] Every outbound request is byte-for-byte what it is today: same path, same query values, same order of concerns.
- [ ] The voice aggregation is still passed explicitly with no default anywhere on the path.
- [ ] The comment recording the aggregation-default bug is gone; the regression test guarding the same bug stays and still passes.
- [ ] The existing dashboard client test suite passes without changes to its assertions.
