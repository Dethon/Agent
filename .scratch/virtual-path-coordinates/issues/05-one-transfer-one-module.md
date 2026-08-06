# 05 — One transfer, one module

**What to build:** no behaviour change. A transfer becomes one thing with one name, and
the copy tool stops being the home of the move rules.

The machinery that performs a transfer lives inside the copy tool as a pair of internal
statics taking eight parameters, and the move tool reaches into it. A boolean tells it
which of the two it is performing, and that boolean gates three separate rules: whether to
ask the source's move-out question, whether to delete the source afterwards, and whether a
directory that streamed nothing counts as done. Both tools duplicate the probe that
decides file versus directory before calling in.

Move the machinery into a module of its own with one entry point. It owns the file-versus-
directory decision, so both tools shrink to: resolve source, resolve destination,
delegate, serialize. It returns the typed result inside the standard result union, so
errors stay typed until the tool boundary and the tools serialize at the end the way every
other tool does. The boolean becomes a two-value intent, so the three rules it gates read
as move rules rather than as flag checks.

**Blocked by:** 04 — Copy and move answer with a typed result.

- [ ] A transfer is performed by one module with one entry point, not by statics inside
      the copy tool.
- [ ] That module owns the file-versus-directory decision; neither tool probes for it any
      more.
- [ ] Copy and move each shrink to resolving both ends, delegating, and serializing. The
      only difference between them is the intent they pass.
- [ ] Copy or move is expressed as a two-value intent, not a boolean, and the three rules
      it gates read as rules about a move.
- [ ] The module returns the typed result inside the standard result union; error
      envelopes are no longer built as raw nodes inside the machinery.
- [ ] The existing transfer tests still drive the tools rather than the module, and pass
      with no change to what they assert.
- [ ] The conformance test from ticket 03 stays green throughout.
- [ ] `CONTEXT.md` defines **transfer**: a copy or move across any two virtual paths, same
      mount or not, file or directory — distinct from a backend's native copy and move
      primitives. The intent type gets no glossary entry.

**Status:** ready-for-agent

## Comments

From the spec at `.scratch/virtual-path-coordinates/spec.md`.

The module gets no tests of its own. The existing directory-transfer and delete-source
tests already drive the tools rather than the internal machinery, so extracting it changes
nothing about how it is covered. A second seam for the same machinery would only duplicate
what the tool-level tests already assert.

This is the largest change in the set and it lands last, as a pure refactor against a
conformance test that is already green.

The review that started this listed the transfer machinery as its own deepening candidate.
It is absorbed here because typing the result in ticket 04 rewrites these signatures
anyway.
