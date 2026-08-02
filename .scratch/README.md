# Architecture audit — candidate index and implementation order

Seven candidates from the architecture review of 2026-08-02. Each has a `plan.md`,
a `spec.md` and numbered tickets under `issues/`. 43 tickets in total.

Ordering inside a candidate is the `**Blocked by:**` line in each ticket, and the
`NN-` filename prefix lists them blockers-first. This file records the ordering
*between* candidates, which no ticket can express.

| Candidate | Review | Tickets | Area |
|---|---|---|---|
| `voice-turn-module` | 1 and 7 | 5 | Voice session and turn state |
| `channel-server-interface` | 2 | 8 | Channel server registration |
| `filesystem-backend-depth` | 3 | 10 | Virtual filesystem backends |
| `config-patch-once` | 4 | 4 | Per-message model and effort override |
| `single-conversation-id` | 5 | 3 | Turn identity and telemetry attribution |
| `webchat-slice-shape` | 6 | 6 | WebChat state slices and dispatcher |
| `effect-entry-points` | 9 | 7 | WebChat effects |

## Ordering

Three ordering preferences, no hard dependency, and one candidate that touches
nothing else.

```
webchat-slice-shape ──prefer first──> effect-entry-points

config-patch-once ─ ─ same file ─ ─> single-conversation-id

channel-server-interface <─ ─ same test files ─ ─> voice-turn-module

filesystem-backend-depth        (independent)
```

**`webchat-slice-shape` before `effect-entry-points` is weaker than the plan
claims.** `effect-entry-points/plan.md` states it as a dependency: land the
slice work first so effect tests are written against final dispatch semantics.
Checking what would actually break, less than that.

The slice work changes `Store.Dispatch` to skip the emission when a reducer
returns its input, and adds a catch-all to `Dispatcher`. No test specced in
`effect-entry-points` asserts on emission counts — they assert on store state
and on call order recorded by fakes, both unaffected by the skip. Ticket 05 of
the slice work collapses each slice to two files without renaming a type or a
namespace, so an effect test that references `TopicsStore` or `SelectAgent`
compiles the same before and after. `RegisterHandler<TAction>` survives the
slice work untouched, which is what the effects use.

What is left is a file-level rebase. `webchat-slice-shape` tickets 05 and 06
and `effect-entry-points` ticket 05 all rewrite `TopicDeleteEffect`, one the
handler body and the other its signature. Taking the slice work first means
rebasing once rather than twice. Reversed, nothing fails; it just costs a
second pass over the same file.

The one place the order does carry information is `AgentActivityEffect`, which
keeps a raw `StateObservable` subscription under both plans and so is the last
effect still exposed to the emission change. `effect-entry-points` ticket 07
converts its two action handlers and leaves that subscription alone, so even
there the tests do not straddle the change.

**`config-patch-once` before `single-conversation-id` is a preference.** Both
rewrite `ChatMonitor`, in different regions: the first replaces the inner
`Merge` with sequential consumption, the second collapses the three id names and
the attribution comment. Neither breaks the other. Taking them in this order
means `single-conversation-id`'s deliverable test — one scheduled fire
publishing five events on one id — is written against serialized turns, where
event order is deterministic. Reversed, it is written against concurrent turns
and then rebased onto serialization. The grouping key stays `AgentKey` under
both, so serialization semantics do not depend on the id unification.

**`channel-server-interface` and `voice-turn-module` collide only in tests.**
`channel-server-interface` ticket 07 and `voice-turn-module` ticket 05 both
rewrite `Tests/Integration/McpChannelVoice/WyomingSatelliteHostTests.cs` (2,215
lines) and `WakeArbitrationHostTests.cs`. Whichever lands second rebases. Either
order works and both ticket sets record the collision.

**`filesystem-backend-depth` shares no file or symbol with any other candidate.**
It can run at any point, including alongside everything else.

## Starting frontier

Tickets whose blockers are all satisfied on day one, across the whole audit:

- `channel-server-interface` 01
- `config-patch-once` 01, 02
- `filesystem-backend-depth` 01, 03, 08, 10
- `voice-turn-module` 01, 03
- `single-conversation-id` 01
- `webchat-slice-shape` 01, 02, 04
- `effect-entry-points` 01, 02

That is the whole ticket set's frontier, and it is every candidate at once —
`filesystem-backend-depth` alone opens four. The seven candidates are close to
disjoint in code, so parallelism is limited by review capacity rather than by
the graph.

## How this was derived

Cross-candidate contact was checked by intersecting the symbols named in every
spec and ticket, then by grepping the plans for the file paths the specs
deliberately omit. Three contact points came out of it, all listed above. Rerun
both checks before adding a candidate:

```
grep -rln "<SymbolA>\|<SymbolB>" .scratch/*/issues/ .scratch/*/spec.md
grep -rln "<FileOrPath>" .scratch/*/plan.md
```

## Conventions

Tickets are markdown under `.scratch/<slug>/issues/`, tracked in git, each
carrying a `Status:` line from the triage vocabulary. Never `gh issue create`,
despite the GitHub remote. See `docs/agents/issue-tracker.md` and
`docs/agents/triage-labels.md`.

Implementation follows the project rules in `CLAUDE.md` and `.claude/rules/`:
red-green-refactor, `.cs` files with no trailing newline, the pre-commit hook
reformatting and re-staging whole files, and tests that start their own
containers rather than needing the compose stack.
