using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Domain.Tools.FileSystem;

public class VfsExecTool(IVirtualFileSystemRegistry registry)
{
    public const string Key = "exec";
    public const string Name = "exec";

    public const string ToolDescription = """
        Execute a bash command on a filesystem that supports execution.
        The path argument is the working directory (CWD) for the command, expressed as a virtual path.
        On the sandbox filesystem, /sandbox uses the agent's home directory as the default CWD;
        deeper paths (e.g., /sandbox/home/sandbox_user/myproject) are used literally as the CWD.
        Commands run via `bash -lc` so login shell env (PATH, etc.) is initialised.
        Non-zero exit codes are returned as data (in `exitCode`), not as errors.
        Output is truncated at the backend's per-stream cap; check `truncated` in the result.
        Optional `timeoutSeconds` is clamped to the backend's max; on timeout the process tree is killed.
        """;

    [Description(ToolDescription)]
    public async Task<JsonNode> RunAsync(
        [Description("Virtual path used as CWD (e.g., /sandbox or /sandbox/home/sandbox_user/myproject)")]
        string path,
        [Description("Bash command line; passed to `bash -lc`")]
        string command,
        [Description("Optional timeout in seconds. Backend clamps to its max.")]
        int? timeoutSeconds = null,
        CancellationToken cancellationToken = default)
    {
        var resolution = registry.Resolve(path);
        return await resolution.Backend.ExecAsync(resolution.RelativePath, command, timeoutSeconds, cancellationToken);
    }
}