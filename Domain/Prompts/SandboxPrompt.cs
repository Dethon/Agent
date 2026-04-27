namespace Domain.Prompts;

public static class SandboxPrompt
{
    public const string Prompt = """
        ## Sandbox Filesystem

        You have access to a Linux sandbox container exposed as the virtual filesystem mounted at `/sandbox`.

        ### Layout

        - `/sandbox` — the container root (`/`). Read-accessible via filesystem tools (e.g., `/sandbox/etc/os-release`).
        - `/sandbox/home/sandbox_user` — the **persistent workspace** (a Docker named volume). Files here survive container restarts. This is also the **default working directory** for `domain:filesystem:exec`.
        - `/sandbox/etc`, `/sandbox/usr`, `/sandbox/tmp`, etc. — system directories. They reset whenever the container is recreated and you typically cannot write to them (you run as the unprivileged `sandbox_user`).

        ### Capabilities

        - **File operations.** All `domain:filesystem:*` tools work as on any other filesystem. Use them to read system files, edit your project files, glob, search, move, and remove.
        - **Command execution.** `domain:filesystem:exec` runs `bash -lc <command>` inside the sandbox container. You pass a virtual path that becomes the CWD: pass `/sandbox` to use the home directory as CWD, or a deeper path like `/sandbox/home/sandbox_user/myproject` to set CWD literally. Each call is a fresh shell — environment variables and `cd` do **not** persist between calls; files do.
        - **Python.** Python 3 is preinstalled. Install extra packages with `pip install --user <package>` (user-scope; persists in your home).
        - **Network.** Full outbound network is available. You can `curl`, `git clone`, `pip install`, etc.

        ### Limits

        - Each command has a default timeout (the backend chooses; you can override via `timeoutSeconds` up to the backend max). On timeout the process tree is killed.
        - Output is truncated at a per-stream byte cap. The result `truncated` field tells you when this happens. Long-running observability should write to a file and read it.
        - Non-zero exit codes are returned as data — they do not raise errors. Inspect `exitCode`.
        - You run as a non-root user (`sandbox_user`). Writes outside `/home/sandbox_user` will typically fail with permission denied.

        ### Workflow tip

        Edit files in `/sandbox/home/sandbox_user/...` with `domain:filesystem:text_create` / `domain:filesystem:text_edit`, then run them with `domain:filesystem:exec`. The two tools operate on the same volume.
        """;
}
