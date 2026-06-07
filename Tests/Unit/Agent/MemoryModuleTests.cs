using Agent.Modules;
using Domain.Contracts;
using Infrastructure.Memory;
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
        // CronValidator must resolve: MemoryDreamingService's ctor depends on it, and the Agent host
        // has no ValidateOnBuild, so a missing registration would only surface at StartAsync.
        provider.GetService<ICronValidator>().ShouldNotBeNull();

        // Guard the rest of the module's registration surface by descriptor presence (not resolution —
        // several of these pull external deps like Redis that this bare-config test deliberately omits),
        // so accidentally dropping the store, recall hook, or either hosted worker is caught here.
        services.ShouldContain(d => d.ServiceType == typeof(IMemoryStore));
        services.ShouldContain(d => d.ServiceType == typeof(IMemoryRecallHook));
        services.ShouldContain(d => d.ImplementationType == typeof(MemoryDreamingService));
        services.ShouldContain(d => d.ImplementationType == typeof(MemoryExtractionWorker));
    }
}