# 0016 — A tool answers in the coordinates it was asked in

Status: accepted
Date: 2026-08-06

## Context

The agent talks to every filesystem in virtual paths: a mount point followed by a path
under it. That is the only spelling the filesystem prompt teaches, and the only one
`VirtualFileSystemRegistry.Resolve` accepts.

Backends answer in their own coordinates, and they do not agree with each other. A disk
root reports the container-absolute path. Some virtual filesystems report a leading-slash
mount-relative path, others a bare one. That is fine on the wire — the mount point is not
the backend's business — and it is a defect the moment a tool passes it through.

Several tools did. `remove` reported the path it deleted in the backend's spelling, plus a
trash location that sits outside every mount. `create` and `edit` reported the file they
wrote in whichever spelling their backend used, and the two disagreed on the same mount.
The directory branch of a transfer reported per-entry sources without their mount point.

So the model's obvious next move failed. It removed a file, read back the path the response
gave it, fed that to `text_read`, and got "No filesystem mounted for path". Nothing in the
response said the path was not reusable.

The same defect had already been fixed twice, months apart, on whichever tools were noticed
at the time: once for `info` and `read`, once for `text_search`. Each fix added another
private normalizer to another tool. There were four of them, no shared implementation, and
nothing that failed when the next tool forgot.

## Decision

**A tool answers in the coordinates it was asked in. Every path in a response is either the
caller's own string echoed back, or a backend entry with its mount point prefixed. The
backend's own spelling never reaches the model.**

The two halves are different operations, and only one of them is a translation.

Where the caller named the path, the tool **echoes the caller's own argument** — `read`,
`info`, `create`, `edit`, and the original path in `remove`. Translation cannot be used
here: at least one backend answers with the container-absolute path, and prefixing a mount
point onto that produces nonsense.

Where the backend produced paths the caller never named, the tool **translates** through
one shared implementation, `FileSystemResolution.ToVirtualPath`. This covers glob entries,
search hit files, and the per-entry sources and destinations of a directory transfer. The
leading slash a backend may or may not have supplied is trimmed; a trailing one marks a
directory and is preserved.

Backends keep their existing conventions. Normalising every backend to answer
mount-relative was considered and rejected as a wider blast radius for no additional
guarantee — the invariant holds at the tool boundary either way.

What makes forgetting impossible is a test, not a type.
`Tests/Unit/Domain/Tools/FileSystem/VfsVirtualPathConformanceTests.cs` drives every
filesystem tool against a backend answering in three deliberately hostile spellings and
fails if any path in any response is not a virtual path. Its tool set is derived from
`FileSystemOperations.All`, so an operation added without a case is impossible.

Two fields have no virtual path and are named as exemptions where the rule is enforced. The
trash location a disk root reports sits outside every mount; three backends already
answered empty and the disk root joins them, since nothing reads it. The working directory
an `exec` reports is the backend's own, filled by four backends with four different
meanings.

## A tool's result type is not its backend's

This is why `copy` and `move` answer with `FsTransferResult` rather than with the backend's
`FsCopyResult` or `FsMoveResult`. Those are the wire contract for a backend's native
primitive: one mount, one call, paths in that backend's coordinates. The tool-level
operation spans two mounts, recurses directories, and reports per-entry outcomes that no
backend result can express — in virtual paths, because that is the frame it was asked in.

The same reasoning applies to any future tool whose answer is not simply its backend's
answer relabelled. The frames differ, so the types differ.

## Considered options

**A translating decorator over the backend.** Wrap every backend at mount time and rewrite
paths on the way out. Rejected: it needs the same per-result-type table of which fields are
paths, only placed where no tool can see it, and it interposes on every call for a
formatting concern. It also cannot express the echo half — a decorator does not know what
string the caller passed.

**Normalise every backend to answer mount-relative.** Rejected as a wider blast radius for
no additional guarantee. Every MCP server would need touching, the wire format would move,
and the tool boundary would still be the place the invariant has to hold.

**Rewrite backend error prose too.** Out of scope. A jail refusal names the resolved path
and the mount root in the backend's own coordinates, and that string crosses the MCP wire
with no mount context on the far side; fixing it means threading the mount point into every
backend. Only messages a tool builds itself are covered, because those are constructed in
the agent process with the resolution in hand.

## Consequences

- Any path the model reads out of a filesystem response can be fed straight back into
  another filesystem tool.
- Mount-point translation has one implementation, so a change to the convention is one edit
  rather than four.
- A tool that starts answering in backend coordinates fails a test rather than reaching
  production.
- An exemption is a decision spent in the test file with a line of reason, rather than an
  oversight nobody notices.
- The backend result contract is unchanged, so no MCP server needed updating.
