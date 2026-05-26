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

        return sb.ToString();
    }

    private static IEnumerable<string> BuildAreaPaths(HaCatalog catalog) =>
        catalog.AreaSlugs().SelectMany(area =>
            catalog.EntityIdsInArea(area).Select(entityId =>
                $"/ha/areas/{area}/{HaSlug.Compose(entityId, HaCatalog.FriendlyName(catalog.EntityById(entityId)))}"));

    private static IEnumerable<string> BuildEntityPaths(HaCatalog catalog) =>
        catalog.Entities.Select(e =>
            $"/ha/entities/{HaCatalog.ClassOf(e.EntityId)}/{HaSlug.Compose(HaCatalog.ObjectOf(e.EntityId), HaCatalog.FriendlyName(e))}");
}