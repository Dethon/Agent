using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Tools.Calendar;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Calendar;

public class EventUpdateToolTests
{
    private readonly Mock<ICalendarProvider> _providerMock = new();
    private readonly TestableEventUpdateTool _tool;

    public EventUpdateToolTests()
    {
        _tool = new TestableEventUpdateTool(_providerMock.Object);
    }

    [Fact]
    public async Task Run_DelegatesToProviderWithEventIdAndRequest()
    {
        var request = new EventUpdateRequest
        {
            Subject = "Updated Subject",
            Location = "New Room"
        };
        var updatedEvent = new CalendarEvent
        {
            Id = "evt-1",
            Subject = "Updated Subject",
            Start = DateTimeOffset.UtcNow,
            End = DateTimeOffset.UtcNow.AddHours(1),
            Location = "New Room"
        };
        _providerMock.Setup(p => p.UpdateEventAsync("token", "evt-1", request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(updatedEvent);

        await _tool.InvokeRun("token", "evt-1", request);

        _providerMock.Verify(p => p.UpdateEventAsync("token", "evt-1", request, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_WithPartialUpdate_PassesThroughAllFields()
    {
        var request = new EventUpdateRequest
        {
            Subject = "Only Subject Changed"
        };
        var updatedEvent = new CalendarEvent
        {
            Id = "evt-1",
            Subject = "Only Subject Changed",
            Start = DateTimeOffset.UtcNow,
            End = DateTimeOffset.UtcNow.AddHours(1)
        };
        _providerMock.Setup(p => p.UpdateEventAsync("token", "evt-1", request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(updatedEvent);

        var result = await _tool.InvokeRun("token", "evt-1", request);

        result["subject"]!.GetValue<string>().ShouldBe("Only Subject Changed");
    }

    [Fact]
    public async Task Run_WithAllFieldsSet_ReturnsUpdatedEvent()
    {
        var start = new DateTimeOffset(2026, 5, 1, 14, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2026, 5, 1, 15, 30, 0, TimeSpan.Zero);
        var request = new EventUpdateRequest
        {
            Subject = "Full Update",
            Body = "Updated body",
            Start = start,
            End = end,
            Location = "Room Z",
            IsAllDay = false,
            Attendees = ["new@example.com"],
            Recurrence = "FREQ=DAILY"
        };
        var updatedEvent = new CalendarEvent
        {
            Id = "evt-2",
            Subject = "Full Update",
            Body = "Updated body",
            Start = start,
            End = end,
            Location = "Room Z",
            IsAllDay = false,
            Attendees = ["new@example.com"],
            Recurrence = "FREQ=DAILY",
            CalendarId = "cal-1",
            Organizer = "admin@example.com",
            Status = "accepted"
        };
        _providerMock.Setup(p => p.UpdateEventAsync("token", "evt-2", request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(updatedEvent);

        var result = await _tool.InvokeRun("token", "evt-2", request);

        result["id"]!.GetValue<string>().ShouldBe("evt-2");
        result["subject"]!.GetValue<string>().ShouldBe("Full Update");
        result["body"]!.GetValue<string>().ShouldBe("Updated body");
        result["location"]!.GetValue<string>().ShouldBe("Room Z");
        result["isAllDay"]!.GetValue<bool>().ShouldBeFalse();
        result["recurrence"]!.GetValue<string>().ShouldBe("FREQ=DAILY");
    }

    [Fact]
    public async Task Run_PatchSemantics_AllFieldsOptional()
    {
        var request = new EventUpdateRequest();
        var updatedEvent = new CalendarEvent
        {
            Id = "evt-3",
            Subject = "Unchanged",
            Start = DateTimeOffset.UtcNow,
            End = DateTimeOffset.UtcNow.AddHours(1)
        };
        _providerMock.Setup(p => p.UpdateEventAsync("token", "evt-3", request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(updatedEvent);

        var result = await _tool.InvokeRun("token", "evt-3", request);

        result["id"]!.GetValue<string>().ShouldBe("evt-3");
        _providerMock.Verify(p => p.UpdateEventAsync("token", "evt-3", request, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void HasExpectedNameAndDescription()
    {
        EventUpdateTool.ToolName.ShouldNotBeNullOrWhiteSpace();
        EventUpdateTool.ToolDescription.ShouldNotBeNullOrWhiteSpace();
    }
}

internal class TestableEventUpdateTool(ICalendarProvider provider) : EventUpdateTool(provider)
{
    public Task<JsonNode> InvokeRun(
        string accessToken,
        string eventId,
        EventUpdateRequest request,
        CancellationToken ct = default)
        => Run(accessToken, eventId, request, ct);
}
