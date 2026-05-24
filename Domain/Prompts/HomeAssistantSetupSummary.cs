using System.Text;
using Domain.Tools.HomeAssistant.Vfs;

namespace Domain.Prompts;

// Builds the compact "Current setup" index appended to HomeAssistantPrompt at MCP-prompt-fetch
// time. It orients the agent (mount root, rooms, device classes, totals) without dumping every
// entity — details are pulled on demand through the /ha virtual filesystem. Backed by the shared
// HaCatalogProvider cache. Returns "" when the catalog is empty so the caller falls back to the
// static prompt alone.
public class HomeAssistantSetupSummary(HaCatalogProvider catalogProvider)
{
    public async Task<string> GetAsync(CancellationToken ct = default)
    {
        var catalog = await catalogProvider.GetAsync(ct);
        if (catalog.Entities.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        sb.Append("## Current Home Assistant setup\n\n");
        sb.Append("Mounted at `/ha` — browse `/ha/entities/<class>/<id>/` or `/ha/areas/<room>/`.\n");
        sb.Append("Total: ").Append(catalog.Entities.Count).Append(" entities").Append('\n').Append('\n');

        sb.Append("### Rooms\n");
        foreach (var area in catalog.Areas.OrderBy(a => a.Name, StringComparer.Ordinal))
        {
            var count = catalog.EntityIdsInArea(area.Id).Count;
            if (count > 0)
            {
                sb.Append("- ").Append(area.Name).Append(" (`").Append(area.Id).Append("`): ")
                  .Append(count).Append(" entities\n");
            }
        }
        if (catalog.EntityIdsInArea(HaCatalog.UnassignedArea).Count is var u and > 0)
        {
            sb.Append("- (unassigned): ").Append(u).Append(" entities\n");
        }

        sb.Append("\n### Device classes\n");
        sb.Append(string.Join(", ", catalog.ClassDomains()));

        return sb.ToString();
    }
}