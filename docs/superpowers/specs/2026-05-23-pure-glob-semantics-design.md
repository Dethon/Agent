# Pure glob semantics — design

- **Date:** 2026-05-23
- **Status:** Approved (pending spec review)
- **Branch:** filesystem-migration

## Problem

The virtual-filesystem glob tool takes a `mode` parameter (`files` / `directories`,
defaulting to `directories`). The two modes return **disjoint** result sets — a file
pattern like `*.sh` returns nothing in `directories` mode, and directories never appear
in `files` mode.

In practice the model frequently **omits `mode`**, so the call silently defaults to
`directories` and a file-seeking glob returns an empty result. The empty result reads as
"no matches" rather than "wrong mode", so the failure is silent and recurring. Heavy
prose in the tool description ("You MUST set `mode`…") has not fixed it.

Relocating the decision (e.g. splitting into `glob_files` / `glob_directories`) only moves
the same binary choice from a parameter to a tool name; the model can mis-pick a tool as
easily as a parameter. The decision itself is the problem.

## Goal

Remove `mode` entirely and adopt **pure glob semantics**: the pattern alone determines
what matches, exactly as in a shell, `.gitignore`, `ripgrep`, or `fd`. This eliminates the
decision rather than moving it — the model writes a pattern, which it is already expert at,
and an empty result genuinely means "nothing matched".

### Non-goals

- No change to `read`, `create`, `edit`, `search`, `move`, `copy`, `remove`, `exec`, or
  `info` tools.
- No change to the `fs_*` typed result contract shape (`FsResultContract`). Directory
  marking is carried in entry strings, not a new field.
- No new directory-exclusion rules (e.g. `.trash`), hidden-file behavior changes, or
  pattern-syntax changes beyond the trailing-slash convention below.

## Model-facing contract

`glob(basePath, pattern)` — no `mode` parameter.

- The pattern matches **both files and directories** by path.
- Pattern syntax is unchanged: `*` = single segment, `**` = recursive, `?` = single char.
- A **trailing `/`** on the pattern restricts matches to **directories only**
  (`*/`, `src/**/`, `notes/`).
- In results, **directory entries carry a trailing `/`**; file entries do not. This lets
  the model tell types apart for free (no extra field, minimal tokens), symmetric with the
  input convention.
- Result shape is unchanged: `{ entries, truncated, total }`, `entries` lexically sorted.
- An empty `entries` now unambiguously means "nothing matched the pattern". The wrong-mode
  failure class is gone.

### Examples

```
glob('/vault', '*')        -> ["archive/", "notes/", "todo.md"]      # files + dirs, dirs marked
glob('/vault', '*/')       -> ["archive/", "notes/"]                 # directories only
glob('/vault', '**/*.md')  -> ["notes/a.md", "notes/b.md"]           # files (dirs rarely match *.md)
glob('/vault', '**/')      -> ["archive/", "notes/", "notes/sub/"]   # all directories, recursive
glob('/vault', 'notes')    -> ["notes/"]                             # a single dir, marked
glob('/vault', 'todo.md')  -> ["todo.md"]                            # a single file
glob('/vault', '*.zip')    -> []                                     # genuinely no matches
```

### Edge cases (preserve current behavior unless noted)

- Empty / whitespace pattern → `ArgumentException` (existing guard).
- Pattern containing `..` → `ArgumentException` (existing guard).
- `basePath` containing `..` or resolving outside the root → `ArgumentException` (existing).
- The base/root directory itself is **excluded** from results (existing
  `GlobDirectories` behavior).
- A trailing-slash pattern that lexically matches a file excludes it (dirs only).
- Exactly one trailing `/` is stripped to detect dirs-only intent; the remaining pattern is
  matched normally.
- No special handling of `.trash` or dotfiles is added or removed — whatever the current
  matcher includes, the unified matcher includes.

## Architecture — layers to change

The glob path spans two layers. Both are live; neither is legacy.

```
Agent process                          MCP server process
-------------                          ------------------
VfsGlobFilesTool (domain__filesystem__glob)
  -> IVirtualFileSystemRegistry.Resolve
  -> IFileSystemBackend.GlobAsync
       McpFileSystemBackend  --(fs_glob over MCP)-->  FsGlobTool (Library/Sandbox/Vault)
                                                        -> GlobFilesTool.Run
                                                        -> IFileSystemClient.Glob
                                                        -> LocalFileSystemClient
                                              -->  FsGlobTool (HomeAssistant)
                                                        -> HaFileSystem.GlobAsync
                                                        -> HaTree.Glob
```

### 1. Agent-facing layer (Domain)

- **`Domain/Tools/FileSystem/VfsGlobFilesTool.cs`**
  - Rename `Name` from `glob_files` to `glob` (`Key` stays `glob`).
  - Drop the `VfsGlobMode mode` parameter.
  - Rewrite `ToolDescription`: pure semantics, trailing-slash rules, dir entries marked,
    empty = no matches. Drop all wrong-mode warnings.
- **`Domain/Contracts/IFileSystemBackend.cs`**
  - `GlobAsync(string basePath, string pattern, VfsGlobMode mode, CancellationToken ct)`
    → `GlobAsync(string basePath, string pattern, CancellationToken ct)`.
- **`Infrastructure/Agents/Mcp/McpFileSystemBackend.cs`**
  - Drop the `["mode"] = mode.ToString()` argument from the `fs_glob` call.

### 2. File-backed servers (Library, Sandbox, Vault)

- **`McpServer{Library,Sandbox,Vault}/McpTools/FsGlobTool.cs`**
  - Drop the `string mode = "directories"` parameter and the `GlobMode` parsing.
