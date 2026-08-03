# 06 — Entry points for space switching and user identity

**What to build:** switching space and choosing a user become awaitable and testable. Both effects already carry their work in a private async method behind a handler registration; the change is making that method public, taking the payload as parameters, and attaching the fault logging from ticket 01.

Both depend on the config service, and the space effect also depends on the browser push service, which is why this ticket waits on ticket 02 while ticket 05 does not.

Each effect gets its own test file. Switching space resolves the new space, joins it, and moves the push subscription's space context. Choosing a user loads the user list and persists the selection. Neither is pinned today.

The diffs should be nothing but the signature and the fault log.

**Blocked by:** 01 (fault logging), 02 (interfaces for config and push).

**Status:** done

- [x] Space-switching work is reachable by calling a public method with a slug and awaiting it.
- [x] User-identity work is reachable by calling a public method and awaiting it.
- [x] Dispatching either action still runs the same work.
- [x] A fault in either effect is logged rather than discarded.
- [x] Switching to a slug the config service does not recognise does not leave the app joined to it.
- [x] Choosing a user persists the choice so it survives a reload.
- [x] Both effects are constructed in their tests with no `HttpClient` and no Blazor JS runtime.
- [x] Both effects have a test file that dispatches the action in at least one case.
- [x] No production behaviour changes beyond the fault logging.
