using Domain.Contracts;
using Domain.DTOs;
using McpServerCalendar.McpTools;
using ModelContextProtocol.Protocol;
using Moq;
using Shouldly;

namespace Tests.Unit.McpServerCalendar;

public class McpCalendarToolTests
{
    private readonly Mock<ICalendarProvider> _providerMock = new();

    [Fact]
    public async Task McpCalendarListTool_DelegatesToProviderAndReturnsCallToolResult()
    {
        var calendars = new List<CalendarInfo>
        {
            new() { Id = "cal-1", Name = "Personal", IsDefault = true, CanEdit = true }
        };
        _providerMock.Setup(p => p.ListCalendarsAsync("token-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(calendars);

        var tool = new McpCalendarListTool(_providerMock.Object);
        var result = await tool.McpRun("token-1");

        result.ShouldNotBeNull();
        result.IsError.ShouldBe(false);
        result.Content.ShouldNotBeEmpty();
        var text = result.Content.OfType<TextContentBlock>().First().Text;
        text.ShouldContain("cal-1");
        text.ShouldContain("Personal");
    }

    [Fact]
    public async Task McpEventListTool_DelegatesToProviderWithParsedDates()
    {
        var start = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2026, 3, 31, 0, 0, 0, TimeSpan.Zero);
        _providerMock.Setup(p => p.ListEventsAsync("token", null, start, end, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<CalendarEvent>
            {
                new() { Id = "evt-1", Subject = "Meeting", Start = start.AddHours(9), End = start.AddHours(10) }
            });

        var tool = new McpEventListTool(_providerMock.Object);
        var result = await tool.McpRun("token", start.ToString("o"), end.ToString("o"));

        result.IsError.ShouldBe(false);
        var text = result.Content.OfType<TextContentBlock>().First().Text;
        text.ShouldContain("evt-1");
        text.ShouldContain("Meeting");
    }

    [Fact]
    public async Task McpEventListTool_WithCalendarId_PassesItThrough()
    {
        var start = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2026, 3, 31, 0, 0, 0, TimeSpan.Zero);
        _providerMock.Setup(p => p.ListEventsAsync("token", "cal-1", start, end, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<CalendarEvent>());

        var tool = new McpEventListTool(_providerMock.Object);
        await tool.McpRun("token", start.ToString("o"), end.ToString("o"), "cal-1");

        _providerMock.Verify(p => p.ListEventsAsync("token", "cal-1", start, end, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task McpEventGetTool_DelegatesToProvider()
    {
        var calendarEvent = new CalendarEvent
        {
            Id = "evt-1", Subject = "Review", Start = DateTimeOffset.UtcNow, End = DateTimeOffset.UtcNow.AddHours(1)
        };
        _providerMock.Setup(p => p.GetEventAsync("token", "evt-1", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(calendarEvent);

        var tool = new McpEventGetTool(_providerMock.Object);
        var result = await tool.McpRun("token", "evt-1");

        result.IsError.ShouldBe(false);
        var text = result.Content.OfType<TextContentBlock>().First().Text;
        text.ShouldContain("evt-1");
        text.ShouldContain("Review");
    }

    [Fact]
    public async Task McpEventCreateTool_DelegatesToProvider()
    {
        var created = new CalendarEvent
        {
            Id = "evt-new", Subject = "Meeting",
            Start = new DateTimeOffset(2026, 3, 15, 10, 0, 0, TimeSpan.Zero),
            End = new DateTimeOffset(2026, 3, 15, 11, 0, 0, TimeSpan.Zero)
        };
        _providerMock.Setup(p => p.CreateEventAsync("token", It.IsAny<EventCreateRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(created);

        var tool = new McpEventCreateTool(_providerMock.Object);
        var result = await tool.McpRun("token", "Meeting",
            "2026-03-15T10:00:00+00:00", "2026-03-15T11:00:00+00:00");

        result.IsError.ShouldBe(false);
        var text = result.Content.OfType<TextContentBlock>().First().Text;
        text.ShouldContain("evt-new");
    }

    [Fact]
    public async Task McpEventUpdateTool_DelegatesToProvider()
    {
        var updated = new CalendarEvent
        {
            Id = "evt-1", Subject = "Updated Meeting",
            Start = DateTimeOffset.UtcNow, End = DateTimeOffset.UtcNow.AddHours(1)
        };
        _providerMock.Setup(p => p.UpdateEventAsync("token", "evt-1", It.IsAny<EventUpdateRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(updated);

        var tool = new McpEventUpdateTool(_providerMock.Object);
        var result = await tool.McpRun("token", "evt-1", subject: "Updated Meeting");

        result.IsError.ShouldBe(false);
        var text = result.Content.OfType<TextContentBlock>().First().Text;
        text.ShouldContain("Updated Meeting");
    }

    [Fact]
    public async Task McpEventDeleteTool_DelegatesToProvider()
    {
        _providerMock.Setup(p => p.DeleteEventAsync("token", "evt-1", null, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var tool = new McpEventDeleteTool(_providerMock.Object);
        var result = await tool.McpRun("token", "evt-1");

        result.IsError.ShouldBe(false);
        _providerMock.Verify(p => p.DeleteEventAsync("token", "evt-1", null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task McpCheckAvailabilityTool_DelegatesToProvider()
    {
        var start = new DateTimeOffset(2026, 3, 15, 8, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2026, 3, 15, 18, 0, 0, TimeSpan.Zero);
        _providerMock.Setup(p => p.CheckAvailabilityAsync("token", start, end, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<FreeBusySlot>
            {
                new() { Start = start.AddHours(1), End = start.AddHours(2), Status = FreeBusyStatus.Busy }
            });

        var tool = new McpCheckAvailabilityTool(_providerMock.Object);
        var result = await tool.McpRun("token", start.ToString("o"), end.ToString("o"));

        result.IsError.ShouldBe(false);
        var text = result.Content.OfType<TextContentBlock>().First().Text;
        text.ShouldContain("Busy");
    }

    [Fact]
    public async Task McpEventListTool_InvalidDateString_ThrowsFormatException()
    {
        var tool = new McpEventListTool(_providerMock.Object);

        await Should.ThrowAsync<FormatException>(
            () => tool.McpRun("token", "not-a-date", "2026-03-31T00:00:00+00:00"));
    }

    [Fact]
    public async Task McpEventUpdateTool_OnlySubjectProvided_PassesNullForAllOtherFields()
    {
        EventUpdateRequest? capturedRequest = null;
        var updated = new CalendarEvent
        {
            Id = "evt-1", Subject = "Only Subject",
            Start = DateTimeOffset.UtcNow, End = DateTimeOffset.UtcNow.AddHours(1)
        };
        _providerMock.Setup(p => p.UpdateEventAsync("token", "evt-1", It.IsAny<EventUpdateRequest>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, EventUpdateRequest, CancellationToken>((_, _, req, _) => capturedRequest = req)
            .ReturnsAsync(updated);

        var tool = new McpEventUpdateTool(_providerMock.Object);
        await tool.McpRun("token", "evt-1", subject: "Only Subject");

        capturedRequest.ShouldNotBeNull();
        capturedRequest.Subject.ShouldBe("Only Subject");
        capturedRequest.Start.ShouldBeNull();
        capturedRequest.End.ShouldBeNull();
        capturedRequest.Location.ShouldBeNull();
        capturedRequest.Body.ShouldBeNull();
        capturedRequest.Attendees.ShouldBeNull();
        capturedRequest.IsAllDay.ShouldBeNull();
        capturedRequest.Recurrence.ShouldBeNull();
    }

    [Fact]
    public async Task McpEventCreateTool_AttendeesString_ParsedIntoList()
    {
        EventCreateRequest? capturedRequest = null;
        var created = new CalendarEvent
        {
            Id = "evt-new", Subject = "Team Sync",
            Start = new DateTimeOffset(2026, 3, 15, 10, 0, 0, TimeSpan.Zero),
            End = new DateTimeOffset(2026, 3, 15, 11, 0, 0, TimeSpan.Zero)
        };
        _providerMock.Setup(p => p.CreateEventAsync("token", It.IsAny<EventCreateRequest>(), It.IsAny<CancellationToken>()))
            .Callback<string, EventCreateRequest, CancellationToken>((_, req, _) => capturedRequest = req)
            .ReturnsAsync(created);

        var tool = new McpEventCreateTool(_providerMock.Object);
        await tool.McpRun("token", "Team Sync",
            "2026-03-15T10:00:00+00:00", "2026-03-15T11:00:00+00:00",
            attendees: "alice@example.com, bob@example.com , carol@example.com");

        capturedRequest.ShouldNotBeNull();
        capturedRequest.Attendees.ShouldNotBeNull();
        capturedRequest.Attendees.Count.ShouldBe(3);
        capturedRequest.Attendees[0].ShouldBe("alice@example.com");
        capturedRequest.Attendees[1].ShouldBe("bob@example.com");
        capturedRequest.Attendees[2].ShouldBe("carol@example.com");
    }

    [Fact]
    public async Task McpEventCreateTool_NullAttendees_PassesNullToRequest()
    {
        EventCreateRequest? capturedRequest = null;
        var created = new CalendarEvent
        {
            Id = "evt-new", Subject = "Solo Meeting",
            Start = new DateTimeOffset(2026, 3, 15, 10, 0, 0, TimeSpan.Zero),
            End = new DateTimeOffset(2026, 3, 15, 11, 0, 0, TimeSpan.Zero)
        };
        _providerMock.Setup(p => p.CreateEventAsync("token", It.IsAny<EventCreateRequest>(), It.IsAny<CancellationToken>()))
            .Callback<string, EventCreateRequest, CancellationToken>((_, req, _) => capturedRequest = req)
            .ReturnsAsync(created);

        var tool = new McpEventCreateTool(_providerMock.Object);
        await tool.McpRun("token", "Solo Meeting",
            "2026-03-15T10:00:00+00:00", "2026-03-15T11:00:00+00:00");

        capturedRequest.ShouldNotBeNull();
        capturedRequest.Attendees.ShouldBeNull();
        capturedRequest.CalendarId.ShouldBeNull();
        capturedRequest.Location.ShouldBeNull();
        capturedRequest.Body.ShouldBeNull();
        capturedRequest.IsAllDay.ShouldBeNull();
        capturedRequest.Recurrence.ShouldBeNull();
    }

    [Fact]
    public async Task McpCheckAvailabilityTool_InvalidDateString_ThrowsFormatException()
    {
        var tool = new McpCheckAvailabilityTool(_providerMock.Object);

        await Should.ThrowAsync<FormatException>(
            () => tool.McpRun("token", "yesterday", "tomorrow"));
    }

    [Fact]
    public async Task McpEventCreateTool_AllOptionalParams_PassedCorrectly()
    {
        EventCreateRequest? capturedRequest = null;
        var created = new CalendarEvent
        {
            Id = "evt-full", Subject = "Full Event",
            Start = new DateTimeOffset(2026, 4, 1, 9, 0, 0, TimeSpan.Zero),
            End = new DateTimeOffset(2026, 4, 1, 10, 0, 0, TimeSpan.Zero)
        };
        _providerMock.Setup(p => p.CreateEventAsync("token", It.IsAny<EventCreateRequest>(), It.IsAny<CancellationToken>()))
            .Callback<string, EventCreateRequest, CancellationToken>((_, req, _) => capturedRequest = req)
            .ReturnsAsync(created);

        var tool = new McpEventCreateTool(_providerMock.Object);
        await tool.McpRun("token", "Full Event",
            "2026-04-01T09:00:00+00:00", "2026-04-01T10:00:00+00:00",
            calendarId: "cal-work",
            location: "Room 42",
            body: "Discuss Q2 plans",
            attendees: "a@x.com,b@x.com",
            isAllDay: false,
            recurrence: "weekly");

        capturedRequest.ShouldNotBeNull();
        capturedRequest.Subject.ShouldBe("Full Event");
        capturedRequest.Start.ShouldBe(new DateTimeOffset(2026, 4, 1, 9, 0, 0, TimeSpan.Zero));
        capturedRequest.End.ShouldBe(new DateTimeOffset(2026, 4, 1, 10, 0, 0, TimeSpan.Zero));
        capturedRequest.CalendarId.ShouldBe("cal-work");
        capturedRequest.Location.ShouldBe("Room 42");
        capturedRequest.Body.ShouldBe("Discuss Q2 plans");
        capturedRequest.Attendees.ShouldNotBeNull();
        capturedRequest.Attendees.Count.ShouldBe(2);
        capturedRequest.IsAllDay.ShouldBe(false);
        capturedRequest.Recurrence.ShouldBe("weekly");
    }
}