- **`Domain/Tools/Files/GlobFilesTool.cs`**
  - `Run(pattern, GlobMode mode, ct, basePath)` → `Run(pattern, ct, basePath)`.
  - Replace the `mode switch` (`RunDirectories` / `RunFiles`) with a single unified call,
    marking directory entries with a trailing `/`, applying the existing 200-entry cap to
    the **combined** result set (`{entries, truncated, total}`).
  - Keep the existing `..` / rooted-path / `basePath` validation.
- **`Domain/Contracts/IFileSystemClient.cs`**
  - Collapse `GlobFiles(basePath, pattern, ct)` + `GlobDirectories(basePath, pattern, ct)`
    into a single `Glob(string basePath, string pattern, CancellationToken ct)` that returns
    both, honoring the trailing-slash convention and marking directories.
- **`Infrastructure/Clients/LocalFileSystemClient.cs`** — the genuinely tricky bit.
  - `Microsoft.Extensions.FileSystemGlobbing.Matcher` is **file-only** (the reason
    `GlobDirectories` does its two-pass dance). Implement a unified matcher:
    1. Enumerate all files **and** all directories recursively under the root, as paths
       relative to the root.
    2. If the pattern ends with `/`: strip one trailing `/`, match against directory
       relative paths only.
    3. Otherwise: match the pattern against the union of file and directory relative paths
       (in-memory `Matcher.Match(IEnumerable<string>)` — lexical, no extra FS access; this
       is the overload `GlobDirectories` already uses, so glob-pattern semantics stay
       identical to today).
    4. Map matches back to full paths, append `/` to directory entries, exclude the root
       itself, sort lexically.
  - A parity test must confirm the unified file matches equal today's
    `GetResultsInFullPath`-based `GlobFiles` for representative patterns.
  - Performance note: this enumerates files + dirs and matches in memory. It is no heavier
    than today's `GlobDirectories`, which already enumerates all directories recursively.

### 3. Home Assistant server

- **`McpServerHomeAssistant/McpTools/FsGlobTool.cs`**
  - Drop the `mode` parameter and `GlobMode` parsing; rewrite the description (it currently
    instructs the model to choose `directories`/`files`).
- **`Domain/Tools/HomeAssistant/Vfs/HaFileSystem.cs`**
  - `GlobAsync(basePath, pattern, GlobMode mode, ct)` → `GlobAsync(basePath, pattern, ct)`.
  - HA glob stays **uncapped** (existing rationale: bounded by entity count).
- **`Domain/Tools/HomeAssistant/Vfs/HaTree.cs`**
  - `Glob(catalog, basePath, pattern, bool directories)` → `Glob(catalog, basePath, pattern)`.
  - Derive dirs-only from a trailing `/` on the pattern. Mark directory-ish nodes (domains,
    areas, entity directories) with a trailing `/`; leaf nodes (`state.json`, `*.sh`) stay
    bare. Continue returning **canonical** entity paths (per the canonical-path work).

### 4. Cleanup

- Remove `Domain/DTOs/FileSystemEnums.cs` → `VfsGlobMode` once unused (verify no other
  references; `VfsTextSearchOutputMode` in the same file stays).
- Remove `Domain/Tools/Files/GlobMode.cs` once unused.

### 5. Prompts & descriptions

- **`Domain/Prompts/HomeAssistantPrompt.cs`** — 4× `glob_files` → `glob`; remove any
  `mode`-based guidance; update examples to the trailing-slash convention (e.g.
  `glob /ha/areas/*/` to list rooms).
- **`McpServerHomeAssistant/McpTools/FsGlobTool.cs`** description — rewrite (above).
- **`VfsGlobFilesTool.ToolDescription`** and **`GlobFilesTool.Description`** — rewrite for
  pure semantics.
- The filesystem feature prompt (`FileSystemToolFeature.BuildPrompt`) does not mention glob
  mode; no change expected (verify).

## Testing (TDD, RED-first per layer)

Write the failing test first at each layer, then implement.

- **`LocalFileSystemClient`** (unit): mixed files+dirs returned with dirs marked; `*/`
  returns dirs only; `**` recursive; root excluded; parity with old `GlobFiles` for file
  patterns; `..`/rooted/`basePath` guards intact.
- **`GlobFilesTool`** (unit, `Tests/Unit/Domain/Tools/GlobFilesToolTests.cs`): combined cap
  at 200 with `{entries, truncated, total}`; dir marking; rewrite the `GlobMode`-based
  cases.
- **`VfsGlobFilesTool`** (unit, `Tests/Unit/Domain/Tools/FileSystem/VfsGlobFilesToolTests.cs`):
  dispatches to backend without a mode arg; rewrite mode-based cases.
- **`HaTree.Glob`** (unit): trailing-slash dirs-only; dir-ish nodes marked; canonical paths
  preserved; uncapped.
- **MCP server integration** (`McpVaultServerTests`, `McpLibraryServerTests`,
  `McpAgentFileSystemTests`): `fs_glob` over the wire with no `mode` arg; mixed results;
  trailing-slash behavior.
- Confirm `FsResultContract.TryValidate("fs_glob", …)` still passes (shape unchanged).

## Rollout / compatibility

Agent and its MCP servers deploy together; `fs_glob` is an internal protocol. Drop `mode`
from the wire signature in lockstep across all four servers and the backend client in the
same change — no compatibility shim.

## Open questions

None outstanding. Type marking (trailing slash) and the rename (`glob`) are decided.
