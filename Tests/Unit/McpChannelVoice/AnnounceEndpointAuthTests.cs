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
        builder.Services
            .AddSingleton(announce)
            .AddSingleton(svc ?? new Mock<AnnouncementService>(MockBehavior.Loose,
                new SatelliteRegistry(new Dictionary<string, SatelliteConfig>()),
                new SatelliteSessionRegistry(),
                Mock.Of<ITextToSpeech>(),
                new VoiceSettings(),
                Mock.Of<IMetricsPublisher>(),
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
}