using System.Text.Json;
using Domain.DTOs;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.McpTools;

public class ResubscribeDownloadsToolTests(ThreadSessionServerFixture fixture)
    : IClassFixture<ThreadSessionServerFixture>
{
    [Fact]
    public async Task ResubscribeDownloads_WithMixedDownloads_ReturnsCorrectStatuses()
    {
        // Arrange
        var sessionKey = $"ResubscribeMixed_{Guid.NewGuid()}";
        var baseId = Random.Shared.Next(10000, 90000);
        var inProgressId = baseId;
        var completedId = baseId + 1;
        var trackedId = baseId + 2;
        var notFoundId = baseId + 3;

        fixture.DownloadClient.SetDownload(inProgressId, DownloadState.InProgress); // Will resubscribe
        fixture.DownloadClient.SetDownload(completedId, DownloadState.Completed); // Already completed
        fixture.DownloadClient.SetDownload(trackedId, DownloadState.InProgress); // Will be tracked first
        // notFoundId doesn't exist - not found

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var client = await CreateMcpClient(sessionKey, cts.Token);

        // First call to establish tracking for trackedId
        await client.CallToolAsync(
            "download_resubscribe",
            new Dictionary<string, object?> { ["downloadIds"] = new[] { trackedId } },
            cancellationToken: cts.Token);

        // Act - Now call with all IDs including the one already tracked
        var result = await client.CallToolAsync(
            "download_resubscribe",
            new Dictionary<string, object?>
            { ["downloadIds"] = new[] { inProgressId, completedId, trackedId, notFoundId } },
            cancellationToken: cts.Token);

        // Assert
        var response = ParseResponse(result);
        var summary = response.GetProperty("summary");
        summary.GetProperty("resubscribed").GetInt32().ShouldBe(1);
        summary.GetProperty("needsAttention").GetInt32().ShouldBe(2); // completed + not found
        summary.GetProperty("alreadyTracked").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task ResubscribeDownloads_AddsToTrackedDownloadsManager()
    {
        // Arrange
        var sessionKey = $"ResubscribeAddsTracking_{Guid.NewGuid()}";
        var downloadId = Random.Shared.Next(10000, 99999);
        fixture.DownloadClient.SetDownload(downloadId, DownloadState.InProgress);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var client = await CreateMcpClient(sessionKey, cts.Token);

        // Act - First call should resubscribe
        var firstResult = await client.CallToolAsync(
            "download_resubscribe",
            new Dictionary<string, object?> { ["downloadIds"] = new[] { downloadId } },
            cancellationToken: cts.Token);

        var firstResponse = ParseResponse(firstResult);
        firstResponse.GetProperty("summary").GetProperty("resubscribed").GetInt32().ShouldBe(1);

        // Assert - Second call should show it's already tracked (proving it was added)
        var secondResult = await client.CallToolAsync(
            "download_resubscribe",
            new Dictionary<string, object?> { ["downloadIds"] = new[] { downloadId } },
            cancellationToken: cts.Token);

        var secondResponse = ParseResponse(secondResult);
        secondResponse.GetProperty("summary").GetProperty("alreadyTracked").GetInt32().ShouldBe(1);
    }

    private async Task<McpClient> CreateMcpClient(string sessionKey, CancellationToken ct)
    {
        var client = await McpClient.CreateAsync(
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

        return client;
    }

    private static JsonElement ParseResponse(CallToolResult result)
    {
        var text = result.Content.OfType<TextContentBlock>().First().Text;
        return JsonDocument.Parse(text).RootElement;
    }
}