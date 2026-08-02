# 04 — Pin the toast rules

**What to build:** the toast slice gets its first test. It carries four rules a user can see and none of them are pinned: an error whose text matches a toast already on screen does not produce a second toast, the list never grows past three, a message longer than 150 characters is truncated with an ellipsis, and a message that is empty or whitespace is replaced by a generic fallback.

The slice collapse in ticket 05 moves this code. Without these tests, a mistake during the move is invisible until a user sees the wrong thing.

Characterize current behaviour. The store is already constructed directly in the streaming service and stream resume service tests, so the construction pattern is settled.

Toast is already close to the two-file target shape — its reducers are inline in the store and it has no separate reducers file — so ticket 05 will barely touch it. That makes it the cheapest place to establish the pattern, not a reason to skip the tests.

**Blocked by:** None — can start immediately.

**Status:** ready-for-agent

- [ ] Dispatching an error whose text matches a toast already present leaves the list unchanged.
- [ ] Dispatching a fourth distinct error leaves three toasts, and the oldest is the one dropped.
- [ ] A message longer than 150 characters is stored truncated to 150 characters plus an ellipsis.
- [ ] A message of exactly 150 characters is not truncated.
- [ ] An empty or whitespace-only message is replaced by the fallback text.
- [ ] Dismissing a toast by id removes that toast and leaves the others.
- [ ] Dismissing an id that is not present leaves the list unchanged.
- [ ] Tests assert on observable state, never on which handlers were registered.
- [ ] The tests construct the store through a real dispatcher, so they survive the collapse in ticket 05 unchanged.
