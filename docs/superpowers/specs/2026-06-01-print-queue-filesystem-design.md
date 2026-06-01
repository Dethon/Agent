# Print Queue Filesystem — Design

**Date:** 2026-06-01
**Status:** Approved (pending implementation plan)
**Branch:** `printer`

## Summary

A new MCP filesystem server, **`McpServerPrinter`**, exposes a `filesystem://print-queue`
resource (mount `/print-queue`). When the agent **copies or creates** a file into the mount,
the document is **immediately submitted** to a single configured IPP/CUPS printer. **Removing**
a still-pending file **cancels** the print job. Jobs **auto-disappear** from the listing once the
printer reports them finished. `move` and `exec` are intentionally unsupported.

This mirrors the existing non-disk VFS backends (`ScheduleFileSystem`, `HaFileSystem`) and the
`McpServerScheduling` server skeleton: a domain `IFileSystemBackend` engine + thin `fs_*` MCP tool
wrappers + a `filesystem://` resource that the agent auto-discovers and mounts. **Zero agent code
changes** are required — only a config entry pointing the agent at the new server.

## Goals

- Copying/creating a file into `/print-queue` prints it on a single configured printer.
- Removing a file that has **not yet finished printing** cancels it (it will not print).
- Support all standard filesystem operations **except `move` and `exec`**.
- Binary documents (PDF, images, PostScript) are first-class — the primary ingestion path is
  binary chunk streaming.

## Non-Goals

- Multiple/selectable printers, per-job printer override, or CUPS destination discovery
  (single configured destination only).
- Persistent print history. Finished jobs disappear from the queue (no retention/TTL).
- Document format conversion. Whatever the configured IPP/CUPS endpoint accepts is what we send;
  CUPS errors are surfaced as typed tool errors.
- Shelling out to `lp`/`lpstat`/`cancel`. All printer communication is in-process IPP over HTTP.

## Key Decisions (from brainstorming)

| Decision | Choice |
|----------|--------|
| Print backend | Real printer via **CUPS/IPP** |
| Dispatch trigger | **Immediate submit** on copy/create; **cancel via job id** on remove |
| Queue layout | **Flat docs** + a separate read-only **`status.json`** view |
| Printer config | **Single configured destination** (env var) |
| Post-print retention | **Auto-disappear** once the printer reports the job finished |
| CUPS transport | **IPP over HTTP** via the **SharpIpp** NuGet, behind an injectable `IPrinterClient` |

## Architecture

### Components & boundaries

- **`PrinterQueueFileSystem : IFileSystemBackend`** — `Domain/Tools/Printing/Vfs/`
  The VFS engine. Pure path-parsing / validation / operation-routing logic. Depends only on the
  two injected contracts below. Unit-testable in isolation with fakes.

- **`IPrinterClient`** — `Domain/Contracts/IPrinterClient.cs`
  The only abstraction that knows IPP exists:
  - `Task<PrintJobHandle> SubmitAsync(string jobName, string contentType, ReadOnlyMemory<byte> document, CancellationToken ct)` — returns the assigned job id.
  - `Task<IReadOnlyList<PrintJobStatus>> GetActiveJobsAsync(CancellationToken ct)` — active (pending/processing) jobs at the configured destination: `{ JobId, JobName, State }`.
  - `Task CancelAsync(int jobId, CancellationToken ct)`.

- **`IppPrinterClient : IPrinterClient`** — `Infrastructure/Clients/Printer/`
  Thin **SharpIpp** + `HttpClient` adapter against the configured IPP URI (a CUPS server or a
  direct-IPP printer — same protocol). Maps IPP operations `Print-Job`, `Get-Jobs`, `Cancel-Job`.

- **`IPrintSpool` / `PrintSpool`** — `Domain/Contracts/IPrintSpool.cs` + `Infrastructure/.../Printer/`
  Disk-backed store under the spool volume, keyed by **filename**, holding
  `{ JobId, ContentType, Bytes, SubmittedAt }`. Lets `read`, `search`, `edit`, and binary read-back
  work while a job is still active. Pruned during reconciliation. Testable via a temp directory.

- **`McpServerPrinter`** project — thin `fs_*` MCP tool wrappers (including
  `fs_blob_read`/`fs_blob_write` for binary), the `filesystem://print-queue` resource, a
  `printing_prompt` MCP prompt, and the DI / Settings / `Program.cs` / Dockerfile skeleton mirroring
  `McpServerScheduling`.

### Data flow

