namespace Domain.Prompts;

public static class SandboxPrompt
{
    public const string Prompt = """
        ## Sandbox Filesystem

        You have access to a Linux sandbox container exposed as the virtual filesystem mounted at `/sandbox`. Among the filesystems available to you, the sandbox is the **only** one that supports command execution — other mounts return an "unsupported operation" error envelope if you try.

        ### Layout

        - `/sandbox` — the container root (`/`). Read-accessible via filesystem tools (e.g., `/sandbox/etc/os-release`).
        - `/sandbox/home/sandbox_user` — the **persistent workspace** (a Docker named volume). Files here survive container restarts. This is also the default working directory for command execution when you target `/sandbox`.
        - `/sandbox/etc`, `/sandbox/usr`, `/sandbox/tmp`, etc. — system directories. They reset whenever the container is recreated and you typically cannot write to them (you run as the unprivileged `sandbox_user`).

        ### Capabilities

        - **File operations.** Standard read/write/glob/search/move/remove all work here as on any other mount.
        - **Command execution.** Commands run via `bash -lc` inside the container. Each call is a fresh shell — environment variables and `cd` do **not** persist between calls; files written to the persistent workspace do. See the exec tool's description for argument details, working-directory mapping, and limits.
        - **Preinstalled tooling.** `bash`, `python3` + `pip` + `venv`, `git`, `curl`, `jq`, `unzip`, plus the standard coreutils. Install extra Python packages with `pip install --user <package>` (user-scope; persists in your home).
        - **Network.** Full **outbound** network is available (you can `curl`, `git clone`, `pip install`). The sandbox does **not** publish inbound ports — external clients cannot reach a server you start inside it.

        ### Behaviour you should rely on

        - **Exit codes** are returned as data, not raised as errors — branch on them.
        - **Output is capped** per stream and the result flags truncation; for long output, redirect to a file and read it back with the file tools.
        - **Timeouts** kill the entire process tree and surface a timeout flag; raise the limit only when you genuinely need a longer-running command.
        - **Writes outside the persistent workspace** typically fail with permission denied; keep working files under `/sandbox/home/sandbox_user/...`.

        ### Workflow tip

        Edit files under `/sandbox/home/sandbox_user/...` with the filesystem write tools, then run them with the exec tool. Both operate on the same volume.
        """;
}
