using System.Text;
using Domain.Tools.HomeAssistant.Vfs;

namespace Domain.Prompts;

// Builds the directory dump appended to HomeAssistantPrompt at MCP-prompt-fetch time. The agent
// reads the paths verbatim — both `/ha/areas/<room>/<full-entity-id>_(<slug>)` and
// `/ha/entities/<class>/<object-id>_(<slug>)` are listed so any query axis is one copy away.
// Backed by the shared HaCatalogProvider cache. Returns "" when the catalog is empty so the
// caller falls back to the static prompt alone.
public class HomeAssistantSetupSummary(HaCatalogProvider catalogProvider)
{
    public async Task<string> GetAsync(CancellationToken ct = default)
    {
        var catalog = await catalogProvider.GetAsync(ct);
        if (catalog.Entities.Count == 0)
        {
            return string.Empty;
        }

        var paths = BuildAreaPaths(catalog)
            .Concat(BuildEntityPaths(catalog))
            .OrderBy(p => p, StringComparer.Ordinal);

        var sb = new StringBuilder();
        sb.Append("## Current Home Assistant setup\n\n");
        sb.Append("Mounted at `/ha` — every device directory below. Use the paths verbatim.\n\n");
        foreach (var p in paths)
        {
            sb.Append(p).Append('\n');
        }

        var actions = BuildActionTable(catalog);
        if (actions.Count > 0)
        {
            sb.Append("\n## Actions by entity class\n\n");
            sb.Append(
                "Action files live in the ENTITY directory (`/ha/entities/<class>/<id>/<action>.sh`), "
                + "never in the class directory — `glob` on `/ha/entities/<class>/*.sh` always returns "
                + "nothing. Use this table instead of globbing to discover actions. Classes absent "
                + "here are read-only. If one entity lacks a listed action, `exec` returns exitCode "
                + "127 and `stderr` names the ones it does have.\n\n");
            foreach (var line in actions)
            {
                sb.Append(line).Append('\n');
            }
        }

        return sb.ToString();
    }

    // Grouped by class, not per entity: every entity of a class exposes the same actions, so the
    // per-entity form costs ~4.4k tokens to say what ~350 says. That size difference is the whole
    // point — a round trip costs ~1.15s, prompt prefill ~0.05ms/token, so buying a turn back with
    // tokens only works while the tokens stay cheap.
    private static IReadOnlyList<string> BuildActionTable(HaCatalog catalog) =>
        catalog.ClassDomains()
            .Select(classDomain => (classDomain, actions: ActionsFor(classDomain, catalog)))
            .Where(x => x.actions.Count > 0)
            .Select(x => $"{x.classDomain}: {string.Join(", ", x.actions)}")
            .ToList();

    private static IReadOnlyList<string> ActionsFor(string classDomain, HaCatalog catalog) =>
        catalog.ObjectIdsFor(classDomain)
            .SelectMany(objectId => HaActionResolver
                .ServicesFor($"{classDomain}.{objectId}", catalog.Services)
                .Select(svc => $"{HaActionResolver.CommandName(svc, classDomain)}.sh"))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(a => a, StringComparer.Ordinal)
            .ToList();

    private static IEnumerable<string> BuildAreaPaths(HaCatalog catalog) =>
        catalog.AreaSlugs().SelectMany(area =>
            catalog.EntityIdsInArea(area).Select(entityId =>
                $"/ha/areas/{area}/{HaSlug.Compose(entityId, HaCatalog.FriendlyName(catalog.EntityById(entityId)))}"));

    private static IEnumerable<string> BuildEntityPaths(HaCatalog catalog) =>
        catalog.Entities.Select(e =>
            $"/ha/entities/{HaCatalog.ClassOf(e.EntityId)}/{HaSlug.Compose(HaCatalog.ObjectOf(e.EntityId), HaCatalog.FriendlyName(e))}");
}