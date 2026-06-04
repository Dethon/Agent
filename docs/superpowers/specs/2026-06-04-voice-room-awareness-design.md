# Voice Room Awareness — Design

**Date:** 2026-06-04
**Branch:** voice-impl
**Status:** Approved design, pending implementation plan

## Problem

When a user talks to the agent through a voice satellite, the agent does not know
which physical room the user is speaking from. Each satellite already carries a
`Room` in its config (e.g. `fran-office-01` → `FranOffice`) and room is tracked in
metrics, but the room never reaches the LLM's reasoning. The model only sees
`Message from household:` (the satellite's `Identity`, surfaced as `Sender`).

## Goals

Surfacing the room to the model enables, with no further mechanism:

1. **Act on the right room** — resolve ambiguous commands ("turn on the lights",
   "is it cold in here") to the room the user is in, by passing the room name to the
   existing Home Assistant tools for area scoping.
2. **Natural awareness** — reference location conversationally without the user
   stating it.
3. **Route replies/announcements** — target follow-ups/announcements back to the
   speaker's room via the existing `AnnounceTarget.Room`.

Per-room persistent memory is explicitly **out of scope**.

## Approach

**Carry the room as an optional structured `Location` field end-to-end, rendered
into the model's per-message prefix** — reusing the exact mechanism that already
surfaces `Sender` and `Timestamp`.

Rejected alternatives:
- **Fold room into the `Sender` string** at the voice dispatcher: conflates identity
  and room everywhere `Sender` is used; brittle string-munging.
- **Per-conversation system prompt injection**: more plumbing than the message-prefix
  path and does not generalize to other channels.

## Data Flow

```
SatelliteConfig.Room ("the office")
   └─ TranscriptDispatcher sets notification.Location = session.Config.Room
        └─ ChannelMessageNotification.Location          (new optional field)
             └─ McpChannelConnection maps → ChannelMessage.Location  (new field)
                  └─ ChatMonitor: userMessage.SetLocation(msg.Location)  (new annotation)
                       └─ OpenRouterChatClient folds it into the user-message prefix
```

The model sees, for example:

```
[Current time: 2026-06-04 18:22:01 +02:00] Message from household (in the office):
```

When `Location` is null/empty (Telegram, WebChat, ServiceBus, scheduling) the prefix
is unchanged — fully backward-compatible.

## Components Changed

| File | Change |
|------|--------|
| `Domain/DTOs/Channel/ChannelMessageNotification.cs` | add `string? Location { get; init; }` |
| `Domain/DTOs/ChannelMessage.cs` | add `string? Location` |
| `Infrastructure/Clients/Channels/McpChannelConnection.cs` | map `Location = notification.Location` when building `ChannelMessage` (the `channel/message` path only; `channel/cancel` leaves it null) |
| `Domain/Extensions/ChatMessageExtensions.cs` | add `GetLocation()` / `SetLocation(string?)` annotation, mirroring `GetSenderId`/`SetSenderId` |
| `Domain/Monitor/ChatMonitor.cs` | `userMessage.SetLocation(x.Message.Location)` alongside `SetSenderId`/`SetTimestamp` |
| `Infrastructure/Agents/ChatClients/OpenRouterChatClient.cs` | read location via `GetLocation()` and fold it into the existing prefix switch |
| `McpChannelVoice/Services/TranscriptDispatcher.cs` | set `Location = session.Config.Room` on the emitted `ChannelMessageNotification` |

No Home Assistant coupling is introduced: the model receives the room *name* and uses
existing HA tools / `AnnounceTarget.Room` to act and route. Those goals fall out for
free once the name is in context.

## Prefix Rendering

`OpenRouterChatClient` currently builds the user-message prefix from a
`(sender, timestamp)` switch. Extend it so that when a non-empty location is present
the `Message from {sender}` segment becomes `Message from {sender} (in {location})`.
Location is only appended when a sender is present (location without a sender is not a
meaningful combination for voice and is treated as no-op). Concretely:

- sender + location (+ optional timestamp): `… Message from {sender} (in {location}):`
- sender, no location: unchanged (`… Message from {sender}:`)
- no sender: unchanged regardless of location

## Wording / Config Note

The room value is surfaced **verbatim**, so it must read naturally after "in ".
`"FranOffice"` reads awkwardly ("in the FranOffice"). Set the satellite config `Room`
to a natural phrase such as `"the office"` or `"office"`. This is a config value
change in `McpChannelVoice/appsettings*.json`, not a code concern, but the
implementation plan should update the existing sample satellite's `Room` to a natural
phrase so the rendered output reads correctly end-to-end.

## Testing (TDD)

- **`OpenRouterChatClient`** (`Tests/Unit/Infrastructure/Agents/ChatClients/…`): prefix
  rendering with location present across the sender/timestamp combinations, and absent
  (regression: existing prefixes unchanged).
- **`ChatMessageExtensions`** (`Tests/Unit/Domain/ChatMessageSerializationTests.cs`):
  `Get/SetLocation` round-trip and survival through serialization.
- **`ChatMonitor`**: attaches `Location` from the inbound `ChannelMessage` onto the
  user `ChatMessage`.
- **`TranscriptDispatcher`** (`Tests/Unit/McpChannelVoice/TranscriptDispatcherTests.cs`):
  populates `Location` from `SatelliteConfig.Room`; empty/whitespace room → null/empty
  (no prefix).

## Error Handling / Edge Cases

- Null/empty/whitespace location → no change to prefix (guarded like sender).
- Catalog-only or roomless satellites → empty room treated as absent.
- `channel/cancel` and non-voice channels → `Location` stays null; no behavior change.
- Serialization: `Location` is a new optional property on an existing record; the
  shared `ChannelProtocol` options serialize it without a resolver change.
