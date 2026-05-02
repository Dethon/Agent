namespace Domain.Prompts;

public static class SandboxPrompt
{
    public const string Prompt = """
        ## Sandbox Filesystem

        You have access to a Linux sandbox container exposed as the virtual filesystem mounted at `/sandbox`. Among the filesystems available to you, the sandbox is the **only** one that supports `domain__filesystem__exec` — other mounts will return an error envelope if you try.

        ### Layout

        - `/sandbox` — the container root (`/`). Read-accessible via filesystem tools (e.g., `/sandbox/etc/os-release`).
        - `/sandbox/home/sandbox_user` — the **persistent workspace** (a Docker named volume). Files here survive container restarts. This is also the **default working directory** for `domain__filesystem__exec` when you pass `path=/sandbox`.
        - `/sandbox/etc`, `/sandbox/usr`, `/sandbox/tmp`, etc. — system directories. They reset whenever the container is recreated and you typically cannot write to them (you run as the unprivileged `sandbox_user`, uid 1655).

        ### Capabilities

        - **File operations.** All `domain__filesystem__*` read/write tools work as on any other filesystem — read, create, edit, glob, search, move, remove.
        - **Command execution.** `domain__filesystem__exec` runs `bash -lc <command>` inside the sandbox container. Pass a virtual path that becomes the CWD: `/sandbox` resolves to `/home/sandbox_user`; deeper paths like `/sandbox/home/sandbox_user/myproject` are used literally. Each call is a fresh shell — environment variables and `cd` do **not** persist between calls; files do.
        - **Preinstalled tooling.** `bash`, `python3` + `pip` + `venv`, `git`, `curl`, `jq`, `unzip`, plus the standard coreutils. Install extra Python packages with `pip install --user <package>` (user-scope; persists in your home).
        - **Network.** Full **outbound** network is available (you can `curl`, `git clone`, `pip install`). The sandbox does **not** publish inbound ports — external clients cannot reach a server you start inside it.

        ### Limits (concrete values)

        - **Default timeout:** 60 s per `exec` call. Override via `timeoutSeconds`, clamped to a max of **1800 s** (30 min). On timeout the entire process tree is killed and `timedOut: true` is returned.
        - **Output cap:** each of `stdout` and `stderr` is truncated at **65 536 bytes** (64 KiB). The result `truncated: true` flags this — for long output, redirect to a file and read it back with `domain__filesystem__read`.
        - **Exit codes:** non-zero exit codes are returned as data (in `exitCode`); they do not raise errors.
        - **Permissions:** writes outside `/home/sandbox_user` typically fail with permission denied.

        ### Workflow tip

        Edit files in `/sandbox/home/sandbox_user/...` with `domain__filesystem__text_create` / `domain__filesystem__text_edit`, then run them with `domain__filesystem__exec`. The two tools operate on the same volume.
        """;
}
