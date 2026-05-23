# Design: Shared typed `fs_*` result contract

**Date:** 2026-05-23
**Status:** Approved (design)
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
- Future backends inherit the contract for free.

## Non-goals

- No change to the error-envelope contract (`ToolError` / `ToolResponse`).
- No refactor of the agent-side `Vfs*Tool` consumers — they remain pass-through
  (the "consumer" of the shape is the LLM plus the conformance tests).
- No redesign of payload semantics. Typing **preserves the current wire shape**
  for every op, with exactly one deliberate exception (the `fs_glob` unification
  noted below).

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

FsExecResult        (ExecTool, HaFileSystem)
  { string Stdout, string Stderr, int ExitCode, bool Truncated }

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

Only **one** payload changes on the wire: `fs_glob` becomes
`{entries,truncated,total}` always (was a bare array, or a
`{files,truncated,total,message}` object when capped). Ripple:

- `VfsGlobFilesTool` description and any filesystem prompt text that shows glob
  output (`HomeAssistantPrompt`, Vault/Sandbox filesystem prompts) — one-line
  updates to reflect `entries`.
- Existing glob tests under `Tests/Unit/Domain/Tools/FileSystem` and
  `Tests/Integration/Domain/Tools/FileSystem` — update to the new shape (TDD).

All other ops keep their exact current wire shape; the records merely give them a
type and a single serialization path.

## Components / units of work

1. `Domain/DTOs/FileSystem/Fs*Result.cs` records + `FsResultSerialization.cs`.
2. Refactor disk base tools (`Domain/Tools/Files/*`, `Domain/Tools/Text/*`,
   `Domain/Tools/Bash/ExecTool.cs`) to build records via `ToNode`.
3. Refactor `HaFileSystem` + `HaFileSystem.Exec` to build the same records.
4. `Tests/Unit/Domain/Tools/FileSystem/FileSystemContractTests.cs` — the
   cross-backend conformance harness.
5. Prompt/description touch-ups for the glob shape change.

## TDD outline

Per `.claude/rules/tdd.md`, RED → GREEN → REVIEW per unit, commit per triplet:

1. **Conformance harness (RED).** Write `FileSystemContractTests` asserting each
   backend's op output strict-deserializes into the records. Fails today
   (HA glob is a bare array; types are untyped JSON).
2. **DTOs + serialization helper (GREEN for type existence).**
3. **Per-producer refactor (GREEN, one op at a time):** read → info → glob →
   search → exec → create/edit/move/remove/copy/blob. Each op: adjust its unit
   tests to the typed shape (only glob changes), refactor producer, green.
4. **Prompt/description updates** for glob.
5. **REVIEW** after each triplet; commit referencing the op.

## Resolved decisions

- **Scope:** all `fs_*` ops (not just the HA-overlapping read-family).
- **Glob shape:** always `{entries,truncated,total}`.
- **Contract location:** producer-side only; agent consumers stay pass-through.
- **DTO home:** `Domain/DTOs/FileSystem/` (matches the documented DTO convention;
  `FileSystemMount.cs` already lives in `Domain/DTOs/`).

## Verification to do during planning (not blocking design)

- Confirm `Domain/Tools/Bash/ExecTool.cs` emits exactly
  `{stdout,stderr,exitCode,truncated}` (HA mirrors it; grep was inconclusive on
  the build site — read the full file before writing `FsExecResult`).
