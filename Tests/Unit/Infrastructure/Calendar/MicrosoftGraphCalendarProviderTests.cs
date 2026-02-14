using System.Net;
using System.Text.Json;
using Domain.DTOs;
using Infrastructure.Calendar;
using Shouldly;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Tests.Unit.Infrastructure.Calendar;

public class MicrosoftGraphCalendarProviderTests : IDisposable
{
    private readonly WireMockServer _server;
    private readonly MicrosoftGraphCalendarProvider _provider;
    private const string AccessToken = "test-access-token-123";

    public MicrosoftGraphCalendarProviderTests()
    {
        _server = WireMockServer.Start();
        var httpClient = new HttpClient { BaseAddress = new Uri(_server.Url!) };
        _provider = new MicrosoftGraphCalendarProvider(httpClient);
    }

    [Fact]
    public async Task ListCalendarsAsync_CallsGetMeCalendars_ReturnsMappedCalendarInfoList()
    {
        // Arrange
        var graphResponse = new
        {
            value = new[]
            {
                new { id = "cal-1", name = "Calendar", color = "auto", isDefaultCalendar = true, canEdit = true },
                new { id = "cal-2", name = "Work", color = "lightBlue", isDefaultCalendar = false, canEdit = false }
            }
        };

        _server.Given(Request.Create()
                .WithPath("/me/calendars")
                .WithHeader("Authorization", "Bearer " + AccessToken)
                .UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(JsonSerializer.Serialize(graphResponse)));

        // Act
        var result = await _provider.ListCalendarsAsync(AccessToken);

        // Assert
        result.Count.ShouldBe(2);

        result[0].Id.ShouldBe("cal-1");
        result[0].Name.ShouldBe("Calendar");
        result[0].Color.ShouldBe("auto");
        result[0].IsDefault.ShouldBeTrue();
        result[0].CanEdit.ShouldBeTrue();

        result[1].Id.ShouldBe("cal-2");
        result[1].Name.ShouldBe("Work");
        result[1].Color.ShouldBe("lightBlue");
        result[1].IsDefault.ShouldBeFalse();
        result[1].CanEdit.ShouldBeFalse();
    }

    [Fact]
    public async Task ListEventsAsync_WithCalendarId_CallsGetMeCalendarsIdEvents()
    {
        // Arrange
        var calendarId = "cal-123";
        var start = new DateTimeOffset(2026, 2, 15, 0, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2026, 2, 16, 0, 0, 0, TimeSpan.Zero);

        var graphResponse = new
        {
            value = new[]
            {
                new
                {
                    id = "evt-1",
                    subject = "Team Standup",
                    body = new { content = "Daily standup", contentType = "text" },
                    start = new { dateTime = "2026-02-15T09:00:00.0000000", timeZone = "UTC" },
                    end = new { dateTime = "2026-02-15T09:30:00.0000000", timeZone = "UTC" },
                    location = new { displayName = "Room A" },
                    isAllDay = false,
                    attendees = new[] { new { emailAddress = new { address = "bob@example.com" } } },
                    organizer = new { emailAddress = new { address = "alice@example.com" } }
                }
            }
        };

        _server.Given(Request.Create()
                .WithPath($"/me/calendars/{calendarId}/events")
                .WithHeader("Authorization", "Bearer " + AccessToken)
                .UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(JsonSerializer.Serialize(graphResponse)));

        // Act
        var result = await _provider.ListEventsAsync(AccessToken, calendarId, start, end);

        // Assert
        result.Count.ShouldBe(1);
        result[0].Id.ShouldBe("evt-1");
        result[0].Subject.ShouldBe("Team Standup");
    }

