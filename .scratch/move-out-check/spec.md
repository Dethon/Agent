# A mount is asked before a path leaves it

Status: ready-for-agent

## Problem Statement

The agent moves files between mounts. When the two ends are different mounts, the move is not
a move: the bytes are streamed from the source to the destination and then the source is
deleted. The mount losing the path is never asked to move anything, so none of its own move
refusals run.

On the media library that has visible consequences.

Moving a live download's directory to another mount does not fail. It streams whatever files
the download has written so far, and then deletes the source — and on the media library,
deleting a download directory is the documented way to cancel a download. The agent asks for
a move and gets a cancelled download plus a partial copy on the other mount, reported as a
success or as a per-file summary that says nothing about the torrent that just stopped.

Moving a single payload file out of a live download is not much better. The whole file is
copied to the other mount first, and only then is the delete refused. The agent is left
holding a duplicate it did not ask for and an error saying the source could not be removed.

Both contradict what a refusal means here: the caller is told why, and nothing was attempted.

A guard exists for exactly this and has never run. `VfsMoveTool` type-tests both ends of a
cross-mount move for a domain interface that only the media backend implements — but the
agent never holds the media backend. Every mount the agent has is an MCP proxy to a server in
another process, so the test cannot succeed in any deployment. No test covers it either: the
tests that once did were mock topologies the production system never builds, and none remain.

## Solution

A mount is asked before a path leaves it, and the question travels across the MCP seam the
same way every other filesystem operation does.

A new **move-out check** joins the filesystem operation list. It is transfer machinery rather
than something the model calls, like the two byte-streaming operations: it never appears in a
mount's capability list and the agent's model never sees it as a tool.

Before a cross-mount move streams anything, the source mount is asked about the path being
moved. If it refuses, the agent is told why and nothing is attempted — no bytes are copied,
no directory is listed, no download is cancelled. If it allows, or has no rule to state, the
move proceeds exactly as it does today.

Only the source is asked. Moving something *into* a live download is already refused by the
media library's landing rule when the first chunk arrives, before anything is written.

The media library answers the check with the refusal it already has for moving out of a live
download — the same rule, the same reason, the same wording. Nothing about what the media
library considers refusable changes. What changes is that the answer now reaches the agent.

The dead guard interface is deleted.

## User Stories

1. As an agent, I want a cross-mount move of a live download's directory to be refused, so that
   asking to move files never cancels a download I did not ask to cancel.
2. As an agent, I want that refusal to arrive before any bytes are streamed, so that a refused
   move leaves no partial copy on the destination mount for me to clean up.
3. As an agent, I want a cross-mount move of a file inside a live download's directory to be
   refused up front, so that I am not left holding a duplicate and an error about a source that
   could not be removed.
4. As an agent, I want a cross-mount move of a path that merely contains a live download — an
   ancestor directory — to be refused, so that a move higher up the tree cannot take a running
   download with it.
5. As an agent, I want the refusal to name the offending path and give a reason, so that I can
   tell the difference between "wait for the download to finish" and "this mount cannot do that".
6. As an agent, I want the refusal to carry the same reason a same-mount move of that path gives,
   so that the answer does not depend on where the destination happens to be.
7. As an agent, I want the refusal to be marked permanent rather than retryable, so that I do
   not retry a move that will be refused every time until the download ends.
8. As an agent, I want a cross-mount move out of a mount that has no such rule — the vault, the
   sandbox, the timers mount — to keep working exactly as before, so that the new check costs me
   nothing anywhere else.
9. As an agent, I want a cross-mount move of a leftover status file to succeed, so that a file no
   live download owns keeps behaving like the ordinary file it is.
10. As an agent, I want a cross-mount move of an ordinary media file, outside any live download's
    directory, to succeed, so that organising the library is unaffected.
11. As an agent, I want a cross-mount **copy** out of a live download's directory to keep working,
    so that taking a snapshot of what has arrived so far is still possible — a copy leaves the
    source in place, so the download keeps its files.
12. As an agent, I want a same-mount move on the media library to be refused exactly as it is
    today, so that the two kinds of move give one answer.
13. As an agent, I want a move *into* a live download's directory to stay refused, so that files
    I place there are not silently destroyed when the download is cancelled.
14. As an agent, I want the move-out check to be invisible in the mount's capability list, so that
    the list still describes operations I can call rather than machinery I cannot.
15. As an agent, I want the check to be absent from my tool set, so that I cannot call it directly
    and mistake its answer for having performed a move.
16. As a maintainer, I want a mount to declare it has a move-out rule by implementing one, so that
    the operation a server advertises stays the single source of truth for what its mount does.
17. As a maintainer, I want a mount that implements no rule to need no code at all, so that adding
    the check does not touch six backends that have nothing to say.
18. As a maintainer, I want the check asked through the same proxy as every other operation, so
    that there is no second mechanism for carrying a question across the process boundary.
19. As a maintainer, I want no caller anywhere to test what kind of backend it is holding, so that
    the defect being fixed cannot recur in a different operation.
20. As a maintainer, I want a server that predates the check to keep working against a newer agent,
     so that a mount which never registered it is treated as having nothing to say rather than as
     refusing everything.
21. As a maintainer, I want the check asked in the one place that already knows a move is deleting
    its source, so that a future caller of the transfer machinery cannot forget to ask.
22. As a maintainer, I want the refusal to travel as the standard error envelope, so that its code,
    message, hint and retryability reach the agent without a second refusal shape being invented.
23. As a maintainer, I want the check asked once per move rather than per file, so that a directory
    move does not pay a round trip per entry for a question whose answer covers the whole subtree.
24. As a maintainer, I want an integration test that mounts the media library alongside another
    filesystem through real discovery, so that the topology every cross-mount bug lives in is
    finally exercised.
