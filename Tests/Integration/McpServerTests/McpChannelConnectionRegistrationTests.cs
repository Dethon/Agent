using Domain.DTOs.Channel;
using Infrastructure.Clients.Channels;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.McpServerTests;

public class McpChannelConnectionRegistrationTests
{
    public class AgainstScheduling(McpSchedulingServerFixture fixture)
        : IClassFixture<McpSchedulingServerFixture>
    {
        [Fact]
        public async Task RegisterAgentsAsync_PopulatesCatalog_OnServerWithTool()
        {
            var conn = new McpChannelConnection("scheduling");
            await conn.ConnectAsync(fixture.McpEndpoint, CancellationToken.None);

            await conn.RegisterAgentsAsync(
                [
                    new AgentCatalogEntry("jonas", "Jonas", "general"),
                    new AgentCatalogEntry("zeta", "Zeta", "extra")
                ],
                CancellationToken.None);

            var client = await McpClient.CreateAsync(
                new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri(fixture.McpEndpoint) }),
                cancellationToken: CancellationToken.None);

            var create = await client.CallToolAsync(
                "fs_create",
                new Dictionary<string, object?>
                {
                    ["path"] = "/zeta/itest-conn/schedule.json",
                    ["content"] = """{"prompt":"hi","cron":"0 6 * * *"}"""
                },
                cancellationToken: CancellationToken.None);

            (create.IsError ?? false).ShouldBeFalse();

            await client.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    public class AgainstServerWithoutTool(McpVaultServerFixture fixture)
        : IClassFixture<McpVaultServerFixture>
    {
        [Fact]
        public async Task RegisterAgentsAsync_OnServerWithoutTool_DoesNotThrow()
        {
            var conn = new McpChannelConnection("vault");
            await conn.ConnectAsync(fixture.McpEndpoint, CancellationToken.None);

            await Should.NotThrowAsync(() =>
                conn.RegisterAgentsAsync([new AgentCatalogEntry("jonas", "Jonas", null)], CancellationToken.None));

            await conn.DisposeAsync();
        }
    }
}