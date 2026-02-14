using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Tools.Calendar;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Calendar;

public class EventCreateToolTests
{
    private readonly Mock<ICalendarProvider> _providerMock = new();
    private readonly TestableEventCreateTool _tool;

    public EventCreateToolTests()
    {
        _tool = new TestableEventCreateTool(_providerMock.Object);
    }

    [Fact]
    public async Task Run_DelegatesToProviderWithRequest()
    {
        var request = new EventCreateRequest
        {
            Subject = "Sprint Planning",
            Start = new DateTimeOffset(2026, 3, 15, 10, 0, 0, TimeSpan.Zero),
            End = new DateTimeOffset(2026, 3, 15, 11, 0, 0, TimeSpan.Zero),
            CalendarId = "cal-1",
            Body = "Bi-weekly sprint planning",
            Location = "Room C",
            IsAllDay = false,
            Attendees = ["dev@example.com"],
            Recurrence = null
        };
        var createdEvent = new CalendarEvent
        {
            Id = "evt-new",
            Subject = "Sprint Planning",
            Start = request.Start,
            End = request.End,
            CalendarId = "cal-1"
        };
        _providerMock.Setup(p => p.CreateEventAsync("token", request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(createdEvent);

        await _tool.InvokeRun("token", request);

        _providerMock.Verify(p => p.CreateEventAsync("token", request, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_ReturnsCreatedEventIdAndSubject()
    {
        var request = new EventCreateRequest
        {
            Subject = "Lunch",
            Start = new DateTimeOffset(2026, 3, 15, 12, 0, 0, TimeSpan.Zero),
            End = new DateTimeOffset(2026, 3, 15, 13, 0, 0, TimeSpan.Zero)
        };
        var createdEvent = new CalendarEvent
        {
            Id = "evt-42",
            Subject = "Lunch",
            Start = request.Start,
            End = request.End
        };
        _providerMock.Setup(p => p.CreateEventAsync("token", request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(createdEvent);

        var result = await _tool.InvokeRun("token", request);

        result["id"]!.GetValue<string>().ShouldBe("evt-42");
        result["subject"]!.GetValue<string>().ShouldBe("Lunch");
    }

    [Fact]
    public async Task Run_ReturnsAllCreatedEventFields()
    {
        var start = new DateTimeOffset(2026, 4, 1, 9, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2026, 4, 1, 10, 0, 0, TimeSpan.Zero);
        var request = new EventCreateRequest
        {
            Subject = "Review",
            Start = start,
            End = end,
            Location = "Online",
            Attendees = ["a@b.com"]
        };
        var createdEvent = new CalendarEvent
        {
            Id = "evt-99",
            Subject = "Review",
            Start = start,
            End = end,
            Location = "Online",
            IsAllDay = false,
            Attendees = ["a@b.com"],
            Organizer = "me@b.com",
            Status = "tentative",
            CalendarId = "cal-1"
        };
        _providerMock.Setup(p => p.CreateEventAsync("token", request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(createdEvent);

        var result = await _tool.InvokeRun("token", request);

        result["id"]!.GetValue<string>().ShouldBe("evt-99");
        result["subject"]!.GetValue<string>().ShouldBe("Review");
        result["location"]!.GetValue<string>().ShouldBe("Online");
        result["isAllDay"]!.GetValue<bool>().ShouldBeFalse();
        result["start"].ShouldNotBeNull();
        result["end"].ShouldNotBeNull();
    }

    [Fact]
    public void HasExpectedNameAndDescription()
    {
        EventCreateTool.ToolName.ShouldNotBeNullOrWhiteSpace();
        EventCreateTool.ToolDescription.ShouldNotBeNullOrWhiteSpace();
    }
}

internal class TestableEventCreateTool(ICalendarProvider provider) : EventCreateTool(provider)
{
    public Task<JsonNode> InvokeRun(
        string accessToken,
        EventCreateRequest request,
        CancellationToken ct = default)
        => Run(accessToken, request, ct);
}
