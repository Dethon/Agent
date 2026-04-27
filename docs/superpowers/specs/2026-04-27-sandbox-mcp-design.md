# Sandbox MCP — Design Spec

**Date:** 2026-04-27
**Branch:** sandbox-mcp
**Status:** Approved (brainstorm)

## Goal

Give the agent a Linux sandbox in which it can write files and execute arbitrary bash and Python commands, exposed through the existing virtual-filesystem abstraction. A new MCP server (`McpServerSandbox`) hosts the sandbox; a new domain tool (`VfsExec`) lets the agent route commands to any filesystem whose MCP server opts in to a new `fs_exec` capability. Other filesystems (Vault, Library) stay file-only by simply not exposing the new tool.

## Non-Goals

- Long-running / streaming / background jobs — synchronous-capped only.
- Per-call ephemeral subcontainers, external bash services, or remote shells — execution happens inside the MCP server's own container.
- Network egress restrictions — the sandbox has full outbound network by design.
- Multi-tenant sandbox isolation — one shared sandbox container, one shared persistent volume.
- Approval-flow plumbing changes — `VfsExec` reuses the existing per-tool approval config.

## Architecture Overview

A new MCP server, **`McpServerSandbox`**, plays the same role as `McpServerVault` and `McpServerLibrary`: it exposes a `filesystem://sandbox` resource and is auto-discovered/mounted at `/sandbox` by `McpFileSystemDiscovery`. It registers the standard seven `fs_*` tools so all existing `Vfs*` domain tools work against it unchanged.

What's new is one extra capability:

1. **MCP tool layer** — `McpServerSandbox` adds `fs_exec`, which runs `bash -lc <command>` inside its own container.
2. **Domain abstraction** — `IFileSystemBackend` gains `ExecAsync(...)`. `McpFileSystemBackend.ExecAsync` calls `fs_exec` over MCP. Backends whose servers don't expose `fs_exec` get the same error JSON the existing operations produce when an MCP tool is missing — same opt-in pattern as the rest of the contract.
3. **Domain tool layer** — `VfsExecTool` resolves a virtual path through `IVirtualFileSystemRegistry` (same as `VfsTextRead`) and delegates to `backend.ExecAsync`. Registered through `FileSystemToolFeature` with key `"exec"` and the canonical name `domain:filesystem:exec`. The raw MCP `fs_exec` tool is filtered out of the agent toolset when the domain tool is enabled.

## Sandbox Container Layout

| Virtual path | Container path | Persistent? | Purpose |
|---|---|---|---|
| `/sandbox` | `/` | partly | Container root — full read access via `Vfs*` tools and bash. |
| `/sandbox/home/sandbox_user` | `/home/sandbox_user` | yes (named volume `sandbox-data`) | Default CWD; the agent's persistent workspace. |
| `/sandbox/etc`, `/sandbox/usr`, `/sandbox/tmp`, etc. | `/etc`, `/usr`, `/tmp`, etc. | no (ephemeral container layer) | Reachable for inspection or use; resets on container recreate. |

**Default CWD rule.** When `VfsExec` is called with a path that resolves to `""` or `"."` relative to the sandbox mount, `fs_exec` uses `HomeDir` (`/home/sandbox_user`) as CWD. Any other resolved relative path becomes the literal CWD inside the container, validated to stay under `ContainerRoot`.

**User.** All commands run as the unprivileged `sandbox_user`. No `sudo`. Package installs use `pip install --user` or equivalent user-scoped tooling.

