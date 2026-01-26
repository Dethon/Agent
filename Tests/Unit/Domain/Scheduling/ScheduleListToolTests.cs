using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Tools.Scheduling;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Scheduling;

public class ScheduleListToolTests
{
    private readonly Mock<IScheduleStore> _store = new();
    private readonly ScheduleListTool _tool;

    public ScheduleListToolTests()
    {
        _tool = new ScheduleListTool(_store.Object);
    }

    [Fact]
    public async Task Run_ReturnsSchedulesWithUserId()
    {
        var schedules = new List<Schedule>
        {
            new()
            {
                Id = "sched_1",
                Agent = new AgentDefinition
                {
                    Id = "jack",
                    Name = "Jack",
                    Model = "test",
                    McpServerEndpoints = []
                },
                Prompt = "Test prompt",
                CronExpression = "0 9 * * *",
                UserId = "user123",
                CreatedAt = DateTime.UtcNow,
                NextRunAt = DateTime.UtcNow.AddHours(1)
            }
        };
        _store.Setup(s => s.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(schedules);

        var result = await _tool.RunAsync();

        result["count"]?.GetValue<int>().ShouldBe(1);
        var schedulesArray = result["schedules"]?.AsArray();
        schedulesArray.ShouldNotBeNull();
        schedulesArray.Count.ShouldBe(1);

        var first = schedulesArray[0]!.AsObject();
        first["userId"]?.GetValue<string>().ShouldBe("user123");
        first["channel"].ShouldBeNull(); // Channel should not be present
    }

    [Fact]
    public async Task Run_OmitsUserIdWhenNull()
    {
        var schedules = new List<Schedule>
        {
            new()
            {
                Id = "sched_1",
                Agent = new AgentDefinition
                {
                    Id = "jack",
                    Name = "Jack",
                    Model = "test",
                    McpServerEndpoints = []
                },
                Prompt = "Test prompt",
                CronExpression = "0 9 * * *",
                UserId = null,
                CreatedAt = DateTime.UtcNow,
                NextRunAt = DateTime.UtcNow.AddHours(1)
            }
        };
        _store.Setup(s => s.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(schedules);

        var result = await _tool.RunAsync();

        var schedulesArray = result["schedules"]?.AsArray();
        var first = schedulesArray![0]!.AsObject();
        first["userId"].ShouldBeNull(); // Should not be present when null
    }
}
