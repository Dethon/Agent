using System.Text.Json;
using System.Text.RegularExpressions;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.FileSystem;
using Domain.Tools;
using Domain.Tools.FileSystem;

namespace Domain.Tools.Scheduling.Vfs;

public sealed class ScheduleFileSystem(
    IScheduleStore store,
    IAgentCatalog agents,
    ICronValidator cronValidator,
    TimeProvider timeProvider) : IFileSystemBackend
{
    public string FilesystemName => "schedules";

    private static readonly JsonSerializerOptions _json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly JsonSerializerOptions _parseOptions = new() { PropertyNameCaseInsensitive = true };

    // Glob matches the same semantics as every other filesystem: the pattern (relative to basePath)
    // filters the schedule tree, `*`/`**`/`?`/`{a,b}` behave as documented, a trailing slash lists
    // directories only, and directory results are marked with a trailing slash.
    public async Task<FsResult<FsGlobResult>> GlobAsync(string basePath, string pattern, CancellationToken ct)
    {
        var all = await store.ListAsync(ct);
        var prefix = string.IsNullOrEmpty(basePath?.Trim('/')) ? string.Empty : basePath.Trim('/') + "/";

        var dirsOnly = pattern.EndsWith('/');
        var effectivePattern = dirsOnly ? pattern.TrimEnd('/') : pattern;
        var matches = GlobRegex.CompileMatcher(prefix + effectivePattern);

        var dirs = ScheduleTree.Directories(agents, all).Where(matches).Select(p => $"/{p}/");
        if (dirsOnly)
        {
            return Glob(dirs.OrderBy(p => p, StringComparer.Ordinal).ToList());
        }

        var files = ScheduleTree.Files(agents, all).Where(matches).Select(p => $"/{p}");
        return Glob(dirs.Concat(files).OrderBy(p => p, StringComparer.Ordinal).ToList());
    }

    public async Task<FsResult<FsInfoResult>> InfoAsync(string path, CancellationToken ct)
    {
        var node = SchedulePath.Parse(path);
        var exists = await NodeExistsAsync(node, ct);
        var isDir = node.Kind is ScheduleNodeKind.Root or ScheduleNodeKind.AgentDir or ScheduleNodeKind.ScheduleDir;
        return new FsResult<FsInfoResult>.Ok(new FsInfoResult
        {
            Exists = exists,
            Path = path,
            IsDirectory = exists ? isDir : null
        });
    }

    public async Task<FsResult<FsReadResult>> ReadAsync(string path, int? offset, int? limit, CancellationToken ct)
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
                return NotFound<FsReadResult>(path);
        }

        return new FsResult<FsReadResult>.Ok(new FsReadResult
        {
            FilePath = path,
            Content = content,
            TotalLines = content.Split('\n').Length,
            Truncated = false
        });
    }

    // fs_search follows the standard VFS convention: it scans each schedule's searchable schedule.json
    // content line-by-line, honoring regex, scope (path/directoryPath), filePattern, maxResults,
    // contextLines, and the Content/FilesOnly output shape — identical to the file-backed backends.
    public async Task<FsResult<FsSearchResult>> SearchAsync(string query, bool regex, string? path,
        string? directoryPath, string? filePattern, int maxResults, int contextLines,
        VfsTextSearchOutputMode outputMode, CancellationToken ct)
    {
        var matcher = new Regex(regex ? query : Regex.Escape(query), RegexOptions.IgnoreCase);
        var scope = path ?? directoryPath;

        // schedule.json is the only searchable file per schedule, so a filePattern either includes it
        // (search the scoped schedules) or excludes it entirely (nothing to search).
        var scoped = VfsContentSearch.MatchesFilePattern(filePattern, SchedulePath.ScheduleFileName)
            ? await ScopeSchedulesAsync(scope, ct)
            : [];

        var results = new List<FsSearchFileResult>();
        var totalMatches = 0;
        var filesWithMatches = 0;
        var filesSearched = 0;
        var truncated = false;

        foreach (var schedule in scoped)
        {
            if (totalMatches >= maxResults)
            {
                truncated = true;
                break;
            }
            filesSearched++;
            var lines = RenderSpec(schedule).Split('\n');
            var (matches, more) = VfsContentSearch.FindMatches(lines, matcher, contextLines, maxResults - totalMatches);
            truncated |= more;
            if (matches.Count == 0)
            {
                continue;
            }
            filesWithMatches++;
            totalMatches += matches.Count;
            results.Add(VfsContentSearch.BuildFileResult($"/{schedule.AgentId}/{schedule.Id}/schedule.json", matches, outputMode));
        }

        return new FsResult<FsSearchResult>.Ok(new FsSearchResult
        {
            Query = query,
            Regex = regex,
            Path = scope ?? "/",
            FilesSearched = filesSearched,
            FilesWithMatches = filesWithMatches,
            TotalMatches = totalMatches,
            Truncated = truncated,
            Results = results
        });
    }

    // Restricts the searched set to the requested scope: a single schedule (file/dir path), one
    // agent's schedules (agent dir), or everything (root / null). An unknown path scopes to nothing.
    private async Task<IReadOnlyList<Schedule>> ScopeSchedulesAsync(string? scope, CancellationToken ct)
    {
        var all = await store.ListAsync(ct);
        if (string.IsNullOrWhiteSpace(scope))
        {
            return all;
        }

        var node = SchedulePath.Parse(scope);
        return node.Kind switch
        {
            ScheduleNodeKind.Root => all,
            ScheduleNodeKind.AgentDir when agents.Exists(node.AgentId!) =>
                all.Where(s => s.AgentId == node.AgentId).ToList(),
            ScheduleNodeKind.ScheduleDir or ScheduleNodeKind.ScheduleFile
                or ScheduleNodeKind.StatusFile or ScheduleNodeKind.RunNowFile =>
                all.Where(s => s.AgentId == node.AgentId && s.Id == node.ScheduleId).ToList(),
            _ => []
        };
    }

    private string RenderSpec(Schedule s) => JsonSerializer.Serialize(new
    {
        prompt = s.Prompt,
        cron = s.CronExpression,
        runAt = ToZone(s.RunAt),
        userId = s.UserId,
        deliverTo = s.DeliverTo
    }, _json);

    private string RenderStatus(Schedule s) => JsonSerializer.Serialize(new
    {
        createdAt = ToZone(s.CreatedAt),
        lastRunAt = ToZone(s.LastRunAt),
        nextRunAt = ToZone(s.NextRunAt)
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

    private static FsResult<FsGlobResult> Glob(IReadOnlyList<string> entries) => new FsResult<FsGlobResult>.Ok(new FsGlobResult
    {
        Entries = entries,
        Truncated = false,
        Total = entries.Count
    });

    public async Task<FsResult<FsCreateResult>> CreateAsync(string path, string content, bool overwrite, bool createDirectories, CancellationToken ct)
    {
        var node = SchedulePath.Parse(path);
        if (node.Kind != ScheduleNodeKind.ScheduleFile || node.AgentId is null || node.ScheduleId is null)
        {
            return Invalid<FsCreateResult>($"Create a schedule at /<agentId>/<scheduleId>/schedule.json (got '{path}')");
        }

        if (!agents.Exists(node.AgentId))
        {
            return new FsResult<FsCreateResult>.Err(Error(ToolError.Codes.NotFound, $"Unknown agent '{node.AgentId}'"));
        }

        // Schedules use a unique-id model: create always rejects an existing id regardless of `overwrite`. Use fs_edit to modify an existing schedule.
        if (await store.GetAsync(node.ScheduleId, ct) is not null)
        {
            return new FsResult<FsCreateResult>.Err(Error(ToolError.Codes.AlreadyExists, $"Schedule '{node.ScheduleId}' already exists"));
        }

        var spec = ParseSpec(content, out var specError);
        if (specError is not null)
        {
            return new FsResult<FsCreateResult>.Err(specError);
        }

        var validation = ValidateSpec(spec!);
        if (validation is not null)
        {
            return new FsResult<FsCreateResult>.Err(validation);
        }

        spec = spec! with { RunAt = spec.RunAt is { } r ? ToUtc(r) : null };

        var schedule = new Schedule
        {
            Id = node.ScheduleId,
            AgentId = node.AgentId,
            Prompt = spec!.Prompt!,
            CronExpression = spec.Cron,
            RunAt = spec.RunAt,
            UserId = spec.UserId,
            DeliverTo = spec.DeliverTo,
            CreatedAt = timeProvider.GetUtcNow().UtcDateTime,
            NextRunAt = ComputeNextRunAt(spec)
        };

        await store.CreateAsync(schedule, ct);
        return new FsResult<FsCreateResult>.Ok(new FsCreateResult
        {
            Status = "created", FilePath = path, Size = content.Length.ToString(), Lines = content.Split('\n').Length
        });
    }

    public async Task<FsResult<FsEditResult>> EditAsync(string path, IReadOnlyList<TextEdit> edits, CancellationToken ct)
    {
        var node = SchedulePath.Parse(path);
        if (node.Kind != ScheduleNodeKind.ScheduleFile)
        {
            return await RejectWriteAsync<FsEditResult>(node, path, ct);
        }

        if (await GetScheduleAsync(node, ct) is not { } existing)
        {
            return NotFound<FsEditResult>(path);
        }

        var current = RenderSpec(existing);
        var updatedText = edits.Aggregate(current, (acc, e) =>
            e.ReplaceAll ? acc.Replace(e.OldString, e.NewString)
                         : ReplaceFirst(acc, e.OldString, e.NewString));

        var spec = ParseSpec(updatedText, out var specError);
        if (specError is not null)
        {
            return new FsResult<FsEditResult>.Err(specError);
        }

        var validation = ValidateSpec(spec!);
        if (validation is not null)
        {
            return new FsResult<FsEditResult>.Err(validation);
        }

        spec = spec! with { RunAt = spec.RunAt is { } r ? ToUtc(r) : null };

        // Only recompute the next fire when the timing actually changes; a prompt-only edit must
        // not push out (or skip) an already-scheduled run by recomputing NextRunAt from "now".
        var timingChanged = spec.Cron != existing.CronExpression || spec.RunAt != existing.RunAt;

        var updated = existing with
        {
            Prompt = spec.Prompt!,
            CronExpression = spec.Cron,
            RunAt = spec.RunAt,
            UserId = spec.UserId,
            DeliverTo = spec.DeliverTo,
            NextRunAt = timingChanged ? ComputeNextRunAt(spec) : existing.NextRunAt
        };

        await store.CreateAsync(updated, ct);
        return new FsResult<FsEditResult>.Ok(new FsEditResult
        {
            Status = "edited", FilePath = path, TotalOccurrencesReplaced = edits.Count,
            Edits = edits.Select(_ => new FsEditDetail { OccurrencesReplaced = 1, AffectedLines = new FsLineRange { Start = 1, End = 1 } }).ToList()
        });
    }

    public async Task<FsResult<FsMoveResult>> MoveAsync(string sourcePath, string destinationPath, CancellationToken ct)
    {
        var src = SchedulePath.Parse(sourcePath);
        var dst = SchedulePath.Parse(destinationPath);
        if (src.Kind != ScheduleNodeKind.ScheduleDir || dst.Kind != ScheduleNodeKind.ScheduleDir)
        {
            return IsReadOnlyFile(src.Kind) && await NodeExistsAsync(src, ct)
                ? ReadOnly<FsMoveResult>(sourcePath)
                : Invalid<FsMoveResult>("Move a schedule dir to /<agentId>/<scheduleId>");
        }

        if (await GetScheduleAsync(src, ct) is not { } existing)
        {
            return NotFound<FsMoveResult>(sourcePath);
        }

        if (!agents.Exists(dst.AgentId!))
        {
            return new FsResult<FsMoveResult>.Err(Error(ToolError.Codes.NotFound, $"Unknown agent '{dst.AgentId}'"));
        }

        if (dst.ScheduleId != src.ScheduleId && await store.GetAsync(dst.ScheduleId!, ct) is not null)
        {
            return new FsResult<FsMoveResult>.Err(Error(ToolError.Codes.AlreadyExists, $"Schedule '{dst.ScheduleId}' already exists"));
        }

        if (dst.ScheduleId != src.ScheduleId)
        {
            await store.DeleteAsync(src.ScheduleId!, ct);
        }

        await store.CreateAsync(existing with { Id = dst.ScheduleId!, AgentId = dst.AgentId! }, ct);
        return new FsResult<FsMoveResult>.Ok(new FsMoveResult
        {
            Status = "moved", Message = "reassigned", Source = sourcePath, Destination = destinationPath
        });
    }

    public async Task<FsResult<FsRemoveResult>> DeleteAsync(string path, CancellationToken ct)
    {
        var node = SchedulePath.Parse(path);
        if (node.Kind != ScheduleNodeKind.ScheduleDir)
        {
            return await RejectWriteAsync<FsRemoveResult>(node, path, ct);
        }

        if (await GetScheduleAsync(node, ct) is null)
        {
            return NotFound<FsRemoveResult>(path);
        }

        await store.DeleteAsync(node.ScheduleId!, ct);
        return new FsResult<FsRemoveResult>.Ok(new FsRemoveResult
        {
            Status = "deleted", Message = "removed", OriginalPath = path, TrashPath = ""
        });
    }

    public async Task<FsResult<FsExecResult>> ExecAsync(string path, string command, int? timeoutSeconds, CancellationToken ct)
    {
        var node = SchedulePath.Parse(path);
        if (node.Kind != ScheduleNodeKind.ScheduleDir || await GetScheduleAsync(node, ct) is not { } schedule)
        {
            return NotFound<FsExecResult>(path);
        }

        var trimmed = command.Trim();
        if (trimmed != SchedulePath.RunNowFileName)
        {
            return Exec("", $"command not found: {trimmed}\navailable: {SchedulePath.RunNowFileName}", 127, path);
        }

        // Queue the schedule for the dispatcher's next tick by setting NextRunAt=now. LastRunAt is left
        // untouched (null = don't change it); the dispatcher stamps the real fire-time when it actually runs.
        await store.UpdateLastRunAsync(schedule.Id, null, DateTime.UtcNow, ct);
        return Exec($"queued '{schedule.Id}' to run now\n", "", 0, path);
    }

    // The schedule control surface is not byte-backed: copy and raw chunk streaming are unsupported.
    public Task<FsResult<FsCopyResult>> CopyAsync(string sourcePath, string destinationPath,
        bool overwrite, bool createDirectories, CancellationToken ct) =>
        Task.FromResult(Unsupported<FsCopyResult>("The schedules filesystem does not support copy."));

    public IAsyncEnumerable<ReadOnlyMemory<byte>> ReadChunksAsync(string path, CancellationToken ct) =>
        throw new NotSupportedException("The schedules filesystem does not support raw byte streaming.");

    public Task<long> WriteChunksAsync(string path, IAsyncEnumerable<ReadOnlyMemory<byte>> chunks,
        bool overwrite, bool createDirectories, CancellationToken ct) =>
        throw new NotSupportedException("The schedules filesystem does not support raw byte streaming.");

    private static FsResult<FsExecResult> Exec(string stdout, string stderr, int exitCode, string cwd) =>
        new FsResult<FsExecResult>.Ok(new FsExecResult
        {
            Stdout = stdout, Stderr = stderr, ExitCode = exitCode,
            Truncated = false, TimedOut = false, DurationMs = 0, Cwd = cwd
        });

    private sealed record SpecDto
    {
        public string? Prompt { get; init; }
        public string? Cron { get; init; }
        public DateTime? RunAt { get; init; }
        public string? UserId { get; init; }
        public IReadOnlyList<string>? DeliverTo { get; init; }
    }

    private static SpecDto? ParseSpec(string content, out ToolErrorResult? error)
    {
        error = null;
        try
        {
            var spec = JsonSerializer.Deserialize<SpecDto>(content, _parseOptions);
            if (spec is null)
            {
                error = Error(ToolError.Codes.InvalidArgument, "schedule.json is empty");
            }

            return spec;
        }
        catch (JsonException ex)
        {
            error = Error(ToolError.Codes.InvalidArgument, $"Invalid schedule.json: {ex.Message}");
            return null;
        }
    }

    private ToolErrorResult? ValidateSpec(SpecDto spec)
    {
        if (string.IsNullOrWhiteSpace(spec.Prompt))
        {
            return Error(ToolError.Codes.InvalidArgument, "prompt is required");
        }

        if (spec.Cron is null && spec.RunAt is null)
        {
            return Error(ToolError.Codes.InvalidArgument, "Provide either cron or runAt");
        }

        if (spec.Cron is not null && spec.RunAt is not null)
        {
            return Error(ToolError.Codes.InvalidArgument, "Provide only cron OR runAt, not both");
        }

        if (spec.Cron is not null && !cronValidator.IsValid(spec.Cron))
        {
            return Error(ToolError.Codes.InvalidArgument, $"Invalid cron expression: {spec.Cron}");
        }

        if (spec.RunAt is { } runAt)
        {
            if (runAt.Kind == DateTimeKind.Unspecified && timeProvider.LocalTimeZone.IsInvalidTime(runAt))
            {
                return Error(ToolError.Codes.InvalidArgument,
                    "runAt falls in a daylight-saving gap (that local time does not exist); pick another time or add an explicit offset");
            }

            if (ToUtc(runAt) <= timeProvider.GetUtcNow().UtcDateTime)
            {
                return Error(ToolError.Codes.InvalidArgument, "runAt must be in the future");
            }
        }

        return null;
    }

    // A bare (zoneless) runAt is wall-clock time in the operating zone; an offset/Z runAt is honored.
    private DateTime ToUtc(DateTime runAt) =>
        runAt.Kind == DateTimeKind.Unspecified
            ? TimeZoneInfo.ConvertTimeToUtc(runAt, timeProvider.LocalTimeZone)
            : runAt.ToUniversalTime();

    // Stored times are UTC; render them in the operating zone so the LLM reads local wall-clock.
    private DateTimeOffset? ToZone(DateTime? utc) =>
        utc is { } u
            ? TimeZoneInfo.ConvertTime(new DateTimeOffset(DateTime.SpecifyKind(u, DateTimeKind.Utc)), timeProvider.LocalTimeZone)
            : null;

    private DateTime? ComputeNextRunAt(SpecDto spec) =>
        spec.RunAt ?? (spec.Cron is not null
            ? cronValidator.GetNextOccurrence(spec.Cron, timeProvider.GetUtcNow(), timeProvider.LocalTimeZone)
            : null);

    private static string ReplaceFirst(string text, string oldValue, string newValue)
    {
        var i = text.IndexOf(oldValue, StringComparison.Ordinal);
        return i < 0 ? text : text[..i] + newValue + text[(i + oldValue.Length)..];
    }

    private static bool IsReadOnlyFile(ScheduleNodeKind kind) =>
        kind is ScheduleNodeKind.StatusFile or ScheduleNodeKind.AgentInfoFile or ScheduleNodeKind.RunNowFile;

    // A write aimed at a path that isn't a writable schedule.json is either a known read-only file
    // (status.json/agent_info.json/run_now.sh) that exists — rejected as read-only — or a genuine miss.
    private async Task<FsResult<T>> RejectWriteAsync<T>(ScheduleNode node, string path, CancellationToken ct) where T : class =>
        IsReadOnlyFile(node.Kind) && await NodeExistsAsync(node, ct) ? ReadOnly<T>(path) : NotFound<T>(path);

    private static FsResult<T> ReadOnly<T>(string path) where T : class =>
        new FsResult<T>.Err(Error(ToolError.Codes.UnsupportedOperation, $"{path} is read-only"));

    private static FsResult<T> Invalid<T>(string message) where T : class =>
        new FsResult<T>.Err(Error(ToolError.Codes.InvalidArgument, message));

    private static FsResult<T> NotFound<T>(string path) where T : class =>
        new FsResult<T>.Err(Error(ToolError.Codes.NotFound, $"Path not found: {path}"));

    private static FsResult<T> Unsupported<T>(string message) where T : class =>
        new FsResult<T>.Err(Error(ToolError.Codes.UnsupportedOperation, message));

    private static ToolErrorResult Error(string code, string message) =>
        new() { ErrorCode = code, Message = message, Retryable = false };
}