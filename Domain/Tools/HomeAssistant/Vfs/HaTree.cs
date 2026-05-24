using System.Text.RegularExpressions;

namespace Domain.Tools.HomeAssistant.Vfs;

public static class HaTree
{
    private static readonly TimeSpan _globMatchTimeout = TimeSpan.FromSeconds(1);

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

    public static IReadOnlyList<string> Glob(HaCatalog catalog, string basePath, string pattern)
    {
        var dirsOnly = pattern.EndsWith('/');
        var effectivePattern = dirsOnly ? pattern.TrimEnd('/') : pattern;
        var prefix = string.IsNullOrEmpty(basePath) ? string.Empty : basePath.Trim('/') + "/";
        var regex = GlobToRegex(prefix + effectivePattern);

        var dirs = Directories(catalog).Where(p => regex.IsMatch(p)).Select(p => p + "/");
        if (dirsOnly)
        {
            return dirs.OrderBy(p => p, StringComparer.Ordinal).ToList();
        }

        var files = Files(catalog).Where(p => regex.IsMatch(p));
        return dirs.Concat(files).OrderBy(p => p, StringComparer.Ordinal).ToList();
    }

    // GlobToRegex emits only literals, '[^/]*'/'[^/]', and the recursive-wildcard forms below. The
    // '`**/` -> (?:[^/]+/)*' construct matches zero or more whole path segments, so `**/X` also matches
    // X at the base level — matching the Local file matcher rather than requiring a leading '/'. The
    // segment separator inside the group can't overlap '[^/]+', so the pattern can't catastrophically
    // backtrack; the match timeout below is belt-and-suspenders only.
    private static Regex GlobToRegex(string glob)
    {
        var sb = new System.Text.StringBuilder("^");
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            if (c == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
            {
                // `**/` matches zero or more whole segments (incl. none); bare `**` matches anything.
                if (i + 2 < glob.Length && glob[i + 2] == '/')
                {
                    sb.Append("(?:[^/]+/)*");
                    i += 2;
                }
                else
                {
                    sb.Append(".*");
                    i++;
                }
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
        // Glob regexes are compiled fresh per call and matched once over a small pool, so the
        // interpreter is cheaper than RegexOptions.Compiled's JIT cost (matches the search path).
        return new Regex(sb.ToString(), RegexOptions.None, _globMatchTimeout);
    }
}