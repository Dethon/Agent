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
        description = "Scheduled agent tasks, grouped by agent. Discover agents by globbing /schedules (each agent is a directory); read /schedules/<agentId>/agent_info.json to learn what an agent does. Create a schedule with fs_create at /schedules/<agentId>/<descriptive-unique-id>/schedule.json containing JSON {prompt, cron|runAt, userId?, deliverTo?}: provide EXACTLY ONE of cron (recurring, standard 5-field UTC cron, e.g. \"0 9 * * *\" = daily 09:00, \"30 14 * * 1-5\" = weekdays 14:30) or runAt (one-shot ISO-8601 UTC datetime, auto-deleted after it fires). deliverTo is an optional list of channel ids (e.g. [\"signalr\",\"telegram\"]) to receive the result; omit for the default. Change prompt/timing with fs_edit, reassign to another agent or rename with fs_move, remove with fs_delete. Read /schedules/<agentId>/<scheduleId>/status.json for createdAt/lastRunAt/nextRunAt. Fire a schedule immediately with fs_exec on its directory using command run_now.sh. Use descriptive, unique schedule ids."
    });
}