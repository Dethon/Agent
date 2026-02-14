# Outlook Calendar MCP Server — Design

## Summary

New MCP server exposing Outlook Calendar operations via Microsoft Graph API, with per-user delegated OAuth2 authentication and multi-calendar support. Designed with a provider abstraction so future calendar providers (Google, Apple, CalDAV) can be added without changing the MCP layer.

## Architecture

Three-layer structure following existing project patterns:

### Domain Layer — Provider-Agnostic Contracts and DTOs

- `ICalendarProvider` interface with methods for all 7 operations
- `ICalendarTokenStore` interface for token persistence
- DTOs: `CalendarInfo`, `CalendarEvent`, `FreeBusySlot`, `EventCreateRequest`, `EventUpdateRequest`, `OAuthTokens`
- No dependency on Microsoft Graph or any specific provider

### Infrastructure Layer — Microsoft Graph Implementation

- `MicrosoftGraphCalendarProvider : ICalendarProvider` — all operations via Microsoft Graph SDK
- `RedisCalendarTokenStore : ICalendarTokenStore` — encrypted per-user token storage in Redis
- OAuth2 flow handling in the Agent backend (authorization code exchange, token refresh via MSAL)
- Future providers are additional `ICalendarProvider` implementations here

### McpServerCalendar (Port 6006) — Thin MCP Server

- 7 MCP tools that delegate to `ICalendarProvider` resolved via DI
- Receives access tokens per-call from the Agent (no auth logic in the MCP server)
- Docker container following the same pattern as other MCP servers

### WebChat.Client — OAuth UI

- Connected Accounts settings section with Connect/Disconnect for each provider
- OAuth popup targets the Agent backend, not the MCP server
- `ConnectedAccountsStore` tracks connection status per provider

## Authentication & Token Management

### User-Initiated OAuth Flow

1. User clicks "Connect Outlook Calendar" in WebChat settings
2. Blazor opens a popup to the Agent's `/auth/microsoft/authorize` endpoint
3. Agent redirects to Microsoft's OAuth consent page (scopes: `Calendars.ReadWrite`, `User.Read`), using Authorization Code flow with PKCE
4. User consents → Microsoft redirects to Agent's `/auth/microsoft/callback`
5. Agent exchanges authorization code for tokens via MSAL, stores encrypted tokens in Redis keyed by user identity
6. Callback page uses `window.opener.postMessage(...)` to notify the Blazor app
7. Blazor closes popup, updates UI to "Connected"

### Token Lifecycle

- Access tokens (~1 hour TTL) are silently refreshed using stored refresh tokens
- If refresh token expires/is revoked, connection status resets — user re-authenticates from UI
- Users disconnect via settings (deletes tokens from Redis)

### When Tokens Are Missing

- MCP tools check for a valid access token parameter
- If missing, tool returns: "Calendar not connected. Please connect your Outlook account from the WebChat settings."
- Agent relays this to the user naturally

### Azure AD App Registration

- Single app registration with delegated permissions: `Calendars.ReadWrite`, `User.Read`
- Client ID and secret in .NET User Secrets

## Separation of Concerns

| Responsibility | Owner |
|---------------|-------|
| OAuth endpoints (authorize, callback) | Agent backend |
| Token storage and refresh | Agent backend + RedisCalendarTokenStore |
| Connected accounts status API | Agent backend |
| Access token injection into MCP calls | Agent backend |
| Calendar CRUD operations | McpServerCalendar + ICalendarProvider |
| Graph API communication | MicrosoftGraphCalendarProvider (Infrastructure) |
| OAuth UI (popup, connect/disconnect) | WebChat.Client |

The MCP server is stateless and user-agnostic — it receives an access token per call and uses it to make Graph API requests.

## Domain Contracts

### ICalendarProvider

