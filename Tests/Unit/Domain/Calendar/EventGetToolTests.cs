using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Tools.Calendar;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Calendar;

public class EventGetToolTests
{
    private readonly Mock<ICalendarProvider> _providerMock = new();
    private readonly TestableEventGetTool _tool;

    public EventGetToolTests()
    {
        _tool = new TestableEventGetTool(_providerMock.Object);
    }

    [Fact]
    public async Task Run_CallsProviderWithCorrectEventIdAndCalendarId()
    {
        var calendarEvent = MakeEvent("evt-1", "Meeting");
        _providerMock.Setup(p => p.GetEventAsync("token", "evt-1", "cal-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(calendarEvent);

        await _tool.InvokeRun("token", "evt-1", "cal-1");

        _providerMock.Verify(p => p.GetEventAsync("token", "evt-1", "cal-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_WithNullCalendarId_PassesNullToProvider()
    {
        var calendarEvent = MakeEvent("evt-2", "Lunch");
        _providerMock.Setup(p => p.GetEventAsync("token", "evt-2", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(calendarEvent);

        await _tool.InvokeRun("token", "evt-2");

        _providerMock.Verify(p => p.GetEventAsync("token", "evt-2", null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_ReturnsAllEventFields()
    {
        var start = new DateTimeOffset(2026, 3, 15, 10, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2026, 3, 15, 11, 0, 0, TimeSpan.Zero);
        var calendarEvent = new CalendarEvent
        {
            Id = "evt-1",
            CalendarId = "cal-1",
            Subject = "Design Review",
            Body = "Review the new design proposals",
            Start = start,
            End = end,
            Location = "Conference Room B",
            IsAllDay = false,
            Recurrence = "FREQ=WEEKLY;BYDAY=MO",
            Attendees = ["alice@example.com", "bob@example.com"],
            Organizer = "alice@example.com",
            Status = "accepted"
        };
        _providerMock.Setup(p => p.GetEventAsync("token", "evt-1", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(calendarEvent);

        var result = await _tool.InvokeRun("token", "evt-1");

        result["id"]!.GetValue<string>().ShouldBe("evt-1");
        result["calendarId"]!.GetValue<string>().ShouldBe("cal-1");
        result["subject"]!.GetValue<string>().ShouldBe("Design Review");
        result["body"]!.GetValue<string>().ShouldBe("Review the new design proposals");
        result["location"]!.GetValue<string>().ShouldBe("Conference Room B");
        result["isAllDay"]!.GetValue<bool>().ShouldBeFalse();
        result["recurrence"]!.GetValue<string>().ShouldBe("FREQ=WEEKLY;BYDAY=MO");
        result["organizer"]!.GetValue<string>().ShouldBe("alice@example.com");
        result["status"]!.GetValue<string>().ShouldBe("accepted");

        var attendees = result["attendees"]!.AsArray();
        attendees.Count.ShouldBe(2);
        attendees[0]!.GetValue<string>().ShouldBe("alice@example.com");
        attendees[1]!.GetValue<string>().ShouldBe("bob@example.com");
    }

    [Fact]
    public async Task Run_ReturnsStartAndEndAsDates()
    {
        var start = new DateTimeOffset(2026, 3, 15, 10, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2026, 3, 15, 11, 0, 0, TimeSpan.Zero);
        var calendarEvent = MakeEvent("evt-1", "Test", start, end);
        _providerMock.Setup(p => p.GetEventAsync("token", "evt-1", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(calendarEvent);

        var result = await _tool.InvokeRun("token", "evt-1");

        result["start"].ShouldNotBeNull();
        result["end"].ShouldNotBeNull();
    }

    [Fact]
    public void HasExpectedNameAndDescription()
    {
        EventGetTool.ToolName.ShouldNotBeNullOrWhiteSpace();
        EventGetTool.ToolDescription.ShouldNotBeNullOrWhiteSpace();
    }

    private static CalendarEvent MakeEvent(
        string id, string subject,
        DateTimeOffset? start = null, DateTimeOffset? end = null)
        => new()
        {
            Id = id,
            Subject = subject,
            Start = start ?? DateTimeOffset.UtcNow,
            End = end ?? DateTimeOffset.UtcNow.AddHours(1)
        };
}

internal class TestableEventGetTool(ICalendarProvider provider) : EventGetTool(provider)
{
    public Task<JsonNode> InvokeRun(
        string accessToken,
        string eventId,
        string? calendarId = null,
        CancellationToken ct = default)
        => Run(accessToken, eventId, calendarId, ct);
}
