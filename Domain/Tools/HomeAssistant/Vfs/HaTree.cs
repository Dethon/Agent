using Domain.Tools.FileSystem;

namespace Domain.Tools.HomeAssistant.Vfs;

public static class HaTree
{
    public static IReadOnlyList<string> Directories(HaCatalog catalog)
    {
        var dirs = new List<string> { "entities", "areas" };

        dirs.AddRange(catalog.ClassDomains().Select(c => $"entities/{c}"));
        dirs.AddRange(catalog.Entities.Select(e =>
            $"entities/{HaCatalog.ClassOf(e.EntityId)}/{HaSlug.Compose(HaCatalog.ObjectOf(e.EntityId), HaCatalog.FriendlyName(e))}"));

        foreach (var area in catalog.AreaSlugs())
        {
            dirs.Add($"areas/{area}");
            dirs.AddRange(catalog.EntityIdsInArea(area).Select(id =>
                $"areas/{area}/{HaSlug.Compose(id, HaCatalog.FriendlyName(catalog.EntityById(id)))}"));
        }

        return dirs.OrderBy(d => d, StringComparer.Ordinal).ToList();
    }

    public static IReadOnlyList<string> Files(HaCatalog catalog)
    {
        var files = new List<string>();

        foreach (var e in catalog.Entities)
        {
            var entDir = $"entities/{HaCatalog.ClassOf(e.EntityId)}/{HaSlug.Compose(HaCatalog.ObjectOf(e.EntityId), HaCatalog.FriendlyName(e))}";
            files.AddRange(LeafFiles(entDir, e.EntityId, catalog));
        }

        foreach (var area in catalog.AreaSlugs())
        {
            foreach (var id in catalog.EntityIdsInArea(area))
            {
                var entDir = $"areas/{area}/{HaSlug.Compose(id, HaCatalog.FriendlyName(catalog.EntityById(id)))}";
                files.AddRange(LeafFiles(entDir, id, catalog));
            }
        }

        return files.OrderBy(f => f, StringComparer.Ordinal).ToList();
    }

    private static IEnumerable<string> LeafFiles(string entityDir, string entityId, HaCatalog catalog)
    {
        yield return $"{entityDir}/{HaVfsPath.StateFileName}";
        var classDomain = HaCatalog.ClassOf(entityId);
        foreach (var svc in HaActionResolver.ServicesFor(entityId, catalog.Services))
        {
            yield return $"{entityDir}/{HaActionResolver.CommandName(svc, classDomain)}.sh";
        }
    }

    public static IReadOnlyList<string> Glob(HaCatalog catalog, string basePath, string pattern)
    {
        var dirsOnly = pattern.EndsWith('/');
        var effectivePattern = dirsOnly ? pattern.TrimEnd('/') : pattern;
        var prefix = string.IsNullOrEmpty(basePath) ? string.Empty : basePath.Trim('/') + "/";
        var matches = GlobRegex.CompileMatcher(prefix + effectivePattern);

        var dirs = Directories(catalog).Where(matches).Select(p => p + "/");
        if (dirsOnly)
        {
            return dirs.OrderBy(p => p, StringComparer.Ordinal).ToList();
        }

        var files = Files(catalog).Where(matches);
        return dirs.Concat(files).OrderBy(p => p, StringComparer.Ordinal).ToList();
    }
}