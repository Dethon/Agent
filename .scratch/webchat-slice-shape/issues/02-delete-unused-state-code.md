# 02 — Delete the state code nobody calls

**What to build:** the state folder stops advertising things the application does not use. A developer searching for how selectors work should not find 60 lines with no call site, and the action list should describe what the app actually dispatches.

Three groups go.

The generic selector helper has no call site anywhere in the solution and no test. It is deleted outright.

The streaming selectors file holds two lambdas with a single consumer, the render coordinator. The lambdas move into that consumer and the file goes.

Three actions — the connection status change, the connection error, and the approval responding marker — are each declared, reduced and registered, and never dispatched by anything. All three are removed: the action record, the reducer arm, and the store registration.

Doing this before the slice collapse means there is less to move. Doing it before the connection tests means those tests pin only arms that will still exist.

The two selector files that stay are the agent settings selectors, which encode the patchable-model whitelist and the default-diffing rules, and the unread selectors. Both have real logic and both have tests.

**Blocked by:** None — can start immediately.

**Status:** done

- [x] The generic selector helper file is gone and nothing references it.
- [x] The streaming selectors file is gone and its two projections live at their only consumer, which behaves identically.
- [x] The render coordinator's existing tests pass unchanged.
- [x] The connection status change action, its reducer arm and its registration are gone.
- [x] The connection error action, its reducer arm and its registration are gone.
- [x] The approval responding action, its reducer arm and its registration are gone.
- [x] A solution-wide search finds no remaining reference to any of the three removed actions.
- [x] The agent settings selectors and the unread selectors are untouched.
- [x] The full unit suite passes with no test edits.

## Comments

`ApprovalState.IsResponding` went with the approval-responding action. Once the
only arm that set it was gone the property could only ever be false, and nothing
in the client reads it.
