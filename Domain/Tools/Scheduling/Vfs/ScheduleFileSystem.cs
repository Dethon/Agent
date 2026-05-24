using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.FileSystem;
using Domain.Tools;

namespace Domain.Tools.Scheduling.Vfs;

public sealed class ScheduleFileSystem(
    IScheduleStore store,
    IScheduleAgentCatalog agents,
    ICronValidator cronValidator)
{
    private static readonly JsonSerializerOptions _json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task<JsonNode> GlobAsync(string basePath, string pattern, CancellationToken ct)
    {
        var node = SchedulePath.Parse(basePath);
        // pattern is unused: the schedule tree is shallow (agent/schedule) and entries are returned unfiltered.
        switch (node.Kind)
        {
            case ScheduleNodeKind.Root:
                {
                    var entries = agents.GetAll().Select(a => $"/{a.Id}").ToList();
                    return Glob(entries);
                }
            case ScheduleNodeKind.AgentDir when agents.Exists(node.AgentId!):
                {
                    var all = await store.ListAsync(ct);
                    var entries = all.Where(s => s.AgentId == node.AgentId)
                        .Select(s => $"/{node.AgentId}/{s.Id}").ToList();
                    return Glob(entries);
                }
            default:
                return NotFound(basePath);
        }
    }

    public async Task<JsonNode> InfoAsync(string path, CancellationToken ct)
    {
        var node = SchedulePath.Parse(path);
        var exists = await NodeExistsAsync(node, ct);
        var isDir = node.Kind is ScheduleNodeKind.Root or ScheduleNodeKind.AgentDir or ScheduleNodeKind.ScheduleDir;
        return FsResultContract.ToNode(new FsInfoResult
        {
            Exists = exists,
            Path = path,
            IsDirectory = exists ? isDir : null
        });
    }

    public async Task<JsonNode> ReadAsync(string path, int? offset, int? limit, CancellationToken ct)
    {
        var node = SchedulePath.Parse(path);
        // offset/limit are unused: schedule files are small JSON blobs, always returned whole (Truncated = false).
        string content;
        switch (node.Kind)
        {
            case ScheduleNodeKind.AgentInfoFile when agents.Get(node.AgentId!) is { } info:
                content = JsonSerializer.Serialize(info, _json);
                break;
            case ScheduleNodeKind.ScheduleFile when await GetScheduleAsync(node, ct) is { } s:
                content = RenderSpec(s);
                break;
            case ScheduleNodeKind.StatusFile when await GetScheduleAsync(node, ct) is { } s:
                content = RenderStatus(s);
                break;
            case ScheduleNodeKind.RunNowFile when await GetScheduleAsync(node, ct) is not null:
                content = "# Run this schedule now:\n#   exec run_now.sh\n";
                break;
            default:
                return NotFound(path);
        }

        return FsResultContract.ToNode(new FsReadResult
        {
            FilePath = path,
            Content = content,
            TotalLines = content.Split('\n').Length,
            Truncated = false
        });
    }

    private static string RenderSpec(Schedule s) => JsonSerializer.Serialize(new
    {
        prompt = s.Prompt,
        cron = s.CronExpression,
        runAt = s.RunAt,
        userId = s.UserId,
        deliverTo = s.DeliverTo
    }, _json);

    private static string RenderStatus(Schedule s) => JsonSerializer.Serialize(new
    {
        createdAt = s.CreatedAt,
        lastRunAt = s.LastRunAt,
        nextRunAt = s.NextRunAt
    }, _json);

    private async Task<Schedule?> GetScheduleAsync(ScheduleNode node, CancellationToken ct)
    {
        if (node.AgentId is null || node.ScheduleId is null || !agents.Exists(node.AgentId))
        {
            return null;
        }

        var s = await store.GetAsync(node.ScheduleId, ct);
        return s is not null && s.AgentId == node.AgentId ? s : null;
    }

    private async Task<bool> NodeExistsAsync(ScheduleNode node, CancellationToken ct) => node.Kind switch
    {
        ScheduleNodeKind.Root => true,
        ScheduleNodeKind.AgentDir or ScheduleNodeKind.AgentInfoFile => agents.Exists(node.AgentId!),
        ScheduleNodeKind.ScheduleDir or ScheduleNodeKind.ScheduleFile
            or ScheduleNodeKind.StatusFile or ScheduleNodeKind.RunNowFile => await GetScheduleAsync(node, ct) is not null,
        _ => false
    };

    private static JsonNode Glob(IReadOnlyList<string> entries) => FsResultContract.ToNode(new FsGlobResult
    {
        Entries = entries,
        Truncated = false,
        Total = entries.Count
    });

    private static JsonNode NotFound(string path) =>
        ToolError.Create(ToolError.Codes.NotFound, $"Path not found: {path}", retryable: false);
}