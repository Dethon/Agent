using System.Net;
using System.Text;
using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs;
using Infrastructure.Calendar;
using McpServerCalendar.McpTools;
using ModelContextProtocol.Protocol;
using Moq;
using Shouldly;
using WebChat.Client.State;
using WebChat.Client.State.ConnectedAccounts;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Tests.Unit.Calendar;

public class CrossFeatureBoundaryTests
{
    // === 1. Domain-to-MCP end-to-end delegation: CalendarListTool -> McpCalendarListTool ===
    // Verifies the MCP wrapper (McpCalendarListTool) correctly delegates to the domain tool's
    // base class (CalendarListTool), which in turn calls ICalendarProvider, and the result is
    // a valid CallToolResult containing the expected data. This spans Domain, MCP, and Infrastructure contracts.

    [Fact]
    public async Task McpCalendarListTool_EndToEnd_DelegatesThroughDomainToolToProvider_ReturnsFormattedResult()
    {
        var providerMock = new Mock<ICalendarProvider>();
        var calendars = new List<CalendarInfo>
        {
            new() { Id = "cal-work", Name = "Work Calendar", IsDefault = false, CanEdit = true, Color = "#FF0000" },
            new() { Id = "cal-personal", Name = "Personal", IsDefault = true, CanEdit = true, Color = null }
        };
        providerMock.Setup(p => p.ListCalendarsAsync("my-access-token", It.IsAny<CancellationToken>()))
            .ReturnsAsync(calendars);

        // McpCalendarListTool inherits CalendarListTool (Domain) and wraps it for MCP
        var mcpTool = new McpCalendarListTool(providerMock.Object);
        var result = await mcpTool.McpRun("my-access-token");

        // Verify the CallToolResult is well-formed
        result.ShouldNotBeNull();
        result.IsError.ShouldBe(false);
        result.Content.ShouldNotBeEmpty();

        // Verify the text content contains data from both calendars
        var text = result.Content.OfType<TextContentBlock>().First().Text;
        text.ShouldContain("cal-work");
        text.ShouldContain("Work Calendar");
        text.ShouldContain("cal-personal");
        text.ShouldContain("Personal");

        // Verify the JSON can be parsed and has proper structure
        var json = JsonDocument.Parse(text);
        json.RootElement.GetArrayLength().ShouldBe(2);

        var first = json.RootElement[0];
        first.GetProperty("id").GetString().ShouldBe("cal-work");
        first.GetProperty("name").GetString().ShouldBe("Work Calendar");
        first.GetProperty("isDefault").GetBoolean().ShouldBeFalse();
        first.GetProperty("canEdit").GetBoolean().ShouldBeTrue();
        first.GetProperty("color").GetString().ShouldBe("#FF0000");

        var second = json.RootElement[1];
        second.GetProperty("id").GetString().ShouldBe("cal-personal");
        second.GetProperty("isDefault").GetBoolean().ShouldBeTrue();
        // Null color should be represented in JSON
        second.GetProperty("color").ValueKind.ShouldBe(JsonValueKind.Null);

        // Verify the provider was called exactly once with the correct token
        providerMock.Verify(p => p.ListCalendarsAsync("my-access-token", It.IsAny<CancellationToken>()), Times.Once);
    }

    // === 2. ConnectedAccountsStore rapid state transitions (connect -> disconnect -> connect) ===
    // Verifies that the Redux-like store handles rapid alternation between connected/disconnected
    // without state corruption or lost updates. This tests the reducer, action, and store integration.

