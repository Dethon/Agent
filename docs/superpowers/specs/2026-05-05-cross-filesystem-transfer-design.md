# Cross-Filesystem Transfer Design

**Date:** 2026-05-05
**Status:** Draft

## Problem

The virtual filesystem layer exposes per-mount tools (`text_read`, `text_create`, `text_edit`, `move`, …) that are restricted to a single backend. `VfsMoveTool` explicitly rejects cross-filesystem operations with the `CrossFilesystem` error and instructs the agent to "copy across mounts manually (read from source, create at destination), then remove the source."

Processing a vault file via the sandbox therefore costs at least five tool calls: read vault → create in sandbox → exec → read result → create back in vault. This is slow, token-expensive, and turns trivial workflows (e.g., run a script over a vault note) into multi-turn ceremonies.

The system also has no way to transfer binary content between mounts. Every transfer path goes through the text APIs (`ReadAsync` returns a JSON payload of text content; `CreateAsync` accepts a `string`), so images, archives, PDFs, and build artifacts cannot move between vault and sandbox at all.

## Goals

- Collapse vault ↔ sandbox transfers into a single tool call.
- Support both copy and move semantics, in either direction.
- Support both individual files and directory trees (recursive).
- Support binary content end-to-end.
- Keep same-filesystem operations atomic and efficient by delegating to native primitives.
- Keep backends independent — no backend should need to know about other backends.

## Non-goals

- Streaming progress events to the LLM during a transfer.
- Resumable / restartable transfers across agent restarts.
- Cross-FS atomic move (impossible across independent stores; documented as best-effort).
- Native same-FS copy implementations beyond what is trivial (no rsync-style delta logic).

## Architecture

Two new domain tools sit in `Domain/Tools/FileSystem/`:

- `VfsCopyTool` — new.
- `VfsMoveTool` — existing, extended to handle cross-FS by removing the `CrossFilesystem` rejection.

Both tools resolve the source and destination virtual paths via `IVirtualFileSystemRegistry` and dispatch on whether the two backends are the same instance:

| Operation | Same-FS | Cross-FS |
|-----------|---------|----------|
| Copy      | `Backend.CopyAsync` (native) | Stream `OpenReadStreamAsync` → `WriteFromStreamAsync` |
| Move      | `Backend.MoveAsync` (native, atomic) | Stream copy, then `source.DeleteAsync` |

Directory sources are auto-detected via `InfoAsync`. For same-FS operations the native primitive handles directories itself (atomic rename for move, recursive copy for copy). For cross-FS operations the tool globs the source tree and applies the streaming pipeline per file, accumulating per-entry results.

The orchestration lives in the tools themselves. `IVirtualFileSystemRegistry` stays minimal — it resolves paths and nothing more. Backends remain unaware of each other.

## Backend interface changes

`Domain/Contracts/IFileSystemBackend.cs` gains three methods:

```csharp
Task<JsonNode> CopyAsync(string sourcePath, string destinationPath,
    bool overwrite, bool createDirectories, CancellationToken ct);

Task<Stream> OpenReadStreamAsync(string path, CancellationToken ct);

Task WriteFromStreamAsync(string path, Stream content,
    bool overwrite, bool createDirectories, CancellationToken ct);
```

`CopyAsync` is the same-FS native primitive; it must accept either a file or directory source. The streaming pair is used only by the cross-FS transfer path.

The existing text methods (`ReadAsync`, `CreateAsync`, `EditAsync`, …) are untouched. Cross-FS transfer never goes through them, so binary payloads never round-trip through the JSON text channel.

### Implementations

**`LocalFileSystemClient`** (sandbox):

- `CopyAsync` — `File.Copy` for files; recursive directory copy for dirs.
- `OpenReadStreamAsync` — `File.OpenRead`.
- `WriteFromStreamAsync` — `File.Create` + `Stream.CopyToAsync`, honouring `overwrite` and `createDirectories`.

**`McpFileSystemBackend`** (vault, library, sandbox):

Backed by three new raw MCP tools per filesystem-exposing server:

- `fs_copy(sourcePath, destinationPath, overwrite, createDirectories)` — native copy on the server side. Returns the same `{path, bytes, …}` shape as `fs_move`.
- `fs_blob_read(path, offset, length)` — returns `{ contentBase64, eof, totalBytes }`. Server enforces `length` ≤ chunk cap (256 KiB).
- `fs_blob_write(path, contentBase64, offset, overwrite, createDirectories)` — appends the decoded bytes at `offset`. The first call (`offset = 0`) creates the file (respecting `overwrite`); subsequent calls append. Returns `{ path, bytesWritten, totalBytes }`.

The backend exposes `OpenReadStreamAsync` and `WriteFromStreamAsync` as `Stream` adapters that buffer in 256 KiB chunks under the hood.

These three raw tools are filtered out of the agent's tool surface, alongside the existing `fs_*` raws, when domain tools are active.

MCP servers that gain the new tools: **McpServerVault**, **McpServerLibrary**, **McpServerSandbox** — every server exposing a `filesystem://` resource today. (Idealista does not expose a filesystem.) Library is currently read-only-ish (only `fs_glob`/`fs_info`/`fs_move` exposed); adding `fs_blob_read` and `fs_copy` lets the agent extract media files into other mounts. `fs_blob_write` on Library is a deliberate capability extension consistent with its existing `fs_move` (organisational writes).

