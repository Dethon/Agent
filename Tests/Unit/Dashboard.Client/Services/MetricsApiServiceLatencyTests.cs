using System.Net;
using System.Text;
using Dashboard.Client.Services;
using Domain.DTOs.Metrics.Enums;
using Shouldly;

namespace Tests.Unit.Dashboard.Client.Services;

public class MetricsApiServiceLatencyTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string LastUri = "";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastUri = request.RequestUri!.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("null", Encoding.UTF8, "application/json")
            });
        }
    }

    [Fact]
    public async Task LatencyClientMethods_CallExpectedRoutes()
    {
        var handler = new CapturingHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var api = new MetricsApiService(http);
        var from = new DateOnly(2026, 3, 1);
        var to = new DateOnly(2026, 3, 2);

        await api.GetLatencyAsync(from, to);
        handler.LastUri.ShouldContain("api/metrics/latency?from=2026-03-01&to=2026-03-02");

        await api.GetLatencyGroupedAsync(LatencyDimension.Stage, LatencyMetric.P95, from, to);
        handler.LastUri.ShouldContain("api/metrics/latency/by/Stage?metric=P95");

        await api.GetLatencyTrendAsync(LatencyMetric.P95, from, to);
        handler.LastUri.ShouldContain("api/metrics/latency/trend?metric=P95");
    }
}