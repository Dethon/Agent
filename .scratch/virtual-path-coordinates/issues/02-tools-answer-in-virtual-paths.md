# 02 — Every tool answers in the coordinates it was asked in

**What to build:** the model can take any path out of any filesystem tool response and
feed it straight into another filesystem tool, and the call works.

Today it cannot. Remove a file and the response names the path in the backend's own
spelling, which for a disk root is the container-absolute one; feed that to a read and the
registry answers "No filesystem mounted". Create and edit disagree with each other on the
same mount about how a path is spelled. Exec reports a working directory in the backend's
coordinates. The directory branch of a transfer reports per-entry sources without their
mount point.

Establish one invariant and make every tool obey it:

> A tool answers in the coordinates it was asked in. Every path in a response is either
> the caller's own string echoed back, or a backend entry with its mount point prefixed.
> The backend's own spelling never reaches the model.

The two halves are different operations. Where the caller named the path, echo the
caller's argument — translation cannot be used, because at least one backend answers with
the container-absolute path and prefixing a mount point onto that produces nonsense. Where
the backend produced paths the caller never named, translate through one shared
implementation that lives on the resolution the registry already returns.

Two fields have no virtual path and are handled rather than translated. The trash location
a disk root reports sits outside every mount; three backends already return it empty and
the disk root joins them, since nothing reads it. The working directory an exec reports is
the backend's own, filled by four backends with four different meanings; it stays as it is
and is documented as exempt.

**Blocked by:** 01 — A result can be mapped without unwrapping.

- [ ] The resolution the registry returns can translate a backend-produced path into a
      full virtual path, trimming whatever leading-slash convention the backend used and
      preserving a trailing directory marker.
- [ ] Remove, create, edit and exec no longer put a backend-local path in a response.
- [ ] The four tools that already normalize (info, read, glob, search) go through the
      shared translation instead of their own private copies, with no change to what they
      answer.
- [ ] The per-entry sources and destinations in a directory transfer are full virtual
      paths, and so are the paths in the error strings the transfer tools build
      themselves.
- [ ] The trash location is empty for every backend.
- [ ] Each fixed tool has a test showing the path it reports resolves — in the style the
      existing info, read and search tests already use for this property.
- [ ] An ADR records the invariant, and notes that it is why a tool's result type may
      differ from its backend's.
- [ ] `CONTEXT.md` defines **virtual path** as the only coordinate system that crosses the
      tool boundary, and names backend coordinates as what must never appear in a
      response.
- [ ] The virtual-filesystem rule file states the invariant alongside the existing
      capability-by-overriding and move-out-question rules.

**Status:** ready-for-agent

## Comments

From the spec at `.scratch/virtual-path-coordinates/spec.md`.

A translating decorator over the backend was considered and rejected: it needs the same
per-result-type table of which fields are paths, only placed where no tool can see it, and
it interposes on every call for a formatting concern. What makes forgetting impossible is
the conformance test in ticket 03, not the type.

Normalizing every backend to answer mount-relative was also rejected — wider blast radius
for no additional guarantee, since the invariant holds at the tool boundary either way.
Backends keep their conventions.

The spec put the transfer directory-branch leak in the transfer work. It is here instead,
so the test in ticket 03 lands green rather than staying red until ticket 05.

Backend error prose is out of scope: a jail refusal names paths in the backend's own
coordinates and crosses the MCP wire with no mount context on the far side. Only messages
a tool builds itself are covered.
