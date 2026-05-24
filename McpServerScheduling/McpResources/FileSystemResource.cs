using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace McpServerScheduling.McpResources;

[McpServerResourceType]
public class FileSystemResource
{
    [McpServerResource(UriTemplate = "filesystem://schedules", Name = "Schedules Filesystem", MimeType = "application/json")]
    [Description("Scheduled-task control surface")]
    public string GetInfo() => JsonSerializer.Serialize(new
    {
        name = "schedules",
        mountPoint = "/schedules",
        description = "Scheduled agent tasks grouped by agent: /<agentId>/<scheduleId>/schedule.json (edit), status.json (read-only), run_now.sh (exec). Agent dirs are always listed; read agent_info.json to learn an agent. Create a schedule with fs_create using a descriptive, unique id; reassign with fs_move; supports fs_exec for run_now."
    });
}