```
agent VfsCopy (/vault/x.pdf -> /print-queue/x.pdf)
  -> different backends -> streams bytes -> fs_blob_write MCP tool
  -> PrinterQueueFileSystem.WriteChunksAsync
  -> IPrinterClient.SubmitAsync(jobName=x.pdf, ...) -> jobId
  -> IPrintSpool.Put(x.pdf, jobId, bytes)

agent VfsRemove (/print-queue/x.pdf)
  -> fs_remove -> PrinterQueueFileSystem.DeleteAsync
  -> if job still active: IPrinterClient.CancelAsync(jobId)
  -> IPrintSpool.Remove(x.pdf)
```

## Virtual Layout

Flat documents plus a single aggregate status file:

- `/print-queue/<filename>` — one flat file per active (pending/printing) job.
- `/print-queue/status.json` — **read-only** aggregate:
  `[{ filename, jobId, state, submittedAt, sizeBytes }]`.

**Filename is the logical key.** Submitting a name that already maps to a pending job requires
`overwrite=true` (which cancels the prior job and resubmits); otherwise `AlreadyExists`. This keeps
`create`/`edit`/`copy` semantics unambiguous and avoids duplicate-name collisions in the listing.

## Operation Mapping (`IFileSystemBackend`)

| Method | Behavior |
|--------|----------|
| `WriteChunksAsync` (binary copy-in) | **Primary path.** Buffer chunks → bytes → if a pending job has that name and `overwrite=false`, return `AlreadyExists`; else `SubmitAsync(jobName=filename)` → `Spool.Put`. Returns byte count. |
| `CreateAsync` (text) | Same as above with UTF-8 bytes and `text/plain` content type. |
| `ReadAsync` | Return spooled text content (line-numbered). Binary document → typed error suggesting `status.json` / blob read. |
| `ReadChunksAsync` | Binary read-back of the spooled bytes (enables copy-out). **Implemented** (unlike Schedule/HA backends, which throw). |
| `InfoAsync` | For a job file: `exists`, `size`, `submittedAt`, and live `state` from the printer. For `status.json`: directory-less virtual file metadata. |
| `GlobAsync` | List active job filenames (after reconciliation) matching the pattern, plus `status.json`. |
| `SearchAsync` | Search across text documents' content; skip binary documents. Regex with match-timeout protection (mirrors `HaFileSystem`). |
| `EditAsync` | Apply text edits to a spooled text doc → `CancelAsync(old jobId)` → `SubmitAsync` (new job) → update spool. Binary document → `UnsupportedOperation`. |
| `CopyAsync` (intra-queue) | Duplicate a pending document's bytes under a new filename → submit as a new job. (Cross-backend copy never reaches this — it uses chunk streaming.) |
| `DeleteAsync` | If the job is still active, `CancelAsync(jobId)`; drop the spool entry. **This implements "remove before print = don't print."** Already-finished → just clear spool / `NotFound`. |
| `MoveAsync` | **`UnsupportedOperation`** — "The print queue does not support move; copy a document in to print it." |
| `ExecAsync` | **`UnsupportedOperation`** — "The print queue does not support exec." |
| writes/edits/removes to `status.json` | `ReadOnly` error (mirrors `ScheduleFileSystem` read-only files). |

## Reconciliation ("auto-disappear when printed")

There is no background sweeper. On every `glob`, `info`, and `status.json` read, the backend:

1. Calls `IPrinterClient.GetActiveJobsAsync()` to get the set of active job ids at the destination.
2. Intersects that set with the spool entries by `JobId`.
3. Prunes spool entries whose job id is no longer active (the printer finished or dropped them),
   deleting their spooled bytes.

The result is the live, active-only view the design requires. Reconciliation is lazy (driven by
reads), keeping the server simple and stateless beyond the spool directory.

## Configuration & Environment Variables

Both values are **non-secret** (a network endpoint and a path), so per the project convention they
live in `appsettings.json` and the docker-compose `environment` block — **not** in `.env`.

- `PRINTER__PRINTERURI` — the IPP endpoint, e.g. `ipp://cups:631/printers/Main`.
- `PRINTER__SPOOLPATH` — spool directory inside the container, e.g. `/spool` (a mounted volume).

Files to update **in the same change** (per `CLAUDE.md` "Environment Variables" rule):

- `McpServerPrinter/appsettings.json` — placeholder keys.
- `DockerCompose/docker-compose.yml` — `mcp-printer` service `environment` + a spool volume.
- (No `.env` entry — neither value is a secret.)

## Agent Wiring

- Add `http://mcp-printer:8080/mcp` to the relevant agent's `mcpServerEndpoints` in
  `Agent/appsettings.json`.
