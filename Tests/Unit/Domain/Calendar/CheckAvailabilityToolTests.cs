using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Tools.Calendar;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Calendar;

public class CheckAvailabilityToolTests
{
    private readonly Mock<ICalendarProvider> _providerMock = new();
    private readonly TestableCheckAvailabilityTool _tool;

    private static readonly DateTimeOffset StartDate = new(2026, 3, 15, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset EndDate = new(2026, 3, 15, 18, 0, 0, TimeSpan.Zero);

    public CheckAvailabilityToolTests()
    {
        _tool = new TestableCheckAvailabilityTool(_providerMock.Object);
    }

    [Fact]
    public async Task Run_PassesDateRangeToProvider()
    {
        _providerMock.Setup(p => p.CheckAvailabilityAsync("token", StartDate, EndDate, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<FreeBusySlot>());

        await _tool.InvokeRun("token", StartDate, EndDate);

        _providerMock.Verify(p => p.CheckAvailabilityAsync("token", StartDate, EndDate, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_ReturnsSlotsAsJsonArray()
    {
        var slots = new List<FreeBusySlot>
        {
            new()
            {
                Start = StartDate,
                End = StartDate.AddHours(1),
                Status = FreeBusyStatus.Busy
            },
            new()
            {
                Start = StartDate.AddHours(1),
                End = StartDate.AddHours(2),
                Status = FreeBusyStatus.Free
            }
        };
        _providerMock.Setup(p => p.CheckAvailabilityAsync("token", StartDate, EndDate, It.IsAny<CancellationToken>()))
            .ReturnsAsync(slots);

        var result = await _tool.InvokeRun("token", StartDate, EndDate);

        var array = result.AsArray();
        array.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Run_MapsFreeBusySlotFieldsToJson()
    {
        var slotStart = new DateTimeOffset(2026, 3, 15, 9, 0, 0, TimeSpan.Zero);
        var slotEnd = new DateTimeOffset(2026, 3, 15, 10, 0, 0, TimeSpan.Zero);
        var slots = new List<FreeBusySlot>
        {
            new() { Start = slotStart, End = slotEnd, Status = FreeBusyStatus.Busy }
        };
        _providerMock.Setup(p => p.CheckAvailabilityAsync("token", StartDate, EndDate, It.IsAny<CancellationToken>()))
            .ReturnsAsync(slots);

        var result = await _tool.InvokeRun("token", StartDate, EndDate);

        var slot = result.AsArray()[0]!;
        slot["start"].ShouldNotBeNull();
        slot["end"].ShouldNotBeNull();
        slot["status"]!.GetValue<string>().ShouldBe("Busy");
    }

    [Fact]
    public async Task Run_MapsAllFreeBusyStatusValues()
    {
        var slots = new List<FreeBusySlot>
        {
            new() { Start = StartDate, End = StartDate.AddHours(1), Status = FreeBusyStatus.Free },
            new() { Start = StartDate.AddHours(1), End = StartDate.AddHours(2), Status = FreeBusyStatus.Tentative },
            new() { Start = StartDate.AddHours(2), End = StartDate.AddHours(3), Status = FreeBusyStatus.OutOfOffice }
        };
        _providerMock.Setup(p => p.CheckAvailabilityAsync("token", StartDate, EndDate, It.IsAny<CancellationToken>()))
            .ReturnsAsync(slots);

        var result = await _tool.InvokeRun("token", StartDate, EndDate);

        var array = result.AsArray();
        array[0]!["status"]!.GetValue<string>().ShouldBe("Free");
        array[1]!["status"]!.GetValue<string>().ShouldBe("Tentative");
        array[2]!["status"]!.GetValue<string>().ShouldBe("OutOfOffice");
    }

    [Fact]
    public async Task Run_WhenNoSlots_ReturnsEmptyArray()
    {
        _providerMock.Setup(p => p.CheckAvailabilityAsync("token", StartDate, EndDate, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<FreeBusySlot>());

        var result = await _tool.InvokeRun("token", StartDate, EndDate);

        result.AsArray().Count.ShouldBe(0);
    }

    [Fact]
    public void HasExpectedNameAndDescription()
    {
        CheckAvailabilityTool.ToolName.ShouldNotBeNullOrWhiteSpace();
        CheckAvailabilityTool.ToolDescription.ShouldNotBeNullOrWhiteSpace();
    }
}

internal class TestableCheckAvailabilityTool(ICalendarProvider provider) : CheckAvailabilityTool(provider)
{
    public Task<JsonNode> InvokeRun(
        string accessToken,
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken ct = default)
        => Run(accessToken, start, end, ct);
}
