using System.Net;
using System.Text.Json;
using Domain.DTOs.Voice;
using McpChannelVoice.Services.Tse;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.McpChannelVoice;

// Exercises the real tse-extractor sidecar (DockerCompose/tse-extractor), spun as a testcontainer by
// TseExtractorFixture with a synthetic speaker enrolled. The container is a heavy, locally-built
// image over a bind-mounted checkpoint, so when Docker/image/checkpoint is unavailable the fixture
// records a SkipReason and every test skips (never hard-fails) -- the External-category convention.
[Trait("Category", "External")]
public class TseExtractorServiceTests(TseExtractorFixture fixture) : IClassFixture<TseExtractorFixture>
{
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

    private async Task<JsonElement> GetHealthAsync()
    {
        using var http = fixture.CreateHttpClient();
        var body = await http.GetStringAsync("/health");
        return JsonDocument.Parse(body).RootElement;
    }

    [SkippableFact]
    public async Task HealthReportsReadyAndTheEnrolledSpeaker()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        var health = await GetHealthAsync();
        health.GetProperty("status").GetString().ShouldBe("ready");
        var speakers = health.GetProperty("speakers").EnumerateArray().Select(s => s.GetString());
        speakers.ShouldContain(TseExtractorFixture.EnrolledSpeaker);
    }

    [SkippableFact]
    public async Task UnknownSpeakerIs404()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        using var http = fixture.CreateHttpClient();
        using var content = new ByteArrayContent(SyntheticUtteranceWav());
        var response = await http.PostAsync("/extract?speaker=nobody-here", content);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [SkippableFact]
    public async Task ExtractRoundTripsForAnEnrolledSpeaker()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        var wav = SyntheticUtteranceWav();
        using var http = fixture.CreateHttpClient();
        using var content = new ByteArrayContent(wav);
        var response = await http.PostAsync($"/extract?speaker={TseExtractorFixture.EnrolledSpeaker}", content);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var reply = await response.Content.ReadAsByteArrayAsync();
        var decoded = WavCodec.Decode(reply); // valid RIFF, hub format
        decoded.Data.Length.ShouldBeGreaterThan(0);
        // The dangerous failure mode is a 200 carrying subtly wrong audio; the sidecar clamps
        // its output to the mixture's length, so pin sample count equality (not "not silence" —
        // a synthetic mixture against a synthetic enrollment may legitimately come back near-silent).
        var mixture = WavCodec.Decode(wav);
        decoded.Data.Length.ShouldBe(mixture.Data.Length);
    }
}