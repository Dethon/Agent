using Domain.DTOs.Voice;
using McpChannelVoice.Services.Stt;
using McpChannelVoice.Services.Tts;
using McpChannelVoice.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit.Abstractions;

namespace Tests.Integration.McpChannelVoice;

// Pins the real Lemonade container's transcription contract, end to end through both endpoints:
// Kokoro synthesizes a Spanish phrase, whisper transcribes it back, and the verbose_json body
// must carry the avg_logprob / no_speech_prob signals the gibberish gate thresholds — the unit
// suite stubs these, so only this test proves the deployed server actually emits them.
// Requires the mcp-lemonade compose service; skips when it isn't reachable. First run on a cold
// volume downloads the whisper + Kokoro models, hence the generous timeouts.
public class LemonadeQualitySignalsTests(ITestOutputHelper output)
{
    private static readonly string _baseUrl =
        Environment.GetEnvironmentVariable("LEMONADE_BASE_URL") ?? "http://localhost:13305/v1";

    private sealed class PassthroughFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new() { Timeout = Timeout.InfiniteTimeSpan };
    }

    [SkippableFact]
    public async Task TranscribeAsync_RealLemonade_EmitsGibberishGateSignals()
    {
        Skip.IfNot(await LemonadeIsUp(), $"mcp-lemonade not reachable at {_baseUrl}");
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var factory = new PassthroughFactory();

        var tts = new OpenAiTextToSpeech(
            factory,
            new OpenAiTtsConfig { BaseUrl = _baseUrl },
            NullLogger<OpenAiTextToSpeech>.Instance);
        var stt = new OpenAiSpeechToText(
            factory,
            new OpenAiSttConfig
            {
                BaseUrl = _baseUrl,
                Language = "es",
                RequestTimeout = TimeSpan.FromMinutes(10)
            },
            NullLogger<OpenAiSpeechToText>.Instance);

        var result = await stt.TranscribeAsync(
            tts.SynthesizeAsync("hola, ¿qué hora es?", new SynthesisOptions(), cts.Token),
            new TranscriptionOptions(),
            cts.Token);

        output.WriteLine(
            $"text=\"{result.Text}\" avg_logprob={result.AvgLogProb} no_speech_prob={result.NoSpeechProb}");

        result.Text.ShouldNotBeNullOrWhiteSpace();
        result.AvgLogProb.ShouldNotBeNull();
        result.NoSpeechProb.ShouldNotBeNull();
        result.AvgLogProb!.Value.ShouldBeLessThanOrEqualTo(0);
        result.NoSpeechProb!.Value.ShouldBeInRange(0, 1);
    }

    private static async Task<bool> LemonadeIsUp()
    {
        var root = _baseUrl.TrimEnd('/');
        root = root.EndsWith("/v1", StringComparison.Ordinal) ? root[..^3] : root;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        try
        {
            return (await http.GetAsync($"{root}/api/v1/health")).IsSuccessStatusCode;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }
}