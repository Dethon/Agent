# One refusal rule for the downloads overlay

Status: resolved

## Problem Statement

The agent works with the media library through the `media` mount, where downloads live under
`downloads/<id>/`. Some paths there belong to a live download and some do not, and the mount
does not answer that consistently.

The visible symptom is that the same file behaves differently depending on how the agent
reaches it. A `downloads/<id>/status.json` whose download has already finished or been
cancelled — a leftover — can be listed, its existence confirmed, read as text and deleted,
but a streamed read of it fails with an error saying it is a read-only virtual file. The
error is wrong: there is no live download behind that path, so nothing is being rendered.
The agent is told a file it can see and remove cannot be read, with no way to tell why.

Underneath, "does this path belong to a download right now" is asked by three different
predicates crossed with two error shapes, at seven hand-picked sites. Seven separate
commits have each fixed one site. Every future operation added to the mount gets to pick
its own answer, so the next divergence is a matter of time.

## Solution

The media library answers the question once.

A path belongs to the overlay only while a download owns its id. A leftover is an ordinary
file: listable, readable, writable and removable like anything else on the mount. How a path
is spelled never makes it the overlay's.

Every operation on the mount asks one rule before doing anything, and the rule gives back
either nothing or one refusal with one reason. The agent gets the same answer for the same
path whichever operation it used to get there — a ranged read and a streamed read of the
same file no longer disagree.

The five reasons an operation is refused:

- reading text from a path that is not a status file, because the rest of the mount is bytes
- reading a live download's status file as bytes, because it is a rendered view and not a file
- landing anything inside a live download's directory, because it is removed when the download
  is cancelled
- moving a live download's directory, an ancestor of it, or anything inside it out, because
  the download keeps writing and recreates what it lost while the moved copy is orphaned
- deleting a live status file, or deleting a path that is neither a download nor its status file

## User Stories

1. As the agent, I want a leftover status file to read the same way whether I use a text read,
   a ranged byte read or a streamed read, so that I do not conclude a file is broken because
   of which tool I picked.
2. As the agent, I want a leftover status file to be readable at all, so that a file I can see
   in a glob and remove with a delete is not also unreadable.
3. As the agent, I want a live download's status file to report its current state, progress and
   eta when I read it as text, so that I can tell a user how a download is going.
4. As the agent, I want reading a live download's status file as bytes to be refused with a
   reason that tells me to read it as text, so that I can recover on the first try.
5. As the agent, I want a text read of a media file that is not a status file to be refused
   with a reason naming what this mount does read, so that I stop trying to read video as text.
6. As the agent, I want deleting a live download's directory to cancel the download and clean
   up its files, so that cancelling is one obvious operation and not a separate tool.
7. As the agent, I want deleting a leftover download directory to remove it, so that a crash or
   an external removal does not leave the library with rubbish I cannot clear.
8. As the agent, I want deleting a leftover status file to remove it, so that ordinary files are
   removable regardless of where they sit.
9. As the agent, I want deleting a live download's status file to be refused, so that I do not
   partially dismantle a download I meant to leave running.
10. As the agent, I want deleting any other media path to be refused with a reason saying what
    delete does on this mount, so that I do not think the library is a general scratch space.
11. As the agent, I want moving a live download's directory to be refused, so that I do not end
    up with an orphaned copy while the download recreates what it lost.
12. As the agent, I want moving a directory that contains a live download to be refused, so that
    reorganising a parent folder cannot take a running download with it.
13. As the agent, I want moving a payload file out of a live download's directory to be refused,
    so that I do not move a file the download is still writing.
14. As the agent, I want a move whose destination lands inside a live download's directory to be
    refused, so that I do not put a file where cancelling the download destroys it.
15. As the agent, I want a copy whose destination lands inside a live download's directory to be
    refused for the same reason, so that copy and move agree.
16. As the agent, I want a byte write landing inside a live download's directory to be refused,
    so that the streamed and ranged halves of a write agree.
17. As the agent, I want a cross-mount copy into a live download's directory to be refused, so
    that streaming in from another filesystem is not a way around the rule.
