using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Tools.Calendar;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Calendar;

public class EventListToolTests
{
    private readonly Mock<ICalendarProvider> _providerMock = new();
    private readonly TestableEventListTool _tool;

    private static readonly DateTimeOffset StartDate = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset EndDate = new(2026, 3, 31, 23, 59, 59, TimeSpan.Zero);

    public EventListToolTests()
    {
        _tool = new TestableEventListTool(_providerMock.Object);
    }

    [Fact]
    public async Task Run_WithCalendarId_PassesCalendarIdToProvider()
    {
        _providerMock.Setup(p => p.ListEventsAsync("token", "cal-1", StartDate, EndDate, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<CalendarEvent>());

        await _tool.InvokeRun("token", StartDate, EndDate, "cal-1");

        _providerMock.Verify(p => p.ListEventsAsync("token", "cal-1", StartDate, EndDate, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_WithoutCalendarId_PassesNullToProvider()
    {
        _providerMock.Setup(p => p.ListEventsAsync("token", null, StartDate, EndDate, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<CalendarEvent>());

        await _tool.InvokeRun("token", StartDate, EndDate);

        _providerMock.Verify(p => p.ListEventsAsync("token", null, StartDate, EndDate, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_ReturnsEventsAsJsonArray()
    {
        var events = new List<CalendarEvent>
        {
            new()
            {
                Id = "evt-1",
                Subject = "Team Standup",
                Start = StartDate.AddHours(9),
                End = StartDate.AddHours(9).AddMinutes(30),
                CalendarId = "cal-1",
                Location = "Room A",
                IsAllDay = false,
                Attendees = ["alice@example.com", "bob@example.com"],
                Organizer = "alice@example.com",
                Status = "accepted"
            }
        };
        _providerMock.Setup(p => p.ListEventsAsync("token", null, StartDate, EndDate, It.IsAny<CancellationToken>()))
            .ReturnsAsync(events);

        var result = await _tool.InvokeRun("token", StartDate, EndDate);

        var array = result.AsArray();
        array.Count.ShouldBe(1);
        array[0]!["id"]!.GetValue<string>().ShouldBe("evt-1");
        array[0]!["subject"]!.GetValue<string>().ShouldBe("Team Standup");
        array[0]!["location"]!.GetValue<string>().ShouldBe("Room A");
        array[0]!["isAllDay"]!.GetValue<bool>().ShouldBeFalse();
    }

    [Fact]
    public async Task Run_WhenNoEvents_ReturnsEmptyArray()
    {
        _providerMock.Setup(p => p.ListEventsAsync("token", null, StartDate, EndDate, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<CalendarEvent>());

        var result = await _tool.InvokeRun("token", StartDate, EndDate);

        result.AsArray().Count.ShouldBe(0);
    }

    [Fact]
    public async Task Run_PassesStartAndEndDatesToProvider()
    {
        var start = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2026, 6, 30, 23, 59, 59, TimeSpan.Zero);
        _providerMock.Setup(p => p.ListEventsAsync("token", null, start, end, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<CalendarEvent>());

        await _tool.InvokeRun("token", start, end);

        _providerMock.Verify(p => p.ListEventsAsync("token", null, start, end, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void HasExpectedNameAndDescription()
    {
        EventListTool.ToolName.ShouldNotBeNullOrWhiteSpace();
        EventListTool.ToolDescription.ShouldNotBeNullOrWhiteSpace();
    }
}

internal class TestableEventListTool(ICalendarProvider provider) : EventListTool(provider)
{
    public Task<JsonNode> InvokeRun(
        string accessToken,
        DateTimeOffset start,
        DateTimeOffset end,
        string? calendarId = null,
        CancellationToken ct = default)
        => Run(accessToken, start, end, calendarId, ct);
}
