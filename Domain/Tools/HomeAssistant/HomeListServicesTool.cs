using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Domain.Tools.HomeAssistant;

public class HomeListServicesTool(IHomeAssistantClient client)
{
    protected const string Name = "home_list_services";

    protected const string Description =
        """
        Lists Home Assistant services. Pass `domain` to filter (e.g. 'vacuum', 'light').
        Each entry includes domain, service, description, and field metadata so the
        agent can construct valid `data` payloads for `home_call_service`.
        """;

    protected async Task<JsonObject> RunAsync(string? domain, CancellationToken ct)
    {
        var services = await client.ListServicesAsync(ct);

        var filtered = services
            .Where(s => domain is null || s.Domain.Equals(domain, StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s.Domain, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Service, StringComparer.OrdinalIgnoreCase)
            .Select(s =>
            {
                var fields = new JsonObject();
                foreach (var (name, field) in s.Fields)
                {
                    var f = new JsonObject
                    {
                        ["required"] = field.Required
                    };
                    if (!string.IsNullOrEmpty(field.Description))
                    {
                        f["description"] = field.Description;
                    }
                    if (field.Example is not null)
                    {
                        f["example"] = field.Example.DeepClone();
                    }
                    fields[name] = f;
                }
                var item = new JsonObject
                {
                    ["domain"] = s.Domain,
                    ["service"] = s.Service,
                    ["fields"] = fields
                };
                if (!string.IsNullOrEmpty(s.Description))
                {
                    item["description"] = s.Description;
                }
                return item;
            })
            .ToArray<JsonNode?>();

        return new JsonObject
        {
            ["ok"] = true,
            ["services"] = new JsonArray(filtered)
        };
    }
}
