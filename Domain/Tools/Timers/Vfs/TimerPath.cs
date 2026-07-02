namespace Domain.Tools.Timers.Vfs;

public enum TimerNodeKind
{
    Root, TimerDir, TimerFile, StatusFile, Unknown
}

public sealed record TimerNode(TimerNodeKind Kind, string? TimerId);

public static class TimerPath
{
    public const string TimerFileName = "timer.json";
    public const string StatusFileName = "status.json";

    public static TimerNode Parse(string path)
    {
        var segments = (path ?? "").Trim('/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (Array.Exists(segments, s => s is "." or ".."))
        {
            return new TimerNode(TimerNodeKind.Unknown, null);
        }

        return segments switch
        {
            [] => new TimerNode(TimerNodeKind.Root, null),
            [var id] when !IsReserved(id) => new TimerNode(TimerNodeKind.TimerDir, id),
            [var id, TimerFileName] when !IsReserved(id) => new TimerNode(TimerNodeKind.TimerFile, id),
            [var id, StatusFileName] when !IsReserved(id) => new TimerNode(TimerNodeKind.StatusFile, id),
            _ => new TimerNode(TimerNodeKind.Unknown, null)
        };
    }

    private static bool IsReserved(string segment) =>
        segment is TimerFileName or StatusFileName;
}