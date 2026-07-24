using System.Net;
using Domain.DTOs.Voice;
using Infrastructure.Clients.Voice;
using Shouldly;

namespace Tests.Unit.Infrastructure.Clients.Voice;

public class HttpSatelliteCatalogTests
{
    [Fact]
    public async Task GetAllAsync_FetchesRosterFromHub()
    {
        var handler = new VoiceHubStubHandler(_ => VoiceHubStubHandler.Json(
            HttpStatusCode.OK, new List<SatelliteDescriptor> { new("kitchen-01", "Kitchen") }));
        var sut = new HttpSatelliteCatalog(VoiceHubStubHandler.Client(handler), "secret");

        var roster = await sut.GetAllAsync(CancellationToken.None);

        handler.LastRequest!.Method.ShouldBe(HttpMethod.Get);
        handler.LastRequest.RequestUri!.AbsolutePath.ShouldBe("/api/voice/satellites");
        handler.LastRequest.Headers.GetValues("X-Announce-Token").ShouldContain("secret");
        roster.ShouldContain(s => s.Id == "kitchen-01" && s.Room == "Kitchen");
    }

    [Fact]
    public async Task ResolveAsync_ForwardsTargetToHubResolveEndpoint()
    {
        // Resolution is never done locally off the roster — a DisplayLocation-form room must go to the
        // hub, whose registry dual-keys Room and DisplayLocation, or it would resolve to nothing here.
        var handler = new VoiceHubStubHandler(_ => VoiceHubStubHandler.Json(
            HttpStatusCode.OK, new List<string> { "kitchen-01" }));
        var sut = new HttpSatelliteCatalog(VoiceHubStubHandler.Client(handler), "secret");

        var ids = await sut.ResolveAsync(new AnnounceTarget { Room = "Kitchen (Madrid, Spain)" }, CancellationToken.None);

        handler.LastRequest!.Method.ShouldBe(HttpMethod.Post);
        handler.LastRequest.RequestUri!.AbsolutePath.ShouldBe("/api/voice/satellites/resolve");
        handler.LastBody.ShouldContain("Madrid");
        ids.ShouldBe(["kitchen-01"]);
    }
}