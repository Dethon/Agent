using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Tools.Calendar;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Calendar;

public class AdversarialCalendarToolTests
{
    private readonly Mock<ICalendarProvider> _providerMock = new();

    // === 1. Provider exceptions propagate (not swallowed) ===

    [Fact]
    public async Task CalendarListTool_WhenProviderThrows_ExceptionPropagates()
    {
        _providerMock.Setup(p => p.ListCalendarsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Auth failed"));

        var tool = new TestableCalendarListTool(_providerMock.Object);

        await Should.ThrowAsync<InvalidOperationException>(() => tool.InvokeRun("token"));
    }

    [Fact]
    public async Task EventGetTool_WhenProviderThrows_ExceptionPropagates()
    {
        _providerMock.Setup(p => p.GetEventAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new KeyNotFoundException("Event not found"));

        var tool = new TestableEventGetTool(_providerMock.Object);

        await Should.ThrowAsync<KeyNotFoundException>(() => tool.InvokeRun("token", "evt-nonexistent"));
    }

    [Fact]
    public async Task EventDeleteTool_WhenProviderThrows_ExceptionPropagates()
    {
        _providerMock.Setup(p => p.DeleteEventAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new UnauthorizedAccessException("No permission"));

        var tool = new TestableEventDeleteTool(_providerMock.Object);

        await Should.ThrowAsync<UnauthorizedAccessException>(() => tool.InvokeRun("token", "evt-1"));
    }

    [Fact]
    public async Task EventCreateTool_WhenProviderThrows_ExceptionPropagates()
    {
        _providerMock.Setup(p => p.CreateEventAsync(It.IsAny<string>(), It.IsAny<EventCreateRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArgumentException("Invalid event data"));

        var tool = new TestableEventCreateTool(_providerMock.Object);
        var request = new EventCreateRequest
        {
            Subject = "Test",
            Start = DateTimeOffset.UtcNow,
            End = DateTimeOffset.UtcNow.AddHours(1)
        };

        await Should.ThrowAsync<ArgumentException>(() => tool.InvokeRun("token", request));
    }

    [Fact]
    public async Task EventUpdateTool_WhenProviderThrows_ExceptionPropagates()
    {
        _providerMock.Setup(p => p.UpdateEventAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<EventUpdateRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Conflict"));

        var tool = new TestableEventUpdateTool(_providerMock.Object);

        await Should.ThrowAsync<InvalidOperationException>(
            () => tool.InvokeRun("token", "evt-1", new EventUpdateRequest { Subject = "X" }));
    }

    [Fact]
    public async Task EventListTool_WhenProviderThrows_ExceptionPropagates()
    {
        _providerMock.Setup(p => p.ListEventsAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("Graph API timeout"));

        var tool = new TestableEventListTool(_providerMock.Object);

        await Should.ThrowAsync<TimeoutException>(
            () => tool.InvokeRun("token", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1)));
    }

    [Fact]
    public async Task CheckAvailabilityTool_WhenProviderThrows_ExceptionPropagates()
    {
        _providerMock.Setup(p => p.CheckAvailabilityAsync(It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Network error"));

        var tool = new TestableCheckAvailabilityTool(_providerMock.Object);

        await Should.ThrowAsync<HttpRequestException>(
            () => tool.InvokeRun("token", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1)));
    }

    // === 2. CalendarEvent with all null optional fields maps without crashing ===

    [Fact]
    public async Task EventGetTool_WithAllNullOptionalFields_ReturnsJsonWithoutCrashing()
    {
        var calendarEvent = new CalendarEvent
        {
            Id = "evt-minimal",
            Subject = "Bare Minimum",
            Start = DateTimeOffset.UtcNow,
            End = DateTimeOffset.UtcNow.AddHours(1),
            // All optional fields left as default: CalendarId=null, Body=null, Location=null,
            // Recurrence=null, Organizer=null, Status=null, IsAllDay=false, Attendees=[]
        };
        _providerMock.Setup(p => p.GetEventAsync("token", "evt-minimal", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(calendarEvent);

        var tool = new TestableEventGetTool(_providerMock.Object);
        var result = await tool.InvokeRun("token", "evt-minimal");

        result["id"]!.GetValue<string>().ShouldBe("evt-minimal");
        result["subject"]!.GetValue<string>().ShouldBe("Bare Minimum");
        // Null string properties are stored as null JsonNode values -- keys exist but values are null
        var obj = result.AsObject();
        obj.ContainsKey("calendarId").ShouldBeTrue();
        obj.ContainsKey("body").ShouldBeTrue();
        obj.ContainsKey("location").ShouldBeTrue();
        obj.ContainsKey("recurrence").ShouldBeTrue();
        obj.ContainsKey("organizer").ShouldBeTrue();
        obj.ContainsKey("status").ShouldBeTrue();
        result["isAllDay"]!.GetValue<bool>().ShouldBeFalse();
        result["attendees"]!.AsArray().Count.ShouldBe(0);
    }

    // === 3. JSON response includes ALL DTO fields (exact field count) ===

    [Fact]
    public async Task CalendarEventMapper_ProducesExactly12Fields()
    {
        var calendarEvent = new CalendarEvent
        {
            Id = "evt-1",
            CalendarId = "cal-1",
            Subject = "Test",
            Body = "Body text",
            Start = DateTimeOffset.UtcNow,
            End = DateTimeOffset.UtcNow.AddHours(1),
            Location = "Room A",
            IsAllDay = false,
            Recurrence = "FREQ=DAILY",
            Attendees = ["a@b.com"],
            Organizer = "o@b.com",
            Status = "accepted"
        };
        _providerMock.Setup(p => p.GetEventAsync("token", "evt-1", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(calendarEvent);

        var tool = new TestableEventGetTool(_providerMock.Object);
        var result = await tool.InvokeRun("token", "evt-1");

        // CalendarEvent has exactly 12 properties: Id, CalendarId, Subject, Body, Start, End,
        // Location, IsAllDay, Recurrence, Attendees, Organizer, Status
        var obj = result.AsObject();
        obj.Count.ShouldBe(12, "CalendarEventMapper should produce exactly 12 JSON fields matching all CalendarEvent DTO properties");

        // Verify every expected key exists
        obj.ContainsKey("id").ShouldBeTrue();
        obj.ContainsKey("calendarId").ShouldBeTrue();
        obj.ContainsKey("subject").ShouldBeTrue();
        obj.ContainsKey("body").ShouldBeTrue();
        obj.ContainsKey("start").ShouldBeTrue();
        obj.ContainsKey("end").ShouldBeTrue();
        obj.ContainsKey("location").ShouldBeTrue();
        obj.ContainsKey("isAllDay").ShouldBeTrue();
        obj.ContainsKey("recurrence").ShouldBeTrue();
        obj.ContainsKey("attendees").ShouldBeTrue();
        obj.ContainsKey("organizer").ShouldBeTrue();
        obj.ContainsKey("status").ShouldBeTrue();
    }

    [Fact]
    public async Task CalendarListTool_EachItemHasExactly5Fields()
    {
        var calendars = new List<CalendarInfo>
        {
            new() { Id = "cal-1", Name = "Personal", IsDefault = true, CanEdit = true, Color = "#0000FF" }
        };
        _providerMock.Setup(p => p.ListCalendarsAsync("token", It.IsAny<CancellationToken>()))
            .ReturnsAsync(calendars);

        var tool = new TestableCalendarListTool(_providerMock.Object);
        var result = await tool.InvokeRun("token");

        var item = result.AsArray()[0]!.AsObject();
        item.Count.ShouldBe(5, "CalendarInfo JSON should have exactly 5 fields: id, name, isDefault, canEdit, color");
    }

    [Fact]
    public async Task CheckAvailabilityTool_EachSlotHasExactly3Fields()
    {
        var slots = new List<FreeBusySlot>
        {
            new() { Start = DateTimeOffset.UtcNow, End = DateTimeOffset.UtcNow.AddHours(1), Status = FreeBusyStatus.Busy }
        };
        _providerMock.Setup(p => p.CheckAvailabilityAsync("token", It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(slots);

        var tool = new TestableCheckAvailabilityTool(_providerMock.Object);
        var result = await tool.InvokeRun("token", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1));

        var slot = result.AsArray()[0]!.AsObject();
        slot.Count.ShouldBe(3, "FreeBusySlot JSON should have exactly 3 fields: start, end, status");
    }

    [Fact]
    public async Task EventDeleteTool_ConfirmationHasExactly2Fields()
    {
        _providerMock.Setup(p => p.DeleteEventAsync("token", "evt-1", null, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var tool = new TestableEventDeleteTool(_providerMock.Object);
        var result = await tool.InvokeRun("token", "evt-1");

        var obj = result.AsObject();
        obj.Count.ShouldBe(2, "Delete confirmation should have exactly 2 fields: status, eventId");
    }

    // === 4. Start/End dates are ISO 8601 formatted strings ===

    [Fact]
    public async Task CalendarEventMapper_SerializesStartEndAsIso8601()
    {
        var start = new DateTimeOffset(2026, 3, 15, 10, 30, 0, TimeSpan.FromHours(2));
        var end = new DateTimeOffset(2026, 3, 15, 11, 30, 0, TimeSpan.FromHours(2));
        var calendarEvent = new CalendarEvent
        {
            Id = "evt-1",
            Subject = "Test",
            Start = start,
            End = end
        };
        _providerMock.Setup(p => p.GetEventAsync("token", "evt-1", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(calendarEvent);

        var tool = new TestableEventGetTool(_providerMock.Object);
        var result = await tool.InvokeRun("token", "evt-1");

        var startStr = result["start"]!.GetValue<string>();
        var endStr = result["end"]!.GetValue<string>();

        // Verify ISO 8601 round-trip format
        startStr.ShouldBe(start.ToString("o"));
        endStr.ShouldBe(end.ToString("o"));

        // Verify we can parse them back
        DateTimeOffset.Parse(startStr).ShouldBe(start);
        DateTimeOffset.Parse(endStr).ShouldBe(end);
    }

    [Fact]
    public async Task CheckAvailabilityTool_SerializesStartEndAsIso8601()
    {
        var slotStart = new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.FromHours(-5));
        var slotEnd = new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.FromHours(-5));
        var slots = new List<FreeBusySlot>
        {
            new() { Start = slotStart, End = slotEnd, Status = FreeBusyStatus.Busy }
        };
        _providerMock.Setup(p => p.CheckAvailabilityAsync("token", It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(slots);

        var tool = new TestableCheckAvailabilityTool(_providerMock.Object);
        var result = await tool.InvokeRun("token", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1));

        var slot = result.AsArray()[0]!;
        var startStr = slot["start"]!.GetValue<string>();
        var endStr = slot["end"]!.GetValue<string>();

        startStr.ShouldBe(slotStart.ToString("o"));
        endStr.ShouldBe(slotEnd.ToString("o"));
    }

    // === 5. CalendarListTool with null Color ===

    [Fact]
    public async Task CalendarListTool_WithNullColor_IncludesColorKeyAsNull()
    {
        var calendars = new List<CalendarInfo>
        {
            new() { Id = "cal-1", Name = "No Color", IsDefault = false, CanEdit = true, Color = null }
        };
        _providerMock.Setup(p => p.ListCalendarsAsync("token", It.IsAny<CancellationToken>()))
            .ReturnsAsync(calendars);

        var tool = new TestableCalendarListTool(_providerMock.Object);
        var result = await tool.InvokeRun("token");

        var item = result.AsArray()[0]!.AsObject();
        // The "color" key should exist in the JSON even when null
        item.ContainsKey("color").ShouldBeTrue("JSON should include 'color' key even when value is null");
    }

    // === 6. Attendees with empty list ===

    [Fact]
    public async Task CalendarEventMapper_WithEmptyAttendees_ReturnsEmptyJsonArray()
    {
        var calendarEvent = new CalendarEvent
        {
            Id = "evt-1",
            Subject = "Solo Meeting",
            Start = DateTimeOffset.UtcNow,
            End = DateTimeOffset.UtcNow.AddHours(1),
            Attendees = []
        };
        _providerMock.Setup(p => p.GetEventAsync("token", "evt-1", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(calendarEvent);

        var tool = new TestableEventGetTool(_providerMock.Object);
        var result = await tool.InvokeRun("token", "evt-1");

        var attendees = result["attendees"]!.AsArray();
        attendees.ShouldNotBeNull();
        attendees.Count.ShouldBe(0);
    }

    // === 7. EventUpdateTool with completely empty request (all nulls) ===

    [Fact]
    public async Task EventUpdateTool_WithEmptyRequest_DelegatesToProvider()
    {
        var emptyRequest = new EventUpdateRequest();
        var unchangedEvent = new CalendarEvent
        {
            Id = "evt-1",
            Subject = "Unchanged",
            Start = DateTimeOffset.UtcNow,
            End = DateTimeOffset.UtcNow.AddHours(1)
        };
        _providerMock.Setup(p => p.UpdateEventAsync("token", "evt-1", emptyRequest, It.IsAny<CancellationToken>()))
            .ReturnsAsync(unchangedEvent);

        var tool = new TestableEventUpdateTool(_providerMock.Object);
        var result = await tool.InvokeRun("token", "evt-1", emptyRequest);

        result["id"]!.GetValue<string>().ShouldBe("evt-1");
        // Verify it actually called the provider (didn't short-circuit)
        _providerMock.Verify(p => p.UpdateEventAsync("token", "evt-1", emptyRequest, It.IsAny<CancellationToken>()), Times.Once);
    }

    // === 8. CancellationToken is properly forwarded ===

    [Fact]
    public async Task EventListTool_ForwardsCancellationToken()
    {
        using var cts = new CancellationTokenSource();
        var ct = cts.Token;
        _providerMock.Setup(p => p.ListEventsAsync("token", null, It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), ct))
            .ReturnsAsync(new List<CalendarEvent>());

        var tool = new TestableEventListTool(_providerMock.Object);
        await tool.InvokeRun("token", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1), null, ct);

        // Verify the exact CancellationToken was forwarded
        _providerMock.Verify(p => p.ListEventsAsync("token", null, It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), ct), Times.Once);
    }

    // === 9. Large attendees list ===

    [Fact]
    public async Task CalendarEventMapper_WithManyAttendees_MapsAllCorrectly()
    {
        var attendees = Enumerable.Range(1, 50).Select(i => $"user{i}@example.com").ToList();
        var calendarEvent = new CalendarEvent
        {
            Id = "evt-1",
            Subject = "All Hands",
            Start = DateTimeOffset.UtcNow,
            End = DateTimeOffset.UtcNow.AddHours(1),
            Attendees = attendees
        };
        _providerMock.Setup(p => p.GetEventAsync("token", "evt-1", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(calendarEvent);

        var tool = new TestableEventGetTool(_providerMock.Object);
        var result = await tool.InvokeRun("token", "evt-1");

        var resultAttendees = result["attendees"]!.AsArray();
        resultAttendees.Count.ShouldBe(50);
        resultAttendees[0]!.GetValue<string>().ShouldBe("user1@example.com");
        resultAttendees[49]!.GetValue<string>().ShouldBe("user50@example.com");
    }

    // === 10. Multiple events in list are each fully mapped ===

    [Fact]
    public async Task EventListTool_WithMultipleEvents_EachEventHasAllFields()
    {
        var events = new List<CalendarEvent>
        {
            new()
            {
                Id = "evt-1", Subject = "Event 1",
                Start = DateTimeOffset.UtcNow, End = DateTimeOffset.UtcNow.AddHours(1),
                CalendarId = "cal-1", Body = "body1", Location = "loc1",
                IsAllDay = false, Recurrence = null, Attendees = ["a@b.com"],
                Organizer = "org@b.com", Status = "accepted"
            },
            new()
            {
                Id = "evt-2", Subject = "Event 2",
                Start = DateTimeOffset.UtcNow.AddHours(2), End = DateTimeOffset.UtcNow.AddHours(3),
                CalendarId = "cal-2", Body = null, Location = null,
                IsAllDay = true, Recurrence = "FREQ=WEEKLY", Attendees = [],
                Organizer = null, Status = null
            }
        };
        _providerMock.Setup(p => p.ListEventsAsync("token", null, It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(events);

        var tool = new TestableEventListTool(_providerMock.Object);
        var result = await tool.InvokeRun("token", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1));

        var array = result.AsArray();
        array.Count.ShouldBe(2);

        // Both items should have all 12 fields
        array[0]!.AsObject().Count.ShouldBe(12);
        array[1]!.AsObject().Count.ShouldBe(12);

        // Verify second event with null fields doesn't crash and keeps all keys
        array[1]!["id"]!.GetValue<string>().ShouldBe("evt-2");
        array[1]!["isAllDay"]!.GetValue<bool>().ShouldBeTrue();
        array[1]!.AsObject().ContainsKey("body").ShouldBeTrue();
        array[1]!.AsObject().ContainsKey("location").ShouldBeTrue();
    }
}