## Tool behaviour

Both `VfsCopyTool` and `VfsMoveTool` share the following dispatch:

1. Resolve `sourcePath` and `destinationPath` via the registry.
2. `source.Backend.InfoAsync(srcRel)` to determine whether the source is a file or directory.
3. Branch on (file vs directory) × (same-FS vs cross-FS) × (copy vs move).

### File source

- **Same-FS, copy:** `Backend.CopyAsync(srcRel, destRel, overwrite, createDirectories)`.
- **Same-FS, move:** `Backend.MoveAsync(srcRel, destRel)` (existing behaviour).
- **Cross-FS, copy:** open `source.OpenReadStreamAsync(srcRel)` and pipe into `dest.WriteFromStreamAsync(destRel, …)`.
- **Cross-FS, move:** stream copy, then `source.DeleteAsync(srcRel)`. The delete only runs on a successful copy.

Result is a single-entry envelope (see Result schema).

### Directory source

- **Same-FS, copy:** `Backend.CopyAsync` once (native recursion).
- **Same-FS, move:** `Backend.MoveAsync` once (native rename).
- **Cross-FS, either op:**
  1. `source.Backend.GlobAsync(srcRel, "**/*", Files)` to enumerate.
  2. For each entry: compute the path tail relative to `srcRel`, append to `destRel`, perform the streaming transfer.
  3. For move: delete each source file immediately after its copy succeeds. Empty source directories are removed at the end (in reverse-depth order). A directory that still has unmoved files (because some failed) is left in place.
  4. Per-file failures do not abort the loop. Each result is recorded.

### Flags

- `overwrite` — default `false`. When `false` and the destination exists, the per-entry result is `failed` with reason `destination_exists`.
- `createDirectories` — default `true`. When `false` and the destination's parent does not exist, the per-entry result is `failed` with reason `parent_missing`.

### Errors that abort the entire call

These short-circuit before any transfer happens:

- Source path does not resolve to a mounted backend.
- Destination path does not resolve to a mounted backend.
- Source does not exist.
- Single-file call with `overwrite=false` and destination already exists.

Mid-loop errors (per file in a directory transfer) never abort.

### Cancellation

The transfer loop honours `CancellationToken`. On cancellation the result reflects whatever was already completed; in-flight entries are reported as `failed` with reason `cancelled`.

## Result schema

### Single-file call

```json
{
  "status": "ok",
  "source": "/vault/notes/foo.md",
  "destination": "/sandbox/notes/foo.md",
  "bytes": 1234
}
```

### Directory or multi-file call

```json
{
  "status": "ok",
  "summary": { "transferred": 12, "failed": 1, "skipped": 0, "totalBytes": 845120 },
  "entries": [
    { "source": "/vault/proj/a.md", "destination": "/sandbox/proj/a.md", "status": "ok", "bytes": 1234 },
    { "source": "/vault/proj/big.bin", "destination": "/sandbox/proj/big.bin", "status": "failed", "error": "size_limit_exceeded" }
  ]
}
```

`status` roll-up:

- `ok` — every entry succeeded.
- `partial` — at least one success and at least one failure.
- `failed` — every entry failed.

## Files touched

**Domain:**

- `Domain/Contracts/IFileSystemBackend.cs` — add `CopyAsync`, `OpenReadStreamAsync`, `WriteFromStreamAsync`.
- `Domain/Tools/FileSystem/VfsCopyTool.cs` — new.
- `Domain/Tools/FileSystem/VfsMoveTool.cs` — drop `CrossFilesystem` rejection; add cross-FS streaming and directory recursion paths.
- `Domain/Tools/FileSystem/FileSystemToolFeature.cs` — register `VfsCopyTool`.

**Infrastructure:**

- `Infrastructure/Clients/LocalFileSystemClient.cs` — implement the three new methods.
- `Infrastructure/Agents/Mcp/McpFileSystemBackend.cs` — implement the three new methods over the new raw tools, with chunked stream adapters.
- `Infrastructure/Agents/Mcp/McpFileSystemDiscovery.cs` (and the raw-tool filter) — extend the filtered tool name set with `fs_copy`, `fs_blob_read`, `fs_blob_write`.

**MCP servers** (per filesystem-exposing server):

- `McpServerVault/McpTools/` — add `fs_copy`, `fs_blob_read`, `fs_blob_write` tools.
- `McpServerLibrary/McpTools/` — same.
- `McpServerSandbox/McpTools/` — same.

**Tests:**

- Unit tests for `VfsCopyTool` and `VfsMoveTool` covering: same-FS file, same-FS directory, cross-FS file, cross-FS directory, partial failure roll-up, overwrite/createDirectories flags, abort-error short-circuits, cancellation mid-transfer.
- Integration tests covering binary roundtrip (vault ↔ sandbox) and large-file chunking through the MCP backend.

## Open considerations

- The 256 KiB chunk size for `fs_blob_*` is a starting value; tune once measured against real MCP message limits.
- `fs_blob_write` semantics use offset-append. An alternative is a session-id streaming model, but offset-append is simpler and stateless on the server.
- A native cross-FS optimisation (e.g., the agent passing a presigned URL between backends) is out of scope; the streaming-through-agent path is fine for the data volumes we expect.
