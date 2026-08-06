# 0015 — A mount is asked before a path leaves it

Status: accepted
Date: 2026-08-06

## Context

A move between two mounts is not a move. `VfsMoveTool` hands it to the transfer engine,
which streams the source's bytes into the destination and then deletes the source. The
source backend's own `MoveAsync` — where a refusal like "this path belongs to a live
download" lives — is never called.

`ICrossMountMoveGuard` was added to close that gap: `VfsMoveTool` type-tested both ends
of a cross-mount move for it and asked before streaming. The type test cannot succeed.
`registry.Mount` has exactly one caller, `McpFileSystemDiscovery`, and it always mounts an
`McpFileSystemBackend` proxy. The only implementer, `MediaLibraryDiskFileSystem`, lives in
the `McpServerLibrary` process behind that proxy. The refusal has never fired in any
deployment, and no test covers it — the interface is unreachable and untested.

What that leaves, traced through the transfer engine:

- **Moving into a live download** is already refused. `WriteChunksAsync` on the media mount
  asks the `Land` intent and throws before writing, and `TransferFileAsync` turns that into
  the standard envelope with the source untouched.
- **Moving a payload file out of a live download** streams the whole file to the other
  mount, and only then does the tail `DeleteAsync` refuse it. The agent is left with a copy
  it did not ask for and an error saying the source could not be removed.
- **Moving `downloads/<id>` itself out** streams whatever files exist so far, and then the
  tail delete lands on a download directory — which `fs_delete` permits, because deleting
  one is the documented way to cancel a download. The move cancels the torrent and leaves a
  partial copy behind.

That last case is the bug the guard was written for. `CONTEXT.md` defines a refusal as an
operation where "the caller is told why, and nothing was attempted"; on the way out of the
media mount, that has not been true.

## Decision

**A mount is asked before a path leaves it, and the question crosses the seam like every
other operation.**

`fs_move_out_check` becomes a thirteenth entry in `FileSystemOperations.All`. Its `ToolKey`
and `Capability` are null, like the two byte-streaming operations: it is transfer
machinery, not something the model calls, so it appears in no mount's capability list. Its
backend method sits on `IFileSystemBackend`, and `FileSystemBackendBase` defaults it to
allowed — a backend declares that it has a rule by overriding, and `AddFileSystemTools`
registers the tool for exactly those backends.

An ok payload means allowed. An error envelope means refused, and carries its code,
message, hint and retryability to the agent unchanged, which is how every other refusal on
this mount already travels.

`McpFileSystemBackend` implements the method itself: it calls the wire tool when its client
advertised it, and answers allowed when it did not. A server's advertised tool set is
already the single source of truth for what a mount can do, so a mount that never
registered the check has nothing to say and its moves proceed as before. No caller asks
whether a backend has a rule — the shape that produced the dead type test is gone rather
than corrected.

The check is asked **once, on the source, under `deleteSource`**, inside
`TransferFileAsync` and `TransferDirectoryAsync`, before any byte is streamed and before
the directory listing. Once is enough for the media mount: its predicate overlaps in both
directions, so an ancestor of a live download's directory and anything inside one both
refuse. Only the source is asked; the destination end already answers for itself when the
first chunk arrives.

`MediaLibraryDiskFileSystem` overrides the check by asking the rule ADR-0014 established —
now reachable from the side of the seam the agent is on — for **both intents the streamed
move is made of**: `MoveOut`, and then `Delete`. A streamed move ends by deleting the
source, so a path this mount would refuse to delete can never finish one. Asking only
`MoveOut` would answer "allowed" for every ordinary media path and then fail at the tail
delete, after the bytes, which is the shape this ADR exists to remove.
`ICrossMountMoveGuard`, `RefuseMoveAsync` and `VfsMoveTool.CrossMountRefusalAsync` are
deleted.

## Considered options

**Lean on the refusals that already exist.** The destination side is covered, and the
source's tail delete refuses payload files on its own. Rejected because it does not cover
the case that matters: `fs_delete` on `downloads/<id>` is the cancel, so the delete
permits exactly the path a move must refuse. It also cannot honour "nothing was attempted"
— the file is already copied by the time anything says no.

**Refuse at the delete, and teach `fs_delete` that a delete can be the tail of a move.**
One fewer operation, and the distinction lives where the damage happens. Rejected: it adds
a second meaning to an operation every backend implements in order to fix one mount, and
it still streams the bytes first. A move refused after a full copy of an in-flight download
leaves garbage on the destination that the agent then has to clean up.

**Reuse `fs_move` as the question**, calling it with a destination the backend can see is
off its mount. No new operation, but it overloads an operation that performs an effect with
one that only asks, on a contract every server implements independently — a backend that
did not recognise the convention would attempt a real move.

**Fail closed when a mount does not advertise the check.** The safest reading of silence.
Rejected because it would block every cross-mount move out of vault, sandbox, timers,
printer and Home Assistant, none of which will ever implement a rule.

## Consequences

- A cross-mount move of a live download's directory is refused before the glob, so the
  download is not cancelled and no partial copy is created. A move of a payload file out of
  one is refused before the copy exists rather than after.
- Nothing else moves off the media mount either, because `fs_delete` there removes only a
  download directory and a leftover `status.json`. That was already true — the move simply
  failed at the end instead of the start, leaving a duplicate on both mounts. Copying off
  the mount is unaffected: a copy has no delete to fail at.
- Capability-by-overriding stays the one mechanism for what a backend can do. A backend
  answering a question it was never taught is now a compile-time default rather than a
  runtime type test.
- The check is coarser than the operation it guards: it asks about one path, not about the
  pair, so a backend cannot refuse a move only for particular destinations. No mount wants
  that today, and adding the destination would re-create the double-asking the `Land`
  refusal already covers.
- A download that goes live during a directory transfer is not caught — the check runs once,
  before streaming. The window is the duration of one transfer, and the alternative is a
  round trip per file.
- Integration coverage needs a topology nothing exercised before: a media mount alongside
  another filesystem, mounted through the real proxy, which is where every cross-mount bug
  lives. The existing multi-filesystem fixture already hosts two in-process servers for the
  cross-mount move tests and gains a third for the media library.
