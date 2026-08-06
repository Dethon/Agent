# A tool answers in the coordinates it was asked in

Status: ready-for-agent

## Problem Statement

The agent talks to every filesystem in virtual paths: a mount point followed by a path
under it. That is the only spelling the filesystem prompt teaches, and the only one the
registry accepts.

Several tools answer in a different spelling. `remove` reports the path it deleted in the
backend's own coordinates, which for a disk root is the container-absolute path, and
reports a trash location that sits outside every mount. `create` and `edit` report the
file they wrote in whichever spelling their backend happens to use, and the two disagree
with each other on the same mount. `exec` reports a working directory in the backend's
coordinates. The directory branch of `copy` and `move` reports per-entry sources in
mount-relative form, without the mount point.

So the model's obvious next move fails. It removes a file, reads back the path the
response gave it, feeds that to `text_read`, and gets "No filesystem mounted for path".
Nothing about the response says the path was not reusable.

The same defect has already been fixed twice, months apart, on whichever tools were
noticed at the time: once for `info` and `read`, once for `text_search`. Each fix added
another private normalizer to another tool. There are four of those now, no shared
implementation, and nothing that fails when the next tool forgets.

## Solution

One invariant, stated once and enforced by one test:

> A tool answers in the coordinates it was asked in. Every path in a response is either
> the caller's own string echoed back, or a backend entry with its mount point prefixed.
> The backend's own spelling never reaches the model.

Two things follow. Every tool that names a path the caller passed echoes that string
instead of whatever the backend returned. Every tool that reports paths the backend
produced, which the caller never named, prefixes the mount point through one shared
translation.

A conformance test drives all ten filesystem tools against a backend that answers in
deliberately hostile coordinates and fails if any path in any response is not a virtual
path. A new tool, or a new operation, cannot skip it.

Along the way `copy` and `move` stop hand-rolling an untyped response and start
answering with a real result type, which means the transfer machinery they share moves
out of the copy tool into a module of its own.

## User Stories

1. As the agent, I want the path `remove` reports to be a virtual path, so that I can
   feed it to another filesystem tool without the call being refused.
2. As the agent, I want the path `create` reports to be the path I asked it to create, so
   that a follow-up edit targets the same file.
3. As the agent, I want the path `edit` reports to be a virtual path, so that reading back
   what I just edited does not fail.
4. As the agent, I want `create` and `edit` on the same mount to agree on how a path is
   spelled, so that I do not have to guess which one is reusable.
5. As the agent, I want every glob entry to carry its mount point, so that I can pass any
   result straight into read, edit or info.
6. As the agent, I want every search hit to carry its mount point, so that the obvious
   next call after a search works.
7. As the agent, I want per-entry sources in a directory transfer to be virtual paths, so
   that I can retry a failed entry without reconstructing its path myself.
8. As the agent, I want a transfer's error messages to name paths the way I named them,
   so that I can tell which of my arguments the message is about.
9. As the agent, I want a response to never contain a path I cannot use, so that I do not
   spend a turn discovering that by trial.
10. As the agent, I want `remove` to not report a trash location I cannot act on, so that
    I do not attempt to read or restore from it.
11. As the agent, I want a same-mount move to report no byte count rather than a negative
    one, so that I do not reason about a sentinel value.
12. As the agent, I want a file transfer and a directory transfer to answer in one
    recognisable shape, so that I can read either response the same way.
13. As the agent, I want a directory transfer to tell me which entries succeeded and which
    failed in virtual paths, so that a partial transfer is actionable.
14. As a maintainer, I want one implementation of mount-point translation, so that a
    change to the convention is one edit rather than four.
15. As a maintainer, I want a test that fails when a tool answers in backend coordinates,
    so that the third recurrence of this defect is a red test rather than a production
    surprise.
16. As a maintainer, I want that test to be driven by the one operations list, so that
    adding an operation cannot silently skip the rule.
17. As a maintainer, I want the fields that genuinely have no virtual path to be listed
    explicitly with their reasons, so that an exemption is a decision rather than an
    oversight.
18. As a maintainer, I want the backend result contract left alone, so that the MCP wire
    format does not change and no server needs updating.
19. As a maintainer, I want `copy` and `move` to answer with a typed result, so that their
    response shape is validated like every other tool's rather than assembled by hand.
20. As a maintainer, I want the transfer machinery to live in its own module, so that the
    copy tool is not the home of the move rules.
