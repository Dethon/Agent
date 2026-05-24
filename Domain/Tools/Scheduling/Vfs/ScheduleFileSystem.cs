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

    private static readonly JsonSerializerOptions _parseOptions = new() { PropertyNameCaseInsensitive = true };

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

    public async Task<JsonNode> SearchAsync(string query, CancellationToken ct)
    {
        var all = await store.ListAsync(ct);
        var hits = all.Where(s =>
            s.Id.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            s.Prompt.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            s.AgentId.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

        return FsResultContract.ToNode(new FsSearchResult
        {
            Query = query,
            Regex = false,
            Path = "/",
            FilesSearched = all.Count,
            FilesWithMatches = hits.Count,
            TotalMatches = hits.Count,
            Truncated = false,
            Results = hits.Select(s => new FsSearchFileResult { File = $"/{s.AgentId}/{s.Id}/schedule.json", MatchCount = 1 }).ToList()
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

    public async Task<JsonNode> CreateAsync(string path, string content, bool overwrite, bool createDirectories, CancellationToken ct)
    {
        var node = SchedulePath.Parse(path);
        if (node.Kind != ScheduleNodeKind.ScheduleFile || node.AgentId is null || node.ScheduleId is null)
        {
            return Invalid($"Create a schedule at /<agentId>/<scheduleId>/schedule.json (got '{path}')");
        }

        if (!agents.Exists(node.AgentId))
        {
            return ToolError.Create(ToolError.Codes.NotFound, $"Unknown agent '{node.AgentId}'", retryable: false);
        }

        // Schedules use a unique-id model: create always rejects an existing id regardless of `overwrite`. Use fs_edit to modify an existing schedule.
        if (await store.GetAsync(node.ScheduleId, ct) is not null)
        {
            return ToolError.Create(ToolError.Codes.AlreadyExists, $"Schedule '{node.ScheduleId}' already exists", retryable: false);
        }

        var spec = ParseSpec(content, out var specError);
        if (specError is not null)
        {
            return specError;
        }

        var validation = ValidateSpec(spec!);
        if (validation is not null)
        {
            return validation;
        }

        var schedule = new Schedule
        {
            Id = node.ScheduleId,
            AgentId = node.AgentId,
            Prompt = spec!.Prompt!,
            CronExpression = spec.Cron,
            RunAt = spec.RunAt,
            UserId = spec.UserId,
            DeliverTo = spec.DeliverTo,
            CreatedAt = DateTime.UtcNow,
            NextRunAt = ComputeNextRunAt(spec)
        };

        await store.CreateAsync(schedule, ct);
        return FsResultContract.ToNode(new FsCreateResult
        {
            Status = "created", FilePath = path, Size = content.Length.ToString(), Lines = content.Split('\n').Length
        });
    }

    public async Task<JsonNode> EditAsync(string path, IReadOnlyList<TextEdit> edits, CancellationToken ct)
    {
        var node = SchedulePath.Parse(path);
        if (node.Kind != ScheduleNodeKind.ScheduleFile || await GetScheduleAsync(node, ct) is not { } existing)
        {
            return NotFound(path);
        }

        var current = RenderSpec(existing);
        var updatedText = edits.Aggregate(current, (acc, e) =>
            e.ReplaceAll ? acc.Replace(e.OldString, e.NewString)
                         : ReplaceFirst(acc, e.OldString, e.NewString));

        var spec = ParseSpec(updatedText, out var specError);
        if (specError is not null)
        {
            return specError;
        }

        var validation = ValidateSpec(spec!);
        if (validation is not null)
        {
            return validation;
        }

        var updated = existing with
        {
            Prompt = spec!.Prompt!,
            CronExpression = spec.Cron,
            RunAt = spec.RunAt,
            UserId = spec.UserId,
            DeliverTo = spec.DeliverTo,
            NextRunAt = ComputeNextRunAt(spec)
        };

        await store.CreateAsync(updated, ct);
        return FsResultContract.ToNode(new FsEditResult
        {
            Status = "edited", FilePath = path, TotalOccurrencesReplaced = edits.Count,
            Edits = edits.Select(_ => new FsEditDetail { OccurrencesReplaced = 1, AffectedLines = new FsLineRange { Start = 1, End = 1 } }).ToList()
        });
    }

    public async Task<JsonNode> MoveAsync(string sourcePath, string destinationPath, CancellationToken ct)
    {
        var src = SchedulePath.Parse(sourcePath);
        var dst = SchedulePath.Parse(destinationPath);
        if (src.Kind != ScheduleNodeKind.ScheduleDir || dst.Kind != ScheduleNodeKind.ScheduleDir)
        {
            return Invalid("Move a schedule dir to /<agentId>/<scheduleId>");
        }

        if (await GetScheduleAsync(src, ct) is not { } existing)
        {
            return NotFound(sourcePath);
        }

        if (!agents.Exists(dst.AgentId!))
        {
            return ToolError.Create(ToolError.Codes.NotFound, $"Unknown agent '{dst.AgentId}'", retryable: false);
        }

        if (dst.ScheduleId != src.ScheduleId && await store.GetAsync(dst.ScheduleId!, ct) is not null)
        {
            return ToolError.Create(ToolError.Codes.AlreadyExists, $"Schedule '{dst.ScheduleId}' already exists", retryable: false);
        }

        if (dst.ScheduleId != src.ScheduleId)
        {
            await store.DeleteAsync(src.ScheduleId!, ct);
        }

        await store.CreateAsync(existing with { Id = dst.ScheduleId!, AgentId = dst.AgentId! }, ct);
        return FsResultContract.ToNode(new FsMoveResult
        {
            Status = "moved", Message = "reassigned", Source = sourcePath, Destination = destinationPath
        });
    }

    public async Task<JsonNode> DeleteAsync(string path, CancellationToken ct)
    {
        var node = SchedulePath.Parse(path);
        if (node.Kind != ScheduleNodeKind.ScheduleDir || await GetScheduleAsync(node, ct) is null)
        {
            return NotFound(path);
        }

        await store.DeleteAsync(node.ScheduleId!, ct);
        return FsResultContract.ToNode(new FsRemoveResult
        {
            Status = "deleted", Message = "removed", OriginalPath = path, TrashPath = ""
        });
    }

    private sealed record SpecDto
    {
        public string? Prompt { get; init; }
        public string? Cron { get; init; }
        public DateTime? RunAt { get; init; }
        public string? UserId { get; init; }
        public IReadOnlyList<string>? DeliverTo { get; init; }
    }

    private static SpecDto? ParseSpec(string content, out JsonNode? error)
    {
        error = null;
        try
        {
            var spec = JsonSerializer.Deserialize<SpecDto>(content, _parseOptions);
            if (spec is null)
            {
                error = Invalid("schedule.json is empty");
            }

            return spec;
        }
        catch (JsonException ex)
        {
            error = Invalid($"Invalid schedule.json: {ex.Message}");
            return null;
        }
    }

    private JsonNode? ValidateSpec(SpecDto spec)
    {
        if (string.IsNullOrWhiteSpace(spec.Prompt))
        {
            return Invalid("prompt is required");
        }

        if (spec.Cron is null && spec.RunAt is null)
        {
            return Invalid("Provide either cron or runAt");
        }

        if (spec.Cron is not null && spec.RunAt is not null)
        {
            return Invalid("Provide only cron OR runAt, not both");
        }

        if (spec.Cron is not null && !cronValidator.IsValid(spec.Cron))
        {
            return Invalid($"Invalid cron expression: {spec.Cron}");
        }

        if (spec.RunAt is not null && spec.RunAt <= DateTime.UtcNow)
        {
            return Invalid("runAt must be in the future");
        }

        return null;
    }

    private DateTime? ComputeNextRunAt(SpecDto spec) =>
        spec.RunAt ?? (spec.Cron is not null ? cronValidator.GetNextOccurrence(spec.Cron, DateTime.UtcNow) : null);

    private static string ReplaceFirst(string text, string oldValue, string newValue)
    {
        var i = text.IndexOf(oldValue, StringComparison.Ordinal);
        return i < 0 ? text : text[..i] + newValue + text[(i + oldValue.Length)..];
    }

    private static JsonNode Invalid(string message) =>
        ToolError.Create(ToolError.Codes.InvalidArgument, message, retryable: false);

    private static JsonNode NotFound(string path) =>
        ToolError.Create(ToolError.Codes.NotFound, $"Path not found: {path}", retryable: false);
}