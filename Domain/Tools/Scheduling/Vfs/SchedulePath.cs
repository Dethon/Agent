namespace Domain.Tools.Scheduling.Vfs;

public enum ScheduleNodeKind
{
    Root, AgentDir, AgentInfoFile, ScheduleDir, ScheduleFile, StatusFile, RunNowFile, Unknown
}

public sealed record ScheduleNode(ScheduleNodeKind Kind, string? AgentId, string? ScheduleId);

public static class SchedulePath
{
    public const string ScheduleFileName = "schedule.json";
    public const string StatusFileName = "status.json";
    public const string AgentInfoFileName = "agent_info.json";
    public const string RunNowFileName = "run_now.sh";

    public static ScheduleNode Parse(string path)
    {
        var segments = (path ?? "").Trim('/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return segments switch
        {
            [] => new ScheduleNode(ScheduleNodeKind.Root, null, null),
            [var agent] => new ScheduleNode(ScheduleNodeKind.AgentDir, agent, null),
            [var agent, AgentInfoFileName] => new ScheduleNode(ScheduleNodeKind.AgentInfoFile, agent, null),
            [var agent, var sched] => new ScheduleNode(ScheduleNodeKind.ScheduleDir, agent, sched),
            [var agent, var sched, ScheduleFileName] => new ScheduleNode(ScheduleNodeKind.ScheduleFile, agent, sched),
            [var agent, var sched, StatusFileName] => new ScheduleNode(ScheduleNodeKind.StatusFile, agent, sched),
            [var agent, var sched, RunNowFileName] => new ScheduleNode(ScheduleNodeKind.RunNowFile, agent, sched),
            _ => new ScheduleNode(ScheduleNodeKind.Unknown, null, null)
        };
    }
}