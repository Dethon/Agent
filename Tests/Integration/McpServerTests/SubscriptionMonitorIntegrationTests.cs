using System.Collections.Concurrent;
using System.Text.Json;
using Domain.DTOs;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.McpServerTests;

public class SubscriptionMonitorIntegrationTests(ThreadSessionServerFixture fixture)
    : IClassFixture<ThreadSessionServerFixture>
{
    [Fact]
    public async Task WhenTwoDownloadsComplete_SendsUpdatedForEachUri()
    {
        // Arrange
        var sessionKey = $"MonitorTwoComplete_{Guid.NewGuid()}";
        var id1 = Random.Shared.Next(10000, 90000);
        var id2 = id1 + 1;

        fixture.DownloadClient.SetDownload(id1, DownloadState.InProgress);
        fixture.DownloadClient.SetDownload(id2, DownloadState.InProgress);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var client = await CreateMcpClient(sessionKey, cts.Token);

        await client.CallToolAsync(
            "ResubscribeDownloads",
            new Dictionary<string, object?> { ["downloadIds"] = new[] { id1, id2 } },
            cancellationToken: cts.Token);

        var receivedUris = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var gotBoth = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        client.RegisterNotificationHandler(
            "notifications/resources/updated",
            (notification, _) =>
            {
                var dict = notification.Params?.Deserialize<Dictionary<string, string>>();
                if (dict is null)
                {
                    return ValueTask.CompletedTask;
                }

                dict.TryGetValue("uri", out var uri);
                uri ??= dict.GetValueOrDefault("Uri");

                if (!string.IsNullOrWhiteSpace(uri))
                {
                    receivedUris.TryAdd(uri, 1);
                    if (receivedUris.Count >= 2)
                    {
                        gotBoth.TrySetResult();
                    }
                }

                return ValueTask.CompletedTask;
            });

        var resources = await client.ListResourcesAsync(cancellationToken: cts.Token);
        foreach (var resource in resources)
        {
            await client.SubscribeToResourceAsync(resource.Uri, cancellationToken: cts.Token);
        }

        // Act - complete both downloads (same monitor tick)
        fixture.DownloadClient.SetDownload(id1, DownloadState.Completed, progress: 100);
        fixture.DownloadClient.SetDownload(id2, DownloadState.Completed, progress: 100);

        // Assert
        var completed = await Task.WhenAny(gotBoth.Task, Task.Delay(TimeSpan.FromSeconds(20), cts.Token));
        completed.ShouldBe(gotBoth.Task);

        receivedUris.Keys.ShouldContain($"download://{id1}/");
        receivedUris.Keys.ShouldContain($"download://{id2}/");

        // Ensure we don't get re-notified on the next monitor tick.
        await Task.Delay(TimeSpan.FromSeconds(6), cts.Token);
        receivedUris.Count.ShouldBe(2);
    }

    private async Task<McpClient> CreateMcpClient(string sessionKey, CancellationToken ct)
    {
        return await McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(fixture.McpEndpoint)
            }),
            new McpClientOptions
            {
                ClientInfo = new Implementation
                {
                    Name = sessionKey,
                    Version = "1.0.0"
                }
            },
            cancellationToken: ct);
    }
}