# Spec — Turn Rendering Has an Owner

Status: ready-for-agent

Grilled from candidate 12 of `.scratch/architecture-audit-2026-08-03/candidates.md`,
which holds the file and line evidence for the claims below. Two of its line
references had drifted by the time of grilling, and one of its four claims did not
survive; both corrections are recorded in the Out of Scope and Further Notes sections.

Vocabulary follows `CONTEXT.md`. A **turn** is one message the agent answers and
everything that comes back from it. This spec adds four terms that the grilling
pinned down and that `CONTEXT.md` does not yet carry:

- **Decoration** — everything prepended to a user turn on its way to the model that
  the user did not type: who sent it, from where, when, what alert they dismissed,
  and what the agent remembers about them. It exists only on the copy sent to the
  model and is never persisted.
- **Recall block** — the decoration that carries remembered facts. The model is told
  to look for it by name.
- **Extraction window** — the slice of conversation the memory extractor reads,
  rendered with turn markers so the extractor knows which turn is the current one.
- **Memory anchor** — the point in a conversation's persisted history that an
  extraction window is cut at. It is taken before the current turn is persisted, so
  it excludes the turn that produced it.

## Problem Statement

The text the model reads on every user turn is assembled in a place nobody would
look for it, and no test asserts any of it.

The agent's own system prompt tells the model that remembered facts arrive in a
block named `[Memory context]` at the start of user messages. Nothing in the memory
subsystem produces that block. It is built by a private static method inside the
OpenRouter chat client, an adapter whose job is HTTP transport. The promise and the
thing that keeps the promise are in different layers, and the block has no test
anywhere in the repo — the only way to reach it today is to drive the chat client.

The same is true one layer over. The extraction prompt tells the extractor that it
will see turn markers, that the last one is labelled the current turn, and that it
must extract from that one only. Three separate components conspire to deliver
that: the extraction worker appends the fallback content last, the window renderer
labels whatever is last as the current turn, and the prompt names the marker. No
test connects any two of them. Renaming the marker on one side breaks the feature
silently.

The window's slicing rule is a private method inside a background service, so
asserting it means standing up the whole worker. Four of that worker's ten unit
tests exist only to check where the slice starts and ends, and each one builds a
fake extractor, embedding service, memory store, thread store, metrics publisher
and agent definition provider to assert the result of a `Take` and a `TakeLast`.

The anchor those tests are about is a bare integer. It is correct only because the
recall hook runs before the turn is persisted; nothing says so, and nothing would
fail if that order changed. If it ever did, the extraction window would pull the
current user message out of the persisted history *and* append the fallback copy,
handing the extractor the same turn twice with the real one labelled as context.
The memories that come back would be wrong and no test would go red.

The gate that turns memory off for an agent is copied into two services. Both
copies are fail-open in two ways that neither states: an unknown agent id and an
absent agent id both leave memory on.

## Solution

Give the outgoing turn and the extraction window each an owner in the domain, and
pair every rendered marker with the prompt that names it.

Everything a user turn carries to the model — sender, location, satellite, local
timestamp, dismissed alert and the recall block — is built by one domain function
that takes a message and returns the decorated copy. The chat client keeps deciding
*when* to decorate, because the decoration must land on the copy it sends and never
on the copy that gets persisted, but it stops deciding *what* the decoration says.

The extraction window becomes a pair of pure domain functions, one that cuts the
window at the anchor and one that renders it with markers. The worker keeps its one
call to fetch the persisted history and hands the result over.

The anchor stops being an integer and becomes a named value whose factory states the
precondition it depends on, and a test at the chat monitor pins that precondition so
that reordering the call goes red.

The feature gate becomes one extension on the agent definition provider, with the
fail-open policy stated once and covered by a test.

Nothing the user or the model sees changes. Every rendered string is byte-identical
before and after.

## User Stories

1. As a developer adding a new decoration to a user turn, I want one function that
   owns everything prepended to a turn, so that I do not have to find the transform
   buried in an HTTP adapter.
2. As a developer changing what the recall block looks like, I want to edit a domain
   function next to the prompt that promises it, so that the promise and the text
   stay in one place.
3. As a developer renaming a turn marker, I want a test to go red when the prompt
   and the renderer disagree, so that I cannot break extraction silently.
4. As a developer writing a test for the recall block, I want to call a function
   directly, so that I do not need an HTTP transport or a mocked chat client.
5. As a developer asserting where the extraction window starts, I want to call the
   slicing function directly, so that I do not build six fakes to check an index.
6. As a developer reading the extraction request, I want the anchor to name what it
   points at, so that I know it excludes the turn that produced it.
7. As a developer moving the recall call inside the chat monitor, I want a test to
   go red if I move it after the turn is persisted, so that I cannot silently
   corrupt every extraction window from then on.
8. As a developer turning memory off for an agent, I want one gate to change, so
   that recall and extraction cannot disagree about whether the agent has memory.