21. As a maintainer, I want copy and move to be told which they are, so that the rules
    that only apply to a move read as move rules.
22. As a maintainer, I want the file-versus-directory decision made once inside the
    transfer, so that both tools stop duplicating it.
23. As a maintainer, I want both tools to shrink to resolve, resolve, delegate, so that
    the difference between copy and move is one value.
24. As a maintainer, I want the existing transfer tests to keep driving the tools, so that
    extracting the machinery does not rewrite the test suite.
25. As a maintainer, I want the invariant recorded as an architectural decision, so that
    a future reader knows why a tool's result type differs from its backend's.
26. As a maintainer, I want "virtual path" pinned in the glossary, so that "coordinates"
    stops being an informal word in review comments.
27. As a maintainer, I want "transfer" pinned in the glossary, so that the tool-level
    operation has a name distinct from a backend's native copy or move.
28. As a maintainer, I want the change delivered as three commits, so that the bug is
    fixed and guarded before the largest refactor begins.

## Implementation Decisions

### The translation

`FileSystemResolution`, today a behaviourless three-tuple of backend, mount-relative path
and mount point, gains one method that prefixes its mount point onto a backend-produced
path, trimming the leading slash the backend may or may not have supplied and preserving
a trailing directory marker.

It is a helper the tools call, not a decorator wrapping the backend. A translating backend
decorator was considered and rejected: it needs the same per-result-type table of which
fields are paths, only placed where no tool can see it, and it interposes on every call
for a formatting concern. What makes forgetting impossible is the conformance test, not
the type.

### Echo versus translate

The two are different operations and only one is a translation.

Where the caller named the path, the tool echoes the caller's own argument. This covers
`read`, `info`, `create`, `edit`, and the original path in `remove`. Translation cannot be
used here: at least one backend answers with the container-absolute path, and prefixing a
mount point onto that produces nonsense.

Where the backend produced paths the caller never named, the tool translates. This covers
glob entries, search hit files, and per-entry sources and destinations in a directory
transfer.

Backends keep their existing conventions. Normalising every backend to answer
mount-relative was considered and rejected as a wider blast radius for no additional
guarantee, since the invariant holds at the tool boundary either way.

### Fields with no virtual path

The trash location a disk root reports sits outside every mount, so no virtual path for it
exists. Three backends already return it empty. The disk root joins them; nothing reads
the field.

The working directory an exec reports is the backend's own, filled by four different
backends with four different meanings. It is exempt, named as such where the rule is
enforced.

### The transfer result

`copy` and `move` currently assemble an untyped response by hand, so their shape is never
validated. They gain a tool-level result type. It is deliberately not the backend's copy
or move result: those are the wire contract for a backend's native primitive and stay
unchanged, while the tool-level operation spans two mounts, recurses directories and
reports per-entry outcomes that no backend result can express.

The shape, from the grilling session, trimmed to the decisions it encodes:

```
FsTransferResult
    Status        required     ok | partial | failed
    Source        required     virtual path
    Destination   required     virtual path
    Bytes         optional     omitted when a native move ran and measured nothing
    Summary       optional     directory transfers only
    Entries       optional     directory transfers only
```

One type serves both the file and the directory branch. `Bytes` being absent replaces the
negative sentinel a same-mount move reports today; the contract omits nulls on
serialization, so the model sees no field rather than a value to interpret.

The type is not added to the one operations list, which is keyed by backend operation and
would have to invent a fake thirteenth entry to hold it. The conformance test maps the two
tool keys to it explicitly instead.

### The transfer module

The transfer machinery moves out of the copy tool into a module of its own with one entry
point taking a request and returning the typed result inside the standard result union,
so errors stay typed until the tool boundary and the tools serialize at the end like every
other tool does.

It owns the probe that decides file versus directory, which both tools duplicate today.
Both tools shrink to: resolve source, resolve destination, delegate, serialize.

The boolean that distinguishes a copy from a move becomes a two-value intent. It gates
three separate rules today, and each reads as a move rule rather than as a flag check once
it is named.

One directory-transfer failure has no translatable source: a glob entry that is not under
the requested source directory is by definition outside the coordinate frame. That entry
reports no source and puts the backend's raw string in its error message, where it reads
as diagnostics rather than as a path to retry.

### Sequencing

Three commits, in order.

1. The translation helper, the four leaking tools fixed, the trash field blanked.
2. The conformance test.
3. The transfer module, the transfer result type and the intent.

