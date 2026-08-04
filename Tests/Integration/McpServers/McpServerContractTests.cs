using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Server;
using Shouldly;

namespace Tests.Integration.McpServers;

// What every MCP server in the repo must have, however it is built: its own settings available to
// everything it registers, a server, an HTTP transport, and one call-tool filter. Thirteen rows,
// each driving the ConfigModule that ships.
//
// Written as comparisons across servers rather than assertions about one, because the drift this
// pins was invisible precisely because nothing compared them: two servers disagreed about how
// configuration binds, five read a config source they do not have, and seven filters were missing
// the cancellation rule the other six treat as load-bearing.
public class McpServerContractTests
{
    public static TheoryData<string> Servers =>
        McpServerRegistrations.All.Aggregate(
            new TheoryData<string>(),
            (data, row) =>
            {
                data.Add(row.Id);
                return data;
            });

    // Everything the module registers reads its settings out of the container, so a server that
    // resolves a second instance would hand two halves of itself two different configurations.
    [Theory]
    [MemberData(nameof(Servers))]
    public void EveryServer_ResolvesItsSettingsAsASingleton(string id)
    {
        var row = McpServerRegistrations.Get(id);
        using var provider = Build(row);

        var settings = provider.GetService(row.Settings.GetType());

        settings.ShouldBeSameAs(row.Settings, $"{id} must register the settings it was handed");
        provider.GetService(row.Settings.GetType())
            .ShouldBeSameAs(settings, $"{id} must register its settings as a singleton");
    }

    [Theory]
    [MemberData(nameof(Servers))]
    public void EveryServer_RegistersTheMcpHost(string id)
    {
        var row = McpServerRegistrations.Get(id);
        var services = new ServiceCollection();
        row.Configure(services);

        // The transport is asserted on the descriptor rather than by resolving it: its options bind
        // to defaults whether or not anyone asked for the transport, so only the registration says
        // whether the module added it.
        services.ShouldContain(
            descriptor => descriptor.ServiceType == typeof(IConfigureOptions<HttpServerTransportOptions>),
            $"{id} must add the HTTP transport");

        using var provider = services.BuildServiceProvider();
        provider.GetService<IOptions<McpServerOptions>>()
            .ShouldNotBeNull($"{id} must start an MCP server");
    }

    // Two filters nested around each other is the failure this counts: the outer one converts the
    // very cancellation the inner deliberately rethrows, so a cancelled call comes back as an error
    // result the agent's pump will retry.
    [Theory]
    [MemberData(nameof(Servers))]
    public void EveryServer_HasExactlyOneCallToolFilter(string id)
    {
        var row = McpServerRegistrations.Get(id);
        using var provider = Build(row);

        var options = provider.GetRequiredService<IOptions<McpServerOptions>>().Value;

        options.Filters.Request.CallToolFilters.Count
            .ShouldBe(1, $"{id} must have exactly one call-tool filter");
    }

    private static ServiceProvider Build(McpServerRow row)
    {
        var services = new ServiceCollection();
        row.Configure(services);
        return services.BuildServiceProvider();
    }
}