9. As a developer reading the gate, I want the fail-open rule stated once, so that I
   know an unknown agent keeps memory rather than losing it.
10. As a developer reviewing this change, I want every rendered string to be
    byte-identical to before, so that I can review it as a move rather than as a
    behaviour change.
11. As a developer of a future chat client, I want the decoration to be reusable, so
    that a second client does not silently drop the recall block the prompt promises.
12. As the model, I want the recall block to arrive under the exact name my system
    prompt tells me to look for, so that I can find remembered facts.
13. As the model, I want the current turn in an extraction window marked with the
    exact label my extraction prompt names, so that I extract from the right turn.
14. As the model, I want the recall block to render identically for a turn I have
    already seen, so that the static part of my prompt stays cacheable.
15. As a user of the agent, I want memories extracted from what I actually said, so
    that a duplicated current turn does not produce facts about the wrong message.
16. As a user of the agent, I want the memory feature to behave the same in recall
    and extraction, so that facts are not recalled for an agent that will not store
    them.
17. As a user of the agent, I want my persisted history to hold what I typed, so
    that decoration text is never fed back into the extractor as my own words.
18. As a maintainer reading the audit record, I want the claim about the dreaming
    service marked as withdrawn with its reason, so that the next audit does not
    raise it again.
19. As a maintainer sequencing this work, I want its contacts with the metrics and
    conversation-group efforts recorded, so that the same lines are not written
    twice.
20. As a maintainer, I want the new terms in the project glossary, so that the next
    person to touch this uses the same words.

## Implementation Decisions

**Split by the prompt, not into one memory module.** The candidate proposed a single
module owning anchoring, window building, window rendering and recall block
rendering behind one interface. Grilling rejected that. The two halves never call
each other and change for unrelated reasons: recall runs in the request path, and
extraction runs on a background worker. Their only handoff — the anchor and fallback
content that recall produces and extraction consumes — is already a named domain
record. So the work splits into one module per prompt: each owns the text that
satisfies exactly one prompt constant, and each has one reason to change.

**The extraction window module owns cutting and rendering.** It exposes a pure
function that takes the persisted history, the anchor, the fallback content and the
window size, and returns the window; and a render function that takes a window and
returns the marked-up text. The existing conversation window renderer is absorbed
into it under the new name rather than left beside it, so that the module is the one
place the marker vocabulary is written.

**The extraction window module does not fetch.** The worker keeps its single call to
the thread state store and passes the result in. The domain layer does not do state
retrieval, and keeping the function synchronous and dependency-free is what makes
the slicing tests cheap.

**The recall block module owns only its text.** It renders a memory context into the
block the feature prompt names. It does not decide when the block is applied.

**The recall block is rendered per request, not persisted.** The structured memory
context is persisted on the message; the rendered text is not. Every request
re-renders a block for each historical user turn that carries context, and that
output must be byte-stable so the prompt prefix stays cacheable — this is why the
block cannot be rendered by the recall hook instead. Rendering at the hook would put
the block text into the persisted message, which the extraction worker reads back,
feeding remembered facts to the extractor as the user's own words. This is a
constraint on the module, and the byte-stability half of it gets a test.

**One decorator owns everything prepended to a user turn.** The sender, location,
satellite, local timestamp and dismissed-alert prefix moves out of the chat client
alongside the recall block, into a single domain function that takes a message and a
time zone and returns the decorated copy. It is a static function, not an injected
service: the only ambient thing it needs is the local time zone, and passing that as
an argument keeps it pure. The chat client's per-message transform collapses to one
call.

**Prepend order is preserved exactly.** The recall block comes first, then the
sender and timestamp prefix, then the user's own content. The conditions are
preserved too: the prefix only when the message is from the user and at least one of
sender, timestamp or dismissed alert is present; the recall block only when the
message is from the user and carries a memory context.

**The anchor becomes a named value.** The extraction request carries it in place of
the bare integer. Its factory names the precondition — it is built from the
persisted message count taken *before* the current turn is persisted — so the
constraint is visible at the one construction site and at every consumer signature.
The type documents; the chat monitor test enforces.

**One feature gate, general over the feature name.** An extension on the agent
definition provider answers whether a given agent has a named feature enabled. It is
parameterised by feature name rather than hard-coded to memory, because the name is
already a literal at both call sites and parameterising it costs nothing. The
fail-open policy is stated once: a null agent id and an unknown agent id both mean
enabled.

**Marker constants are not shared between prompt and renderer.** The prompts stay
plain raw string literals. The connection is made by tests that assert the same
marker literal appears in the rendered output and in the prompt constant.
Interpolating markers into sixty-line prompt bodies that are read and edited as
prose would cost more readability than it buys, and the extraction prompt already
contains braces in its examples.

**The glossary gains the four terms above.** Decoration, recall block, extraction
window and memory anchor go into `CONTEXT.md` under a memory heading, with the same
`_Avoid_` convention the existing entries use.

