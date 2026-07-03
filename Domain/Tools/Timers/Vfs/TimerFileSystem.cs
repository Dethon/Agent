using System.Text.Json;
using System.Text.RegularExpressions;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.FileSystem;
using Domain.DTOs.Voice;
using Domain.Tools;
using Domain.Tools.FileSystem;

namespace Domain.Tools.Timers.Vfs;

// Hub-local countdown timers as a VFS: create /<id>/timer.json to arm, read status.json for time
// left, delete the directory to cancel. Timers are immutable (delete and recreate) and fire once.
public sealed class TimerFileSystem(ITimerStore store, TimeProvider timeProvider) : IFileSystemBackend
{
    public string FilesystemName => "timers";

    private static readonly JsonSerializerOptions _json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonSerializerOptions _parseOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<FsResult<FsGlobResult>> GlobAsync(string basePath, string pattern, CancellationToken ct)
    {
        var all = await store.ListAsync(ct);
        var prefix = string.IsNullOrEmpty(basePath?.Trim('/')) ? string.Empty : basePath.Trim('/') + "/";

        var dirsOnly = pattern.EndsWith('/');
        var effectivePattern = dirsOnly ? pattern.TrimEnd('/') : pattern;
        var matches = GlobRegex.CompileMatcher(prefix + effectivePattern);

        var dirs = all.Select(t => t.Id).Where(matches).Select(id => $"/{id}/");
        if (dirsOnly)
        {
            return Glob(dirs.OrderBy(p => p, StringComparer.Ordinal).ToList());
        }

        var files = all.SelectMany(t => new[]
            {
                $"{t.Id}/{TimerPath.TimerFileName}",
                $"{t.Id}/{TimerPath.StatusFileName}"
            })
            .Where(matches)
            .Select(p => $"/{p}");
        return Glob(dirs.Concat(files).OrderBy(p => p, StringComparer.Ordinal).ToList());
    }

    public async Task<FsResult<FsInfoResult>> InfoAsync(string path, CancellationToken ct)
    {
        var node = TimerPath.Parse(path);
        var exists = await NodeExistsAsync(node, ct);
        var isDir = node.Kind is TimerNodeKind.Root or TimerNodeKind.TimerDir;
        return new FsResult<FsInfoResult>.Ok(new FsInfoResult
        {
            Exists = exists,
            Path = path,
            IsDirectory = exists ? isDir : null
        });
    }