    [Fact]
    public void ConnectedAccountsStore_RapidConnectDisconnectConnect_FinalStateIsConnectedWithLatestEmail()
    {
        var dispatcher = new Dispatcher();
        using var store = new ConnectedAccountsStore(dispatcher);

        // Rapidly alternate states
        dispatcher.Dispatch(new AccountConnected("microsoft", "first@example.com"));
        store.State.Providers["microsoft"].Connected.ShouldBeTrue();
        store.State.Providers["microsoft"].Email.ShouldBe("first@example.com");

        dispatcher.Dispatch(new AccountDisconnected("microsoft"));
        store.State.Providers["microsoft"].Connected.ShouldBeFalse();
        store.State.Providers["microsoft"].Email.ShouldBeNull();

        dispatcher.Dispatch(new AccountConnected("microsoft", "second@example.com"));
        store.State.Providers["microsoft"].Connected.ShouldBeTrue();
        store.State.Providers["microsoft"].Email.ShouldBe("second@example.com");

        // Additional rapid transitions to stress-test
        dispatcher.Dispatch(new AccountDisconnected("microsoft"));
        dispatcher.Dispatch(new AccountConnected("microsoft", "third@example.com"));
        dispatcher.Dispatch(new AccountDisconnected("microsoft"));
        dispatcher.Dispatch(new AccountConnected("microsoft", "final@example.com"));

        store.State.Providers["microsoft"].Connected.ShouldBeTrue();
        store.State.Providers["microsoft"].Email.ShouldBe("final@example.com");
    }

    [Fact]
    public void ConnectedAccountsStore_RapidTransitions_ObservableEmitsAllIntermediateStates()
    {
        var dispatcher = new Dispatcher();
        using var store = new ConnectedAccountsStore(dispatcher);

        var emissions = new List<ConnectedAccountsState>();
        using var sub = store.StateObservable.Subscribe(emissions.Add);

        dispatcher.Dispatch(new AccountConnected("microsoft", "a@example.com"));
        dispatcher.Dispatch(new AccountDisconnected("microsoft"));
        dispatcher.Dispatch(new AccountConnected("microsoft", "b@example.com"));

        // Initial + 3 dispatches = 4 emissions
        emissions.Count.ShouldBe(4);
        emissions[0].Providers.ShouldBeEmpty(); // initial
        emissions[1].Providers["microsoft"].Connected.ShouldBeTrue();
        emissions[2].Providers["microsoft"].Connected.ShouldBeFalse();
        emissions[3].Providers["microsoft"].Connected.ShouldBeTrue();
        emissions[3].Providers["microsoft"].Email.ShouldBe("b@example.com");
    }

    // === 3. MicrosoftGraphCalendarProvider uses correct Bearer authorization header format ===
    // Verifies that for every API call, the provider sends "Bearer {token}" and that different
    // tokens are correctly isolated per-request (stateless design).

    [Fact]
    public async Task MicrosoftGraphCalendarProvider_AllOperations_SendBearerTokenInAuthorizationHeader()
    {
        using var server = WireMockServer.Start();
        var httpClient = new HttpClient { BaseAddress = new Uri(server.Url!) };
        var provider = new MicrosoftGraphCalendarProvider(httpClient);

        var emptyCalendarsResponse = JsonSerializer.Serialize(new { value = Array.Empty<object>() });
        var singleEventResponse = JsonSerializer.Serialize(new
        {
            id = "evt-1", subject = "Test",
            body = (object?)null,
            start = new { dateTime = "2026-02-15T09:00:00.0000000", timeZone = "UTC" },
            end = new { dateTime = "2026-02-15T10:00:00.0000000", timeZone = "UTC" },
            location = (object?)null, isAllDay = false,
            attendees = Array.Empty<object>(),
            organizer = new { emailAddress = new { address = "me@test.com" } }
        });
        var eventsListResponse = JsonSerializer.Serialize(new { value = new[] { JsonSerializer.Deserialize<object>(singleEventResponse) } });
        var scheduleResponse = JsonSerializer.Serialize(new { value = new[] { new { scheduleItems = Array.Empty<object>() } } });

        // Set up WireMock to respond to all endpoint patterns
        server.Given(Request.Create().WithPath("/me/calendars").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(emptyCalendarsResponse));
        server.Given(Request.Create().WithPath("/me/events").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(eventsListResponse));
        server.Given(Request.Create().WithPath("/me/events/evt-1").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(singleEventResponse));
        server.Given(Request.Create().WithPath("/me/events").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(201).WithHeader("Content-Type", "application/json").WithBody(singleEventResponse));
        server.Given(Request.Create().WithPath("/me/events/evt-1").UsingPatch())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(singleEventResponse));
        server.Given(Request.Create().WithPath("/me/events/evt-1").UsingDelete())
            .RespondWith(Response.Create().WithStatusCode(204));
        server.Given(Request.Create().WithPath("/me/calendar/getSchedule").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(scheduleResponse));

