using Domain.Contracts;
using Domain.DTOs;
using Domain.Tools.Scheduling;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Scheduling;

public class SchedulingToolFeatureTests
{
    private readonly Mock<IScheduleStore> _store = new();
    private readonly Mock<ICronValidator> _cronValidator = new();
    private readonly Mock<IAgentDefinitionProvider> _agentProvider = new();
    private readonly SchedulingToolFeature _feature;

    public SchedulingToolFeatureTests()
    {
        var createTool = new ScheduleCreateTool(_store.Object, _cronValidator.Object, _agentProvider.Object);
        var listTool = new ScheduleListTool(_store.Object);
        var deleteTool = new ScheduleDeleteTool(_store.Object);
        _feature = new SchedulingToolFeature(createTool, listTool, deleteTool, _agentProvider.Object);
    }

    [Fact]
    public void GetTools_ScheduleCreate_DescriptionEnumeratesAvailableAgents()
    {
        var jack = new AgentDefinition
        {
            Id = "jack",
            Name = "Jack",
            Description = "General-purpose home assistant",
            Model = "test",
            McpServerEndpoints = []
        };
        var maid = new AgentDefinition
        {
            Id = "maid",
            Name = "Maid",
            Description = "Cleans the floor",
            Model = "test",
            McpServerEndpoints = []
        };
        _agentProvider.Setup(p => p.GetAll("user-1")).Returns([jack, maid]);

        var tools = _feature.GetTools(new FeatureConfig(UserId: "user-1")).ToList();
        var createFn = tools.Single(t => t.Name.EndsWith(ScheduleCreateTool.Name));

        createFn.Description.ShouldContain("\"jack\"");
        createFn.Description.ShouldContain("Jack");
        createFn.Description.ShouldContain("General-purpose home assistant");
        createFn.Description.ShouldContain("\"maid\"");
        createFn.Description.ShouldContain("Maid");
        createFn.Description.ShouldContain("Cleans the floor");
    }

    [Fact]
    public void GetTools_ScheduleCreate_PassesUserIdToProvider()
    {
        _agentProvider.Setup(p => p.GetAll("user-42")).Returns([]).Verifiable();

        _ = _feature.GetTools(new FeatureConfig(UserId: "user-42")).ToList();

        _agentProvider.Verify(p => p.GetAll("user-42"), Times.AtLeastOnce);
    }
}
