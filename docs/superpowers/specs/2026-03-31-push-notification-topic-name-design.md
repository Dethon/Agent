# Push Notification with Topic Name

## Problem

Push notifications sent when an agent finishes responding use a generic title ("New response"). Users with multiple active topics cannot tell which conversation received a reply without opening the app.

## Solution

Include the topic name as the push notification title. The notification becomes:

- **Title:** `<topic name>` (e.g. "Apartment search in Madrid")
- **Body:** "The agent has finished responding"
- **Fallback:** If no topic name is available, fall back to "New response"

## Approach

Add `TopicName` to the in-memory `ChannelSession` record so it's available when `StreamService.CompleteStream` fires the push notification. This is a lightweight denormalization — `ChannelSession` is ephemeral in-memory state, and topic names are effectively immutable once set.

## Changes

### 1. `McpChannelSignalR/Internal/ChannelSession.cs`

Add optional `TopicName` property to the record.

### 2. `McpChannelSignalR/Services/SessionService.cs`

`StartSession` accepts a `topicName` parameter and stores it in the `ChannelSession`.

### 3. `McpChannelSignalR/Hubs/ChatHub.cs`

`StartSession` hub method accepts `topicName` from the client and passes it to `SessionService.StartSession`.

### 4. `McpChannelSignalR/Services/StreamService.cs`

- `CompleteStream` reads `TopicName` from the session and passes it to `SendPushNotificationAsync`.
- `SendPushNotificationAsync` uses the topic name as the notification title, falling back to "New response" if null.

### 5. WebChat client — `ChatSessionService.StartSessionAsync`

Pass `topic.Name` in the hub `StartSession` call.

## Scope

- Only the SignalR channel (WebChat) is affected. Telegram and ServiceBus channels are unchanged.
- `IPushNotificationService` interface and `WebPushNotificationService` implementation are unchanged — only the arguments passed to `SendToSpaceAsync` differ.
- Existing `StreamService` and `SessionService` tests need updates for the new parameter.