- `McpFileSystemDiscovery` reads the `filesystem://print-queue` resource on session start and mounts
  it at `/print-queue` via `VirtualFileSystemRegistry` — no agent code changes.
- The agent side uses the generic `McpFileSystemBackend`, which calls the standard `fs_*` MCP tools
  (`fs_read`, `fs_create`, `fs_edit`, `fs_glob`, `fs_search`, `fs_info`, `fs_remove`, `fs_copy`,
  `fs_blob_read`, `fs_blob_write`). The server must expose all of these; `fs_move`/`fs_exec` map to
  the unsupported responses above.

## MCP Server Skeleton

Mirrors `McpServerScheduling`:

- `McpServerPrinter/Program.cs` — `GetSettings()` → `ConfigurePrinter(settings)` → `MapMcp("/mcp")`.
- `McpServerPrinter/Settings/PrinterSettings.cs` — `record` with `PrinterUri`, `SpoolPath`.
- `McpServerPrinter/Modules/ConfigModule.cs` — register `IPrinterClient`→`IppPrinterClient`,
  `IPrintSpool`→`PrintSpool`, `PrinterQueueFileSystem`; `.AddMcpServer().WithHttpTransport(...)`
  with the `fs_*` tools, `.WithResources<FileSystemResource>()`, `.WithPrompts<PrinterPrompt>()`,
  and the global `AddCallToolFilter` error handler.
- `McpServerPrinter/McpResources/FileSystemResource.cs` — serves
  `{ name: "print-queue", mountPoint: "/print-queue", description: "..." }` at
  `filesystem://print-queue`.
- `McpServerPrinter/McpTools/Fs*Tool.cs` — thin wrappers calling `PrinterQueueFileSystem`.
- `McpServerPrinter/Dockerfile` — mirrors `McpServerScheduling/Dockerfile`.
- `McpServerPrinter/McpServerPrinter.csproj` — references `Infrastructure`, `ModelContextProtocol.AspNetCore`, and `SharpIpp`.

The `printing_prompt` (`Domain/Prompts/PrintingPrompt.cs`) teaches the idiom: *copy a file into
`/print-queue` to print it; remove it before it finishes to cancel; read `status.json` to see job
state; move/exec are not supported.*

## Error Handling

All operations return `FsResult<T>` (`Ok`/`Err`). Error codes use `ToolError.Codes.*`:

- Unsupported `move`/`exec`, binary `read`/`edit` → `UnsupportedOperation`.
- Submitting an existing pending name without overwrite → `AlreadyExists`.
- Reading/removing a missing file → `NotFound`.
- Writing to `status.json` → a read-only error.
- IPP/printer failures (unreachable endpoint, rejected document) → surfaced as a `Retryable`-tagged
  error with the printer's message and a hint, rather than throwing. The server-level
  `AddCallToolFilter` is the final safety net.

## Testing (TDD)

**Unit — `PrinterQueueFileSystem`** (fake `IPrinterClient` + temp-dir `PrintSpool`):

- `WriteChunksAsync`/`CreateAsync` submit to the printer and spool the bytes.
- `DeleteAsync` cancels an active job and clears the spool (the core "remove = don't print").
- `DeleteAsync` on an already-finished job does **not** cancel and reports cleared/`NotFound`.
- `MoveAsync`/`ExecAsync` return `UnsupportedOperation`.
- `status.json` is read-only; writes/edits/removes are rejected.
- Reconciliation prunes finished jobs from `glob`/`info`/`status.json` and deletes spooled bytes.
- `EditAsync` cancels the old job and resubmits; binary edit is `UnsupportedOperation`.
- `CopyAsync` duplicates a pending document as a new job.
- `GlobAsync`/`SearchAsync` reflect active jobs / text content.
- `AlreadyExists` when resubmitting a pending name without `overwrite`.

**Integration (opt-in, Docker-gated):** `IppPrinterClient` against a real CUPS container, consistent
with this repo's E2E/Docker baseline (these may be skipped in the WSL dev env). `IppPrinterClient`
is kept deliberately thin so the bulk of logic is covered by the unit tests above.

## Open Items / Risks

- **SharpIpp API surface:** confirm the exact SharpIpp request/response types for `Print-Job`,
  `Get-Jobs`, and `Cancel-Job` during implementation (verify via context7 / the package source).
- **Local dev print target:** real printing needs a reachable IPP endpoint. A `cups` container can be
  added to docker-compose for end-to-end manual testing; unit tests never need it.
- **Binary text detection in `ReadAsync`/`SearchAsync`:** use a simple UTF-8 validity / control-byte
  heuristic to decide text vs binary, consistent with how the repo treats blob vs text content.