    [Fact]
    public async Task ListEventsAsync_WithoutCalendarId_CallsGetMeEvents()
    {
        // Arrange
        var start = new DateTimeOffset(2026, 2, 15, 0, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2026, 2, 16, 0, 0, 0, TimeSpan.Zero);

        var graphResponse = new
        {
            value = new[]
            {
                new
                {
                    id = "evt-2",
                    subject = "Lunch",
                    body = new { content = "", contentType = "text" },
                    start = new { dateTime = "2026-02-15T12:00:00.0000000", timeZone = "UTC" },
                    end = new { dateTime = "2026-02-15T13:00:00.0000000", timeZone = "UTC" },
                    location = new { displayName = "" },
                    isAllDay = false,
                    attendees = Array.Empty<object>(),
                    organizer = new { emailAddress = new { address = "me@example.com" } }
                }
            }
        };

        _server.Given(Request.Create()
                .WithPath("/me/events")
                .WithHeader("Authorization", "Bearer " + AccessToken)
                .UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(JsonSerializer.Serialize(graphResponse)));

        // Act
        var result = await _provider.ListEventsAsync(AccessToken, null, start, end);

        // Assert
        result.Count.ShouldBe(1);
        result[0].Id.ShouldBe("evt-2");
        result[0].Subject.ShouldBe("Lunch");
    }

    [Fact]
    public async Task GetEventAsync_CallsGetMeEventsId_ReturnsFullyMappedCalendarEvent()
    {
        // Arrange
        var eventId = "evt-42";
        var graphResponse = new
        {
            id = "evt-42",
            subject = "Architecture Review",
            body = new { content = "Review the new design", contentType = "html" },
            start = new { dateTime = "2026-02-15T14:00:00.0000000", timeZone = "UTC" },
            end = new { dateTime = "2026-02-15T15:30:00.0000000", timeZone = "UTC" },
            location = new { displayName = "Conference Room B" },
            isAllDay = false,
            attendees = new[]
            {
                new { emailAddress = new { address = "bob@example.com" } },
                new { emailAddress = new { address = "charlie@example.com" } }
            },
            organizer = new { emailAddress = new { address = "alice@example.com" } }
        };

        _server.Given(Request.Create()
                .WithPath($"/me/events/{eventId}")
                .WithHeader("Authorization", "Bearer " + AccessToken)
                .UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(JsonSerializer.Serialize(graphResponse)));

        // Act
        var result = await _provider.GetEventAsync(AccessToken, eventId, null);

        // Assert
        result.Id.ShouldBe("evt-42");
        result.Subject.ShouldBe("Architecture Review");
        result.Body.ShouldBe("Review the new design");
        result.Start.ShouldBe(new DateTimeOffset(2026, 2, 15, 14, 0, 0, TimeSpan.Zero));
        result.End.ShouldBe(new DateTimeOffset(2026, 2, 15, 15, 30, 0, TimeSpan.Zero));
        result.Location.ShouldBe("Conference Room B");
        result.IsAllDay.ShouldBeFalse();
        result.Attendees.Count.ShouldBe(2);
        result.Attendees[0].ShouldBe("bob@example.com");
        result.Attendees[1].ShouldBe("charlie@example.com");
        result.Organizer.ShouldBe("alice@example.com");
    }

    [Fact]
    public async Task CreateEventAsync_WithoutCalendarId_CallsPostMeEvents_ReturnsCreatedEvent()
    {
        // Arrange
        var request = new EventCreateRequest
        {
            Subject = "New Meeting",
            Body = "Discuss roadmap",
            Start = new DateTimeOffset(2026, 2, 20, 10, 0, 0, TimeSpan.Zero),
            End = new DateTimeOffset(2026, 2, 20, 11, 0, 0, TimeSpan.Zero),
            Location = "Room C",
            IsAllDay = false,
            Attendees = ["dave@example.com"]
        };

        var graphResponse = new
        {
            id = "evt-new-1",
            subject = "New Meeting",
            body = new { content = "Discuss roadmap", contentType = "text" },
            start = new { dateTime = "2026-02-20T10:00:00.0000000", timeZone = "UTC" },
            end = new { dateTime = "2026-02-20T11:00:00.0000000", timeZone = "UTC" },
            location = new { displayName = "Room C" },
            isAllDay = false,
            attendees = new[] { new { emailAddress = new { address = "dave@example.com" } } },
            organizer = new { emailAddress = new { address = "me@example.com" } }
        };

        _server.Given(Request.Create()
                .WithPath("/me/events")
                .WithHeader("Authorization", "Bearer " + AccessToken)
                .UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(201)
                .WithHeader("Content-Type", "application/json")
                .WithBody(JsonSerializer.Serialize(graphResponse)));

        // Act
        var result = await _provider.CreateEventAsync(AccessToken, request);

        // Assert
        result.Id.ShouldBe("evt-new-1");
        result.Subject.ShouldBe("New Meeting");
        result.Body.ShouldBe("Discuss roadmap");
        result.Location.ShouldBe("Room C");
        result.Attendees.Count.ShouldBe(1);
        result.Attendees[0].ShouldBe("dave@example.com");
    }

