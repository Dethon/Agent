using System.Net;
using System.Net.Http.Json;
using Domain.Contracts;
using Domain.DTOs.Voice;
using McpChannelVoice.Services;
using McpChannelVoice.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class AnnounceEndpointAuthTests
{
    private static async Task<HttpClient> BuildClientAsync(AnnounceSettings announce, AnnouncementService? svc = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        var satelliteRegistry = new SatelliteRegistry(new Dictionary<string, SatelliteConfig>());
        var sessionRegistry = new SatelliteSessionRegistry();
        var tts = Mock.Of<ITextToSpeech>();
        var voiceSettings = new VoiceSettings();
        var metricsPublisher = Mock.Of<IMetricsPublisher>();
        var alertRegistry = new ActiveAlertRegistry();
        var insistentController = new InsistentAnnouncementController(
            satelliteRegistry,
            sessionRegistry,
            tts,
            voiceSettings,
            alertRegistry,
            metricsPublisher,
            TimeProvider.System,
            NullLogger<InsistentAnnouncementController>.Instance);

        builder.Services
            .AddSingleton(announce)
            .AddSingleton(alertRegistry)
            .AddSingleton(insistentController)
            .AddSingleton(svc ?? new Mock<AnnouncementService>(MockBehavior.Loose,
                satelliteRegistry,
                sessionRegistry,
                tts,
                voiceSettings,
                metricsPublisher,
                NullLogger<AnnouncementService>.Instance).Object);

        var app = builder.Build();
        AnnounceEndpoint.Map(app);
        await app.StartAsync();
        return app.GetTestClient();
    }

    [Fact]
    public async Task NoToken_Returns401()
    {
        using var client = await BuildClientAsync(new AnnounceSettings { Enabled = true, Token = "expected" });
        var response = await client.PostAsJsonAsync("/api/voice/announce",
            new AnnounceRequest { Target = new() { SatelliteId = "kitchen-01" }, Text = "hi" });
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task WrongToken_Returns401()
    {
        using var client = await BuildClientAsync(new AnnounceSettings { Enabled = true, Token = "expected" });
        client.DefaultRequestHeaders.Add("X-Announce-Token", "wrong");
        var response = await client.PostAsJsonAsync("/api/voice/announce",
            new AnnounceRequest { Target = new() { SatelliteId = "kitchen-01" }, Text = "hi" });
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Disabled_Returns503()
    {
        using var client = await BuildClientAsync(new AnnounceSettings { Enabled = false, Token = "expected" });
        client.DefaultRequestHeaders.Add("X-Announce-Token", "expected");
        var response = await client.PostAsJsonAsync("/api/voice/announce",
            new AnnounceRequest { Target = new() { SatelliteId = "kitchen-01" }, Text = "hi" });
        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task BlankText_Returns400()
    {
        using var client = await BuildClientAsync(new AnnounceSettings { Enabled = true, Token = "expected" });
        client.DefaultRequestHeaders.Add("X-Announce-Token", "expected");
        var response = await client.PostAsJsonAsync("/api/voice/announce",
            new AnnounceRequest { Target = new() { SatelliteId = "kitchen-01" }, Text = "   " });
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task OversizedText_Returns400()
    {
        using var client = await BuildClientAsync(new AnnounceSettings { Enabled = true, Token = "expected", MaxTextLength = 10 });
        client.DefaultRequestHeaders.Add("X-Announce-Token", "expected");
        var response = await client.PostAsJsonAsync("/api/voice/announce",
            new AnnounceRequest { Target = new() { SatelliteId = "kitchen-01" }, Text = new string('a', 11) });
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task EmptyTarget_Returns400()
    {
        using var client = await BuildClientAsync(new AnnounceSettings { Enabled = true, Token = "expected" });
        client.DefaultRequestHeaders.Add("X-Announce-Token", "expected");
        var response = await client.PostAsJsonAsync("/api/voice/announce",
            new AnnounceRequest { Target = new(), Text = "hi" });
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task NullTarget_Returns400()
    {
        // `required` on AnnounceRequest.Target only enforces the JSON key is present, not non-null,
        // so a {"target": null} body deserializes with Target == null. The handler must 400, not 500.
        using var client = await BuildClientAsync(new AnnounceSettings { Enabled = true, Token = "expected" });
        client.DefaultRequestHeaders.Add("X-Announce-Token", "expected");
        var response = await client.PostAsJsonAsync("/api/voice/announce",
            new AnnounceRequest { Target = null!, Text = "hi" });
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task MalformedVoice_Returns400()
    {
        using var client = await BuildClientAsync(new AnnounceSettings { Enabled = true, Token = "expected" });
        client.DefaultRequestHeaders.Add("X-Announce-Token", "expected");
        var response = await client.PostAsJsonAsync("/api/voice/announce",
            new AnnounceRequest { Target = new() { SatelliteId = "kitchen-01" }, Text = "hi", Voice = "bad voice!" });
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}