using Domain.Exceptions;

namespace Infrastructure.Clients.Voice;

// Shared send path for the three hub adapters: connection failures and request timeouts become the
// typed VoiceHubUnavailableException so callers can fail closed with a retryable error instead of
// leaking a raw HTTP exception. An error *status* is deliberately not mapped — that is the hub
// answering (e.g. a token mismatch), which retrying will not fix.
internal static class VoiceHubHttp
{
    public static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpRequestMessage message, CancellationToken ct)
    {
        try
        {
            return await client.SendAsync(message, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new VoiceHubUnavailableException($"The voice hub is unreachable: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new VoiceHubUnavailableException("The voice hub timed out.", ex);
        }
    }
}