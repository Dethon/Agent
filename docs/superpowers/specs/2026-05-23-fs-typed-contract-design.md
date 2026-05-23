# Design: Shared typed `fs_*` result contract

**Date:** 2026-05-23
**Status:** Implemented
**Branch context:** `filesystem-migration` (PR #42 — HA virtual filesystem)

## Problem

The agent exposes a unified virtual filesystem across MCP servers. Every backend
exposes the same `fs_*` MCP tools (`fs_read`, `fs_glob`, `fs_search`, `fs_info`,
`fs_exec`, `fs_create`, `fs_edit`, `fs_move`, `fs_remove`, `fs_copy`,
`fs_blob_read`, `fs_blob_write`). The **error envelope** is already single-sourced
(`Domain/Tools/ToolError.cs` + `Infrastructure/Utils/ToolResponse.cs`). The
**success payload shape** is not.

Each producer hand-builds `JsonObject`/`JsonArray` literals:

- **Disk backends** (Vault / Library / Sandbox) build payloads in shared base
  tools under `Domain/Tools/Files/` and `Domain/Tools/Text/`. They agree with
  each other only because they share one base class per op — but every shape is
  raw, hand-rolled JSON.
- **Home Assistant** builds the same shapes independently in
  `Domain/Tools/HomeAssistant/Vfs/HaFileSystem(.Exec).cs`, kept aligned only by
  comments such as `// matching the Sandbox/Vault text_read shape`.

Nothing fails at build time when these drift. The agent-side `Vfs*Tool`
consumers (`Domain/Tools/FileSystem/`) are **pure pass-through** — they forward
the backend's raw `JsonNode` straight to the LLM without parsing it — so a shape
mismatch surfaces only as confused model behaviour or a silently dropped field.

Drift this has already caused / currently exists:

- The historical `fs_search` bug where the `results` payload was dropped.
- HA `fs_info` omits `size` / `lastModified` that disk `fs_info` emits.
- HA `fs_glob` returns a bare `string[]`; disk `fs_glob` returns a
  `{files,truncated,total,message}` object once results exceed 200.

## Goals

- A single source of truth for every `fs_*` success payload shape, enforced at
  **compile time** (one typed record per op, referenced by all producers).
- A **runtime guard** (conformance tests) that fails if any backend's output
  stops matching the typed shape — including re-introduced hand-rolled JSON that
  bypasses the records.
- A **strict runtime boundary guard** at the agent's single MCP-client chokepoint
  (`McpFileSystemBackend`) that validates every backend's `fs_*` success payload
  against its DTO and converts a nonconforming payload to a `ToolError` envelope —
  the only mechanism that binds backends written outside this repo, in another
  language, or not yet enrolled in the conformance harness.
- A **published JSON Schema**, generated from the DTOs, as the language-neutral
  contract a non-.NET backend author can validate against.
- Future in-repo backends inherit the contract for free; foreign backends are
  caught at the boundary.

## Non-goals

- No change to the error-envelope contract (`ToolError` / `ToolResponse`); the
  strict boundary guard *reuses* it, it does not redefine it.
- No refactor of the agent-side `Vfs*Tool` consumers — they remain pass-through.
  Enforcement is added one layer below them, in `McpFileSystemBackend` (see
  Approach (c)); the `Vfs*Tool` layer still forwards the (now-validated) node.
- No redesign of payload semantics. Typing **preserves the current wire shape**
  for every op, with two deliberate exceptions noted below: the `fs_glob`
  unification, and HA `fs_exec` gaining `timedOut`/`durationMs`/`cwd` (HA exec is
  improved to populate them, matching BashRunner, instead of making them optional).

## Approach

Two complementary mechanisms.

### (a) Compile-time single source of truth

Define one typed result record per op in `Domain/DTOs/FileSystem/`. Every
producer constructs the record and serializes it through one shared
`JsonSerializerOptions`. Renaming a field, changing its type, or omitting a
required field becomes a compile error across all producers simultaneously.

### (b) Runtime conformance tests

A cross-backend test harness drives each op against each backend (disk via a temp
dir / fake `IFileSystemClient`; HA via a fake `IHomeAssistantClient` + catalog)
and asserts the success payload **strict-deserializes** back into the record
using `JsonSerializerOptions { UnmappedMemberHandling = Disallow }` plus required
members. Extra fields, wrong casing, or wrong types fail the test. A small set of
invariants is also asserted (e.g. `truncated ⇒ entries capped`,
`matchCount == matches.Count`). Error envelopes (returned on failure) are
distinguished by the `ok:false` field and are validated against the existing
`ToolError` shape, not the result record.

### (c) Strict runtime boundary guard

The compile-time and conformance mechanisms bind only in-repo .NET producers that
choose to use the records, and only backends enrolled in the harness. The single
place that sees **every** `fs_*` response from **every** backend — regardless of
implementation language or repo — is `McpFileSystemBackend.CallToolAsync`
(`Infrastructure/Agents/Mcp/`), the agent's MCP-client adapter. That is where the
contract is enforced at runtime.

A static `toolName → result Type` map (`fs_read → FsReadResult`, …,
`fs_blob_write → FsBlobWriteResult`) is added. After the existing error-envelope
handling (success only — `ok:false` envelopes and the client-side "tool missing"
envelope are passed through untouched), the success payload is strict-deserialized
into the op's record using the shared options with
`UnmappedMemberHandling = Disallow` and required members. On a `JsonException`:

1. Log a warning identifying the backend, op, and offending member.
2. Emit a drift `MetricEvent` via `IMetricsPublisher` *if one is wired into the
   backend* (Observability dashboard visibility); otherwise the log line stands.
3. Return a `ToolError` envelope (`internal_error`, `retryable:false`, with a hint
   naming the malformed op) so the LLM never sees a malformed shape (**strict**).

The `Vfs*Tool` consumers above this layer remain pure pass-through. The blob loop
methods (`ReadChunksAsync`/`WriteChunksAsync`) already branch on an `ok:false`
envelope, so a validation failure surfaced as an envelope flows through their
existing error path unchanged.

### (d) JSON Schema as the published, language-neutral contract

A test generates JSON Schema for each DTO via System.Text.Json's built-in
`JsonSerializerOptions.GetJsonSchemaAsNode(type)` (`System.Text.Json.Schema`,
.NET 9+, available on our .NET 10 target — no extra package) and asserts the
committed `docs/contracts/fs/<op>.schema.json` files match (golden-file test, with
a documented regenerate switch). The DTOs are the source of truth; the schema is a
generated artifact the test keeps in lockstep (a DTO change without regeneration
fails CI); the committed files are the spec a non-.NET backend author validates
against.

## DTO catalog (`Domain/DTOs/FileSystem/`)

Field casing on the wire is camelCase (matches today). `?` marks
nullable/optional (omitted when null via `JsonIgnoreCondition.WhenWritingNull`).
Producers in parentheses.

```
FsReadResult        (TextReadTool, HaFileSystem)
  { string FilePath, string Content, int TotalLines, bool Truncated, string? Suggestion }

FsInfoResult        (FileInfoTool, HaFileSystem)
  { bool Exists, string Path, bool? IsDirectory, long? Size, string? LastModified }
  # HA leaves Size/LastModified null → omitted → HA + disk wire unchanged.

FsGlobResult        (GlobFilesTool, HaFileSystem)   ← only intentional wire change
  { IReadOnlyList<string> Entries, bool Truncated, int Total }

FsSearchResult      (TextSearchTool, HaFileSystem)
  { string Query, bool Regex, string Path, int FilesSearched, int FilesWithMatches,
    int TotalMatches, bool Truncated, IReadOnlyList<FsSearchFileResult> Results }
  FsSearchFileResult { string File, IReadOnlyList<FsSearchMatch>? Matches, int? MatchCount }
  FsSearchMatch      { int Line, string Text, string? Section, FsSearchContext? Context }
  FsSearchContext    { IReadOnlyList<string> Before, IReadOnlyList<string> After }
  # FilesOnly output mode → MatchCount set, Matches null. Content mode → vice-versa.
  # HA never sets Section (entities have no sections) → null → omitted.

FsExecResult        (BashRunner [Infrastructure], HaFileSystem)
  { string Stdout, string Stderr, int ExitCode, bool Truncated,
    bool TimedOut, long DurationMs, string Cwd }   # all required
  # Pre-existing drift: BashRunner emitted all 7 keys; HA emitted only the first 4.
  # Rather than make the extras optional, HA exec is IMPROVED to populate them:
  #   - Cwd        = the entity-directory path the action runs in (the exec `path` arg)
  #   - DurationMs = measured elapsed time of the service call (Stopwatch)
  #   - TimedOut   = HA now HONORS the `timeoutSeconds` arg (previously ignored) via a
  #                  linked CancellationTokenSource; on expiry exitCode=-1, timedOut=true
  # exec JSON is built in Infrastructure's BashRunner (ICommandRunner) and Domain's
  # HaFileSystem.Exec, not Domain's ExecTool wrapper.

FsCreateResult      (TextCreateTool)
  { string Status, string FilePath, string Size, int Lines }   # Size is the formatted string today

FsEditResult        (TextEditTool)
  { string Status, string FilePath, int TotalOccurrencesReplaced, IReadOnlyList<FsEditDetail> Edits }
  FsEditDetail { int OccurrencesReplaced, FsLineRange AffectedLines }
  FsLineRange  { int Start, int End }

FsMoveResult        (MoveTool)
  { string Status, string Message, string Source, string Destination }

FsRemoveResult      (RemoveTool)
  { string Status, string Message, string OriginalPath, string TrashPath }

FsCopyResult        (CopyTool)
  { string Status, string Source, string Destination, long Bytes }

FsBlobReadResult    (BlobReadTool)
  { string ContentBase64, bool Eof, long TotalBytes }   # NB: success has no `ok` field

FsBlobWriteResult   (BlobWriteTool)
  { string Path, long BytesWritten, long TotalBytes }
```

Plus `FsResultSerialization.cs` — the single shared `JsonSerializerOptions`
(camelCase, ignore-null) and a `ToNode<T>(T result)` helper that every producer
calls instead of `new JsonObject { ... }`.

### Field-type notes captured during exploration

- `size` is a `long` (bytes) in `FsInfoResult`/`FsCopyResult` but a formatted
  `string` in `FsCreateResult`. These are distinct ops, so each record types its
  own field; we preserve current behaviour rather than reconcile (out of scope).
- `Status` strings differ per op today (`"created"`, `"copied"`, `"success"`).
  Preserved as-is; typing does not unify the values.

## Wire-shape changes & ripple

Two payloads change on the wire:

1. **`fs_glob`** becomes `{entries,truncated,total}` always (was a bare array, or a
   `{files,truncated,total,message}` object when capped). Ripple:
   - `VfsGlobFilesTool` description and any filesystem prompt text that shows glob
     output (`HomeAssistantPrompt`, Vault/Sandbox filesystem prompts) — one-line
     updates to reflect `entries`.
   - Existing glob tests (`Tests/Unit/Domain/Tools/GlobFilesToolTests.cs`, HA glob
     assertions, integration assertions) — update to the new shape (TDD).
2. **HA `fs_exec`** gains `timedOut`/`durationMs`/`cwd` (additive). This is a
   genuine HA improvement — duration measurement, cwd reporting, and honoring the
   `timeoutSeconds` argument that HA previously ignored. BashRunner already emits
   these, so its wire is unchanged.

All other ops keep their exact current wire shape; the records merely give them a
type and a single serialization path.

## Components / units of work

1. `Domain/DTOs/FileSystem/Fs*Result.cs` records + `FsResultSerialization.cs`.
2. Refactor disk base tools (`Domain/Tools/Files/*`, `Domain/Tools/Text/*`,
   `Domain/Tools/Bash/ExecTool.cs`) to build records via `ToNode`.
3. Refactor `HaFileSystem` + `HaFileSystem.Exec` to build the same records.
4. `Tests/Unit/Domain/Tools/FileSystem/FileSystemContractTests.cs` — the
   cross-backend conformance harness.
5. `McpFileSystemBackend` strict boundary guard: `toolName → Type` map +
   strict-deserialize-or-envelope, optional drift `MetricEvent`.
6. JSON Schema artifacts: committed `docs/contracts/fs/*.schema.json` + a
   golden-file test (`FsContractSchemaTests`) that regenerates from the DTOs and
   compares.
7. Prompt/description touch-ups for the glob shape change.

## TDD outline

Per `.claude/rules/tdd.md`, RED → GREEN → REVIEW per unit, commit per triplet:

1. **Conformance harness (RED).** Write `FileSystemContractTests` asserting each
   backend's op output strict-deserializes into the records. Fails today
   (HA glob is a bare array; types are untyped JSON).
2. **DTOs + serialization helper (GREEN for type existence).**
3. **Per-producer refactor (GREEN, one op at a time):** read → info → glob →
   search → exec → create/edit/move/remove/copy/blob. Each op: adjust its unit
   tests to the typed shape (only glob changes), refactor producer, green.
4. **Strict boundary guard (RED→GREEN).** Test: a fake `McpClient` returning a
   malformed success payload yields a `ToolError` envelope from
   `McpFileSystemBackend`; a conforming payload passes through unchanged. Then add
   the map + validation.
5. **JSON Schema (RED→GREEN).** Golden-file test generates schema from each DTO
   and compares to committed files; first run writes them.
6. **Prompt/description updates** for glob.
7. **REVIEW** after each triplet; commit referencing the op.

## Resolved decisions

- **Scope:** all `fs_*` ops (not just the HA-overlapping read-family).
- **Glob shape:** always `{entries,truncated,total}`.
- **Enforcement:** producers build the DTOs (compile-time) **plus** a strict
  runtime guard at the `McpFileSystemBackend` boundary; `Vfs*Tool` consumers stay
  pass-through.
- **Boundary mode:** strict — a nonconforming success payload becomes a
  `ToolError` envelope (the LLM never sees malformed output).
- **JSON Schema:** published, generated from the DTOs, kept in sync by a
  golden-file test; committed under `docs/contracts/fs/`.
- **DTO home:** `Domain/DTOs/FileSystem/` (matches the documented DTO convention;
  `FileSystemMount.cs` already lives in `Domain/DTOs/`).

## Verification to do during planning (not blocking design)

- RESOLVED: exec JSON is built in `Infrastructure/Clients/Bash/BashRunner.cs`
  (`{stdout,stderr,exitCode,timedOut,truncated,durationMs,cwd}`), not in Domain's
  `ExecTool`. HA emitted the 4-key subset. `FsExecResult` requires all 7 keys; HA
  exec is improved to populate `timedOut`/`durationMs`/`cwd` and to honor
  `timeoutSeconds` (previously ignored).
- RESOLVED: `McpFileSystemDiscovery.DiscoverAndMountAsync` already has an
  `ILogger`, passed to the backend for the drift warning. `IMetricsPublisher` is
  not reachable there without bootstrap churn, so the drift signal is the log line
  (per the spec's "otherwise the log line stands"); metric emission is a later
  enhancement, not in this plan.
- Confirm `JsonSerializerOptions.GetJsonSchemaAsNode` output is stable enough for a
  golden-file comparison (pin the generating options; normalize formatting before
  compare).