18. As the agent, I want a cross-mount move out of a live download's directory to be refused, so
    that streaming out — which ends in a delete on the source, and on this mount that delete is
    the cancel — is not a way to silently cancel a download.
19. As the agent, I want copying a file out of a live download's directory to be allowed, so that
    I can take a copy of something already downloaded without waiting.
20. As the agent, I want moving files that have nothing to do with downloads to keep working, so
    that organising the library is unaffected.
21. As the agent, I want a dotted spelling of a path — `downloads/42/./status.json`,
    `downloads/43/../42` — to be treated as the path it resolves to, so that a refusal cannot be
    switched off by writing the same path differently.
22. As the agent, I want an absolute spelling of a path under the library root to be treated the
    same as the mount-relative one, so that the older tools' path style does not bypass the rule.
23. As the agent, I want a directory literally named `042`, `+42` or ` 42 ` to stay an ordinary
    directory, so that a folder whose name happens to look like a number is not mistaken for a
    download.
24. As the agent, I want a glob of `downloads/` to list live download directories and their status
    files alongside the real files on disk, so that one listing shows me everything there.
25. As the agent, I want a glob to show a leftover status file exactly once, so that a real file
    and a rendered view are never both listed for the same path.
26. As the agent, I want a live download's directory to report as existing before its files appear
    on disk, so that a queued download is visible while it is still fetching metadata.
27. As the agent, I want a refusal to arrive as a proper error with a code and a hint rather than
    as a crash, so that I can act on it.
28. As a maintainer, I want the refusal reasons to live in one place, so that adding an operation
    to this mount does not mean choosing a predicate and hoping it matches the others.
29. As a maintainer, I want the tests to describe what the agent sees rather than which internal
    predicate ran, so that the rule can be reshaped without rewriting the suite.
30. As a maintainer, I want the divergence between the ranged and streamed halves of an operation
    to be impossible to reintroduce, so that the eighth commit in this series does not happen.

## Implementation Decisions

Recorded as `docs/adr/0014-a-live-download-owns-the-path-not-its-spelling.md`. The vocabulary
is `CONTEXT.md`: **live download**, **status file**, **leftover**, **refusal**.

**Liveness decides ownership.** The overlay owns a path only while a download owns the id.
A status file with no live download behind it is a leftover and belongs to the disk for every
operation without exception. This is the one behavioural change of the work: a streamed read
of a leftover starts working, and text read, info, glob and delete keep the behaviour they
already have.

**The overlay owns one refusal rule.** `DownloadsOverlay` grows a single method that takes an
intent and one path and answers either nothing or one refusal. Five intents: text read, byte
read, land, move out, delete. One path per call — an operation with two ends asks twice, once
per end with the intent for that end, so a refusal always names the path that offended.

**Land is one intent, not four.** Copy destinations, ranged byte writes, streamed writes and
the destination end of a move share one reason and therefore one intent. There is no separate
"write to a virtual file" intent: a write to a live status file overlaps a live download's
directory, so land already refuses it, with the more useful reason.

**Text read and byte read are separate intents.** Their refusals are complements — text read
refuses everything except a status file, byte read refuses only a live status file — so a
single read intent would have to mean both.

**The rule answers, it never acts.** Cancelling a download, clearing its routing entry and
recovering a leftover directory stay where they are, running only after the rule has said
nothing. Delete contributes its two refusals to the rule and keeps its effects outside it.

**Every operation goes through it.** Each operation on the media mount consults the rule first
and falls through to the disk beneath when the rule says nothing. The two streaming operations
have no error envelope in their signature, so they consult the same rule and wrap its refusal
in the typed filesystem exception the copy tool already turns back into an envelope.
`NotSupportedException` goes back to meaning only what the backend base class uses it for: an
operation the backend does not implement. Changing the streaming signatures to carry an
envelope was considered and rejected — it would touch every backend to remove one wrapper at
one mount.

**Glob and info stay out.** Neither ever refuses. Glob merges the overlay's entries with the
disk's and info answers existence; both keep asking the overlay directly.

**The three predicates go private.** Spelling-only classification stops being reachable from
outside the overlay, and the two liveness predicates become internal to the rule. No caller
can ask half the question again, which is what allowed the seven sites to drift.

