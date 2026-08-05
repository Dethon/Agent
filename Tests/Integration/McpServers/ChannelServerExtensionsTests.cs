using Mcp.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Tests.Integration.McpServers;

// What being a channel server means, asked of the call itself rather than of a server that made it.
public class ChannelServerExtensionsTests
{
    private sealed record ProbeSettings(string Name);

    // A channel server has one inbox and one delivery policy. The call-tool filter already guards
    // itself, so a second call used to leave the filter alone and silently swap the emitter — last
    // policy wins, and a server that meant Broadcast would start gating on live subscribers. Today
    // it only fails incidentally, on the duplicate channel_receive tool name, which says nothing
    // about the policy that was lost.
    [Fact]
    public void AddChannelServer_CalledTwice_FailsInsteadOfSwappingThePolicy()
    {
        var builder = new ServiceCollection().AddMcpHost(new ProbeSettings("probe"));
        builder.AddChannelServer(DeliveryPolicy.Broadcast);

        Should.Throw<InvalidOperationException>(
                () => builder.AddChannelServer(DeliveryPolicy.GateOnLive))
            .Message.ShouldContain("AddChannelServer");
    }
}