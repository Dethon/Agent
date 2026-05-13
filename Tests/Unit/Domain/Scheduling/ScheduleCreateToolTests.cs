using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Tools.Scheduling;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Scheduling;

public class ScheduleCreateToolTests
{
    private readonly Mock<IScheduleStore> _store = new();
    private readonly Mock<ICronValidator> _cronValidator = new();
    private readonly Mock<IAgentDefinitionProvider> _agentProvider = new();
    private readonly TestableScheduleCreateTool _tool;

    public ScheduleCreateToolTests()
    {
        _tool = new TestableScheduleCreateTool(_store.Object, _cronValidator.Object, _agentProvider.Object);
    }

    [Fact]
    public async Task Run_InputValidationErrors_ReturnExpectedMessages()
    {
        // Missing agent id
        var missing = await _tool.TestRun("", "prompt", "0 9 * * *", null, null);
        missing["ok"]!.GetValue<bool>().ShouldBeFalse();
        missing["errorCode"]!.GetValue<string>().ShouldBe("invalid_argument");
        missing["message"]!.GetValue<string>().ShouldBe("agentId is required");

        // Neither cron nor runAt
        var neither = await _tool.TestRun("jack", "prompt", null, null, null);
        neither["errorCode"]!.GetValue<string>().ShouldBe("invalid_argument");
        neither["message"]!.GetValue<string>().ShouldBe("Either cronExpression or runAt must be provided");

        // Both cron and runAt
        var both = await _tool.TestRun("jack", "prompt", "0 9 * * *", DateTime.UtcNow.AddDays(1), null);
        both["errorCode"]!.GetValue<string>().ShouldBe("invalid_argument");
        both["message"]!.GetValue<string>().ShouldBe("Provide only cronExpression OR runAt, not both");

        // Invalid cron expression
        _cronValidator.Setup(v => v.IsValid("invalid")).Returns(false);
        var invalidCron = await _tool.TestRun("jack", "prompt", "invalid", null, null);
        invalidCron["errorCode"]!.GetValue<string>().ShouldBe("invalid_argument");
        invalidCron["message"]!.GetValue<string>().ShouldContain("Invalid cron expression");

        // RunAt in the past
        var past = await _tool.TestRun("jack", "prompt", null, DateTime.UtcNow.AddHours(-1), null);
        past["errorCode"]!.GetValue<string>().ShouldBe("invalid_argument");
        past["message"]!.GetValue<string>().ShouldBe("runAt must be in the future");
    }

    [Fact]
    public async Task Run_AgentNotFound_ReturnsError()
    {
        _agentProvider.Setup(p => p.GetById("unknown")).Returns((AgentDefinition?)null);
        _agentProvider.Setup(p => p.GetAll(It.IsAny<string?>())).Returns([]);

        var result = await _tool.TestRun("unknown", "prompt", null, DateTime.UtcNow.AddDays(1), null);

        result["ok"]!.GetValue<bool>().ShouldBeFalse();
        result["errorCode"]!.GetValue<string>().ShouldBe("not_found");
        result["message"]!.GetValue<string>().ShouldContain("Agent 'unknown' not found");
    }

    [Fact]
    public async Task Run_AgentNotFound_ErrorMessageListsAvailableAgentIds()
    {
        var jack = new AgentDefinition { Id = "jack", Name = "Jack", Model = "test", McpServerEndpoints = [] };
        var maid = new AgentDefinition { Id = "maid", Name = "Maid", Model = "test", McpServerEndpoints = [] };
        _agentProvider.Setup(p => p.GetById("ghost")).Returns((AgentDefinition?)null);
        _agentProvider.Setup(p => p.GetAll("user-1")).Returns([jack, maid]);

        var result = await _tool.TestRun("ghost", "prompt", null, DateTime.UtcNow.AddDays(1), "user-1");

        var message = result["message"]!.GetValue<string>();
        message.ShouldContain("jack");
        message.ShouldContain("maid");
    }

    [Fact]
    public async Task Run_ValidOneShot_CreatesSchedule()
    {
        var agent = new AgentDefinition
        {
            Id = "jack",
            Name = "Jack",
            Model = "test",
            McpServerEndpoints = []
        };
        _agentProvider.Setup(p => p.GetById("jack")).Returns(agent);
        _store.Setup(s => s.CreateAsync(It.IsAny<Schedule>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Schedule s, CancellationToken _) => s);

        var runAt = DateTime.UtcNow.AddDays(1);
        var result = await _tool.TestRun("jack", "test prompt", null, runAt, null);

        result["status"]?.GetValue<string>().ShouldBe("created");
        result["agentName"]?.GetValue<string>().ShouldBe("Jack");
        _store.Verify(s => s.CreateAsync(It.Is<Schedule>(sch =>
            sch.Agent.Id == "jack" &&
            sch.Prompt == "test prompt" &&
            sch.RunAt == runAt &&
            sch.UserId == null), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_ValidCron_CreatesSchedule()
    {
        var agent = new AgentDefinition
        {
            Id = "jack",
            Name = "Jack",
            Model = "test",
            McpServerEndpoints = []
        };
        var nextRun = DateTime.UtcNow.AddHours(1);
        _agentProvider.Setup(p => p.GetById("jack")).Returns(agent);
        _cronValidator.Setup(v => v.IsValid("0 9 * * *")).Returns(true);
        _cronValidator.Setup(v => v.GetNextOccurrence("0 9 * * *", It.IsAny<DateTime>())).Returns(nextRun);
        _store.Setup(s => s.CreateAsync(It.IsAny<Schedule>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Schedule s, CancellationToken _) => s);

        var result = await _tool.TestRun("jack", "test prompt", "0 9 * * *", null, "user1");

        result["status"]?.GetValue<string>().ShouldBe("created");
        _store.Verify(s => s.CreateAsync(It.Is<Schedule>(sch =>
            sch.CronExpression == "0 9 * * *" &&
            sch.NextRunAt == nextRun &&
            sch.UserId == "user1"), It.IsAny<CancellationToken>()), Times.Once);
    }

    private class TestableScheduleCreateTool(
        IScheduleStore store,
        ICronValidator cronValidator,
        IAgentDefinitionProvider agentProvider)
        : ScheduleCreateTool(store, cronValidator, agentProvider)
    {
        public Task<JsonNode> TestRun(
            string agentId,
            string prompt,
            string? cronExpression,
            DateTime? runAt,
            string? userId)
        {
            return RunAsync(agentId, prompt, cronExpression, runAt, userId);
        }
    }
}