    [Fact]
    public async Task CreateEventAsync_WithCalendarId_CallsPostMeCalendarsIdEvents()
    {
        // Arrange
        var calendarId = "cal-work";
        var request = new EventCreateRequest
        {
            CalendarId = calendarId,
            Subject = "Sprint Planning",
            Start = new DateTimeOffset(2026, 2, 21, 9, 0, 0, TimeSpan.Zero),
            End = new DateTimeOffset(2026, 2, 21, 10, 0, 0, TimeSpan.Zero)
        };

        var graphResponse = new
        {
            id = "evt-new-2",
            subject = "Sprint Planning",
            body = (object?)null,
            start = new { dateTime = "2026-02-21T09:00:00.0000000", timeZone = "UTC" },
            end = new { dateTime = "2026-02-21T10:00:00.0000000", timeZone = "UTC" },
            location = (object?)null,
            isAllDay = false,
            attendees = Array.Empty<object>(),
            organizer = new { emailAddress = new { address = "me@example.com" } }
        };

        _server.Given(Request.Create()
                .WithPath($"/me/calendars/{calendarId}/events")
                .WithHeader("Authorization", "Bearer " + AccessToken)
                .UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(201)
                .WithHeader("Content-Type", "application/json")
                .WithBody(JsonSerializer.Serialize(graphResponse)));

        // Act
        var result = await _provider.CreateEventAsync(AccessToken, request);

        // Assert
        result.Id.ShouldBe("evt-new-2");
        result.Subject.ShouldBe("Sprint Planning");
    }

    [Fact]
    public async Task UpdateEventAsync_CallsPatchMeEventsId_ReturnsUpdatedEvent()
    {
        // Arrange
        var eventId = "evt-99";
        var request = new EventUpdateRequest
        {
            Subject = "Updated Subject",
            Location = "New Room"
        };

        var graphResponse = new
        {
            id = "evt-99",
            subject = "Updated Subject",
            body = new { content = "Original body", contentType = "text" },
            start = new { dateTime = "2026-02-15T09:00:00.0000000", timeZone = "UTC" },
            end = new { dateTime = "2026-02-15T10:00:00.0000000", timeZone = "UTC" },
            location = new { displayName = "New Room" },
            isAllDay = false,
            attendees = Array.Empty<object>(),
            organizer = new { emailAddress = new { address = "me@example.com" } }
        };

        _server.Given(Request.Create()
                .WithPath($"/me/events/{eventId}")
                .WithHeader("Authorization", "Bearer " + AccessToken)
                .UsingPatch())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(JsonSerializer.Serialize(graphResponse)));

        // Act
        var result = await _provider.UpdateEventAsync(AccessToken, eventId, request);

