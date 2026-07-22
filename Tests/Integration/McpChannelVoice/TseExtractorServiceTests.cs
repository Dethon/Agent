using System.Net;
using System.Text.Json;
using Domain.DTOs.Voice;
using McpChannelVoice.Services.Tse;
using Shouldly;

namespace Tests.Integration.McpChannelVoice;

// Exercises the real tse-extractor sidecar (DockerCompose/tse-extractor, compose service on
// :9098). Unlike Redis-backed integration tests, this service is a manually-run local sidecar
// with no guarantee of being up on a given dev box, so this mirrors
// SpeakerVerificationModelTests's convention for a real, possibly-absent external dependency:
// attempt the real precondition, and skip (never hard-fail) when it isn't met.
[Trait("Category", "External")]
public class TseExtractorServiceTests
{
    private static readonly HttpClient Http = new() { BaseAddress = new Uri("http://localhost:9098") };

    private static byte[] SyntheticUtteranceWav(double seconds = 2.0)
    {
        var samples = (int)(16000 * seconds);
        var pcm = new byte[samples * 2];
        for (var i = 0; i < samples; i++)
        {
            var value = (short)(8000 * Math.Sin(2 * Math.PI * 220 * i / 16000.0));
            BitConverter.GetBytes(value).CopyTo(pcm, i * 2);
        }
        return WavCodec.Encode([new AudioChunk { Data = pcm, Format = AudioFormat.WyomingStandard }]);
    }

    private static async Task<JsonElement?> TryGetHealthAsync()
    {
        // Bounded probe: an unreachable sidecar isn't always a fast "connection refused" (a
        // stopped-but-recently-published Docker port can hang instead of resetting on this
        // host), so cap the wait rather than inheriting HttpClient's 100s default timeout.
        // Only a connection-level failure (refused/no route) or that bounded timeout means
        // "not reachable" and skips; a reachable sidecar answering with a non-2xx status (bad
        // checkpoint, failed model load) must fail the test, not be mistaken for absence.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        HttpResponseMessage response;
        try
        {
            response = await Http.GetAsync("/health", cts.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            return null;
        }
        using (response)
        {
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync();
            return JsonDocument.Parse(body).RootElement;
        }
    }

    [SkippableFact]
    public async Task HealthReportsReadyAndSpeakers()
    {
        var health = await TryGetHealthAsync();
        Skip.If(health is null, "tse-extractor sidecar not reachable at http://localhost:9098");
        health!.Value.GetProperty("status").GetString().ShouldBe("ready");
        health.Value.GetProperty("speakers").ValueKind.ShouldBe(JsonValueKind.Array);
    }

    [SkippableFact]
    public async Task UnknownSpeakerIs404()
    {
        Skip.If(await TryGetHealthAsync() is null, "tse-extractor sidecar not reachable at http://localhost:9098");
        using var content = new ByteArrayContent(SyntheticUtteranceWav());
        var response = await Http.PostAsync("/extract?speaker=nobody-here", content);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [SkippableFact]
    public async Task ExtractRoundTripsForAnEnrolledSpeaker()
    {
        var health = await TryGetHealthAsync();
        Skip.If(health is null, "tse-extractor sidecar not reachable at http://localhost:9098");
        var speakers = health!.Value.GetProperty("speakers").EnumerateArray().Select(s => s.GetString()).ToList();
        Skip.If(speakers.Count == 0, "no speaker enrolled on this tse-extractor sidecar; the 404 test still covers the routing");
        var wav = SyntheticUtteranceWav();
        using var content = new ByteArrayContent(wav);
        var response = await Http.PostAsync($"/extract?speaker={speakers[0]}", content);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var reply = await response.Content.ReadAsByteArrayAsync();
        var decoded = WavCodec.Decode(reply); // valid RIFF, hub format
        decoded.Data.Length.ShouldBeGreaterThan(0);
    }
}