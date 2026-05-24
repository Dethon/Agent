using System.ComponentModel;
using Domain.Tools.Scheduling.Vfs;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerScheduling.McpTools;

[McpServerToolType]
public class FsExecTool(ScheduleFileSystem fs)
{
    [McpServerTool(Name = "fs_exec")]
    [Description("Run a schedule action. path is the schedule DIRECTORY (e.g. /jonas/my-schedule); command is 'run_now.sh' to fire it immediately. Not a shell — anything other than run_now.sh returns exit 127.")]
    public async Task<CallToolResult> McpRun(string path, string command, int? timeoutSeconds = null, CancellationToken ct = default)
        => ToolResponse.Create(await fs.ExecAsync(path, command, timeoutSeconds, ct));
}