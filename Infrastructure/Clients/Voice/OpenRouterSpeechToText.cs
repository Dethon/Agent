using System.Net.Http.Headers;
using System.Net.Http.Json;
using Domain.Contracts;
using Domain.DTOs.Metrics;
using Domain.DTOs.Voice;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Clients.Voice;

public sealed class OpenRouterSpeechToText(
    HttpClient http,
    string model,
    string apiKey,
    IMetricsPublisher metrics,
    ILogger<OpenRouterSpeechToText> logger) : ISpeechToText
{
    public async Task<TranscriptionResult> TranscribeAsync(
        IAsyncEnumerable<AudioChunk> audio,
        TranscriptionOptions options,
        CancellationToken ct)
    {
        var buffer = new MemoryStream();
        AudioFormat? format = null;
        await foreach (var chunk in audio.WithCancellation(ct))
        {
            format ??= chunk.Format;
            await buffer.WriteAsync(chunk.Data, ct);
        }

        var wav = PcmWavWriter.Encode(buffer.ToArray(), format ?? AudioFormat.WyomingStandard);

        var body = new
        {
            model,
            input_audio = new
            {
                data = Convert.ToBase64String(wav),
                format = "wav"
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/audio/transcriptions")
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<OpenRouterTranscription>(cancellationToken: ct)
                      ?? throw new InvalidOperationException("Empty OpenRouter response");

        logger.LogInformation("OpenRouter STT: lang={Lang}", payload.Language);

        await metrics.PublishAsync(new TokenUsageEvent
        {
            Sender = "voice-sat",
            Model = model,
            InputTokens = 0,
            OutputTokens = 0,
            Cost = 0m,
            Origin = "voice"
        }, ct);

        return new TranscriptionResult
        {
            Text = payload.Text,
            Language = payload.Language,
            Confidence = null
        };
    }

    private sealed record OpenRouterTranscription(string Text, string? Language);
}