**Network.** Default Docker network — full outbound. Agent can `pip install`, `curl`, `apt-get` (won't work without sudo, but `pip --user` does).

## Contract Changes

### `IFileSystemBackend`

Add:

```csharp
Task<JsonNode> ExecAsync(
    string path,            // relative to mount; "" or "." → backend's default CWD
    string command,         // passed to `bash -lc`
    int? timeoutSeconds,    // null → backend default; clamped to backend max
    CancellationToken ct);
```

**Return shape (success or non-zero exit):**

```json
{
  "stdout": "...",
  "stderr": "...",
  "exitCode": 0,
  "timedOut": false,
  "truncated": false,
  "durationMs": 142,
  "cwd": "/home/sandbox_user/myproject"
}
```

**Error shape (couldn't spawn / backend has no exec support):** existing `{"error": true, "message": "..."}` convention used by all other `IFileSystemBackend` methods.

Non-zero exit codes are NOT errors — they are returned as data.

### `McpFileSystemBackend`

Add `ExecAsync` that calls MCP tool `"fs_exec"` with `{filesystem, path, command, timeoutSeconds}`. Reuses the existing `CallToolAsync` helper; the same error path applies when the server doesn't expose the tool.

### `VfsExecTool`

New file `Domain/Tools/FileSystem/VfsExecTool.cs`. Mirrors `VfsTextReadTool`:

```csharp
[Description("Execute a bash command on a filesystem that supports execution. The path is the working directory (CWD). On the sandbox filesystem, /sandbox resolves to the agent's home directory. Commands run via `bash -lc`. Non-zero exit codes are returned as data, not errors. Output is truncated at the backend's cap.")]
public async Task<JsonNode> RunAsync(
    [Description("Virtual path used as CWD. /sandbox uses the home dir; deeper paths are used literally.")] string path,
    [Description("Bash command line; passed to `bash -lc`.")] string command,
    [Description("Optional timeout in seconds. Backend clamps to its max.")] int? timeoutSeconds = null,
    CancellationToken ct = default);
```

Resolves via `IVirtualFileSystemRegistry`, calls `resolution.Backend.ExecAsync(resolution.RelativePath, command, timeoutSeconds, ct)`.

### `FileSystemToolFeature`

- `AllToolKeys` gains `"exec"`.
- `GetTools` adds `VfsExecTool` when enabled.
- Tool name: `domain:filesystem:exec`.

### `ThreadSession`

- `_fileSystemMcpToolNames` gains `"fs_exec"` so the raw MCP tool is filtered when the domain tool is active.

### Agent config

Default: `exec` enabled, approval-required. Operators can disable approval per agent (e.g., the ServiceBus auto-approval path already exists for trusted runs).

## `McpServerSandbox` Project

```
McpServerSandbox/
├── Program.cs
├── McpServerSandbox.csproj           # net10.0, refs Infrastructure
├── Dockerfile                         # .NET runtime + bash + python3 + pip + curl + git + ca-certs
│                                       # creates non-root sandbox_user with $HOME=/home/sandbox_user
├── appsettings.json
├── Settings/McpSettings.cs            # ContainerRoot="/", HomeDir="/home/sandbox_user",
│                                       # DefaultTimeoutSeconds=60, MaxTimeoutSeconds=600,
│                                       # OutputCapBytes=65536
├── Modules/ConfigModule.cs            # WithTools<Fs*Tool>() x8 (incl. FsExecTool),
│                                       # WithResources<FileSystemResource>,
│                                       # WithPrompts<SandboxPrompt>
├── McpResources/
│   └── FileSystemResource.cs          # filesystem://sandbox, mountPoint=/sandbox
├── McpPrompts/
│   └── SandboxPrompt.cs               # NEW — explains layout, persistence, network, Python, caps
└── McpTools/
    ├── FsReadTool.cs FsCreateTool.cs FsEditTool.cs
    ├── FsGlobTool.cs FsSearchTool.cs FsMoveTool.cs FsDeleteTool.cs
    └── FsExecTool.cs                   # NEW
```

The seven file tools are thin over `LocalFileSystemClient` rooted at `ContainerRoot` (`/`). **No extension allowlist** — the agent legitimately needs `.py`, `.sh`, `.ipynb`, etc.

### `FsExecTool` semantics

- Spawns `bash -lc <command>` via `Process.Start`.
- `WorkingDirectory`:
  - empty / `.` / null relative path → `HomeDir`
  - else → `Path.Combine(ContainerRoot, relativePath)`, canonicalised. The full container is intentionally addressable, so there is no "escape" check — the only validation is that the resolved CWD exists and is a directory; otherwise the call returns the error JSON shape.
- Runs as `sandbox_user` (already the container's default user, so no explicit setuid needed).
- Timeout: `Math.Clamp(timeoutSeconds ?? DefaultTimeoutSeconds, 1, MaxTimeoutSeconds)`.
- Reads stdout/stderr concurrently; each stream stops capturing once it hits `OutputCapBytes`. Process keeps running so the exit code remains observable. `truncated=true` if either stream hit the cap.
- On timeout: `Process.Kill(entireProcessTree: true)`. `timedOut=true`, partial output returned, `exitCode = -1`.
- Returns the JSON shape defined above.

### `SandboxPrompt`

New `[McpServerPromptType]` registered via `WithPrompts`. Renders a system-message prompt the agent receives at session start. Content explains:

- The sandbox is a Linux container reachable at virtual mount `/sandbox`.
- `/sandbox/home/sandbox_user` is persistent across restarts; everything else under `/sandbox` is ephemeral container state.
- Default CWD for `VfsExec` is the home directory.
- Python 3 is preinstalled. Use `pip install --user` for extra packages.
- Full network egress is available; offline behaviour is not guaranteed.
- Output is truncated at `OutputCapBytes` per stream; commands time out at `MaxTimeoutSeconds`.
- File edits via `VfsTextCreate` / `VfsTextEdit` and shell commands via `VfsExec` operate on the same volume — round-tripping works.

## Docker Compose

`DockerCompose/docker-compose.yml` — new service:

```yaml
mcp-sandbox:
  build:
    context: ..
    dockerfile: McpServerSandbox/Dockerfile
  environment:
    - McpSettings__ContainerRoot=/
    - McpSettings__HomeDir=/home/sandbox_user
    - McpSettings__DefaultTimeoutSeconds=60
    - McpSettings__MaxTimeoutSeconds=600
    - McpSettings__OutputCapBytes=65536
  volumes:
    - sandbox-data:/home/sandbox_user
  networks:
    - default
```

And:

```yaml
volumes:
  sandbox-data:
```

Override files (`docker-compose.override.windows.yml`, `docker-compose.override.linux.yml`) — no changes; the sandbox needs no user-secrets mount.

`appsettings.json` for the agent — add `mcp-sandbox` to the MCP servers list so the agent connects on startup.

`.env` — no entries needed (no secrets).

The `up -d` command in `CLAUDE.md` should be updated to include the new `mcp-sandbox` service.

## Testing

### Unit

- `VfsExecToolTests` — path resolution, default-CWD logic for empty / `.` paths, timeout pass-through, error when registry has no matching backend.
- `McpFileSystemBackendTests` — adds an `ExecAsync` case using a fake MCP client returning a structured JSON result, plus a missing-tool error case.

### Integration

- `McpAgentSandboxTests` (new fixture `McpSandboxServerFixture`):
  - `python3 -c "print(2+2)"` via `domain:filesystem:exec` returns `4` in stdout, exit 0.
  - Round-trip: `VfsTextCreate("/sandbox/home/sandbox_user/hello.py", "print('hi')")`, then `VfsExec("/sandbox/home/sandbox_user", "python3 hello.py")` produces `hi`.
  - State persistence: env vars set in one call do NOT persist (separate `bash -lc` invocations) — explicit guarantee. Files written DO persist.
  - Timeout: `sleep 5` with `timeoutSeconds=1` returns `timedOut=true`, partial/empty output, killed process tree.
  - Output truncation: `yes | head -c 200000` exceeds 64 KiB cap → `truncated=true`, stdout length ≤ cap, exit 0.
  - Non-zero exit propagation: `false` returns `exitCode=1`, no error wrapper.
  - CWD validation: a path that resolves to a non-existent directory returns the error JSON shape.

### E2E

None — covered at integration level.

## Out-of-Scope / Future

- Long-running background jobs and streaming output.
- Per-session shells with persistent env / cwd between calls.
- Network policy controls.
- Other filesystems (Vault, Library) opting in to `fs_exec`.

## Risks

- **Blast radius.** `bash` + network + persistent home means the agent can install arbitrary code. Mitigated by: non-root user, default approval-required, container isolation.
- **Resource exhaustion.** A bad command can fill the volume or peg CPU. Out of scope for v1; revisit with cgroup limits or volume quotas if it bites.
- **Output cap interaction with binary streams.** Capping bytes on a stream that emits binary may yield malformed JSON in stdout. Acceptable — stdout is treated as text in this contract.
