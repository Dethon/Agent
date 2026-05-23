using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Domain.Tools.HomeAssistant.Vfs;

public static class HaActionResolver
{
    public static IReadOnlyList<HaServiceDefinition> ServicesFor(
        string entityId, IReadOnlyList<HaServiceDefinition> services)
    {
        var classDomain = HaCatalog.ClassOf(entityId);
        return services
            .Where(s => s.Domain.Equals(classDomain, StringComparison.Ordinal))
            .Where(s => TargetAcceptsEntity(s.Target, classDomain))
            .OrderBy(s => s.Service, StringComparer.Ordinal)
            .ToList();
    }

    // target == null  -> not entity-targeted, exclude.
    // target has no "entity" constraint we can read -> accept (it targets entities generically).
    // target.entity is a list of selector objects; accept if any has no "domain" narrowing,
    // or a "domain" list/string that includes the entity's class.
    private static bool TargetAcceptsEntity(JsonNode? target, string classDomain)
    {
        if (target is null)
        {
            return false;
        }
        if (target["entity"] is not JsonNode entity)
        {
            return true;
        }

        var selectors = entity as JsonArray ?? [entity.DeepClone()];
        return selectors.Any(sel => SelectorAcceptsDomain(sel, classDomain));
    }

    private static bool SelectorAcceptsDomain(JsonNode? selector, string classDomain)
    {
        if (selector?["domain"] is not JsonNode domain)
        {
            return true;
        }
        return domain switch
        {
            JsonArray arr => arr.Any(d => d?.GetValue<string>() == classDomain),
            JsonValue v => v.GetValue<string>() == classDomain,
            _ => false
        };
    }
}