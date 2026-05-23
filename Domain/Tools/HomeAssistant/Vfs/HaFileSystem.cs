using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Tools.Files;

namespace Domain.Tools.HomeAssistant.Vfs;

public sealed partial class HaFileSystem(
    HaCatalogProvider catalogProvider,
    Func<IHomeAssistantClient> clientFactory,
    TimeSpan? regexMatchTimeout = null)
{
    private readonly TimeSpan _regexMatchTimeout = regexMatchTimeout ?? TimeSpan.FromSeconds(1);

    // Glob is uncapped for both modes: the result set is bounded by the home's entity count, and
    // capping only one mode (files) while leaving the other (directories) unbounded was inconsistent.
    public async Task<JsonNode> GlobAsync(string basePath, string pattern, GlobMode mode, CancellationToken ct)
    {
        var catalog = await catalogProvider.GetAsync(ct);
        var hits = HaTree.Glob(catalog, basePath, pattern, mode == GlobMode.Directories);
        return new JsonArray(hits.Select(h => (JsonNode?)h).ToArray());
    }

    public async Task<JsonNode> InfoAsync(string path, CancellationToken ct)
    {
        var catalog = await catalogProvider.GetAsync(ct);
        var node = HaVfsPath.Parse(path);
        var (exists, isDir) = Resolve(node, catalog);

        var result = new JsonObject { ["exists"] = exists, ["path"] = path };
        if (exists)
        {
            result["isDirectory"] = isDir;
        }
        return result;
    }

    public async Task<JsonNode> ReadAsync(string path, int? offset, int? limit, CancellationToken ct)
    {
        var node = HaVfsPath.Parse(path);
        return node.Kind switch
        {
            HaVfsKind.StateFile => await ReadStateAsync(path, node.EntityId!, offset, limit, ct),
            HaVfsKind.ActionFile => await ReadActionAsync(path, node, ct),
            _ => NotFound(path)
        };
    }

    public async Task<JsonNode> SearchAsync(
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
            return Domain.Tools.ToolError.Create(
                Domain.Tools.ToolError.Codes.InvalidArgument,
                $"Invalid search pattern '{query}': {ex.Message}",
                retryable: false,
                hint: "Fix the regex, or set regex=false to match a literal string.");
        }

        // state.yaml is the only searchable file per entity, so a filePattern either includes it
        // (search the scoped entities) or excludes it entirely (nothing to search).
        var scoped = MatchesFilePattern(filePattern, HaVfsPath.StateFileName)
            ? ScopeEntities(catalog, path, directoryPath)
            : [];

        var results = new JsonArray();
        var totalMatches = 0;
        var filesWithMatches = 0;

        // The matcher is bounded by _regexMatchTimeout; a pathological caller-supplied pattern trips
        // it during IsMatch (not construction), so catch it here and return a hinted envelope rather
        // than letting RegexMatchTimeoutException leak to the generic MCP boundary wrapper.
        try
        {
            foreach (var entity in scoped)
            {
                if (totalMatches >= maxResults)
                {
                    break;
                }
                var lines = HaStateRenderer.ToYaml(entity).Split('\n');
                var matches = FindMatches(lines, matcher, contextLines, maxResults - totalMatches);
                if (matches.Count == 0)
                {
                    continue;
                }
                filesWithMatches++;
                totalMatches += matches.Count;
                results.Add(BuildFileResult(CanonicalStatePath(entity), matches, outputMode));
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return Domain.Tools.ToolError.Create(
                Domain.Tools.ToolError.Codes.Timeout,
                $"Search pattern '{query}' timed out while matching.",
                retryable: false,
                hint: "Simplify the regex (avoid nested quantifiers), or set regex=false to match a literal string.");
        }

        return new JsonObject
        {
            ["query"] = query,
            ["regex"] = regex,
            ["path"] = path ?? directoryPath ?? string.Empty,
            ["filesSearched"] = scoped.Count,
            ["filesWithMatches"] = filesWithMatches,
            ["totalMatches"] = totalMatches,
            ["truncated"] = totalMatches >= maxResults,
            ["results"] = results
        };
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
                catalog.EntityById(node.EntityId!) is { } entity ? [entity] : [],
            _ => []
        };
    }

    private static List<JsonNode> FindMatches(string[] lines, Regex matcher, int contextLines, int limit) =>
        lines
            .Select((text, index) => (text, index))
            .Where(l => matcher.IsMatch(l.text))
            .Take(limit)
            .Select(l => BuildMatch(lines, l.index, contextLines))
            .ToList();

    private static JsonNode BuildMatch(string[] lines, int index, int contextLines)
    {
        var match = new JsonObject { ["line"] = index + 1, ["text"] = lines[index] };
        if (contextLines <= 0)
        {
            return match;
        }
        var before = lines.Take(index).TakeLast(contextLines).ToList();
        var after = lines.Skip(index + 1).Take(contextLines).ToList();
        if (before.Count > 0 || after.Count > 0)
        {
            match["context"] = new JsonObject
            {
                ["before"] = new JsonArray(before.Select(l => (JsonNode?)l).ToArray()),
                ["after"] = new JsonArray(after.Select(l => (JsonNode?)l).ToArray())
            };
        }
        return match;
    }

    private static JsonNode BuildFileResult(string file, IReadOnlyList<JsonNode> matches, VfsTextSearchOutputMode outputMode) =>
        outputMode == VfsTextSearchOutputMode.FilesOnly
            ? new JsonObject { ["file"] = file, ["matchCount"] = matches.Count }
            : new JsonObject { ["file"] = file, ["matches"] = new JsonArray(matches.ToArray()) };

    private static string CanonicalStatePath(HaEntityState entity) =>
        $"entities/{HaCatalog.ClassOf(entity.EntityId)}/{HaSlug.Compose(HaCatalog.ObjectOf(entity.EntityId), HaCatalog.FriendlyName(entity))}/{HaVfsPath.StateFileName}";

    // Matches a bare file name against a simple glob (* and ?). Used to gate whether the searchable
    // state.yaml is included by a caller-supplied filePattern.
    private static bool MatchesFilePattern(string? filePattern, string fileName)
    {
        if (string.IsNullOrEmpty(filePattern))
        {
            return true;
        }
        var pattern = "^" + Regex.Escape(filePattern).Replace("\\*", "[^/]*").Replace("\\?", ".") + "$";
        return Regex.IsMatch(fileName, pattern, RegexOptions.IgnoreCase);
    }

    private async Task<JsonNode> ReadStateAsync(string path, string entityId, int? offset, int? limit, CancellationToken ct)
    {
        var entity = await clientFactory().GetStateAsync(entityId, ct);
        return entity is null ? NotFound(path) : BuildReadResult(path, HaStateRenderer.ToYaml(entity), offset, limit);
    }

    private async Task<JsonNode> ReadActionAsync(string path, HaVfsNode node, CancellationToken ct)
    {
        var catalog = await catalogProvider.GetAsync(ct);
        if (catalog.EntityById(node.EntityId!) is null)
        {
            return NotFound(path);
        }
        var svc = HaActionResolver.ServicesFor(node.EntityId!, catalog.Services)
            .FirstOrDefault(s => s.Service.Equals(node.Service, StringComparison.Ordinal));
        return svc is null
            ? NotFound(path)
            : BuildReadResult(path, HaServiceHelpRenderer.Render(node.EntityId!, svc), null, null);
    }

    private static (bool Exists, bool IsDir) Resolve(HaVfsNode node, HaCatalog catalog) => node.Kind switch
    {
        HaVfsKind.Root or HaVfsKind.EntitiesRoot or HaVfsKind.AreasRoot => (true, true),
        HaVfsKind.ClassDir => (catalog.ClassDomains().Contains(node.ClassDomain), true),
        HaVfsKind.AreaDir => (catalog.AreaSlugs().Contains(node.Area), true),
        HaVfsKind.EntityDir => (catalog.EntityById(node.EntityId!) is not null, true),
        HaVfsKind.StateFile => (catalog.EntityById(node.EntityId!) is not null, false),
        HaVfsKind.ActionFile => (catalog.EntityById(node.EntityId!) is not null
            && HaActionResolver.ServicesFor(node.EntityId!, catalog.Services).Any(s => s.Service == node.Service), false),
        _ => (false, false)
    };

    // Line-numbered read result matching the Sandbox/Vault text_read shape.
    private static JsonNode BuildReadResult(string filePath, string text, int? offset, int? limit)
    {
        var allLines = text.Split('\n');
        var start = Math.Clamp((offset ?? 1) - 1, 0, allLines.Length);
        var remaining = allLines.Skip(start).ToArray();
        var take = Math.Min(limit ?? remaining.Length, remaining.Length);
        var content = string.Join("\n", remaining.Take(take).Select((l, i) => $"{start + i + 1}: {l}"));

        var result = new JsonObject
        {
            ["filePath"] = filePath,
            ["content"] = content,
            ["totalLines"] = allLines.Length,
            ["truncated"] = take < remaining.Length
        };
        if (take < remaining.Length)
        {
            result["suggestion"] = $"Use offset={start + take + 1} to continue reading.";
        }
        return result;
    }

    private static JsonObject NotFound(string path) =>
        Domain.Tools.ToolError.Create(Domain.Tools.ToolError.Codes.NotFound, $"No such path: {path}");
}