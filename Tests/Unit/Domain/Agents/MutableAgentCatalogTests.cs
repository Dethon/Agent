using Domain.Agents;
using Domain.DTOs.Channel;
using Shouldly;

namespace Tests.Unit.Domain.Agents;

public class MutableAgentCatalogTests
{
    [Fact]
    public void GetAll_BeforeReplace_ReturnsEmpty()
    {
        var catalog = new MutableAgentCatalog();

        catalog.GetAll().ShouldBeEmpty();
        catalog.Exists("jonas").ShouldBeFalse();
        catalog.Get("jonas").ShouldBeNull();
    }

    [Fact]
    public void Replace_ThenQuery_ReflectsNewAgents()
    {
        var catalog = new MutableAgentCatalog();

        catalog.Replace([new AgentCatalogEntry("jonas", "Jonas", "general")]);

        catalog.GetAll().ShouldHaveSingleItem();
        catalog.Exists("jonas").ShouldBeTrue();
        catalog.Exists("ghost").ShouldBeFalse();
        catalog.Get("jonas")!.Name.ShouldBe("Jonas");
    }

    [Fact]
    public void Replace_CalledTwice_DiscardsPreviousAgents()
    {
        var catalog = new MutableAgentCatalog();

        catalog.Replace([new AgentCatalogEntry("jonas", "Jonas", null)]);
        catalog.Replace([new AgentCatalogEntry("jack", "Jack", null)]);

        catalog.Exists("jonas").ShouldBeFalse();
        catalog.Exists("jack").ShouldBeTrue();
    }

    [Fact]
    public void Replace_SnapshotsInput_LaterMutationOfSourceDoesNotLeak()
    {
        var catalog = new MutableAgentCatalog();
        var source = new List<AgentCatalogEntry> { new("jonas", "Jonas", null) };

        catalog.Replace(source);
        source.Add(new AgentCatalogEntry("jack", "Jack", null));

        catalog.GetAll().ShouldHaveSingleItem();
    }
}