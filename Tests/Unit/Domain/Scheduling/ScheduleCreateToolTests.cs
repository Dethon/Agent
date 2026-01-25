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
    public async Task Run_MissingAgentId_ReturnsError()
    {
        var result = await _tool.TestRun("", "prompt", "0 9 * * *", null, "telegram", 123, null, null, null);

        result["error"]?.GetValue<string>().ShouldBe("agentId is required");
    }

    [Fact]
    public async Task Run_NeitherCronNorRunAt_ReturnsError()
    {
        var result = await _tool.TestRun("jack", "prompt", null, null, "telegram", 123, null, null, null);

        result["error"]?.GetValue<string>().ShouldBe("Either cronExpression or runAt must be provided");
    }

    [Fact]
    public async Task Run_BothCronAndRunAt_ReturnsError()
    {
        var result = await _tool.TestRun("jack", "prompt", "0 9 * * *", DateTime.UtcNow.AddDays(1), "telegram", 123,
            null, null, null);

        result["error"]?.GetValue<string>().ShouldBe("Provide only cronExpression OR runAt, not both");
    }

    [Fact]
    public async Task Run_InvalidCron_ReturnsError()
    {
        _cronValidator.Setup(v => v.IsValid("invalid")).Returns(false);

        var result = await _tool.TestRun("jack", "prompt", "invalid", null, "telegram", 123, null, null, null);

        result["error"]?.GetValue<string>().ShouldContain("Invalid cron expression");
    }

    [Fact]
    public async Task Run_RunAtInPast_ReturnsError()
    {
        var result = await _tool.TestRun("jack", "prompt", null, DateTime.UtcNow.AddHours(-1), "telegram", 123, null,
            null, null);

        result["error"]?.GetValue<string>().ShouldBe("runAt must be in the future");
    }

    [Fact]
    public async Task Run_InvalidChannel_ReturnsError()
    {
        var result = await _tool.TestRun("jack", "prompt", null, DateTime.UtcNow.AddDays(1), "invalid", 123, null, null,
            null);

        result["error"]?.GetValue<string>().ShouldBe("channel must be 'telegram' or 'webchat'");
    }

    [Fact]
    public async Task Run_AgentNotFound_ReturnsError()
    {
        _agentProvider.Setup(p => p.GetById("unknown")).Returns((AgentDefinition?)null);

        var result = await _tool.TestRun("unknown", "prompt", null, DateTime.UtcNow.AddDays(1), "telegram", 123, null,
            null, null);

        result["error"]?.GetValue<string>().ShouldBe("Agent 'unknown' not found");
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
        var result = await _tool.TestRun("jack", "test prompt", null, runAt, "telegram", 123, null, null, null);

        result["status"]?.GetValue<string>().ShouldBe("created");
        result["agentName"]?.GetValue<string>().ShouldBe("Jack");
        _store.Verify(s => s.CreateAsync(It.Is<Schedule>(sch =>
            sch.Agent.Id == "jack" &&
            sch.Prompt == "test prompt" &&
            sch.RunAt == runAt), It.IsAny<CancellationToken>()), Times.Once);
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

        var result =
            await _tool.TestRun("jack", "test prompt", "0 9 * * *", null, "webchat", null, null, "user1", null);

        result["status"]?.GetValue<string>().ShouldBe("created");
        _store.Verify(s => s.CreateAsync(It.Is<Schedule>(sch =>
            sch.CronExpression == "0 9 * * *" &&
            sch.NextRunAt == nextRun), It.IsAny<CancellationToken>()), Times.Once);
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
            string channel,
            long? chatId,
            long? threadId,
            string? userId,
            string? targetAgentId)
        {
            return RunAsync(agentId, prompt, cronExpression, runAt, channel, chatId, threadId, userId, targetAgentId);
        }
    }
}