# 05 — Collapse the ten slices to two files each

**What to build:** adding an action to a slice stops being a three-file edit. Every slice becomes a state record file and a store file that holds the action records, the reducers and the store together. The separate actions and reducers files go away, and each store registers one catch-all instead of naming each action type it wants.

After this, a reducer arm added without a matching registration works, because there is no registration to forget. The reducer's existing fall-through arm does the filtering the registration table used to do.

Ten slices: agent activity, agent settings, approval, connection, messages, space, streaming, toast, topics, user identity. One commit per slice so the diff stays reviewable slice by slice.

Toast is already the target shape with its reducers inline — it needs only the catch-all swap. Start there and use it as the reference for the other nine.

The stores keep composing the generic store and keep taking a dispatcher. The Dashboard slices in the same solution inherit their store and have no dispatcher at all, so their inheritance does not transfer; what transfers is the file count and the inline reducers.

The existing store tests are the regression surface and should not need edits. If collapsing a slice forces a test change, that slice's behaviour moved — stop and explain it rather than editing the test to match.

Handler ordering must survive. A store's catch-all still has to run before an effect that reads state in its handler for the same action. Ticket 01's dispatcher test guards the mechanism; this ticket must not reorder store construction to break it.

**Blocked by:** 01 — Make the dispatcher safe to register once; 03 — Pin the connection transition table; 04 — Pin the toast rules.

**Status:** done

- [x] Each of the ten slices is exactly two files: the state record and the store.
- [x] No actions file and no reducers file remains in any slice.
- [x] Each store registers exactly one catch-all and no per-type registrations.
- [x] The effects keep their existing per-type registrations and are otherwise untouched.
- [x] Each slice lands as its own commit.
- [x] The full unit suite passes with no edits to any existing test.
- [x] A reducer arm added to any slice takes effect without touching a second file — demonstrate this once in the commit message or a scratch note.
- [x] The dependency injection registrations and their call order in the client entry point are unchanged.
- [x] The Dashboard state folder is untouched.

## Comments

**Two files means the state record and the store.** Three slices keep a third
file: `AgentSettingsSelectors`, `UnreadSelectors` and `AgentActivitySelectors`.
The spec names the first two as worth keeping and misses the third, which is the
same case — real logic, its own test.

**One existing test changed.** `AgentActivityReducersTests` called the reducer
class directly, which the collapse removes. It is now `AgentActivityStoreTests`
with the same three assertions made through a real `Dispatcher`. No expectation
moved; the test named the implementation detail the ticket deletes.

**The one-file demonstration.** Adding an action to a slice is now: write the
record and its reducer arm in `*Store.cs`. Nothing else. Each store calls
`RegisterCatchAll` once and the reducer's `_ => state` arm does the filtering the
registration table used to do.
