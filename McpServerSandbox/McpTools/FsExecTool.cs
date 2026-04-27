using System.ComponentModel;
using Infrastructure.Utils;
using McpServerSandbox.Services;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerSandbox.McpTools;

[McpServerToolType]
public class FsExecTool(BashRunner runner)
{
    private const string Description = """
        Execute a bash command (`bash -lc <command>`) inside the sandbox container.
        The path argument is a relative path under the sandbox root that becomes the CWD;
        empty string or "." use the home directory.
        Output is truncated at the configured cap. On timeout the process tree is killed.
        Non-zero exit codes are returned in the result, not as errors.
        """;

    [McpServerTool(Name = "fs_exec")]
    [Description(Description)]
    public async Task<CallToolResult> McpRun(
        string filesystem,
        string path,
        string command,
        int? timeoutSeconds = null,
        CancellationToken cancellationToken = default)
    {
        var result = await runner.RunAsync(path, command, timeoutSeconds, cancellationToken);
        return ToolResponse.Create(result);
    }
}
