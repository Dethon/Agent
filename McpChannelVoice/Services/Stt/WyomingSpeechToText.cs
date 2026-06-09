using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs.Voice;
using McpChannelVoice.Services.WyomingProtocol;
using McpChannelVoice.Settings;

namespace McpChannelVoice.Services.Stt;

public sealed class WyomingSpeechToText(
    WyomingSttConfig config,
    ILogger<WyomingSpeechToText> logger) : ISpeechToText
{
    public async Task<TranscriptionResult> TranscribeAsync(
        IAsyncEnumerable<AudioChunk> audio,
        TranscriptionOptions options,
        CancellationToken ct)
    {
        await using var client = new WyomingClient();
        await client.ConnectAsync(config.Host, config.Port, ct);

        var language = options.Language ?? config.Language;
        var transcribeData = new JsonObject();
        if (language is not null)
        {
            transcribeData["language"] = language;
        }

        await client.WriteAsync(WyomingEvent.Header("transcribe", transcribeData), ct);

        var fmt = AudioFormat.WyomingStandard;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await client.WriteAsync(
            WyomingEvent.Header("audio-start", new JsonObject
            {
                ["rate"] = fmt.SampleRateHz,
                ["width"] = fmt.SampleWidthBytes,
                ["channels"] = fmt.Channels,
                ["timestamp"] = 0
            }), ct);

        await foreach (var chunk in audio.WithCancellation(ct))
        {
            await client.WriteAsync(
                WyomingEvent.WithPayload(
                    "audio-chunk",
                    new JsonObject
                    {
                        ["rate"] = chunk.Format.SampleRateHz,
                        ["width"] = chunk.Format.SampleWidthBytes,
                        ["channels"] = chunk.Format.Channels,
                        ["timestamp"] = (long)chunk.Timestamp.TotalMilliseconds
                    },
                    chunk.Data),
                ct);
        }

        await client.WriteAsync(
            WyomingEvent.Header("audio-stop", new JsonObject
            {
                ["timestamp"] = sw.ElapsedMilliseconds
            }), ct);

        await foreach (var evt in client.ReadAllAsync(ct))
        {
            if (evt.Type != "transcript")
            {
                continue;
            }

            var text = evt.Data["text"]?.GetValue<string>() ?? string.Empty;
            var lang = evt.Data["language"]?.GetValue<string>();
            double? score = null;
            if (evt.Data["score"] is JsonNode s)
            {
                score = s.GetValue<double>();
            }

            logger.LogInformation("Wyoming transcript: text={Text} lang={Lang}", text, lang);
            return new TranscriptionResult { Text = text, Language = lang, Confidence = score };
        }

        return new TranscriptionResult { Text = "" };
    }
}