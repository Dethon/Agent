using Mcp.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Shouldly;

namespace Tests.Integration.Channels;

// The error rule has one home, and a server that asks for it twice gets it once. That is the whole
// reason it moved out of AddChannelServer: a dual-role server asks as a tool server and again as a
// channel server, and two filters nested around each other would let the outer one convert the very
// cancellation the inner deliberately rethrows.
//
// What the rule *does* is pinned over the wire by ChannelServerExtensionsTests; these are about
// installing it.
public class CallToolErrorFilterTests
{
    [Fact]
    public void AskingForTheFilterTwice_InstallsItOnce() =>
        CallToolFilterCount(builder => builder
            .AddCallToolErrorFilter(null)
            .AddCallToolErrorFilter(null))
            .ShouldBe(1);

    [Fact]
    public void AChannelServerThatAlsoAsksForTheFilter_GetsOne() =>
        CallToolFilterCount(builder => builder
            .AddCallToolErrorFilter(Marked)
            .AddChannelServer(DeliveryPolicy.Broadcast))
            .ShouldBe(1);

    [Fact]
    public void AChannelServerAlone_StillGetsTheFilter() =>
        CallToolFilterCount(builder => builder.AddChannelServer(DeliveryPolicy.Broadcast))
            .ShouldBe(1);

    private static CallToolResult Marked(Exception ex) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = ex.Message }]
    };

    private static int CallToolFilterCount(Action<IMcpServerBuilder> configure)
    {
        var services = new ServiceCollection();
        configure(services.AddMcpServer());

        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<McpServerOptions>>()
            .Value.Filters.Request.CallToolFilters.Count;
    }
}