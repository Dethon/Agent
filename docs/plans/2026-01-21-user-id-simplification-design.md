# User ID Simplification Design

## Problem

User identification breaks in these scenarios:
- Refreshing the browser
- Loading messages from history
- Receiving messages via broadcast in another browser

The user is only properly identified in the same browser session where they wrote the message.

## Root Cause

1. `ChatHub.SendMessage` receives `senderId` but ignores it - uses `username` from Context.Items instead
2. `ChatPrompt.Sender` carries the username (e.g., "Alice"), not the userId (e.g., "alice")
3. `ChatMonitor` stores username as both `SenderId` AND `SenderUsername` in `AdditionalProperties`
4. The distinction between `id` and `username` is unnecessary since usernames must be unique

## Solution

Eliminate the `Username` field entirely. The `Id` becomes both identifier and display name.

### Data Model Changes

**users.json** (both Agent/wwwroot and WebChat.Client/wwwroot):
```json
// Before
{ "id": "alice", "username": "Alice", "avatarUrl": "avatars/alice.png" }

// After
{ "id": "Alice", "avatarUrl": "avatars/alice.png" }
```

**UserConfig** (Agent/Services/UserConfigService.cs and WebChat.Client/Models/UserConfig.cs):
```csharp
// Before
public record UserConfig(string Id, string Username, string AvatarUrl);

// After
public record UserConfig(string Id, string AvatarUrl);
```

**ChatHistoryMessage** (Domain/DTOs/WebChat/ChatHistoryMessage.cs):
```csharp
// Before
public record ChatHistoryMessage(
    string Role,
    string Content,
    string? SenderId,
    string? SenderUsername,
    string? SenderAvatarUrl);

// After
public record ChatHistoryMessage(
    string Role,
    string Content,
    string? SenderId);
```

**ChatMessageModel** (WebChat.Client/Models/ChatMessageModel.cs):
- Remove `SenderUsername` property
- Remove `SenderAvatarUrl` property
- Keep only `SenderId`

### Backend Changes

**ChatHub.cs**:
- Remove `Username` from Context.Items in `RegisterUser`
- Remove `GetRegisteredUsername()` method
- Add `GetRegisteredUserId()` method
- Pass userId (not username) to `EnqueuePromptAndGetResponses`
- Simplify `GetHistory` to only extract `SenderId`

**ChatMonitor.cs**:
- Remove `SenderUsername` from `AdditionalProperties`
- Keep only `SenderId` (which now correctly contains the userId like "Alice")

### Client Changes

**SendMessageEffect.cs**:
- Remove `SenderUsername` and `SenderAvatarUrl` when creating `ChatMessageModel`

**TopicSelectionEffect.cs**:
- Remove `SenderUsername` and `SenderAvatarUrl` when mapping history to `ChatMessageModel`

**MessageList.razor** (or equivalent UI component):
- Look up user from `UserIdentityStore.State.AvailableUsers` by `SenderId`
- Get avatar from looked-up user
- Display `SenderId` directly as the name (it IS the display name now)

## Files to Modify

| File | Change |
|------|--------|
| `Agent/wwwroot/users.json` | Remove `username`, update `id` to capitalized values |
| `WebChat.Client/wwwroot/users.json` | Same |
| `Agent/Services/UserConfigService.cs` | Remove `Username` from record |
| `WebChat.Client/Models/UserConfig.cs` | Remove `Username` from record |
| `WebChat.Client/Models/ChatMessageModel.cs` | Remove `SenderUsername`, `SenderAvatarUrl` |
| `Domain/DTOs/WebChat/ChatHistoryMessage.cs` | Remove `SenderUsername`, `SenderAvatarUrl` |
| `Agent/Hubs/ChatHub.cs` | Use userId instead of username, simplify GetHistory |
| `Domain/Monitor/ChatMonitor.cs` | Remove `SenderUsername` from AdditionalProperties |
| `WebChat.Client/State/Effects/SendMessageEffect.cs` | Remove username/avatar from message |
| `WebChat.Client/State/Effects/TopicSelectionEffect.cs` | Remove username/avatar from history mapping |
| `WebChat.Client/Components/Chat/MessageList.razor` | Look up avatar by SenderId |

## Backward Compatibility

Existing messages in Redis have `SenderId` set to the old username value (e.g., "Alice"). Since the new `Id` will also be "Alice", these messages will continue to work correctly.

## Implementation Notes

- `ChatMessage.AdditionalProperties` is used to store `SenderId` - this persists through Redis serialization via System.Text.Json
- Agent personalization prompt "You are chatting with {userId}" will now show "Alice" instead of "alice" - reads naturally
- Tests referencing `Username`, `SenderUsername`, or `SenderAvatarUrl` will need updating