    public async Task<FsResult<FsReadResult>> ReadAsync(string path, int? offset, int? limit, CancellationToken ct)
    {
        var node = TimerPath.Parse(path);
        string content;
        switch (node.Kind)
        {
            case TimerNodeKind.TimerFile when await GetTimerAsync(node, ct) is { } t:
                content = RenderSpec(t);
                break;
            case TimerNodeKind.StatusFile when await GetTimerAsync(node, ct) is { } t:
                content = RenderStatus(t);
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

    public async Task<FsResult<FsSearchResult>> SearchAsync(string query, bool regex, string? path,
        string? directoryPath, string? filePattern, int maxResults, int contextLines,
        VfsTextSearchOutputMode outputMode, CancellationToken ct)
    {
        var matcher = new Regex(regex ? query : Regex.Escape(query), RegexOptions.IgnoreCase);
        var scope = path ?? directoryPath;

        var scoped = VfsContentSearch.MatchesFilePattern(filePattern, TimerPath.TimerFileName)
            ? await ScopeTimersAsync(scope, ct)
            : [];

        var results = new List<FsSearchFileResult>();
        var totalMatches = 0;
        var filesWithMatches = 0;
        var filesSearched = 0;
        var truncated = false;

        foreach (var timer in scoped)
        {
            if (totalMatches >= maxResults)
            {
                truncated = true;
                break;
            }
            filesSearched++;
            var lines = RenderSpec(timer).Split('\n');
            var (matches, more) = VfsContentSearch.FindMatches(lines, matcher, contextLines, maxResults - totalMatches);
            truncated |= more;
            if (matches.Count == 0)
            {
                continue;
            }
            filesWithMatches++;
            totalMatches += matches.Count;
            results.Add(VfsContentSearch.BuildFileResult($"/{timer.Id}/{TimerPath.TimerFileName}", matches, outputMode));
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

    public async Task<FsResult<FsCreateResult>> CreateAsync(
        string path, string content, bool overwrite, bool createDirectories, CancellationToken ct)
    {
        var node = TimerPath.Parse(path);
        if (node.Kind != TimerNodeKind.TimerFile || node.TimerId is null)
        {
            return Invalid<FsCreateResult>($"Create a timer at /<timerId>/{TimerPath.TimerFileName} (got '{path}')");
        }

        // Timers are immutable: create always rejects an existing id regardless of `overwrite`.
        if (await store.GetAsync(node.TimerId, ct) is not null)
        {
            return new FsResult<FsCreateResult>.Err(
                Error(ToolError.Codes.AlreadyExists, $"Timer '{node.TimerId}' already exists"));
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

        var now = timeProvider.GetUtcNow().UtcDateTime;
        await store.ArmAsync(new ArmedTimer
        {
            Id = node.TimerId,
            Text = spec!.Text,
            Target = spec.Target!,
            DurationSeconds = spec.DurationSeconds!.Value,
            CreatedAtUtc = now,
            FiresAtUtc = now.AddSeconds(spec.DurationSeconds.Value)
        }, ct);

        return new FsResult<FsCreateResult>.Ok(new FsCreateResult
        {
            Status = "created", FilePath = path, Size = content.Length.ToString(), Lines = content.Split('\n').Length
        });
    }

    public async Task<FsResult<FsEditResult>> EditAsync(string path, IReadOnlyList<TextEdit> edits, CancellationToken ct)
    {
        var node = TimerPath.Parse(path);
        return node.Kind switch
        {
            TimerNodeKind.TimerFile when await GetTimerAsync(node, ct) is not null =>
                Unsupported<FsEditResult>("Timers are immutable — delete the timer and create a new one."),
            TimerNodeKind.StatusFile when await GetTimerAsync(node, ct) is not null =>
                ReadOnly<FsEditResult>(path),
            _ => NotFound<FsEditResult>(path)
        };
    }

    public async Task<FsResult<FsRemoveResult>> DeleteAsync(string path, CancellationToken ct)
    {
        var node = TimerPath.Parse(path);
        if (node.Kind != TimerNodeKind.TimerDir)
        {
            return node.Kind is TimerNodeKind.TimerFile or TimerNodeKind.StatusFile
                   && await GetTimerAsync(node, ct) is not null
                ? Invalid<FsRemoveResult>($"Cancel the timer by deleting its directory: /{node.TimerId}")
                : NotFound<FsRemoveResult>(path);
        }

        return await store.CancelAsync(node.TimerId!, ct)
            ? new FsResult<FsRemoveResult>.Ok(new FsRemoveResult
            {
                Status = "deleted", Message = "cancelled", OriginalPath = path, TrashPath = ""
            })
            : NotFound<FsRemoveResult>(path);
    }

    public Task<FsResult<FsMoveResult>> MoveAsync(string sourcePath, string destinationPath, CancellationToken ct) =>
        Task.FromResult(Unsupported<FsMoveResult>("The timers filesystem does not support move."));

    public Task<FsResult<FsExecResult>> ExecAsync(string path, string command, int? timeoutSeconds, CancellationToken ct) =>
        Task.FromResult(Unsupported<FsExecResult>("The timers filesystem does not support exec."));

    public Task<FsResult<FsCopyResult>> CopyAsync(string sourcePath, string destinationPath,
        bool overwrite, bool createDirectories, CancellationToken ct) =>
        Task.FromResult(Unsupported<FsCopyResult>("The timers filesystem does not support copy."));

    public IAsyncEnumerable<ReadOnlyMemory<byte>> ReadChunksAsync(string path, CancellationToken ct) =>
        throw new NotSupportedException("The timers filesystem does not support raw byte streaming.");

    public Task<long> WriteChunksAsync(string path, IAsyncEnumerable<ReadOnlyMemory<byte>> chunks,
        bool overwrite, bool createDirectories, CancellationToken ct) =>
        throw new NotSupportedException("The timers filesystem does not support raw byte streaming.");

    private sealed record SpecDto
    {
        public int? DurationSeconds { get; init; }
        public string? Text { get; init; }
        public AnnounceTarget? Target { get; init; }
    }

    private static SpecDto? ParseSpec(string content, out ToolErrorResult? error)
    {
        error = null;
        try
        {
            var spec = JsonSerializer.Deserialize<SpecDto>(content, _parseOptions);
            if (spec is null)
            {
                error = Error(ToolError.Codes.InvalidArgument, "timer.json is empty");
            }
            return spec;
        }
        catch (JsonException ex)
        {
            error = Error(ToolError.Codes.InvalidArgument, $"Invalid timer.json: {ex.Message}");
            return null;
        }
    }

    // Kitchen-scale countdowns in a deliberately non-durable store: anything longer belongs on the
    // HA alarms calendar, which survives restarts and escalates.
    public const int MaxDurationSeconds = 4 * 60 * 60;

    private static ToolErrorResult? ValidateSpec(SpecDto spec)
    {
        if (spec.DurationSeconds is not > 0)
        {
            return Error(ToolError.Codes.InvalidArgument, "durationSeconds must be a positive integer");
        }

        if (spec.DurationSeconds > MaxDurationSeconds)
        {
            return Error(ToolError.Codes.InvalidArgument,
                $"durationSeconds must be at most {MaxDurationSeconds} (4 hours) — use the Home Assistant "
                + "alarms calendar for anything longer");
        }

        var target = spec.Target;
        var hasTarget = target is not null
            && (target.SatelliteId is not null
                || target.SatelliteIds is { Count: > 0 }
                || target.Room is not null
                || target.All == true);
        return hasTarget
            ? null
            : Error(ToolError.Codes.InvalidArgument,
                "target is required: {satelliteId | satelliteIds | room | all}");
    }

    private string RenderSpec(ArmedTimer t) => JsonSerializer.Serialize(new
    {
        durationSeconds = t.DurationSeconds,
        text = t.Text,
        target = t.Target
    }, _json);

    private string RenderStatus(ArmedTimer t)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        return JsonSerializer.Serialize(new
        {
            remainingSeconds = Math.Max(0, (int)Math.Ceiling((t.FiresAtUtc - now).TotalSeconds)),
            firesAt = ToZone(t.FiresAtUtc)
        }, _json);
    }

    // Stored times are UTC; render them in the operating zone so the LLM reads local wall-clock.
    private DateTimeOffset ToZone(DateTime utc) =>
        TimeZoneInfo.ConvertTime(new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)), timeProvider.LocalTimeZone);

    private async Task<ArmedTimer?> GetTimerAsync(TimerNode node, CancellationToken ct) =>
        node.TimerId is null ? null : await store.GetAsync(node.TimerId, ct);

    private async Task<bool> NodeExistsAsync(TimerNode node, CancellationToken ct) => node.Kind switch
    {
        TimerNodeKind.Root => true,
        TimerNodeKind.TimerDir or TimerNodeKind.TimerFile or TimerNodeKind.StatusFile =>
            await GetTimerAsync(node, ct) is not null,
        _ => false
    };

    private async Task<IReadOnlyList<ArmedTimer>> ScopeTimersAsync(string? scope, CancellationToken ct)
    {
        var all = await store.ListAsync(ct);
        if (string.IsNullOrWhiteSpace(scope))
        {
            return all;
        }

        var node = TimerPath.Parse(scope);
        return node.Kind switch
        {
            TimerNodeKind.Root => all,
            TimerNodeKind.TimerDir or TimerNodeKind.TimerFile or TimerNodeKind.StatusFile =>
                all.Where(t => t.Id == node.TimerId).ToList(),
            _ => []
        };
    }

    private static FsResult<FsGlobResult> Glob(IReadOnlyList<string> entries) =>
        new FsResult<FsGlobResult>.Ok(new FsGlobResult
        {
            Entries = entries,
            Truncated = false,
            Total = entries.Count
        });

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