using System.Net.Http.Json;
using Domain.Contracts;
using Domain.DTOs.Voice;

namespace Infrastructure.Clients.Voice;

// Reads the satellite roster from the hub (cached — the hub builds it from static config) and forwards
// target resolution to the hub. Resolution is never done locally: the hub's registry dual-keys rooms
// on Room and DisplayLocation, so forwarding is what keeps create-time validation identical to firing.
public sealed class HttpSatelliteCatalog(HttpClient httpClient, string token) : ISatelliteCatalog
{
    private IReadOnlyList<SatelliteDescriptor>? _roster;

    public async Task<IReadOnlyList<SatelliteDescriptor>> GetAllAsync(CancellationToken ct)
    {
        if (_roster is not null)
        {
            return _roster;
        }

        using var message = new HttpRequestMessage(HttpMethod.Get, "api/voice/satellites");
        message.Headers.Add("X-Announce-Token", token);

        var response = await httpClient.SendAsync(message, ct);
        response.EnsureSuccessStatusCode();
        return _roster = await response.Content.ReadFromJsonAsync<List<SatelliteDescriptor>>(ct) ?? [];
    }

    public async Task<IReadOnlyList<string>> ResolveAsync(AnnounceTarget target, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "api/voice/satellites/resolve")
        {
            Content = JsonContent.Create(target)
        };
        message.Headers.Add("X-Announce-Token", token);

        var response = await httpClient.SendAsync(message, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<string>>(ct) ?? [];
    }
}