## Testing Decisions

A good test here asserts what a caller can observe: the text that goes to the model,
the window that goes to the extractor, whether a gate lets work through. It does not
assert that a particular function was called, and it does not reach into private
state. Because every rendered string is meant to be byte-identical before and after,
the strongest tests are the ones that pin exact output.

Three seams, two of them new. Fewer would mean testing rendering through a transport
again, which is the friction this spec exists to remove.

**The decorator.** The highest point at which "what the model sees on an outgoing
user turn" is observable, and the seam for the whole prefix and the recall block.
The twelve existing chat-client prefix tests move here wholesale and lose their
mocked inner client — they are the prior art for what to assert, and their exact
expected strings carry over unchanged. New tests here cover the recall block: that
it carries the marker the feature prompt names, that a context with no profile and a
context with a profile both render as before, and that rendering a context that has
been through a JSON round trip produces bytes identical to rendering the original.
That last one is the cache-stability contract, asserted today only indirectly by the
chat message serialization tests.

**The extraction window.** The five existing conversation window renderer tests move
here under the new name, unchanged in substance. The four slicing assertions
currently routed through the extraction worker move here as direct calls: window
built from history plus fallback, missing history, anchor beyond the available
history, and null thread key. Each loses six fakes.

**The chat monitor.** The anchor ordering invariant, using the existing monitor unit
test harness. A recording memory recall hook captures the persisted message count it
is handed at enrich time; the test asserts that count excludes the turn being built.
This is the only seam that can see the ordering, and it is the only new test that
would catch the failure the anchor type merely documents.

**Marker cross-checks.** Two small tests, one on each renderer seam, asserting a
marker literal against both the rendered output and the corresponding prompt
constant. These are the first tests in the repo to reference the memory prompts at
all.

**Staying where they are.** The extraction worker drift test keeps its end-to-end
assertion that a request anchored earlier ignores messages that arrive later; it is
about the anchor's purpose rather than the slice arithmetic. The two existing
feature-gate tests stay as behaviour tests at their services. The gate extension
gets one test of its own for the fail-open policy on a null and an unknown agent id.

## Out of Scope

**The recall embedding window.** The recall hook builds a second, different window —
user turns only, joined as plain text, fed to the embedding service, with no markers
and no anchor. It shares nothing with the extraction window but the word "window"
and stays a private method in the hook.

**Per-turn recall block accumulation.** Because context is persisted per message and
re-rendered per request, a conversation with many user turns sends many recall
blocks. This was examined during grilling and accepted as intended: the memories
shown against an older turn are the ones that shaped that answer, and re-rendering
them identically is what keeps the prompt prefix cacheable. No change, and no entry
in the audit record.

**A global memory switch.** There is no configuration flag that turns the memory
feature off across recall, extraction and dreaming. Adding one is a new
configuration surface and a behaviour change; it belongs in its own candidate.

**Gating the dreaming service.** The candidate called the missing feature gate there
a defect. It is not. The gate is per agent; dreaming iterates users read from the
memory store and has no agent to check. A user only appears in that list if
memories were stored for them, which required an agent that had memory enabled, and
consolidating memories that already exist is correct regardless of which agents can
read them back. The claim is withdrawn.

**Metrics publishing in the memory services.** The recall hook's stopwatches, its
four publish calls and the swallow-everything guard around its latency publish are
rewritten by the metrics publishing effort, per `docs/adr/0002`. This spec does not
touch them.

## Further Notes

**Sequencing.** This work runs after the metrics publishing module and after the
conversation group. The metrics effort rewrites the recall hook's stopwatch and
publish structure and drops `Async` suffixes from methods that lose their awaits;
this spec edits the same file for the gate and the anchor. The conversation group
effort changes the chat monitor's private per-turn methods to take a turn record,
including the one this spec hangs its ordering test on. Neither contact is deep, but
running first in either case means writing the same lines twice. The conversation
group already waits on metrics publishing, so this adds no new dependency edge.

**Audit record updates.** Candidate 12's index row moves from "Not grilled" to
grilled, pointing here. Its body is replaced by the shape settled above, its
dreaming-gate paragraph is replaced by the withdrawal and its reason, and its two
drifted line references are corrected. The note that candidate 8 contacts candidate
12 stands, but the reason changes: it is the ordering test's attachment point, not
the recall call site moving.

**Two decisions worth an ADR.** That all outgoing user-turn decoration lives in one
domain function rather than in whichever client sends it, and that every user turn
keeps carrying its own recall block. Both are the kind of thing a later reader will
re-open. Neither is written yet; raise them if you want the record.

**The candidate's own framing was wrong in a useful way.** It proposed a module
because it saw one chain. There are two chains that touch only through a record that
already existed. Worth remembering when the next audit proposes a module named after
a subsystem rather than after a contract.