**The cross-mount guard keeps its wiring.** The mount's cross-mount refusal hook delegates to
the same rule and keeps its current registration. The separate problem — that its type test
cannot succeed against the topology production actually builds, so that refusal never fires in
the deployed system — is explicitly not addressed here and needs its own spec.

**Capability is unchanged.** No operation is added or removed from the mount, so the advertised
tool set and the one operations list are untouched. Capability stays per operation, not per
path.

## Testing Decisions

A good test here names a path, an operation and what the agent gets back. It does not name the
rule, the intent enum or any predicate. If the rule's shape changes and the agent's experience
does not, no test should need editing.

**One seam: the media mount.** `Tests/Unit/Domain/Downloads/Vfs/MediaLibraryFileSystemTests.cs`
already constructs the mount over a temp library root with the existing fakes, and every intent
is reachable from there through a public operation — text read, ranged and streamed byte read,
copy, ranged and streamed byte write, both ends of a move, delete, and the cross-mount hook.
The refusal matrix goes there and nowhere else. This revises an earlier decision to put the
matrix on the overlay directly; testing the rule through its own method would pin its signature
in the suite, which is the thing most likely to change.

**The matrix.** A table over intent by path class, where the path classes are: a live download's
directory, a live download's status file, a payload file inside a live download's directory, a
leftover status file, a leftover download directory, and a path unrelated to downloads. Each cell
asserts either the operation succeeding or one refusal with its code and its reason. The dotted,
absolute and lookalike-id spellings are theory rows against the same cells rather than a separate
group of tests, since they exist to prove the classification is the same one.

**What the streaming operations assert.** That the refusal arrives as the typed filesystem
exception carrying the same reason as the envelope its ranged counterpart returns, and that
nothing was written. The existing tests asserting `NotSupportedException` for these change to
the typed exception.

**What stays.** `DownloadsOverlayTests` keeps its coverage of what the overlay produces rather
than what it refuses: the rendered status file's contents, existence answers, glob entries and
merging, the cancel-and-clean effect of deleting a live download, leftover recovery, and routing
removal. Those are not refusals and do not move.
`Tests/Unit/Infrastructure/FileSystemServerConformanceTests.cs` stays as-is and should keep
passing untouched, which is the check that no capability drifted.

**What goes.** The seven per-site assertions that each pinned one operation against one
predicate. They are what made the divergence invisible: each passed while disagreeing with its
neighbour.

**Prior art.** `MediaLibraryFileSystemTests` for mount-level behaviour with a real temp
directory beneath, `DownloadFakes` for the download client, routing store and recording
filesystem client, and `Tests/Unit/Infrastructure/FileSystemServerConformanceTests.cs` for the
per-server capability shape.

**Order of work.** Red-green per intent, following the repo's TDD rule: start with the failing
streamed-read-of-a-leftover test, since that is the divergence the whole change exists to remove.

## Out of Scope

- The cross-mount move guard's seam placement. Its type test cannot succeed against the
  production topology, so that refusal does not fire in the deployed system today and will not
  after this work either. Separate spec.
- Adding an integration fixture that mounts the media library alongside a second filesystem.
  That topology is where cross-mount bugs live and nothing covers it, but it belongs with the
  guard's spec.
- Any change to what a status file reports, to the download manager, or to the routing store.
- Any change to the mount's advertised tool set or to the operations list.
- Trash semantics, the disk root's own behaviour, and every other mount.

## Further Notes

Download ids are link hash codes, so roughly half are negative and re-adding the same link
produces the same id again. One consequence of liveness deciding ownership: a file written
where a leftover status file used to be is shadowed by the rendered view if that download is
revived. This is already how any file inside a download directory behaves, and deleting the
directory clears it. It is called out in the ADR rather than defended against.

The seven commits this work replaces are `2a655f4e`, `ffa0cccb`, `f8cb3cbc`, `7f14527f`,
`ead0b827`, `2b57322d` and `ab637436`. Each is a correct fix to one site. Read together they
are the argument for the single rule, and `ead0b827` in particular is the one whose behaviour
this spec generalises to every operation.

The originating analysis is
`.scratch/architecture-review/2026-08-05-deepening-candidates.md`, candidate 1.
