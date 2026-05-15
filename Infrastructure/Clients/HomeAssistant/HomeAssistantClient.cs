using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Domain.Contracts;
using Domain.Exceptions;
using JetBrains.Annotations;

namespace Infrastructure.Clients.HomeAssistant;

public class HomeAssistantClient(HttpClient httpClient, string token) : IHomeAssistantClient
{
    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<IReadOnlyList<HaEntityState>> ListStatesAsync(CancellationToken ct = default)
    {
        using var request = NewRequest(HttpMethod.Get, "api/states");
        using var response = await httpClient.SendAsync(request, ct);
        await EnsureOkAsync(response, ct);

        var raw = await response.Content.ReadFromJsonAsync<HaStateDto[]>(_json, ct)
                  ?? throw new HomeAssistantException("Empty Home Assistant response.");
        return raw.Select(ToEntity).ToList();
    }

    public async Task<HaEntityState?> GetStateAsync(string entityId, CancellationToken ct = default)
    {
        using var request = NewRequest(HttpMethod.Get, $"api/states/{Uri.EscapeDataString(entityId)}");
        using var response = await httpClient.SendAsync(request, ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        await EnsureOkAsync(response, ct);

        var dto = await response.Content.ReadFromJsonAsync<HaStateDto>(_json, ct);
        return dto is null ? null : ToEntity(dto);
    }

    public async Task<IReadOnlyList<HaServiceDefinition>> ListServicesAsync(CancellationToken ct = default)
    {
        using var request = NewRequest(HttpMethod.Get, "api/services");
        using var response = await httpClient.SendAsync(request, ct);
        await EnsureOkAsync(response, ct);

        var domains = await response.Content.ReadFromJsonAsync<HaServiceDomainDto[]>(_json, ct)
                      ?? throw new HomeAssistantException("Empty services payload.");

        return domains
            .SelectMany(d => (d.Services ?? new Dictionary<string, HaServiceDto>())
                .Select(kv => new HaServiceDefinition
                {
                    Domain = d.Domain ?? string.Empty,
                    Service = kv.Key,
                    Description = kv.Value.Description,
                    Fields = (kv.Value.Fields ?? new Dictionary<string, HaServiceFieldDto>())
                        .ToDictionary(f => f.Key, f => new HaServiceField
                        {
                            Description = f.Value.Description,
                            Required = f.Value.Required ?? false,
                            Example = f.Value.Example,
                            Selector = f.Value.Selector?.DeepClone()
                        }),
                    Target = kv.Value.Target?.DeepClone()
                }))
            .ToList();
    }

    public async Task<HaServiceCallResult> CallServiceAsync(
        string domain, string service, string? entityId,
        IReadOnlyDictionary<string, JsonNode?>? data, CancellationToken ct = default)
    {
        var body = new JsonObject();
        if (data is not null)
        {
            foreach (var kvp in data)
            {
                body[kvp.Key] = kvp.Value?.DeepClone();
            }
        }
        // HA's REST /api/services/{domain}/{service} treats the request body as `service_data`
        // and validates it against the service schema. `target` is only honored on the WebSocket
        // call_service path; on REST it gets rejected as an unknown key with a 400. Send entity_id
        // flat so the call works for any entity-targeted service.
        if (!string.IsNullOrEmpty(entityId))
        {
            body["entity_id"] = entityId;
        }

        var path = $"api/services/{Uri.EscapeDataString(domain)}/{Uri.EscapeDataString(service)}";

        // First attempt: ask for the service response (HA returns {changed_states, service_response}
        // for services that support it, e.g. roborock.get_maps, weather.get_forecasts, calendar.get_events).
        using var firstResp = await PostJsonAsync(path + "?return_response=true", body, ct);
        if (firstResp.StatusCode == HttpStatusCode.BadRequest)
        {
            var errBody = await firstResp.Content.ReadAsStringAsync(ct);
            // HA returns this exact phrase when return_response is passed against a service
            // whose handler is registered with SupportsResponse.NONE. The handler hasn't run,
            // so retrying without the query is safe (no double-execution).
            if (errBody.Contains("does not support responses", StringComparison.OrdinalIgnoreCase))
            {
                using var retryResp = await PostJsonAsync(path, body, ct);
                await EnsureOkAsync(retryResp, ct);
                return await ParseCallResultAsync(retryResp, ct);
            }
            throw new HomeAssistantException(
                $"Home Assistant returned 400: {(errBody.Length > 200 ? errBody[..200] + "…" : errBody)}", 400);
        }
        await EnsureOkAsync(firstResp, ct);
        return await ParseCallResultAsync(firstResp, ct);
    }

    private async Task<HttpResponseMessage> PostJsonAsync(string path, JsonObject body, CancellationToken ct)
    {
        using var request = NewRequest(HttpMethod.Post, path);
        request.Content = JsonContent.Create(body);
        return await httpClient.SendAsync(request, ct);
    }

    public async Task<string> RenderTemplateAsync(string template, CancellationToken ct = default)
    {
        var body = new JsonObject { ["template"] = template };
        using var request = NewRequest(HttpMethod.Post, "api/template");
        request.Content = JsonContent.Create(body);
        using var response = await httpClient.SendAsync(request, ct);
        await EnsureOkAsync(response, ct);
        return await response.Content.ReadAsStringAsync(ct);
    }

    private async Task<HaServiceCallResult> ParseCallResultAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var node = await response.Content.ReadFromJsonAsync<JsonNode>(_json, ct);
        return node switch
        {
            JsonArray arr => new HaServiceCallResult
            {
                ChangedEntities = (arr.Deserialize<HaStateDto[]>(_json) ?? [])
                    .Select(ToEntity).ToList()
            },
            JsonObject obj => new HaServiceCallResult
            {
                ChangedEntities = (obj["changed_states"]?.Deserialize<HaStateDto[]>(_json) ?? [])
                    .Select(ToEntity).ToList(),
                Response = obj["service_response"]?.DeepClone()
            },
            _ => new HaServiceCallResult { ChangedEntities = [] }
        };
    }

