using System.Collections.Concurrent;
using System.Net;
using Domain.Contracts;
using Domain.DTOs;
using McpServerWebSearch.Modules;
using McpServerWebSearch.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Client;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.McpServerTests;

public class McpWebSearchSessionCleanupTests
{
    private const string McpPath = "/mcp";

    [Fact]
    public async Task ClientDispose_TriggersBrowserCloseSession()
    {
        var fakeBrowser = new RecordingFakeWebBrowser();
        var port = TestPort.GetAvailable();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, port));

        var settings = new McpSettings
        {
            BraveSearch = new BraveSearchConfiguration { ApiKey = "test" },
            Camoufox = null,
            CapSolver = null
        };
        builder.Services.ConfigureMcp(settings);
        builder.Services.RemoveAll<IWebBrowser>();
        builder.Services.AddSingleton<IWebBrowser>(fakeBrowser);

        var app = builder.Build();
        app.UseBrowserSessionCleanupOnMcpDelete(McpPath);
        app.MapMcp(McpPath);

        await app.StartAsync();
        try
        {
            var endpoint = $"http://localhost:{port}{McpPath}";

            string? sessionId;
            await using (var client = await McpClient.CreateAsync(
                new HttpClientTransport(new HttpClientTransportOptions
                {
                    Endpoint = new Uri(endpoint)
                })))
            {
                // Force at least one round-trip so the server-assigned session ID is established
                await client.ListToolsAsync();
                sessionId = client.SessionId;
            }

            // After client dispose, the MCP client sends DELETE /mcp; the middleware
            // should call IWebBrowser.CloseSessionAsync with the session id.
            await WaitUntil(
                () => fakeBrowser.ClosedSessionIds.Count > 0,
                timeout: TimeSpan.FromSeconds(5));

            sessionId.ShouldNotBeNullOrEmpty();
            fakeBrowser.ClosedSessionIds.ShouldContain(sessionId!);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private static async Task WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(50);
        }

        if (!condition())
        {
            throw new TimeoutException($"Condition not met within {timeout}");
        }
    }

    private sealed class RecordingFakeWebBrowser : IWebBrowser
    {
        public ConcurrentBag<string> ClosedSessionIds { get; } = [];

        public Task<BrowseResult> NavigateAsync(BrowseRequest request, CancellationToken ct = default)
            => Task.FromResult(new BrowseResult(
                request.SessionId, request.Url, BrowseStatus.Success,
                null, null, 0, false, null, null, null, null));

        public Task<BrowseResult> GetCurrentPageAsync(string sessionId, CancellationToken ct = default)
            => Task.FromResult(new BrowseResult(
                sessionId, "", BrowseStatus.SessionNotFound,
                null, null, 0, false, null, null, null, null));

        public Task<SnapshotResult> SnapshotAsync(SnapshotRequest request, CancellationToken ct = default)
            => Task.FromResult(new SnapshotResult(request.SessionId, null, null, 0, null));

        public Task<WebActionResult> ActionAsync(WebActionRequest request, CancellationToken ct = default)
            => Task.FromResult(new WebActionResult(
                request.SessionId, WebActionStatus.Success, null, false, null, null, null));

        public Task CloseSessionAsync(string sessionId, CancellationToken ct = default)
        {
            ClosedSessionIds.Add(sessionId);
            return Task.CompletedTask;
        }
    }
}