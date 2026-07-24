using System.Net.Http.Json;
using Domain.Contracts;
using Domain.DTOs.Voice;

namespace Infrastructure.Clients.Voice;

// Fires timer rings by POSTing to the voice hub's announce endpoint. The hub owns the live satellite
// sessions, so the out-of-process timers server can only reach them over HTTP. HttpClient.BaseAddress
// is preconfigured to the hub; only the shared announce token is passed here.
public sealed class HttpInsistentAnnouncer(HttpClient httpClient, string token) : IInsistentAnnouncer
{
    public async Task<AnnounceResponse> StartAsync(AnnounceRequest request, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "api/voice/announce")
        {
            Content = JsonContent.Create(request)
        };
        message.Headers.Add("X-Announce-Token", token);

        var response = await httpClient.SendAsync(message, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AnnounceResponse>(ct)
            ?? throw new InvalidOperationException("Voice hub returned an empty announce response.");
    }
}