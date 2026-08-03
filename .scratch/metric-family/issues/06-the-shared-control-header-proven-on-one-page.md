# 06 — The shared control header, proven on one page

**What to build:** The tokens page looks and behaves exactly as it does today, but its
header, its preference persistence and its date derivation come from shared code that
can be tested without a browser.

Every breakdown page repeats the same sequence in its own words: read the saved
group-by, metric and day count; derive a date range from the day count; subscribe to the
store; on each pill change set the store, persist the choice and reload; dispose the
subscription. That logic is reachable today only by driving a real browser against a
real stack, which is why six of the seven families have no coverage of it.

Build two things. A session type holding that logic in plain code, constructed from a
family, the local storage service and the time provider. A control wrapper component
that owns the header markup and the group-by, metric and time pills, forwards to the
session, and exposes the family's state to its child content typed. The wrapper takes an
extra-controls fragment for a family that needs one, and a set of currently unavailable
metric values for a family that disables some.

Migrate the tokens page onto it. Everything below the header — its headline figures, its
chart, its event table and its sorting — stays exactly as it is.

Preference keys do not move. A family's preference prefix is its name followed by a dot,
which is already the convention every page uses, so every choice a user has saved
survives.

Do not introduce a storage interface. The shared Blazor client work that follows this
feature brings one, and adding a second now would only have to be replaced.

**Blocked by:** 05 — the page reloads through the family, and calls a page-load effect
that ticket rewrites.

**Status:** ready-for-agent

- [ ] A session type owns preference loading, preference saving, date derivation from the selected day count, and change handling, in plain code with no component involved.
- [ ] A control wrapper owns the header markup and the group-by, metric and time pills, and forwards to the session.
- [ ] The wrapper accepts an extra-controls fragment and a set of unavailable metric values, both optional.
- [ ] Child content receives the family's state typed.
- [ ] The tokens page keeps its headline figures, its chart, its event table and its sorting unchanged.
- [ ] The tokens page contains no preference reading, no preference writing and no date arithmetic.
- [ ] Preferences saved under the current build still load and still select the same pills.
- [ ] A dictionary-backed JavaScript runtime fake closes the storage edge, leaving the local storage service real. No storage interface is introduced.
- [ ] Unit tests assert that a saved preference is applied on initialisation, that a changed pill is persisted under the family's prefix and triggers a refresh, and that the date range is derived from the day count against a fixed time provider.
- [ ] Those tests run standalone, with no Docker and no browser.
- [ ] The dashboard's Playwright suite passes unchanged.
