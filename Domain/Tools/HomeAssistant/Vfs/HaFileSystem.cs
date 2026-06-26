using System.Text.RegularExpressions;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.FileSystem;
using Domain.Tools.FileSystem;

namespace Domain.Tools.HomeAssistant.Vfs;

public sealed partial class HaFileSystem(
    HaCatalogProvider catalogProvider,
    Func<IHomeAssistantClient> clientFactory,
    TimeSpan? regexMatchTimeout = null) : IFileSystemBackend
{
    public string FilesystemName => "ha";

    private readonly TimeSpan _regexMatchTimeout = regexMatchTimeout ?? TimeSpan.FromSeconds(1);

    // Glob is uncapped: the result set is bounded by the home's entity count.
    public async Task<FsResult<FsGlobResult>> GlobAsync(string basePath, string pattern, CancellationToken ct)
    {
        var catalog = await catalogProvider.GetAsync(ct);
        var hits = HaTree.Glob(catalog, basePath, pattern);
        var entries = hits.ToList();
        return new FsResult<FsGlobResult>.Ok(new FsGlobResult
        {
            Entries = entries,
            Truncated = false,
            Total = entries.Count
        });
    }

    public async Task<FsResult<FsInfoResult>> InfoAsync(string path, CancellationToken ct)
    {
        var catalog = await catalogProvider.GetAsync(ct);
        var node = HaVfsPath.Parse(path);
        var (exists, isDir) = Resolve(node, catalog);

        return new FsResult<FsInfoResult>.Ok(new FsInfoResult { Exists = exists, Path = path, IsDirectory = exists ? isDir : null });
    }

    public async Task<FsResult<FsReadResult>> ReadAsync(string path, int? offset, int? limit, CancellationToken ct)
    {
        var node = HaVfsPath.Parse(path);
        if (node.Kind is not (HaVfsKind.StateFile or HaVfsKind.ActionFile))
        {
            return NotFound(path);
        }

        var catalog = await catalogProvider.GetAsync(ct);
        var resolution = ResolveEntity(catalog, node);
        if (resolution.Entity is null)
        {
            return NotFound(path, resolution.Hint);
        }

        var entityId = resolution.Entity.EntityId;
        return node.Kind == HaVfsKind.StateFile
            ? await ReadStateAsync(path, entityId, offset, limit, ct)
            : ReadAction(path, entityId, node.Service!, catalog);
    }

    public async Task<FsResult<FsSearchResult>> SearchAsync(
        string query, bool regex, string? path, string? directoryPath, string? filePattern,
        int maxResults, int contextLines, VfsTextSearchOutputMode outputMode, CancellationToken ct)
    {
        // Search must reflect live state — values change within a single agent loop. Fetch fresh
        // states (one bulk GET /api/states) and overlay them on the cached structure (areas/services
        // rarely change). glob/info keep the cached catalog; read is already a live per-entity GET.
        var catalog = (await catalogProvider.GetAsync(ct)) with
        {
            Entities = await clientFactory().ListStatesAsync(ct)
        };

        // A caller-supplied regex can be malformed or pathological; compile with a bounded match
        // timeout (ReDoS insurance over small state strings) and surface a parse failure as a
        // hinted envelope instead of a bare exception.
        Regex matcher;
        try
        {
            matcher = new Regex(regex ? query : Regex.Escape(query), RegexOptions.IgnoreCase, _regexMatchTimeout);
        }
        catch (ArgumentException ex)
        {
            return new FsResult<FsSearchResult>.Err(new ToolErrorResult
            {
                ErrorCode = ToolError.Codes.InvalidArgument,
                Message = $"Invalid search pattern '{query}': {ex.Message}",
                Retryable = false,
                Hint = "Fix the regex, or set regex=false to match a literal string."
            });
        }

        // state.json is the only searchable file per entity, so a filePattern either includes it
        // (search the scoped entities) or excludes it entirely (nothing to search).
        var scoped = VfsContentSearch.MatchesFilePattern(filePattern, HaVfsPath.StateFileName)
            ? ScopeEntities(catalog, path, directoryPath)
            : [];

        var results = new List<FsSearchFileResult>();
        var totalMatches = 0;
        var filesWithMatches = 0;
        var filesSearched = 0;
        var truncated = false;

        // The matcher is bounded by _regexMatchTimeout; a pathological caller-supplied pattern trips
        // it during IsMatch (not construction), so catch it here and return a hinted envelope rather
        // than letting RegexMatchTimeoutException leak to the generic MCP boundary wrapper.
        try
        {
            foreach (var entity in scoped)
            {
                if (totalMatches >= maxResults)
                {
                    // Cap reached with entities still unscanned — the result set is genuinely incomplete.
                    truncated = true;
                    break;
                }
                filesSearched++;
                var lines = HaStateRenderer.ToJson(entity).Split('\n');
                var (matches, more) = VfsContentSearch.FindMatches(lines, matcher, contextLines, maxResults - totalMatches);
                truncated |= more;
                if (matches.Count == 0)
                {
                    continue;
                }
                filesWithMatches++;
                totalMatches += matches.Count;
                results.Add(VfsContentSearch.BuildFileResult(CanonicalStatePath(entity), matches, outputMode));
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return new FsResult<FsSearchResult>.Err(new ToolErrorResult
            {
                ErrorCode = ToolError.Codes.Timeout,
                Message = $"Search pattern '{query}' timed out while matching.",
                Retryable = false,
                Hint = "Simplify the regex (avoid nested quantifiers), or set regex=false to match a literal string."
            });
        }

        return new FsResult<FsSearchResult>.Ok(new FsSearchResult
        {
            Query = query,
            Regex = regex,
            Path = path ?? directoryPath ?? string.Empty,
            FilesSearched = filesSearched,
            FilesWithMatches = filesWithMatches,
            TotalMatches = totalMatches,
            Truncated = truncated,
            Results = results
        });
    }

    // Restricts the searched entity set to the requested scope: `path` (a single state file) or
    // `directoryPath` (a class/area/entity subtree). Null/root scope searches everything; an action
    // file or unknown path scopes to nothing (action files are read via read/--help, not searched).
    private static IReadOnlyList<HaEntityState> ScopeEntities(HaCatalog catalog, string? path, string? directoryPath)
    {
        var scope = path ?? directoryPath;
        if (string.IsNullOrWhiteSpace(scope))
        {
            return catalog.Entities;
        }
        var node = HaVfsPath.Parse(scope);
        return node.Kind switch
        {
            HaVfsKind.Root or HaVfsKind.EntitiesRoot or HaVfsKind.AreasRoot => catalog.Entities,
            HaVfsKind.ClassDir => catalog.Entities
                .Where(e => HaCatalog.ClassOf(e.EntityId).Equals(node.ClassDomain, StringComparison.Ordinal))
                .ToList(),
            HaVfsKind.AreaDir => catalog.EntityIdsInArea(node.Area!)
                .Select(catalog.EntityById)
                .OfType<HaEntityState>()
                .ToList(),
            HaVfsKind.EntityDir or HaVfsKind.StateFile =>
                ResolveEntity(catalog, node).Entity is { } entity ? [entity] : [],
            _ => []
        };
    }

    // Composes the entities-root form only — search hits are always reported under entities/, never
    // the area form, so callers get one canonical path per entity regardless of area membership.
    private static string CanonicalStatePath(HaEntityState entity) =>
        $"entities/{HaCatalog.ClassOf(entity.EntityId)}/{HaSlug.Compose(HaCatalog.ObjectOf(entity.EntityId), HaCatalog.FriendlyName(entity))}/{HaVfsPath.StateFileName}";

    private async Task<FsResult<FsReadResult>> ReadStateAsync(string path, string entityId, int? offset, int? limit, CancellationToken ct)
    {
        var entity = await clientFactory().GetStateAsync(entityId, ct);
        return entity is null ? NotFound(path) : BuildReadResult(path, HaStateRenderer.ToJson(entity), offset, limit);
    }

    private static FsResult<FsReadResult> ReadAction(string path, string entityId, string service, HaCatalog catalog)
    {
        var classDomain = HaCatalog.ClassOf(entityId);
        var svc = HaActionResolver.ServicesFor(entityId, catalog.Services)
            .FirstOrDefault(s => HaActionResolver.CommandName(s, classDomain).Equals(service, StringComparison.Ordinal));
        return svc is null
            ? NotFound(path)
            : BuildReadResult(path, HaServiceHelpRenderer.Render(entityId, svc), null, null);
    }

    private readonly record struct EntityResolution(HaEntityState? Entity, string? Hint);

    // Strict canonical resolution: a segment resolves only if it equals the entity's composed
    // directory name (HaSlug.Compose). A recognizable object-id with a non-canonical segment yields
    // a hint naming the correct directory; an unknown object-id yields no hint. Keeps read/exec/info/
    // search in lockstep with glob, which composes the same names.
    private static EntityResolution ResolveEntity(HaCatalog catalog, HaVfsNode node)
    {
        var segment = node.EntitySegment!;
        var candidateId = node.Area is not null
            ? HaSlug.StripNice(segment)
            : $"{node.ClassDomain}.{HaSlug.StripNice(segment)}";

        var entity = catalog.EntityById(candidateId);
        if (entity is null)
        {
            return new EntityResolution(null, null);
        }

        var canonical = node.Area is not null
            ? HaSlug.Compose(entity.EntityId, HaCatalog.FriendlyName(entity))
            : HaSlug.Compose(HaCatalog.ObjectOf(entity.EntityId), HaCatalog.FriendlyName(entity));

        return segment == canonical
            ? new EntityResolution(entity, null)
            : new EntityResolution(null, canonical);
    }

    private static (bool Exists, bool IsDir) Resolve(HaVfsNode node, HaCatalog catalog) => node.Kind switch
    {
        HaVfsKind.Root or HaVfsKind.EntitiesRoot or HaVfsKind.AreasRoot => (true, true),
        HaVfsKind.ClassDir => (catalog.ClassDomains().Contains(node.ClassDomain), true),
        HaVfsKind.AreaDir => (catalog.AreaSlugs().Contains(node.Area), true),
        HaVfsKind.EntityDir => (ResolveEntity(catalog, node).Entity is not null, true),
        HaVfsKind.StateFile => (ResolveEntity(catalog, node).Entity is not null, false),
        HaVfsKind.ActionFile => ResolveEntity(catalog, node).Entity is { } e
            && HaActionResolver.ServicesFor(e.EntityId, catalog.Services)
                .Any(s => HaActionResolver.CommandName(s, HaCatalog.ClassOf(e.EntityId)) == node.Service)
            ? (true, false)
            : (false, false),
        _ => (false, false)
    };

    // Line-numbered read result matching the Sandbox/Vault text_read shape.
    private static FsResult<FsReadResult> BuildReadResult(string filePath, string text, int? offset, int? limit)
    {
        var allLines = text.Split('\n');
        var start = Math.Clamp((offset ?? 1) - 1, 0, allLines.Length);
        var remaining = allLines.Skip(start).ToArray();
        var take = Math.Min(limit ?? remaining.Length, remaining.Length);
        var content = string.Join("\n", remaining.Take(take).Select((l, i) => $"{start + i + 1}: {l}"));
        var truncated = take < remaining.Length;

        return new FsResult<FsReadResult>.Ok(new FsReadResult
        {
            FilePath = filePath,
            Content = content,
            TotalLines = allLines.Length,
            Truncated = truncated,
            Suggestion = truncated ? $"Use offset={start + take + 1} to continue reading." : null
        });
    }

    // Home Assistant is a read + exec control surface: mutating a file, copying, and raw byte
    // streaming have no meaning here, so these IFileSystemBackend members return unsupported.
    public Task<FsResult<FsCreateResult>> CreateAsync(string path, string content, bool overwrite,
        bool createDirectories, CancellationToken ct) =>
        Task.FromResult(Unsupported<FsCreateResult>(nameof(CreateAsync)));

    public Task<FsResult<FsEditResult>> EditAsync(string path, IReadOnlyList<TextEdit> edits, CancellationToken ct) =>
        Task.FromResult(Unsupported<FsEditResult>(nameof(EditAsync)));

    public Task<FsResult<FsMoveResult>> MoveAsync(string sourcePath, string destinationPath, CancellationToken ct) =>
        Task.FromResult(Unsupported<FsMoveResult>(nameof(MoveAsync)));

    public Task<FsResult<FsRemoveResult>> DeleteAsync(string path, CancellationToken ct) =>
        Task.FromResult(Unsupported<FsRemoveResult>(nameof(DeleteAsync)));

    public Task<FsResult<FsCopyResult>> CopyAsync(string sourcePath, string destinationPath, bool overwrite,
        bool createDirectories, CancellationToken ct) =>
        Task.FromResult(Unsupported<FsCopyResult>(nameof(CopyAsync)));

    public IAsyncEnumerable<ReadOnlyMemory<byte>> ReadChunksAsync(string path, CancellationToken ct) =>
        throw new NotSupportedException("The Home Assistant filesystem does not support raw byte streaming.");

    public Task<long> WriteChunksAsync(string path, IAsyncEnumerable<ReadOnlyMemory<byte>> chunks,
        bool overwrite, bool createDirectories, CancellationToken ct) =>
        throw new NotSupportedException("The Home Assistant filesystem does not support raw byte streaming.");

    private static FsResult<T> Unsupported<T>(string operation) where T : class =>
        new FsResult<T>.Err(new ToolErrorResult
        {
            ErrorCode = ToolError.Codes.UnsupportedOperation,
            Message = $"The Home Assistant filesystem does not support '{operation}'.",
            Retryable = false
        });

    private static FsResult<FsReadResult> NotFound(string path, string? canonicalName = null) =>
        new FsResult<FsReadResult>.Err(new ToolErrorResult
        {
            ErrorCode = ToolError.Codes.NotFound,
            Message = $"No such path: {path}",
            Retryable = false,
            Hint = canonicalName is null
                ? null
                : $"Use the exact directory name a listing returns: '{canonicalName}'."
        });
}