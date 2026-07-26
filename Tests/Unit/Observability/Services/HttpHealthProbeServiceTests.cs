using System.Net;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Observability.Hubs;
using Observability.Services;
using Shouldly;
using StackExchange.Redis;

namespace Tests.Unit.Observability.Services;

public class HttpHealthProbeServiceTests
{
    private readonly Mock<IDatabase> _db = new();
    private readonly Mock<IConnectionMultiplexer> _redis = new();
    private readonly Mock<IHubContext<MetricsHub>> _hubContext = new();
    private readonly Mock<IHubClients> _hubClients = new();
    private readonly Mock<IClientProxy> _clientProxy = new();

    public HttpHealthProbeServiceTests()
    {
        _redis.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(_db.Object);
        _hubClients.Setup(c => c.All).Returns(_clientProxy.Object);
        _hubContext.Setup(h => h.Clients).Returns(_hubClients.Object);
    }

    private sealed class StubHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond());
    }

    private HttpHealthProbeService CreateSut() => new(
        Mock.Of<IHttpClientFactory>(),
        _redis.Object,
        _hubContext.Object,
        new ConfigurationBuilder().Build(),
        NullLogger<HttpHealthProbeService>.Instance);

    private static HttpClient Client(Func<HttpResponseMessage> respond) => new(new StubHandler(respond));

    private int RosterWrites(string service) => _db.Invocations
        .Count(i => i.Method.Name == "SortedSetAddAsync"
            && i.Arguments[0].ToString() == "metrics:health:seen"
            && i.Arguments[1].ToString() == service);

    [Fact]
    public async Task ProbeAsync_TargetUnreachable_StillKeepsServiceOnTheRoster()
    {
        var sut = CreateSut();
        var http = Client(() => throw new HttpRequestException("connection refused"));

        await sut.ProbeAsync(http, _db.Object, "lemonade", "http://lemonade:13305/api/v1/health", CancellationToken.None);

        RosterWrites("lemonade").ShouldBe(1);
        _db.Invocations.ShouldNotContain(i => i.Method.Name == "StringSetAsync");
    }

    [Fact]
    public async Task ProbeAsync_TargetResponds_MarksHealthyAndBroadcasts()
    {
        var sut = CreateSut();
        var http = Client(() => new HttpResponseMessage(HttpStatusCode.OK));

        await sut.ProbeAsync(http, _db.Object, "tse-extractor", "http://tse-extractor:9098/health", CancellationToken.None);

        RosterWrites("tse-extractor").ShouldBe(1);
        _db.Invocations
            .Count(i => i.Method.Name == "StringSetAsync"
                && i.Arguments[0].ToString() == "metrics:health:tse-extractor")
            .ShouldBe(1);
        _clientProxy.Verify(c => c.SendCoreAsync(
            "OnHealthUpdate",
            It.Is<object[]>(args => args.Length == 1),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProbeAsync_TargetRespondsNonSuccess_StillMarksHealthy()
    {
        var sut = CreateSut();
        var http = Client(() => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        await sut.ProbeAsync(http, _db.Object, "lemonade", "http://lemonade:13305/api/v1/health", CancellationToken.None);

        _db.Invocations
            .Count(i => i.Method.Name == "StringSetAsync"
                && i.Arguments[0].ToString() == "metrics:health:lemonade")
            .ShouldBe(1);
    }

    [Fact]
    public async Task ProbeAsync_LegacyRosterKey_IsNeverWritten()
    {
        var sut = CreateSut();
        var http = Client(() => new HttpResponseMessage(HttpStatusCode.OK));

        await sut.ProbeAsync(http, _db.Object, "lemonade", "http://lemonade:13305/api/v1/health", CancellationToken.None);

        _db.Invocations.ShouldNotContain(i => i.Method.Name == "SetAddAsync");
    }
}