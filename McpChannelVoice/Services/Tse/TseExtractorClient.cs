using System.Net.Http.Headers;
using McpChannelVoice.Settings;

namespace McpChannelVoice.Services.Tse;

public sealed class TseExtractorClient(
    HttpClient http, TseSettings settings, ILogger<TseExtractorClient> logger) : ITseExtractorClient
{
    public async Task<byte[]?> ExtractAsync(byte[] mixtureWav, string speaker, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(settings.TimeoutMs);
            using var content = new ByteArrayContent(mixtureWav);
            content.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            var url = $"{settings.Endpoint.TrimEnd('/')}/extract?speaker={Uri.EscapeDataString(speaker)}";
            using var response = await http.PostAsync(url, content, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("TSE extract for {Speaker} returned {Status}", speaker, response.StatusCode);
                return null;
            }
            return await response.Content.ReadAsByteArrayAsync(cts.Token);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "TSE extract for {Speaker} failed (fail-open, raw audio proceeds)", speaker);
            return null;
        }
    }
}