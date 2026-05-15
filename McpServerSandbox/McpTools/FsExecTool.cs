using System.ComponentModel;
using Domain.Contracts;
using Domain.Tools.Bash;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerSandbox.McpTools;

[McpServerToolType]
public class FsExecTool(ICommandRunner runner) : ExecTool(runner)
{
    [McpServerTool(Name = "fs_exec")]
    [Description(Description)]
    public async Task<CallToolResult> McpRun(
        string path,
        string command,
        int? timeoutSeconds = null,
        CancellationToken cancellationToken = default)
    {
        return ToolResponse.Create(await Run(path, command, timeoutSeconds, cancellationToken));
    }
}