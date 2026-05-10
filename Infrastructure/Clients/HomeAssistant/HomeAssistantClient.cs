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

    public Task<HaEntityState?> GetStateAsync(string entityId, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<IReadOnlyList<HaServiceDefinition>> ListServicesAsync(CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<HaServiceCallResult> CallServiceAsync(
        string domain, string service, string? entityId,
        IReadOnlyDictionary<string, JsonNode?>? data, CancellationToken ct = default)
        => throw new NotImplementedException();

    private HttpRequestMessage NewRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static async Task EnsureOkAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;

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
}
