# Filesystem Backend Depth Implementation Plan

**Goal:** `IFileSystemBackend` names 12 operations and provides nothing, so five adapters re-derive the same six things and have already diverged. Give it an implementation, derive the MCP surface from it, and delete the 64 wrapper files.

**Why now:** The divergences have shipped. `/schedules` and `/timers` build search regexes with no timeout and no `catch`, while `/print-queue` and `/ha` have both. `PrinterQueueFileSystem.GlobAsync` ignores `basePath` and the trailing-slash dirs-only convention that `VfsGlobFilesTool` advertises to the LLM. And `McpServerTimers` registers `fs_move` with the description "Unsupported on timers", so `DeriveCapabilities` reports `VfsMove` and the system prompt tells the model `/timers` supports move — the model burns a turn on a guaranteed `unsupported_operation`, which is the exact waste the capability list exists to prevent.

**Source:** architecture review 2026-08-02, candidate 3.

## Global Constraints

- TDD per task. `dotnet test Tests/Unit --nologo -v q`.
- `.cs` files have no trailing newline; pre-commit runs `dotnet format` and re-stages whole files.
- File-scoped namespaces, primary constructors, `record` DTOs, no XML doc comments.
- Domain never imports Infrastructure/Agent.
- `.claude/rules/mcp-tools.md` governs tool naming and description style.
- Commit after each task.

## Locked decisions

**Capability is derived from which methods are overridden.** The base returns `Unsupported` for all 12; the registrar reflects at startup over `DeclaringType`. Nothing to declare means nothing can drift. `TimerFileSystem` simply stops overriding `MoveAsync` and `/timers` stops advertising move — the lie becomes unrepresentable rather than merely fixed.

**Descriptions are optional virtuals on the backend**, one per operation, with generic defaults on the base. Description and semantics then sit adjacent in one file instead of in a different project. The per-server text is real (`schedule.json/status.json/agent_info.json/run_now.sh`, HA's `state.json` and `*.sh` usage) and must survive verbatim.

**The disk tools are rewritten to return `FsResult` natively.** Not wrapped. `MapErrorCode` and the `ToolResponse.Create(Exception)` path are deleted, so there is one error model end to end rather than one at the seam.

```csharp
namespace Domain.Contracts;

public abstract class FileSystemBackendBase : IFileSystemBackend
{
    public abstract string FilesystemName { get; }

    // all 12 virtual, all defaulting to Unsupported
    public virtual Task<FsResult<FsMoveResult>> MoveAsync(string s, string d, CancellationToken ct)
        => Task.FromResult(Unsupported<FsMoveResult>("move"));

    // description hooks, generic defaults
    public virtual string DescribeMove => "Move or rename a file in this filesystem.";

    // shared implementation, previously copied into every adapter
    protected static FsResult<T> Unsupported<T>(string op) where T : class;
    protected static FsResult<T> NotFound<T>(string path) where T : class;
    protected static FsResult<T> Invalid<T>(string message) where T : class;
    protected static FsResult<T> ReadOnly<T>(string path) where T : class;
    protected static (string Prefix, bool DirsOnly, Regex Matcher) GlobPrologue(string? basePath, string pattern);
    protected static Regex CompileSearchRegex(string query);   // IgnoreCase + timeout + catch
    protected Task<FsResult<FsSearchResult>> SearchNodes(...); // the ~48-line template
}
```

## The 64 wrappers are two populations

| population | servers | files | today |
|---|---|---|---|
| backend-delegating | Timers, Scheduling, HomeAssistant, most of Printer | ~30 | `FsCreateTool(TimerFileSystem fs)` — one expression |
| disk-backed | Vault, Sandbox, Library, `FsBlobReadTool(IPrintSpool)` | ~32 | `FsCreateTool(McpSettings settings)` — composes `Domain/Tools/Files`, `Domain/Tools/Text`; **throws** |

The second population has no backend object to register from, which is why this plan builds `DiskFileSystem` before it can delete anything there.

## Tasks

1. **`FileSystemBackendBase`.** Envelope helpers, `Unsupported` defaults for all 12, glob prologue, search template, regex timeout + catch, description virtuals. Tests pin the defaults and the prologue, including the dirs-only trailing-slash rule that `PrinterQueueFileSystem` currently omits.
2. **Reparent the four non-disk backends.** `TimerFileSystem`, `ScheduleFileSystem`, `PrinterQueueFileSystem`, `HaFileSystem` override only what they implement. This is where the divergences close: schedules and timers gain the regex timeout, print-queue gains `basePath` and dirs-only, HA stops leaking `nameof(CreateAsync)` into an LLM-facing message. **Each of those four is a behaviour change** — give each its own failing test first.
3. **Rewrite the disk tools to return `FsResult`.** 13 classes, ~1,181 lines, including the 272-line `TextSearchTool`. Hoist the path jail out of the 8 copies into one `PathJail` value type built from the canonical root — this closes `/library` admitting `/library-backup`, and settles the three different `StringComparison` rules (`Ordinal` in `GlobFilesTool`, `OrdinalIgnoreCase` in `TextToolBase`, OS-conditional in `RemoveTool`/`MoveTool`) into one.
4. **`DiskFileSystem : FileSystemBackendBase`.** Parameterised by root and an optional `DownloadsOverlay` (Library composes one; Vault and Sandbox do not). Delete `MapErrorCode` and `ToolResponse.Create(Exception)`.
5. **Generic registrar.** `AddFileSystemTools(backend)` registers an `fs_*` tool only where the method is overridden. Test: for every backend, advertised tool names equal derived-supported operations. This test is the whole point of the plan.
6. **Delete the 64 wrappers**, one server per commit.
7. **`VirtualFileSystemRegistry.Resolve` returns `FsResult`.** It currently throws, and nothing in `Domain/Tools/FileSystem/` catches it, so an unmounted path (`/notes/x.md` instead of `/vault/x.md` — the mistake the prompt at `FileSystemToolFeature.cs:92` warns about) breaks the "errors are data, not exceptions" promise at `:86` across all 12 tool sites at once.
8. **Collapse the six operation lists to one.** `IFileSystemBackend`'s signatures, `FsResultContract.ResultTypes`, `McpFileSystemDiscovery._capabilityMap`, `ThreadSession._fileSystemMcpToolNames`, `FileSystemToolFeature.AllToolKeys` and its factory array. Derive from the base. `FsResultContract.TryValidate` fails closed on an unknown tool name instead of silently passing.
9. **Delete `Domain/Tools/Text/SearchOutputMode`** in favour of `VfsTextSearchOutputMode`. The native rewrite removes the string round-trip in Vault's and Sandbox's `FsSearchTool` that made the two enums agreeing an unchecked coincidence.
10. **Cover the three untested domain tools**: `VfsTextReadTool`, `VfsRemoveTool`, and `VfsTextSearchTool` — the last has real branching (`:38-52` chooses between the `filePath` and `directoryPath` paths and emits `invalid_argument` when both are null).

## Risks

- **Task 3 is the bulk of the work and the bulk of the risk.** `TextSearchTool` at 272 lines is the largest single rewrite. Do it behind its existing tests first, then change the return type.
- **Reflection over `DeclaringType`** must handle a backend that overrides a method and still returns `Unsupported` for some paths — capability is coarse and that is correct, but assert it deliberately so nobody later "fixes" it into a per-path check.
- **Library's `DownloadsOverlay`** means `DiskFileSystem` is a composition, not a root-path wrapper. Do not flatten it.
