using Agent.Modules;
using Domain.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Tests.Unit.Agent;

public class MemoryModuleTests
{
    [Fact]
    public void AddMemory_RegistersCronValidator_RequiredByDreamingService()
    {
        var config = new ConfigurationBuilder().Build();

        var services = new ServiceCollection().AddMemory(config);

        using var provider = services.BuildServiceProvider();
        provider.GetService<ICronValidator>().ShouldNotBeNull();
    }
}