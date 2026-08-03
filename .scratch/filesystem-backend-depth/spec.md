# Spec — Give the Filesystem Backend an Implementation

Status: done

## Problem Statement

The agent reaches every filesystem through one contract. That contract names twelve operations and provides none of them, so each of the five backends behind it re-derives the same handful of behaviours by hand. They have already drifted apart, and the drift is visible to the user.

The timers filesystem advertises an operation it cannot perform. Its server registers a move tool whose own description says "Unsupported on timers", capability derivation reads the registered tool names, and the system prompt then tells the model that `/timers` supports move. The model spends a turn calling it and gets back an unsupported-operation envelope. Avoiding exactly that wasted turn is why the capability list exists.

The search operation is unsafe on two mounts and safe on two others. Schedules and timers compile a user-supplied search regex with no match timeout and no guard around compilation; print-queue and Home Assistant both pass a timeout. A search on the wrong mount can hang a turn or surface a raw exception instead of an error envelope.

Glob means different things per mount. The tool tells the model that a base path scopes the search and that a trailing slash asks for directories only. The print-queue backend implements neither, so the same call returns different results depending on which mount it targets.

Error text leaks internals. Home Assistant's unsupported responses name the C# method rather than the operation the model called, so the model is told about `CreateAsync` when it invoked `text_create`.

Under the backends, the disk-backed servers work a different way again. They throw, and the throw is converted to an error envelope at the MCP boundary by a separate mapping table that must be kept in sync with the domain's error codes by hand. Eight copies of the path-jail check guard those tools, using three different string-comparison rules between them, and the check compares path prefixes without a separator, so a root of `/library` admits `/library-backup`.

Resolution of a virtual path to a mount throws when nothing matches. Nothing in the twelve tool call sites catches it. The prompt promises the model that errors arrive as data rather than as exceptions, and the single most likely mistake — a path with no mount prefix, the mistake the prompt itself warns about — breaks that promise at every site at once.

Sixty-four wrapper files exist only to attach an MCP name and description to a call that is already implemented. Six separate lists enumerate the same twelve operations, and each must be edited together for a new operation to work.

## Solution

Give the contract an implementation, and derive everything else from it.

A base class implements all twelve operations as unsupported and provides the pieces every backend was copying: the error envelopes, the glob prologue including the base path and the dirs-only rule, a search regex compiled with a timeout and guarded against a bad pattern, and the search template itself. A backend overrides only what it can really do.

Capability then stops being declared. The registrar reflects over which methods a backend actually overrides and registers an MCP tool for exactly those. Timers stops overriding move, so `/timers` stops advertising move, and the lie is no longer expressible rather than merely corrected.

Each operation's description becomes an optional hook on the backend, so the words the model reads sit next to the behaviour they describe. The per-server text that names real files — schedules' `schedule.json`, timers' `status.json`, Home Assistant's `state.json` and its shell entries — survives word for word.

The disk-backed tools are rewritten to produce the same result type as everything else, so there is one error model from the tool to the model instead of one on each side of a translation. The boundary's exception mapping is deleted with the translation. The path jail becomes one value type built from a canonical root, which closes the prefix hole and settles the three comparison rules into one.

Path resolution returns a result instead of throwing, so an unmounted path arrives at the model as the error envelope the prompt promises.

The six operation lists collapse into one derived from the base, and validation of an unknown tool name fails instead of passing silently.

## User Stories

