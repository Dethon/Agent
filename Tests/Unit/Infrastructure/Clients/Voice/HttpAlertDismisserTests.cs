using System.Net;
using Domain.DTOs.Voice;
using Infrastructure.Clients.Voice;
using Shouldly;

namespace Tests.Unit.Infrastructure.Clients.Voice;

public class HttpAlertDismisserTests
{
    [Fact]
    public async Task DismissAllAsync_PostsDismissWithTokenAndDeserializesAlerts()
    {
        var handler = new VoiceHubStubHandler(_ => VoiceHubStubHandler.Json(
            HttpStatusCode.OK, new List<DismissedAlert> { new("pasta", AnnounceKind.Timer) }));
        var sut = new HttpAlertDismisser(VoiceHubStubHandler.Client(handler), "secret");

        var result = await sut.DismissAllAsync(CancellationToken.None);

        handler.LastRequest!.Method.ShouldBe(HttpMethod.Post);
        handler.LastRequest.RequestUri!.AbsolutePath.ShouldBe("/api/voice/dismiss");
        handler.LastRequest.Headers.GetValues("X-Announce-Token").ShouldContain("secret");
        result.ShouldContain(d => d.Text == "pasta" && d.Kind == AnnounceKind.Timer);
    }
}