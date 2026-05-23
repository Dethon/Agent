using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Domain.Contracts;
using Domain.Tools.Files;

namespace Domain.Tools.HomeAssistant.Vfs;

public sealed partial class HaFileSystem(HaCatalogProvider catalogProvider, Func<IHomeAssistantClient> clientFactory)
{
    private const int FileResultCap = 200;

    public async Task<JsonNode> GlobAsync(string basePath, string pattern, GlobMode mode, CancellationToken ct)
    {
        var catalog = await catalogProvider.GetAsync(ct);
        var hits = HaTree.Glob(catalog, basePath, pattern, mode == GlobMode.Directories);

        if (mode == GlobMode.Files && hits.Count > FileResultCap)
        {
            return new JsonObject
            {
                ["files"] = new JsonArray(hits.Take(FileResultCap).Select(h => (JsonNode?)h).ToArray()),
                ["truncated"] = true,
                ["total"] = hits.Count,
                ["message"] = $"Showing {FileResultCap} of {hits.Count} matches. Use a more specific pattern."
            };
        }
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
        int maxResults, int contextLines, CancellationToken ct)
    {
        var catalog = await catalogProvider.GetAsync(ct);
        var matcher = regex
            ? new Regex(query, RegexOptions.IgnoreCase)
            : new Regex(Regex.Escape(query), RegexOptions.IgnoreCase);

        var results = new JsonArray();
        var totalMatches = 0;
        var filesWithMatches = 0;

        foreach (var entity in catalog.Entities)
        {
            var file = $"entities/{HaCatalog.ClassOf(entity.EntityId)}/{HaCatalog.ObjectOf(entity.EntityId)}/{HaVfsPath.StateFileName}";
            var lines = HaStateRenderer.ToYaml(entity).Split('\n');
            var matches = lines
                .Select((text, i) => (text, line: i + 1))
                .Where(l => matcher.IsMatch(l.text))
                .Select(l => new JsonObject { ["line"] = l.line, ["text"] = l.text } as JsonNode)
                .ToList();

            if (matches.Count == 0)
            {
                continue;
            }
            filesWithMatches++;
            totalMatches += matches.Count;
            if (results.Count < maxResults)
            {
                results.Add(new JsonObject { ["file"] = file, ["matches"] = new JsonArray(matches.ToArray()) });
            }
        }

        return new JsonObject
        {
            ["query"] = query,
            ["regex"] = regex,
            ["filesSearched"] = catalog.Entities.Count,
            ["filesWithMatches"] = filesWithMatches,
            ["totalMatches"] = totalMatches,
            ["truncated"] = filesWithMatches > maxResults,
            ["results"] = results
        };
    }

    private async Task<JsonNode> ReadStateAsync(string path, string entityId, int? offset, int? limit, CancellationToken ct)
    {
        var entity = await clientFactory().GetStateAsync(entityId, ct);
        return entity is null ? NotFound(path) : BuildReadResult(path, HaStateRenderer.ToYaml(entity), offset, limit);
    }

    private async Task<JsonNode> ReadActionAsync(string path, HaVfsNode node, CancellationToken ct)
    {
        var catalog = await catalogProvider.GetAsync(ct);
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
        HaVfsKind.ActionFile => (HaActionResolver.ServicesFor(node.EntityId!, catalog.Services)
            .Any(s => s.Service == node.Service), false),
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