    private HttpRequestMessage NewRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static async Task EnsureOkAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var snippet = await SafeReadAsync(response, ct);
        throw response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => new HomeAssistantUnauthorizedException(
                "Home Assistant rejected the access token (401)."),
            HttpStatusCode.NotFound => new HomeAssistantNotFoundException(
                $"Home Assistant returned 404: {snippet}"),
            _ => new HomeAssistantException(
                $"Home Assistant returned {(int)response.StatusCode}: {snippet}",
                (int)response.StatusCode)
        };
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            return body.Length > 200 ? body[..200] + "…" : body;
        }
        catch
        {
            return "<unreadable body>";
        }
    }

    private static HaEntityState ToEntity(HaStateDto dto) => new()
    {
        EntityId = dto.EntityId ?? string.Empty,
        State = dto.State ?? string.Empty,
        Attributes = dto.Attributes ?? new Dictionary<string, JsonNode?>(),
        LastChanged = dto.LastChanged,
        LastUpdated = dto.LastUpdated
    };

    [PublicAPI]
    private record HaStateDto
    {
        [JsonPropertyName("entity_id")] public string? EntityId { get; init; }
        [JsonPropertyName("state")] public string? State { get; init; }
        [JsonPropertyName("attributes")] public Dictionary<string, JsonNode?>? Attributes { get; init; }
        [JsonPropertyName("last_changed")] public DateTimeOffset? LastChanged { get; init; }
        [JsonPropertyName("last_updated")] public DateTimeOffset? LastUpdated { get; init; }
    }

    [PublicAPI]
    private record HaServiceDomainDto
    {
        [JsonPropertyName("domain")] public string? Domain { get; init; }
        [JsonPropertyName("services")] public Dictionary<string, HaServiceDto>? Services { get; init; }
    }

    [PublicAPI]
    private record HaServiceDto
    {
        [JsonPropertyName("description")] public string? Description { get; init; }
        [JsonPropertyName("fields")] public Dictionary<string, HaServiceFieldDto>? Fields { get; init; }
        [JsonPropertyName("target")] public JsonNode? Target { get; init; }
    }

    [PublicAPI]
    private record HaServiceFieldDto
    {
        [JsonPropertyName("description")] public string? Description { get; init; }
        [JsonPropertyName("required")] public bool? Required { get; init; }
        [JsonPropertyName("example")] public JsonNode? Example { get; init; }
        [JsonPropertyName("selector")] public JsonNode? Selector { get; init; }
    }
}