25. As a maintainer, I want the dead guard interface and its call site removed in the same change,
    so that nothing is left in the domain contracts that looks like it protects something.

## Implementation Decisions

**The check is a filesystem operation, not a domain interface.** It becomes a thirteenth entry
in the one operation list that the registrar, the payload-type table, the capability map and the
tool feature all derive from. Its tool key and capability are null, exactly like the two
byte-streaming operations, so it is registered on servers and callable by the proxy while
appearing in no capability list and in no model-facing tool set. Its wire name is
`fs_move_out_check`.

**Its argument is one path.** The path being moved off the mount, in mount-relative
coordinates, as every other operation takes.

**The base default is "allowed".** The method sits on the filesystem backend contract, and the
backend base class implements it as allowed. This inverts what an override means for this one
operation — elsewhere an override declares "I can do this", here it declares "I have something
to refuse" — and that inversion is deliberate and documented next to the operation. A backend
with no rule needs no code and registers no tool.

**Refusal is the error envelope.** An ok payload means allowed. An error envelope means refused
and carries its code, message, hint and retryability through unchanged, which is how every other
refusal on the media library already travels. No `allowed` flag, no second refusal shape.

**The proxy answers for itself.** The MCP filesystem backend implements the method directly: it
calls the wire tool when its client advertised it, and answers allowed when it did not. A
server's advertised tool set is already the single source of truth for what a mount can do, so
a mount that never registered the check has nothing to say. This requires the proxy to know
which tools its client advertised; discovery already lists them once per client to derive
capabilities and passes what it needs to the backend it constructs.

**No caller asks what kind of backend it holds.** The type test that produced this defect is
deleted rather than corrected. Every source backend is asked; most answer allowed from the base
default without a round trip.

**The call sits in the transfer machinery, under the delete-source condition.** Both the file
and the directory transfer paths already branch on whether the source is to be deleted, which is
exactly the condition that makes the check necessary. Asking there means file moves and
directory moves share one statement of the rule, and a future entry point into the transfer
machinery cannot forget it. For the directory path this runs before the source listing, so a
refused move does not even enumerate.

**Asked once, on the source root.** The media library's predicate overlaps in both directions —
an ancestor of a live download's directory and anything inside one both refuse — so one call on
the move's source path answers for the whole subtree. A download that goes live during a
transfer is not caught; the window is one transfer, and the alternative is a round trip per file.

**The media library delegates to the rule it already has.** Its override calls the overlay's one
refusal rule with the move-out intent — the same call its own same-mount move makes for its
source end. No new reason, no new wording, no new predicate.

**Deletions.** The guard interface in the domain contracts, the media backend's implementation
of it, and the cross-mount refusal helper in the move tool all go.

**Decision record.** ADR-0015 records this; ADR-0014's closing consequence now points at it.
`CONTEXT.md` gains the term **move-out check** under MCP server hosting.

## Testing Decisions

A good test here asserts what the agent observes: the envelope it gets back, and whether the
files and the download still exist afterwards. It does not assert that a particular method was
called, or how many times. The one exception is the zero-bytes promise, which is only
observable as an absence — that the destination has no file and the source listing never ran —
so it is asserted as an absence on real filesystems rather than as a call count.

**One new integration test, at the existing cross-mount seam.** The multi-filesystem fixture
already hosts two in-process MCP servers and is already used by the cross-mount move and copy
integration tests, which mount them through the real registry and the real proxy. Extend it with
a third host running the media library backend over a fake download client — no containers, and
a live download is whatever the fake reports. The test moves a live download's directory to the
other mount and asserts: the envelope is a refusal naming the path, the destination is empty,
and the download is still live. This is the seam that would have caught the current defect, and
it is the only new one.

**Existing unit seams absorb the rest.** The media library's own tests already assert that each
operation consults the rule with the right intent; the move-out check adds one row. The transfer
machinery's delete-source tests already cover what happens when a source cannot be removed; they
gain a case where the source refuses up front and nothing is written. The filesystem server
conformance tests already assert that every operation in the one list has wiring and a
description hook, and that a backend advertises exactly what it overrides; the thirteenth
operation is covered by those invariants without new files, plus one case that a backend which
does not override it registers no tool and is treated as allowing.

**Prior art.** The cross-mount move and copy integration tests for the fixture-level shape; the
media library's own operation-by-intent tests for the mount's side; the transfer delete-source
tests for the engine's side; the server conformance tests for the operation table.

## Out of Scope

- Changing what the media library refuses. The move-out reason, its wording and the paths it
  covers are settled by ADR-0014 and are untouched.
- The destination end of a cross-mount move. It is already refused by the landing rule when the
  first chunk arrives, and asking it twice is what made a second mechanism look necessary.
- Teaching the delete operation that a delete can be the tail of a move. Considered and rejected
  in ADR-0015: it adds a second meaning to an operation every backend implements, and still
  streams the bytes first.
- Per-entry checks during a directory transfer, and therefore the case of a download that becomes
  live mid-transfer.
- Any refusal on a mount other than the media library. No other backend gains a rule.
- Cross-mount copy. It leaves the source in place, so it is not refused.
- The cross-mount transfer engine's other known rough edges — the copy tool owning the move rules,
  the directory path flattening error codes — which are their own candidate.

## Further Notes

The glossary already defines a refusal as an operation where the caller is told why and nothing
was attempted. On the way out of the media library that has not been true. This change does not
add a promise; it makes an existing one hold.

The check is coarser than the operation it guards: it asks about one path, not about the pair,
so a mount cannot refuse a move only for particular destinations. Nothing wants that today.

The agent and the servers ship together in one stack, so the advertise-or-allow rule is not a
migration concern in practice. It exists so that a mount which will never have a rule needs no
code, and it happens to make version skew harmless.