        // Assert
        result.Id.ShouldBe("evt-99");
        result.Subject.ShouldBe("Updated Subject");
        result.Location.ShouldBe("New Room");
    }

    [Fact]
    public async Task DeleteEventAsync_CallsDeleteMeEventsId_NoContent()
    {
        // Arrange
        var eventId = "evt-to-delete";

        _server.Given(Request.Create()
                .WithPath($"/me/events/{eventId}")
                .WithHeader("Authorization", "Bearer " + AccessToken)
                .UsingDelete())
            .RespondWith(Response.Create()
                .WithStatusCode(204));

        // Act & Assert (should not throw)
        await _provider.DeleteEventAsync(AccessToken, eventId, null);

        // Verify the request was made
        _server.LogEntries.Count.ShouldBe(1);
        _server.LogEntries.First().RequestMessage.Method.ShouldBe("DELETE");
    }

    [Fact]
    public async Task CheckAvailabilityAsync_CallsPostMeCalendarGetSchedule_ReturnsMappedFreeBusySlots()
    {
        // Arrange
        var start = new DateTimeOffset(2026, 2, 15, 8, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2026, 2, 15, 18, 0, 0, TimeSpan.Zero);

        var graphResponse = new
        {
            value = new[]
            {
                new
                {
                    scheduleItems = new[]
                    {
                        new
                        {
                            start = new { dateTime = "2026-02-15T09:00:00.0000000", timeZone = "UTC" },
                            end = new { dateTime = "2026-02-15T10:00:00.0000000", timeZone = "UTC" },
                            status = "busy"
                        },
                        new
                        {
                            start = new { dateTime = "2026-02-15T14:00:00.0000000", timeZone = "UTC" },
                            end = new { dateTime = "2026-02-15T15:00:00.0000000", timeZone = "UTC" },
                            status = "tentative"
                        }
                    }
                }
            }
        };

        _server.Given(Request.Create()
                .WithPath("/me/calendar/getSchedule")
                .WithHeader("Authorization", "Bearer " + AccessToken)
                .UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(JsonSerializer.Serialize(graphResponse)));

        // Act
        var result = await _provider.CheckAvailabilityAsync(AccessToken, start, end);

        // Assert
        result.Count.ShouldBe(2);

        result[0].Start.ShouldBe(new DateTimeOffset(2026, 2, 15, 9, 0, 0, TimeSpan.Zero));
        result[0].End.ShouldBe(new DateTimeOffset(2026, 2, 15, 10, 0, 0, TimeSpan.Zero));
        result[0].Status.ShouldBe(FreeBusyStatus.Busy);

        result[1].Start.ShouldBe(new DateTimeOffset(2026, 2, 15, 14, 0, 0, TimeSpan.Zero));
        result[1].End.ShouldBe(new DateTimeOffset(2026, 2, 15, 15, 0, 0, TimeSpan.Zero));
        result[1].Status.ShouldBe(FreeBusyStatus.Tentative);
    }

    [Fact]
    public async Task HttpError_ThrowsHttpRequestException()
    {
        // Arrange
        _server.Given(Request.Create()
                .WithPath("/me/calendars")
                .UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(HttpStatusCode.Unauthorized)
                .WithBody("Unauthorized"));

        // Act & Assert
        await Should.ThrowAsync<HttpRequestException>(
            () => _provider.ListCalendarsAsync(AccessToken));
    }

    [Fact]
    public async Task AuthorizationHeader_SetPerRequest_WithBearerToken()
    {
        // Arrange
        var token1 = "token-first";
        var token2 = "token-second";

        var graphResponse = new
        {
            value = Array.Empty<object>()
        };

        _server.Given(Request.Create()
                .WithPath("/me/calendars")
                .UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(JsonSerializer.Serialize(graphResponse)));

        // Act - call with two different tokens
        await _provider.ListCalendarsAsync(token1);
        await _provider.ListCalendarsAsync(token2);

        // Assert - each request should have its own Bearer token
        _server.LogEntries.Count.ShouldBe(2);

        var firstAuth = _server.LogEntries.ElementAt(0).RequestMessage.Headers!["Authorization"].First();
        firstAuth.ShouldBe("Bearer token-first");

        var secondAuth = _server.LogEntries.ElementAt(1).RequestMessage.Headers!["Authorization"].First();
        secondAuth.ShouldBe("Bearer token-second");
    }

    public void Dispose() => _server.Dispose();
}
