# Downloads as an Overlay on the Media Filesystem — Design

**Date:** 2026-06-12
**Branch:** `subscription-refactor`
**Status:** Approved
**Revises:** §4 of `2026-06-12-library-channel-refactor-design.md`

## Problem

The branch introduced `filesystem://downloads`, a second filesystem resource on the library
server. It created a three-way alias for one physical directory: the compose file mounts
`${DATA_PATH}/downloads:/downloads` *and* `${DATA_PATH}:/media` into the library container, so
the agent sees a download's payload files at `/media/downloads/<id>/` (media FS), its status
and cleanup at `/downloads/<id>/` (virtual FS), and the completion message reports
qBittorrent's `savePath` (`/downloads/<id>`, a path that exists in neither agent mount's
namespace once you account for the mount points). The prompt has to teach all three names.

## Approach

Remove `filesystem://downloads` entirely. The media filesystem absorbs the download semantics
as an **overlay on its `downloads/` subtree**: a virtual read-only `status.json` per active
download, plus a guarded delete that carries the cancel/cleanup logic. One namespace —
`/media/downloads/<id>/` — holds the payload files, the status file, and the cleanup target.

Decisions made during brainstorming:

1. **Glob merge:** virtual entries appear in `fs_glob` results (the status-report idiom
   depends on discovery; queued torrents with no on-disk directory stay visible).
2. **Leftover cleanup:** `fs_delete /media/downloads/<id>` also works when no torrent owns the
   id but the directory exists on disk — the agent can always recover a wedged downloads area.
3. **Structure:** overlay class + thin per-tool routing (approach A), not a full disk-backed
   `IFileSystemBackend` engine (approach B — rejected: it would mean a second disk
   implementation duplicating the shared `Domain/Tools/Files` semantics for one server).
   Mounting the downloads VFS *at* `/media/downloads` was ruled out: longest-prefix resolution
   would shadow the payload files the agent needs to organize.

## 1. Agent-facing surface

The agent mounts only `/media` from the library server. Within it:

- **`/media/downloads/<id>/status.json`** — virtual, read-only. Same JSON as today minus
  `savePath`, which is now redundant (the status file lives in the directory it pointed at).
- **Glob:** under `downloads/`, disk results merge with virtual entries — an `<id>/` directory
  per active download (synthesized even when qBittorrent has not created it on disk yet,
  deduplicated against disk dirs) plus `<id>/status.json`. Patterns outside `downloads/` are
  pure disk. The 200-entry cap applies after the merge.
- **Read:** serves only virtual status.json paths. Other media files remain non-readable
  (unchanged policy; binary content goes through `fs_blob_read`).
- **Info:** virtual answers for id directories (exists = torrent alive ∨ directory on disk)
  and status.json files; plain disk elsewhere.
- **Move / copy / blob read / blob write:** return an error when a virtual status.json path is
  the source or the destination; otherwise pure disk pass-through. Organizing payload folders
  out of `/media/downloads/<id>/` works exactly as today, and moving a directory never trips
  over the phantom file (it does not exist on disk).
- **Delete:** accepts only an exact `/media/downloads/<id>` target.
  - Active torrent → today's semantics: `Cleanup` (cancel + remove task/files), remove the
    routing entry, remove the on-disk directory — same ordering and best-effort rules
    (cleanup failure aborts before housekeeping; disk-removal failure is swallowed).
  - No torrent, directory on disk → remove the directory, drop any stale routing entry.
  - Neither → `NotFound`.
  - Every other media path → `UnsupportedOperation` (unchanged: the library has no general
    delete).