        var uniqueToken = "unique-bearer-token-" + Guid.NewGuid();
        var start = DateTimeOffset.UtcNow;
        var end = start.AddDays(1);

        // Call each operation with the same unique token
        await provider.ListCalendarsAsync(uniqueToken);
        await provider.ListEventsAsync(uniqueToken, null, start, end);
        await provider.GetEventAsync(uniqueToken, "evt-1", null);
        await provider.CreateEventAsync(uniqueToken, new EventCreateRequest
        {
            Subject = "Test", Start = start, End = end
        });
        await provider.UpdateEventAsync(uniqueToken, "evt-1", new EventUpdateRequest { Subject = "Updated" });
        await provider.DeleteEventAsync(uniqueToken, "evt-1", null);
        await provider.CheckAvailabilityAsync(uniqueToken, start, end);

        // Verify all 7 requests used the correct Bearer token format
        server.LogEntries.Count.ShouldBe(7);
        foreach (var entry in server.LogEntries)
        {
            var authHeader = entry.RequestMessage.Headers!["Authorization"].First();
            authHeader.ShouldBe($"Bearer {uniqueToken}",
                $"Request to {entry.RequestMessage.Path} should use Bearer token");
        }
    }

    // === 4. CalendarAuthService and ConnectedAccountsStore alignment ===
    // Verifies that CalendarAuthService.GetStatusAsync result can drive
    // ConnectedAccountsStore state via AccountStatusLoaded, ensuring the
    // auth service and UI state are aligned on the connected/disconnected contract.

    [Fact]
    public async Task CalendarAuthService_StatusDrivesConnectedAccountsStore_WhenConnected()
    {
        var tokenStoreMock = new Mock<ICalendarTokenStore>();
        tokenStoreMock.Setup(s => s.HasTokensAsync("user-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var settings = new CalendarAuthSettings
        {
            ClientId = "client-id",
            ClientSecret = "client-secret",
            TenantId = "tenant-id"
        };
        var authService = new CalendarAuthService(tokenStoreMock.Object, settings);

        // Get auth status from the service
        var status = await authService.GetStatusAsync("user-1");

        // Feed the status into the ConnectedAccountsStore (as the WebChat would do)
        var dispatcher = new Dispatcher();
        using var store = new ConnectedAccountsStore(dispatcher);

        dispatcher.Dispatch(new AccountStatusLoaded("microsoft", status.Connected, status.Email));

        // Verify the store reflects what the auth service reported
        store.State.Providers["microsoft"].Connected.ShouldBe(status.Connected);
        store.State.Providers["microsoft"].Connected.ShouldBeTrue();
    }

    [Fact]
    public async Task CalendarAuthService_DisconnectThenStatus_DrivesStoreToDisconnected()
    {
        var tokenStoreMock = new Mock<ICalendarTokenStore>();
        tokenStoreMock.Setup(s => s.HasTokensAsync("user-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var settings = new CalendarAuthSettings
        {
            ClientId = "client-id",
            ClientSecret = "client-secret",
            TenantId = "tenant-id"
        };
        var authService = new CalendarAuthService(tokenStoreMock.Object, settings);

        // Disconnect the user
        await authService.DisconnectAsync("user-1");

        // Check status after disconnect
        var status = await authService.GetStatusAsync("user-1");

        // Drive the store
        var dispatcher = new Dispatcher();
        using var store = new ConnectedAccountsStore(dispatcher);
        dispatcher.Dispatch(new AccountStatusLoaded("microsoft", status.Connected, null));

        store.State.Providers["microsoft"].Connected.ShouldBeFalse();
    }

    // === 5. MCP EventCreate tool -> Domain EventCreateTool -> ICalendarProvider contract alignment ===
    // Verifies that attendees string parsing in MCP tool, combined with the domain tool and
    // provider contract, produces the correct request shape end-to-end.

    [Fact]
    public async Task McpEventCreateTool_AttendeesString_FlowsThroughDomainToProviderAsListCorrectly()
    {
        var providerMock = new Mock<ICalendarProvider>();
        EventCreateRequest? capturedRequest = null;

        providerMock.Setup(p => p.CreateEventAsync(It.IsAny<string>(), It.IsAny<EventCreateRequest>(), It.IsAny<CancellationToken>()))
            .Callback<string, EventCreateRequest, CancellationToken>((_, req, _) => capturedRequest = req)
            .ReturnsAsync(new CalendarEvent
            {
                Id = "evt-created", Subject = "Team Sync",
                Start = new DateTimeOffset(2026, 3, 15, 10, 0, 0, TimeSpan.Zero),
                End = new DateTimeOffset(2026, 3, 15, 11, 0, 0, TimeSpan.Zero)
            });

        var mcpTool = new McpEventCreateTool(providerMock.Object);

        // MCP receives attendees as a comma-separated string (as the LLM would provide)
        var result = await mcpTool.McpRun("token", "Team Sync",
            "2026-03-15T10:00:00+00:00", "2026-03-15T11:00:00+00:00",
            attendees: "alice@example.com, bob@example.com , charlie@example.com");

        // Verify the result is valid
        result.IsError.ShouldBe(false);

        // Verify the provider received a properly parsed attendee list
        capturedRequest.ShouldNotBeNull();
        capturedRequest.Attendees.ShouldNotBeNull();
        capturedRequest.Attendees.Count.ShouldBe(3);
        capturedRequest.Attendees[0].ShouldBe("alice@example.com");
        capturedRequest.Attendees[1].ShouldBe("bob@example.com");
        capturedRequest.Attendees[2].ShouldBe("charlie@example.com");

        // Verify no leading/trailing whitespace (MCP tool trims)
        capturedRequest.Attendees.All(a => a == a.Trim()).ShouldBeTrue(
            "All attendee emails should be trimmed of whitespace");
    }

    // === 6. CalendarAuthService authorization URL state encoding is reversible ===
    // Verifies the base64-encoded state parameter in the auth URL can be decoded back to the
    // original userId, which is critical for the OAuth callback to identify the user.

    [Fact]
    public void CalendarAuthService_AuthorizationUrl_StateDecodesBackToUserId_ForSpecialCharacters()
    {
        var tokenStoreMock = new Mock<ICalendarTokenStore>();
        var settings = new CalendarAuthSettings
        {
            ClientId = "client-id",
            ClientSecret = "secret",
            TenantId = "tenant"
        };
        var authService = new CalendarAuthService(tokenStoreMock.Object, settings);

        // Test with various user ID formats that might cause encoding issues
        var testUserIds = new[] { "simple-user", "user@domain.com", "user with spaces", "user/slash", "123" };

        foreach (var userId in testUserIds)
        {
            var url = authService.GetAuthorizationUrl(userId, "https://example.com/callback");
            var uri = new Uri(url);
            var queryParams = System.Web.HttpUtility.ParseQueryString(uri.Query);
            var state = queryParams["state"]!;

            var decodedUserId = Encoding.UTF8.GetString(Convert.FromBase64String(state));
            decodedUserId.ShouldBe(userId, $"State should decode back to user ID '{userId}'");
        }
    }
}