The leak is closed and guarded before the largest refactor starts.

### Documentation

One ADR for the invariant. The result-type split follows from it rather than standing
alone: a tool's frame is not its backend's frame, so the types differ.

Two glossary entries: **virtual path**, the only coordinate system that crosses the tool
boundary, with backend coordinates named as what must never appear in a response; and
**transfer**, a copy or move across any two virtual paths, same mount or not, file or
directory, distinct from a backend's native primitives. The intent type gets no glossary
entry.

The virtual-filesystem rule file gains the invariant alongside the existing
capability-by-overriding and move-out-question rules.

## Testing Decisions

A good test here asserts on what the model receives and nothing else. The subject is the
JSON a tool returns; the backend is a stand-in that answers however the test needs. No
test should reach for the translation helper, the transfer module or a backend method
directly, because none of those is what the invariant is about.

**One seam: the tool entry points.** Each filesystem tool is constructed over a mocked
registry that resolves to a mocked backend, invoked, and its returned JSON inspected.
Every existing test in the filesystem tool suite already works this way, so the
conformance test is new content at an established seam rather than a new seam.

**The conformance test** is driven by the one operations list, mirroring how the server
conformance test, the payload-type table, the capability map and the tool feature's key
set are all derived from it, so an operation added without a matching case is impossible.
A table maps each tool key to an invoker, in the same shape the server conformance test
uses for its backends. Because the two transfer tools answer with a type that is not a
backend operation's result, they are mapped in explicitly.

The stand-in backend answers hostilely for every operation: the container-absolute
spelling, the leading-slash mount-relative spelling, and the bare mount-relative spelling.
The assertion is that every path-shaped field in the response begins with the mount point,
or appears on the exemption list. The exemption list is a small table in the test file with
one line of reason per entry, so an exemption is spent where the rule is enforced.

**The four leaking tools** each get a test showing the reported path is usable, in the
style the existing info, read and search tests already use for the same property.

**The transfer module** gets no tests of its own. The existing directory-transfer and
delete-source tests already drive the tools rather than the internal machinery, so
extracting it changes nothing about how it is covered. The existing copy and move
integration tests stay as they are.

**Prior art** to follow: the server conformance test for the derived-from-one-list table
shape and the written-out expectations that keep the code under test from also being the
yardstick; the info, read and search tool tests for asserting a reported path is a virtual
path; the transfer directory and delete-source tests for driving a transfer through the
tool with mocked backends; the shared backend mock helpers for a stand-in that answers the
move-out question the way a real backend's default does.

Red-green-refactor throughout, one commit per triplet.

## Out of Scope

Backend error prose. A jail refusal names the resolved path and the mount root in the
backend's own coordinates, and that string crosses the MCP wire with no mount context on
the far side. Rewriting it means threading the mount point into every backend. Only
messages a tool builds itself are covered, because those are constructed in the agent
process with the resolution in hand.

The backend result contract. The wire format for every backend operation is unchanged, so
no MCP server needs touching.

Backend path conventions. Backends keep answering in their own coordinates; the invariant
is enforced where the model can see it.

The exec working directory. Exempt, for the reasons above.

A real-topology integration fixture. No fixture today mounts a media library plus a second
filesystem, and building one is the topology work the cross-mount move candidate calls
for, not this.

The other architecture-review candidates. The downloads refusal rule and the cross-mount
move guard are separate specs. This one touches the transfer machinery only as far as
typing its result requires.

## Further Notes

This is candidate 6 from the 2026-08-05 deepening review, reshaped by the grilling session
that followed it. Three of the review's claims did not survive checking and the spec
reflects the corrected version: `copy` and `move` do not leak on their success path, since
they already echo the caller's own strings, but their directory branch does leak in two
places; the four private normalizers are two different operations, not four copies of one;
and the tool count is four leakers, not five.

The review recommended the translating decorator. This spec rejects it, for the reason
given under Implementation Decisions.

Scope grew during grilling. The review scoped this as deduplicating four helpers. The work
that justifies it is the leak, and typing the transfer result pulled in the transfer-module
extraction that the review had listed separately, because typing the result rewrites those
signatures anyway.

Severity was not measured against production transcripts. The prod conversation history
would show a remove followed by a failed read on the reported path, which would have
decided whether to do this at all. That check was skipped because the decision to do it
was already made.