1. As a user, I want the model to be told only about operations a mount can really perform, so that my request is not delayed by a turn spent on a guaranteed failure.
2. As a user, I want a search on any mount to return in bounded time, so that a search phrased as a pattern cannot stall my turn.
3. As a user, I want a malformed search pattern answered with an error I can act on, so that a typo does not end the turn.
4. As a user, I want a glob to mean the same thing on every mount, so that a base path scopes the search wherever I point it.
5. As a user, I want a trailing slash to list directories on every mount, so that the convention the tool advertises is true everywhere.
6. As a user, I want a path with no mount prefix answered with an error envelope, so that the most common path mistake costs one corrected call rather than a failed turn.
7. As a user, I want unsupported-operation messages to name the operation I called, so that the reply tells me what to do instead.
8. As a user, I want files under a mount root to be reachable and files outside it refused, so that a directory whose name merely starts with the root's name is not treated as inside it.
9. As a user, I want the library's downloads view to keep working exactly as it does now, so that this work is invisible to me where nothing was wrong.
10. As an operator, I want one error model from backend to model, so that a failure reads the same wherever it came from.
11. As an operator, I want a search that times out reported as a structured failure, so that it is distinguishable from an empty result.
12. As a maintainer, I want the shared behaviour of a backend written once, so that fixing it once fixes it for every mount.
13. As a maintainer, I want a backend to declare what it supports by overriding it, so that there is no second place for the declaration to drift from.
14. As a maintainer, I want the MCP tool surface generated from the backend, so that a server cannot register a tool its backend does not implement.
15. As a maintainer, I want a test that fails when any server's advertised tools disagree with its backend's real capabilities, so that the drift this work removes cannot come back.
16. As a maintainer, I want each operation's description next to its implementation, so that changing behaviour and changing the words the model reads are one edit.
17. As a maintainer, I want the per-server description text preserved exactly, so that mounts whose usefulness depends on naming their real files keep it.
18. As a maintainer, I want the disk tools to return the shared result type directly, so that no mapping table needs to stay in sync with the error codes.
19. As a maintainer, I want the filesystem path to stop depending on the boundary's exception-to-envelope conversion, so that an unmapped exception type cannot silently become the wrong error code there.
20. As a maintainer, I want one path jail built from a canonical root, so that containment is decided one way rather than three.
21. As a maintainer, I want path resolution to return a result, so that the twelve tool sites do not each need a guard against an exception.
22. As a maintainer, I want the sixty-four wrapper files deleted, so that adding an operation is not sixty-four edits.
23. As a maintainer, I want one list of operations, so that a new operation cannot half-exist because one of six lists was missed.
24. As a maintainer, I want payload validation to fail on an unknown tool name, so that a typo in a name is caught rather than passed.
25. As a maintainer, I want the two search-output enumerations reduced to one, so that their agreement stops being an unchecked coincidence.
26. As a maintainer, I want the three untested domain tools covered, so that read, remove and search have the same footing as the rest.
27. As a maintainer, I want capability treated as per-operation rather than per-path, so that nobody later refines it into a check it cannot answer.
28. As a maintainer, I want the library's downloads composition kept as a composition, so that it is not flattened into a root-path wrapper it cannot be.
29. As a developer adding a filesystem, I want to subclass the base and override what I support, so that the work is proportional to what the filesystem actually does.
30. As a developer adding an operation, I want to add it to the base and have the surface follow, so that the registrar, the capability list and validation need no separate edit.

## Implementation Decisions

**A base class in the domain implements the backend contract.** All twelve operations are virtual and return an unsupported envelope by default. A backend overrides only the operations it implements.

**Capability is derived from which methods are overridden.** The registrar reflects at startup over each method's declaring type and registers an MCP tool only where the backend overrode it. Nothing is declared, so nothing can drift. The timers backend stops overriding move, and its move tool and description disappear with it.

**Capability is per operation, not per path.** A backend may override an operation and still return unsupported for particular paths. That coarseness is intended: the capability list tells the model which operations exist on a mount, not which will succeed on a given file. Assert it deliberately so it is not later refined into a per-path check the registrar cannot answer.

**The base provides what the backends were copying.** Unsupported, not-found, invalid and read-only envelopes; the glob prologue that applies the base path and the trailing-slash dirs-only rule; a search regex compiled case-insensitively with a match timeout and a guard around compilation; and the search template the backends each reimplemented. The four non-disk backends are reparented onto it and each loses its copy.

**Reparenting the four backends is a behaviour change in four places,** each of which gets its own failing test first: schedules and timers gain the regex timeout and the compilation guard; print-queue gains base-path scoping and the dirs-only rule; Home Assistant's unsupported messages name the operation the model called instead of the C# method.

**Descriptions are optional virtuals on the backend,** one per operation, with generic defaults on the base. Per-server text is real and must survive verbatim: the schedules and timers file names, and Home Assistant's state file and shell-entry usage. `.claude/rules/mcp-tools.md` governs naming and description style.

**The disk tools return the shared result type natively rather than throwing,** leaving one error model end to end on the filesystem path. The boundary's exception-to-envelope conversion is not deleted wholesale: every server installs it as a catch-all filter over all its tools, including two servers with no filesystem, so removing it would strip envelopes from property search, web search and Home Assistant service calls. What goes is the filesystem path's dependence on it and the mapping arms specific to filesystem exception types.

**The path jail is hoisted into one value type built from the canonical root,** replacing eight copies and three different string-comparison rules. A prefix must be followed by a separator to count as containment, which is what closes a sibling directory whose name extends the root's.

**A disk-backed filesystem class subclasses the base,** parameterised by a root and an optional downloads overlay. The library composes an overlay; the vault and the sandbox do not. It stays a composition and is not flattened into a root-path wrapper.

