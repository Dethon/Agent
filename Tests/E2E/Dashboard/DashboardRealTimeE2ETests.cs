using System.Text.Json;
using Domain.DTOs.Metrics;
using Microsoft.Playwright;
using Shouldly;
using StackExchange.Redis;
using Tests.E2E.Fixtures;

namespace Tests.E2E.Dashboard;

[Collection("DashboardE2E")]
[Trait("Category", "E2E")]
public class DashboardRealTimeE2ETests(DashboardE2EFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [Fact]
    public async Task LiveMetrics_UpdateWithoutRefresh()
    {
        var page = await fixture.CreatePageAsync();
        await page.GotoAsync(fixture.DashboardUrl, new() { WaitUntil = WaitUntilState.NetworkIdle });

        // Wait for SignalR connection (Live badge)
        await page.Locator(".connection-status.connected").WaitForAsync(new() { Timeout = 30_000 });

        // Read current KPI values
        var inputTokensCard = page.Locator(".kpi-card").Filter(new() { HasText = "Input Tokens" });
        var initialValue = await inputTokensCard.Locator(".kpi-value").TextContentAsync();

        // Publish a token usage event to Redis
        using var redis = await ConnectionMultiplexer.ConnectAsync(fixture.RedisConnectionString);
        var subscriber = redis.GetSubscriber();
        var channel = RedisChannel.Literal("metrics:events");

        var tokenEvent = new TokenUsageEvent
        {
            Sender = "e2e-test",
            Model = "test-model",
            InputTokens = 1000,
            OutputTokens = 500,
            Cost = 0.01m,
            AgentId = "test-agent"
        };
        var json = JsonSerializer.Serialize<MetricEvent>(tokenEvent, JsonOptions);
        await subscriber.PublishAsync(channel, json);

        // Wait for the KPI value to change (SignalR push, no page refresh)
        await page.WaitForFunctionAsync(
            $$"""
            () => {
                const cards = document.querySelectorAll('.kpi-card');
                for (const card of cards) {
                    const label = card.querySelector('.kpi-label');
                    const value = card.querySelector('.kpi-value');
                    if (label && label.textContent.includes('Input Tokens') && value && value.textContent !== '{{initialValue}}') {
                        return true;
                    }
                }
                return false;
            }
            """,
            null,
            new() { Timeout = 30_000 });

        var updatedValue = await inputTokensCard.Locator(".kpi-value").TextContentAsync();
        updatedValue.ShouldNotBe(initialValue);
    }

    [Fact]
    public async Task HealthGrid_ReflectsServiceStatus()
    {
        var page = await fixture.CreatePageAsync();
        await page.GotoAsync(fixture.DashboardUrl, new() { WaitUntil = WaitUntilState.NetworkIdle });

        // Wait for SignalR connection
        await page.Locator(".connection-status.connected").WaitForAsync(new() { Timeout = 30_000 });

        // Publish a heartbeat event
        using var redis = await ConnectionMultiplexer.ConnectAsync(fixture.RedisConnectionString);
        var subscriber = redis.GetSubscriber();
        var channel = RedisChannel.Literal("metrics:events");

        var heartbeat = new HeartbeatEvent
        {
            Service = "e2e-test-service",
            AgentId = "test-agent"
        };
        var json = JsonSerializer.Serialize<MetricEvent>(heartbeat, JsonOptions);
        await subscriber.PublishAsync(channel, json);

        // Wait for the health grid to show the new service
        var serviceEntry = page.Locator(".health-grid .health-name").Filter(new() { HasText = "e2e-test-service" });
        await serviceEntry.WaitForAsync(new() { Timeout = 30_000 });
        (await serviceEntry.IsVisibleAsync()).ShouldBeTrue();
    }
}
