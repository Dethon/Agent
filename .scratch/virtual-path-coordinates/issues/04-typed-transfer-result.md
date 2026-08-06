# 04 — Copy and move answer with a typed result

**What to build:** a copy and a move answer in one recognisable shape, whether they moved
one file or a whole directory, and a same-mount move stops reporting a negative byte
count.

Every other filesystem tool answers with a typed result that the payload contract
validates. The two transfer tools assemble their response by hand instead, so nothing
checks their shape, the file branch and the directory branch answer differently, and a
same-mount move reports minus one bytes because the backend's native move primitive
carries no byte count.

Give them a tool-level result type. It is deliberately not the backend's copy or move
result: those are the wire contract for a backend's native primitive and stay unchanged,
while the tool-level operation spans two mounts, recurses directories, and reports
per-entry outcomes no backend result can express.

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

One type serves both branches. `Bytes` being absent replaces the negative sentinel; the
payload contract omits nulls on serialization, so the model sees no field rather than a
value to interpret.

The transfer machinery stays where it is. Only what it returns changes.

**Blocked by:** 02 — Every tool answers in the coordinates it was asked in.

- [ ] Copy and move both answer with the tool-level transfer result, for a file and for a
      directory alike, and neither assembles a response by hand any more.
- [ ] A same-mount move reports no byte count rather than a negative one.
- [ ] A directory transfer reports its per-entry outcomes inside the typed result, with
      the same partial and failed statuses it distinguishes today.
- [ ] A glob entry that is not under the requested source directory reports no source at
      all, with the backend's raw string in its error message where it reads as
      diagnostics rather than as a path to retry.
- [ ] The type is not added to the one operations list — that list is keyed by backend
      operation and would need a fake entry to hold this. The conformance test maps the
      two tool keys to it explicitly instead.
- [ ] The existing transfer tests still drive the tools, not the machinery, and cover the
      same behaviours through the new shape.
- [ ] The copy and move integration tests pass unchanged in intent.

**Status:** ready-for-agent

## Comments

From the spec at `.scratch/virtual-path-coordinates/spec.md`. The type shape above came
out of the grilling session and is reproduced because prose was a worse way to say it.

Runs in parallel with ticket 03 — both need only ticket 02.

No MCP server changes: the backend result contract for every operation is untouched, so
the wire format does not move.
