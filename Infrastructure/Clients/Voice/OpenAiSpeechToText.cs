using System.Net.Http.Headers;
using System.Net.Http.Json;
using Domain.Contracts;
using Domain.DTOs.Metrics;
using Domain.DTOs.Voice;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Clients.Voice;

public sealed class OpenAiSpeechToText(
    HttpClient http,
    string model,
    string apiKey,
    IMetricsPublisher metrics,
    ILogger<OpenAiSpeechToText> logger) : ISpeechToText
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

        using var content = new MultipartFormDataContent
        {
            { new ByteArrayContent(wav) { Headers = { ContentType = MediaTypeHeaderValue.Parse("audio/wav") } }, "file", "audio.wav" },
            { new StringContent(model), "model" },
            { new StringContent("verbose_json"), "response_format" }
        };
        if (options.Language is not null)
        {
            content.Add(new StringContent(options.Language), "language");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/audio/transcriptions") { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<OpenAiTranscription>(cancellationToken: ct)
                      ?? throw new InvalidOperationException("Empty OpenAI response");

        logger.LogInformation("OpenAI STT: lang={Lang} duration={Duration:F2}", payload.Language, payload.Duration);

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

    private sealed record OpenAiTranscription(string Text, string? Language, double? Duration);
}