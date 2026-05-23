using System.ComponentModel;
using Domain.Tools.HomeAssistant.Vfs;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerHomeAssistant.McpTools;

[McpServerToolType]
public class FsExecTool(HaFileSystem fs)
{
    [McpServerTool(Name = "fs_exec")]
    [Description("Runs a Home Assistant action file (a service call). path is the entity directory CWD (e.g. /ha/entities/light/kitchen); command is an action file invocation like 'turn_on.sh --brightness_pct 60'. Use '<service>.sh --help' to see arguments. This is NOT a shell — only *.sh action files run; anything else returns exit 127.")]
    public async Task<CallToolResult> McpRun(
        string path,
        string command,
        int? timeoutSeconds = null,
        CancellationToken cancellationToken = default)
    {
        return ToolResponse.Create(await fs.ExecAsync(path, command, timeoutSeconds, cancellationToken));
    }
}