- Directory removal is a hard remove (today's `RemoveDirectory`), not trash — for both the
  active and leftover branches.

## 2. Structure

- `DownloadsFileSystem` is reworked into **`DownloadsOverlay`**
  (`Domain/Tools/Downloads/Vfs/`). It drops `IFileSystemBackend` — it is a partial overlay,
  not a whole filesystem — but keeps typed `FsResult<T>` returns. Surface: status read, info,
  glob entries to merge, the guarded delete, and virtual-path predicates
  (`IsStatusPath` / `IsDownloadDir`-shaped). Dependencies: `IDownloadClient`,
  `IDownloadRoutingStore`, `IFileSystemClient`, `LibraryPathConfig`.
- `DownloadsPath` reparses media-relative paths: `downloads/<id>[/status.json]`.
- The library's `Fs*Tool`s keep inheriting the shared `Domain/Tools/Files` disk tools
  (untouched — Vault/Sandbox share them) and consult the overlay first:
  - `FsReadTool`: overlay or error (no disk read on media).
  - `FsGlobTool`: disk glob merged with overlay entries. The legacy `GlobFilesTool.Run` is
    refactored to expose a typed `FsGlobResult` (behavior-preserving) so the merge happens on
    typed results before serialization.
  - `FsDeleteTool`: overlay only.
  - `FsMoveTool` / `FsCopyTool` / `FsBlobReadTool` / `FsBlobWriteTool`: virtual-path guard,
    then legacy disk behavior.
  - `FsInfoTool`: overlay for id directories and status.json paths, legacy elsewhere
    (including payload files inside a download directory).
- The `filesystem` argument stays (the agent-side `McpFileSystemBackend` always sends it).
  Tools accept `null` or `"media"` and reject anything else with `UnsupportedOperation`.

## 3. Paths, config, compose

- The downloads-inside-media relationship is structural (compose volume identity:
  `${DATA_PATH}/downloads` ≡ `/media/downloads`), so the subdir is a shared **constant**
  `"downloads"` — used by the overlay, the resource description, and the completion planner.
  No config knob: a setting would imply tunability the volume mounts do not grant.
- `downloadLocation` (`/downloads`) survives **only** as the savePath base handed to
  qBittorrent's API by `FileDownloadTool`. The library's own disk I/O (leftover/cleanup
  directory removal) moves to `<BaseLibraryPath>/downloads/<id>`.
- **`mcp-library` drops the `${DATA_PATH}/downloads:/downloads` volume mount** in
  `DockerCompose/docker-compose.yml`; `qbittorrent` keeps its own. No new environment
  variables or appsettings keys.

## 4. Completion message

`DownloadCompletionPlanner.BuildPrompt` reports the agent-visible path —
"finished downloading to `/media/downloads/<id>`" — instead of qBittorrent's `item.SavePath`.
The path composes from a shared mount-point constant also used by `FileSystemResource`
(single source for `/media`). After this refactor the agent has no `/downloads` mount, so the
old message would point at a path outside its VFS namespace.

## 5. Prompt, descriptions, docs

- `DownloaderPrompt`: Phase 3 (progress checks at `/media/downloads/<id>/status.json`),
  Phase 4 cleanup (`fs_delete /media/downloads/<id>`), the status-report idiom (glob
  `/media/downloads/*/status.json`), and cancellation all move to the unified paths. One new
  rule: status.json is virtual — read it, never move or copy it, it disappears when the
  download is cleaned up.
- `filesystem://media` resource description documents the downloads-area semantics (virtual
  status.json, delete = cancel + cleanup); the `filesystem://downloads` resource is removed.
- `fs_read` / `fs_delete` tool descriptions updated to the media idiom.
- §4 of `2026-06-12-library-channel-refactor-design.md` gains a pointer to this revision.
- README / CLAUDE.md mentions of the downloads filesystem updated where present.

## 6. Error handling

- Vanished torrent while reading status.json → `NotFound` (unchanged).
- Cleanup failure → aborts before routing/disk housekeeping (unchanged).
- Disk-directory removal failure → swallowed after a successful manager-side cleanup
  (unchanged).
- Unknown `filesystem` argument → `UnsupportedOperation`.

## 7. Testing (TDD throughout)

- `DownloadsOverlayTests` (rework of `DownloadsFileSystemTests`): status read (no `savePath`
  field), glob merge (dedupe against disk dirs, virtual-only dirs for queued torrents,
  patterns outside `downloads/` contribute nothing), delete × three outcomes (active /
  leftover / neither), virtual-path predicates.
- `DownloadsPathTests`: media-relative parsing.
- `LibraryFsRoutingTests`: per-tool routing — read overlay-only, glob merge, move/copy/blob
  guards, delete overlay-only, `filesystem` argument validation.
- `McpLibraryServerTests` (integration): exactly one `filesystem://` resource
  (`filesystem://media`); glob/read/delete exercised end-to-end over MCP.
- `DownloadCompletionPlanner` tests pin the `/media/downloads/<id>` message path.
