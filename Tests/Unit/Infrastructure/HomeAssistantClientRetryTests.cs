using System.Net;
using Domain.Contracts;
using Domain.Exceptions;
using Infrastructure.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Tests.Unit.Infrastructure;

// HA answers ANY exception raised inside a service handler with a 500 (e.g. Music Assistant
// failing to resolve a playlist name yields a bare "Server got itself in trouble"). That is an
// application error, not a transient fault: replaying it cannot succeed, costs a full backoff
// round on a voice turn, and re-runs a POST that may already have applied part of its effect.
// Reads stay retryable — a 500 from /api/states really can be a blip.
public class HomeAssistantClientRetryTests : IDisposable
{
    private readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose()
    {
        _server.Dispose();
    }

    [Fact]
    public async Task CallServiceAsync_When500_IsAttemptedOnceAndNotRetried()
    {
        _server.Given(Request.Create().WithPath("/api/services/light/turn_on").UsingPost())
            .RespondWith(Response.Create().WithStatusCode((int)HttpStatusCode.InternalServerError));

        var client = BuildClient();

        await Should.ThrowAsync<HomeAssistantException>(
            () => client.CallServiceAsync("light", "turn_on", "light.kitchen", null));

        _server.LogEntries.Count().ShouldBe(1);
    }

    [Fact]
    public async Task ListStatesAsync_When500_IsStillRetried()
    {
        _server.Given(Request.Create().WithPath("/api/states").UsingGet())
            .RespondWith(Response.Create().WithStatusCode((int)HttpStatusCode.InternalServerError));

        var client = BuildClient();

        await Should.ThrowAsync<HomeAssistantException>(() => client.ListStatesAsync());

        _server.LogEntries.Count().ShouldBe(3);
    }

    private IHomeAssistantClient BuildClient()
    {
        var services = new ServiceCollection();
        services.AddHomeAssistantClient(_server.Url!, "test-token");
        return services.BuildServiceProvider().GetRequiredService<IHomeAssistantClient>();
    }
}