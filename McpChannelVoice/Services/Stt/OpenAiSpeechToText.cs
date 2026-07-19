using System.Buffers.Binary;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs.Voice;
using McpChannelVoice.Settings;

namespace McpChannelVoice.Services.Stt;

// Transcribes one utterance segment via Lemonade's OpenAI-compatible /audio/transcriptions
// endpoint. The segment is buffered into a WAV blob (mono s16le at the incoming rate — the
// satellites send 16 kHz) and posted as multipart with response_format=verbose_json so the
// per-segment avg_logprob / no_speech_prob quality signals reach the gibberish gate. The
// signals are duration-weighted across the body's segments (one POST usually carries one, but
// whisper may split); a body without segments (plain json shape) degrades to null signals and
// the gate fails open. Lemonade emits neither score nor compression_ratio — left null.
public sealed class OpenAiSpeechToText(
    HttpClient http,
    OpenAiSttConfig config,
    ILogger<OpenAiSpeechToText> logger) : ISpeechToText
{
    public async Task<TranscriptionResult> TranscribeAsync(
        IAsyncEnumerable<AudioChunk> audio,
        TranscriptionOptions options,
        CancellationToken ct)
    {
        var chunks = new List<AudioChunk>();
        await foreach (var chunk in audio.WithCancellation(ct))
        {
            chunks.Add(chunk);
        }

        var dataBytes = chunks.Sum(c => c.Data.Length);
        if (dataBytes == 0)
        {
            return new TranscriptionResult { Text = "" };
        }

        using var content = new MultipartFormDataContent();
        var wav = new ByteArrayContent(BuildWav(chunks, chunks[0].Format, dataBytes));
        wav.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        content.Add(wav, "file", "utterance.wav");
        content.Add(new StringContent(config.Model), "model");
        content.Add(new StringContent("verbose_json"), "response_format");
        if ((options.Language ?? config.Language) is { } language)
        {
            content.Add(new StringContent(language), "language");
        }

        using var response = await PostWithTimeoutAsync(content, ct);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(ct);
        if (JsonNode.Parse(body) is not JsonObject json || json["text"] is null)
        {
            throw new InvalidOperationException("Malformed transcription response from Lemonade");
        }

        var result = ParseResult(json);
        logger.LogInformation(
            "Lemonade transcript: text={Text} lang={Lang} avg_logprob={AvgLogProb} no_speech_prob={NoSpeechProb}",
            result.Text, result.Language, result.AvgLogProb, result.NoSpeechProb);
        return result;
    }

    // The shared Lemonade client has an infinite timeout (streaming TTS), so transcription bounds
    // itself. PostAsync buffers the full response, so this covers body receipt too. The timeout
    // surfaces as TimeoutException, not OperationCanceledException: the satellite host swallows
    // OCE as connection teardown, and a hung Lemonade must reach its SttError/re-arm path instead.
    private async Task<HttpResponseMessage> PostWithTimeoutAsync(
        MultipartFormDataContent content, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(config.RequestTimeout);
        try
        {
            return await http.PostAsync(
                $"{config.BaseUrl.TrimEnd('/')}/audio/transcriptions", content, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Lemonade transcription did not respond within {config.RequestTimeout.TotalSeconds:F0}s");
        }
    }

    private static TranscriptionResult ParseResult(JsonObject json)
    {
        var weighted = ((json["segments"] as JsonArray)?.OfType<JsonObject>() ?? [])
            .Select(s => (
                Weight: Math.Max(
                    (JsonNumber.ReadDouble(s, "end") ?? 0) - (JsonNumber.ReadDouble(s, "start") ?? 0),
                    1e-9),
                Segment: s))
            .ToList();

        return new TranscriptionResult
        {
            Text = json["text"]?.GetValue<string>() ?? string.Empty,
            Language = json["language"]?.GetValue<string>(),
            AvgLogProb = WeightedMean(weighted, s => JsonNumber.ReadDouble(s, "avg_logprob")),
            NoSpeechProb = WeightedMean(weighted, s => JsonNumber.ReadDouble(s, "no_speech_prob"))
        };
    }

    // Segments differ in length, so a plain mean would let a short noise segment outvote long
    // clean speech. Weight by duration; segments without the value abstain (fail-open).
    private static double? WeightedMean(
        IReadOnlyList<(double Weight, JsonObject Segment)> weighted,
        Func<JsonObject, double?> selector)
    {
        var pairs = weighted
            .Where(w => selector(w.Segment) is not null)
            .Select(w => (w.Weight, Value: selector(w.Segment)!.Value))
            .ToList();
        return pairs.Count > 0
            ? pairs.Sum(p => p.Weight * p.Value) / pairs.Sum(p => p.Weight)
            : null;
    }

    private static byte[] BuildWav(IReadOnlyList<AudioChunk> chunks, AudioFormat format, int dataBytes)
    {
        var wav = new byte[44 + dataBytes];
        var span = wav.AsSpan();
        Encoding.ASCII.GetBytes("RIFF", span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], 36 + dataBytes);
        Encoding.ASCII.GetBytes("WAVE", span[8..]);
        Encoding.ASCII.GetBytes("fmt ", span[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(span[20..], 1); // PCM
        BinaryPrimitives.WriteInt16LittleEndian(span[22..], (short)format.Channels);
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], format.SampleRateHz);
        BinaryPrimitives.WriteInt32LittleEndian(
            span[28..], format.SampleRateHz * format.SampleWidthBytes * format.Channels);
        BinaryPrimitives.WriteInt16LittleEndian(span[32..], (short)(format.SampleWidthBytes * format.Channels));
        BinaryPrimitives.WriteInt16LittleEndian(span[34..], (short)(format.SampleWidthBytes * 8));
        Encoding.ASCII.GetBytes("data", span[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], dataBytes);

        var offset = 44;
        foreach (var chunk in chunks)
        {
            chunk.Data.Span.CopyTo(span[offset..]);
            offset += chunk.Data.Length;
        }
        return wav;
    }
}