**The wrappers are deleted one server per commit,** after the registrar and the disk-backed class exist, because the disk-backed servers have no backend object to register from until then.

**Path resolution returns a result instead of throwing.** An unmounted path produces an error envelope at each of the tool sites rather than an exception.

**The six operation lists collapse to one derived from the base:** the contract's own signatures, the payload-type table, the discovery capability map, the session's tool-name filter set, and the tool feature's key set and factory array. Payload validation fails closed on an unknown tool name rather than returning success.

**The domain's own search-output enumeration is deleted** in favour of the virtual filesystem's. The native rewrite removes the string round-trip that made the two enums agreeing unchecked.

## Testing Decisions

A good test here asserts what the model or the operator would see: the tools a server advertises, the envelope a call returns, the entries a glob yields, and the text of a description. It does not assert that a particular helper on the base was called, and it does not read a backend's private state to decide whether an operation is supported.

**Four seams, all but one of them existing.**

*The conformance seam is new and is the point of the work.* One unit file builds each server's dependency registration and asserts, per server, that the advertised `fs_*` tool names equal the operations its backend overrides, and that both equal the capabilities the mount publishes. The timers move lie is a failing case in it on day one. A future server that registers a tool its backend does not implement fails here. The prior art for building a server's registration in a unit test is the timers configuration-module test.

*The four backend divergences are asserted in each backend's own existing file* — the timers and schedules journey tests, the print-queue backend tests, and the Home Assistant search tests. Each fix gets a failing test local to it, using fixtures those files already have. The trade accepted: nothing pins the shared behaviour as shared, so a sixth backend could reintroduce a divergence without a test noticing. The conformance seam catches the capability half of that, not the semantics half.

*The unmounted-path promise is asserted across all ten tools through the tool feature's existing test file.* Each of the ten functions the feature produces is invoked with a path that matches no mount, and every one must return an error envelope rather than throw. This is the surface the model actually calls, and it covers all twelve call sites in one place.

*The disk rewrite happens behind the tests that already exist.* The per-tool test files for glob, move, remove, copy and the blob operations keep their cases and swap exception assertions for envelope assertions. The tool classes survive as the internals of the disk-backed filesystem. The trade accepted: those tests pin that internal composition, so a later reshuffle of the classes can break tests without a behaviour change. There is no follow-up consolidation ticket; per-tool is the resting state.

**New coverage.** The three domain tools with no tests — read, remove and search — get files alongside their existing siblings. Search has real branching worth covering: it chooses between a file path and a directory path and must report an invalid argument when neither is given.

**Prior art.** The timers configuration-module test is the model for building a server's registration in a unit test. The prompt tool-name consistency test is the model for a test whose whole job is that two lists agree. The journey tests for schedules and timers are the model for asserting a backend's behaviour through its public operations.

**What stays put.** The integration tests over hosted vault, library and sandbox servers keep passing unchanged; they are the check that deleting the wrappers did not change the wire. The brace-expander, path and renderer unit tests are untouched.

## Out of Scope

- Adding filesystem operations. The contract stays at twelve.
- Making capability per path. Capability is per operation by decision.
- Changing any mount's set of real capabilities beyond removing timers' move, which was never implemented.
- Changing mount points, mount metadata or the longest-prefix resolution rule.
- The library's downloads overlay semantics. It is composed into the disk-backed filesystem unchanged.
- Reworking the agent-side MCP backend that speaks to remote servers, beyond the capability map collapsing into the shared list.
- The system prompt's wording about capabilities and error envelopes. The prompt already says the right thing; this work makes it true.
- Consolidating the per-tool disk tests into one filesystem-level test file.
- Performance work on glob or search.

## Further Notes

**The disk rewrite is the bulk of the work and the bulk of the risk.** The text-search tool is the largest single file in it. Rewrite it behind its existing tests first, then change the return type; the two steps must not be one commit.

**The wrappers are two populations and only one can be deleted early.** The backend-delegating servers — timers, scheduling, Home Assistant and most of printer — have a backend object to register from as soon as the registrar exists. The disk-backed servers — vault, sandbox, library, and printer's blob read — have none until the disk-backed filesystem class is built, which is why that class comes before any deletion there.

**Reflection over declaring types must tolerate a backend that overrides an operation and still refuses some paths.** That is correct behaviour, not a gap.

**Design decisions were settled by interview** and are recorded in this feature's plan document, including why capability is derived rather than declared and why the disk tools are rewritten rather than wrapped.
