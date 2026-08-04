using Mcp.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Shouldly;

namespace Tests.Integration.McpServers;

// The other half of Mcp.Hosting, beside AddChannelServer: the MCP host every server has, and the
// tool server that is the host plus the error rule. Being a tool server and being a channel server
// are independent facts about a server, which is why they are two calls rather than one with a
// flag — the dual-role servers are genuinely both.
public class ToolServerExtensionsTests
{
    private sealed record ProbeSettings(string Name);

    [Fact]
    public void AddMcpHost_RegistersTheSettingsTheServerAndTheTransport()
    {
        var settings = new ProbeSettings("probe");
        var services = new ServiceCollection();
        services.AddMcpHost(settings);

        services.ShouldContain(
            descriptor => descriptor.ServiceType == typeof(IConfigureOptions<McpServerOptions>));
        services.ShouldContain(
            descriptor => descriptor.ServiceType == typeof(IConfigureOptions<HttpServerTransportOptions>));

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<ProbeSettings>().ShouldBeSameAs(settings);
    }

    // The host on its own carries no error rule: a server that offers the agent nothing to call has
    // nothing to map an exception for.
    [Fact]
    public void AddMcpHost_AddsNoCallToolFilter() =>
        McpServerProbe.CallToolFilterCount(services => services.AddMcpHost(new ProbeSettings("probe")))
            .ShouldBe(0);

    [Fact]
    public void AddToolServer_IsTheHostPlusTheFilter() =>
        McpServerProbe.CallToolFilterCount(services => services.AddToolServer(new ProbeSettings("probe")))
            .ShouldBe(1);

    // A dual-role server asks as a tool server and again as a channel server. Both of the real ones
    // pass the same error shape today, so nothing changes — but the ordering has to be observable
    // rather than assumed.
    [Fact]
    public void ADualRoleServer_EndsUpWithOneFilter() =>
        McpServerProbe.CallToolFilterCount(services => services
            .AddToolServer(new ProbeSettings("probe"), Marked("tool-server"))
            .AddChannelServer(DeliveryPolicy.Broadcast, errorResult: Marked("channel-server")))
            .ShouldBe(1);

    [Fact]
    public async Task AThrowingToolOnAToolServer_ComesBackAsAnErrorResult()
    {
        await using var server = await StartToolServerAsync();

        var result = await server.Client.CallToolAsync("throws");

        result.IsError.ShouldBe(true);
        InMemoryMcpServer.Text(result).ShouldContain("boom");
    }

    // The rule seven tool servers never had. A cancelled fs_exec or web fetch is a call the agent
    // deliberately stopped; coming back as an error result hands its pump something to retry.
    //
    // Asserted through a marked error mapper rather than by expecting a throw: the SDK's own outer
    // handler catches whatever the filter rethrows and answers with a generic message of its own,
    // so "did this exception reach the filter's error path" is the question that can be observed
    // over the wire — and it is the question the rule is about.
    [Fact]
    public async Task ACancelledToolCallOnAToolServer_DoesNotBecomeAnErrorResult()
    {
        await using var server = await StartToolServerAsync(Marked("tool-server"));

        var cancelled = await server.Client.CallToolAsync("cancels");
        var failed = await server.Client.CallToolAsync("throws");

        InMemoryMcpServer.Text(cancelled).ShouldNotContain("tool-server");
        InMemoryMcpServer.Text(failed).ShouldContain("tool-server");
    }

    // The dual-role ordering, over the wire: the tool-server call comes first on both real ones, so
    // its error shape is the one a caller sees.
    [Fact]
    public async Task ADualRoleServer_AnswersWithTheShapeTheFirstCallPassed()
    {
        await using var server = await InMemoryMcpServer.StartAsync(services => services
            .AddToolServer(new ProbeSettings("probe"), Marked("tool-server"))
            .WithTools<FailingTools>()
            .AddChannelServer(DeliveryPolicy.Broadcast, errorResult: Marked("channel-server")));

        var result = await server.Client.CallToolAsync("throws");

        InMemoryMcpServer.Text(result).ShouldContain("tool-server");
    }

    private static Task<RunningServer> StartToolServerAsync(Func<Exception, CallToolResult>? errorResult = null) =>
        InMemoryMcpServer.StartAsync(services => services
            .AddToolServer(new ProbeSettings("probe"), errorResult)
            .WithTools<FailingTools>());

    private static Func<Exception, CallToolResult> Marked(string marker) => ex => new CallToolResult
    {
        IsError = true,
        Content = [new TextContentBlock { Text = $"{marker}: {ex.Message}" }]
    };

}