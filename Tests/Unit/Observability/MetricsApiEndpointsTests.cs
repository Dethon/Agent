using System.Net;
using System.Net.Http.Json;
using Domain.DTOs.Metrics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Observability;
using Observability.Services;
using Shouldly;
using StackExchange.Redis;

namespace Tests.Unit.Observability;

public class MetricsApiEndpointsTests
{
    [Fact]
    public async Task LatencyRoutes_AreMappedAndReturnJson()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var redis = new Mock<IConnectionMultiplexer>();
        var db = new Mock<IDatabase>();
        redis.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(db.Object);
        db.Setup(d => d.SortedSetRangeByScoreAsync(It.IsAny<RedisKey>(), It.IsAny<double>(),
                It.IsAny<double>(), It.IsAny<Exclude>(), It.IsAny<Order>(), It.IsAny<long>(),
                It.IsAny<long>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync([]);
        builder.Services.AddSingleton(redis.Object);
        builder.Services.AddSingleton<MetricsQueryService>();
        var app = builder.Build();
        app.MapMetricsApi();
        await app.StartAsync();
        var client = app.GetTestClient();

        try
        {
            var raw = await client.GetAsync("/api/metrics/latency");
            var grouped = await client.GetAsync("/api/metrics/latency/by/Stage?metric=P95");
            var trend = await client.GetAsync("/api/metrics/latency/trend?metric=P95");

            raw.StatusCode.ShouldBe(HttpStatusCode.OK);
            grouped.StatusCode.ShouldBe(HttpStatusCode.OK);
            trend.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await raw.Content.ReadFromJsonAsync<List<LatencyEvent>>()).ShouldNotBeNull();
            (await grouped.Content.ReadFromJsonAsync<Dictionary<string, decimal>>()).ShouldNotBeNull();
            (await trend.Content.ReadFromJsonAsync<List<LatencyTrendSeries>>()).ShouldNotBeNull();
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}