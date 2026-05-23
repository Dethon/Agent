using System.Text.RegularExpressions;

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
        foreach (var svc in HaActionResolver.ServicesFor(entityId, catalog.Services))
        {
            yield return $"{entityDir}/{svc.Service}.sh";
        }
    }

    public static IReadOnlyList<string> Glob(HaCatalog catalog, string basePath, string pattern, bool directories)
    {
        var pool = directories ? Directories(catalog) : Files(catalog);
        var prefix = string.IsNullOrEmpty(basePath) ? string.Empty : basePath.Trim('/') + "/";
        var regex = GlobToRegex(prefix + pattern);
        return pool.Where(p => regex.IsMatch(p)).ToList();
    }

    private static Regex GlobToRegex(string glob)
    {
        var sb = new System.Text.StringBuilder("^");
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            if (c == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
            {
                sb.Append(".*");
                i++;
            }
            else
            {
                sb.Append(c switch
                {
                    '*' => "[^/]*",
                    '?' => "[^/]",
                    _ => Regex.Escape(c.ToString())
                });
            }
        }
        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.Compiled);
    }
}