```csharp
public interface ICalendarProvider
{
    Task<IReadOnlyList<CalendarInfo>> ListCalendarsAsync(string userId, CancellationToken ct);
    Task<IReadOnlyList<CalendarEvent>> ListEventsAsync(string userId, string? calendarId,
        DateTimeOffset start, DateTimeOffset end, CancellationToken ct);
    Task<CalendarEvent> GetEventAsync(string userId, string eventId,
        string? calendarId, CancellationToken ct);
    Task<CalendarEvent> CreateEventAsync(string userId, EventCreateRequest request,
        CancellationToken ct);
    Task<CalendarEvent> UpdateEventAsync(string userId, string eventId,
        EventUpdateRequest request, CancellationToken ct);
    Task DeleteEventAsync(string userId, string eventId,
        string? calendarId, CancellationToken ct);
    Task<IReadOnlyList<FreeBusySlot>> CheckAvailabilityAsync(string userId,
        DateTimeOffset start, DateTimeOffset end, CancellationToken ct);
}
```

### ICalendarTokenStore

```csharp
public interface ICalendarTokenStore
{
    Task<OAuthTokens?> GetTokensAsync(string userId, CancellationToken ct);
    Task StoreTokensAsync(string userId, OAuthTokens tokens, CancellationToken ct);
    Task RemoveTokensAsync(string userId, CancellationToken ct);
    Task<bool> HasTokensAsync(string userId, CancellationToken ct);
}
```

## DTOs

| DTO | Fields |
|-----|--------|
| `CalendarInfo` | Id, Name, Color, IsDefault, CanEdit |
| `CalendarEvent` | Id, CalendarId, Subject, Body, Start, End, Location, IsAllDay, Recurrence, Attendees, Organizer, Status |
| `EventCreateRequest` | CalendarId?, Subject, Body?, Start, End, Location?, IsAllDay?, Attendees?, Recurrence? |
| `EventUpdateRequest` | Subject?, Body?, Start?, End?, Location?, IsAllDay?, Attendees?, Recurrence? (all optional, patch semantics) |
| `FreeBusySlot` | Start, End, Status (Free/Busy/Tentative/OutOfOffice) |
| `OAuthTokens` | AccessToken, RefreshToken, ExpiresAt |

## MCP Tools

| Tool | Parameters | Returns |
|------|-----------|---------|
| `CalendarList` | `accessToken` | List of calendars (name, id, isDefault) |
| `EventList` | `accessToken`, `startDate`, `endDate`, `calendarId?` | Events in range |
| `EventGet` | `accessToken`, `eventId`, `calendarId?` | Full event details |
| `EventCreate` | `accessToken`, `subject`, `start`, `end`, `calendarId?`, `location?`, `body?`, `attendees?`, `isAllDay?`, `recurrence?` | Created event |
| `EventUpdate` | `accessToken`, `eventId`, `subject?`, `start?`, `end?`, `location?`, `body?`, `attendees?` | Updated event |
| `EventDelete` | `accessToken`, `eventId`, `calendarId?` | Confirmation message |
| `CheckAvailability` | `accessToken`, `startDate`, `endDate` | Free/busy slots |

Note: `accessToken` is injected by the Agent and not visible to end users or the LLM.

## Testing Strategy

### Unit Tests

- **Domain DTOs** — serialization round-trips
- **MicrosoftGraphCalendarProvider** — mock Graph SDK client, verify API calls, parameter mapping, error handling
- **RedisCalendarTokenStore** — mock Redis, verify storage, retrieval, encryption, TTL
- **MCP Tools** — mock `ICalendarProvider`, verify delegation, token passing, missing-token errors
- **Agent OAuth flow** — mock MSAL, verify authorization URL, code exchange, token storage

### Integration Tests

- **RedisCalendarTokenStore** — real Redis via Docker, full round-trip
- **OAuth callback endpoint** — test Agent callback with mock authorization code
- **End-to-end MCP tool call** — Agent invokes McpServerCalendar with mocked Graph API

### Out of Scope

- No live Microsoft Graph API tests (requires real Outlook credentials)
- No Playwright tests for OAuth popup in initial version (manual QA)
