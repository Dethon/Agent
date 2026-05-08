using System.Net;
using Infrastructure.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Tests.Unit.Infrastructure;

public class HttpClientBuilderExtensionsTests : IDisposable
{
    private readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose()
    {
        _server.Dispose();
    }

    [Fact]
    public async Task AddRetryOnRateLimitPolicy_When429ThenSuccess_RetriesAndReturnsSuccess()
    {
        _server.Given(Request.Create().WithPath("/test").UsingGet())
            .InScenario("rate-limit")
            .WillSetStateTo("Retried")
            .RespondWith(Response.Create().WithStatusCode((int)HttpStatusCode.TooManyRequests));

        _server.Given(Request.Create().WithPath("/test").UsingGet())
            .InScenario("rate-limit")
            .WhenStateIs("Retried")
            .RespondWith(Response.Create().WithStatusCode((int)HttpStatusCode.OK).WithBody("ok"));

        var client = BuildClient(attempts: 1, waitTime: TimeSpan.FromMilliseconds(50));

        var response = await client.GetAsync("/test");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        _server.LogEntries.Count().ShouldBe(2);
    }

    [Fact]
    public async Task AddRetryOnRateLimitPolicy_WhenAlways429_ReturnsFinal429AfterAttempts()
    {
        _server.Given(Request.Create().WithPath("/test").UsingGet())
            .RespondWith(Response.Create().WithStatusCode((int)HttpStatusCode.TooManyRequests));

        var client = BuildClient(attempts: 1, waitTime: TimeSpan.FromMilliseconds(50));

        var response = await client.GetAsync("/test");

        response.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        _server.LogEntries.Count().ShouldBe(2);
    }

    [Fact]
    public async Task AddRetryOnRateLimitPolicy_WhenSuccess_DoesNotRetry()
    {
        _server.Given(Request.Create().WithPath("/test").UsingGet())
            .RespondWith(Response.Create().WithStatusCode((int)HttpStatusCode.OK).WithBody("ok"));

        var client = BuildClient(attempts: 1, waitTime: TimeSpan.FromMilliseconds(50));

        var response = await client.GetAsync("/test");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        _server.LogEntries.Count().ShouldBe(1);
    }

    private HttpClient BuildClient(int attempts, TimeSpan waitTime)
    {
        var services = new ServiceCollection();
        services.AddHttpClient("test", c => c.BaseAddress = new Uri(_server.Url!))
            .AddRetryOnRateLimitPolicy(attempts, waitTime);

        var sp = services.BuildServiceProvider();
        return sp.GetRequiredService<IHttpClientFactory>().CreateClient("test");
    }
}
