# Local Command Dispatcher — Design

Date: 2026-08-01
Status: approved by user (conversation), pending spec review

## Problem

Local speaker commands (volume up/down, mute, unmute) are matched by `VoiceCommandMatcher` and
routed inline inside `TranscriptDispatcher`: an enum-to-action-string switch plus a
`speaker-volume` control send. The feature will grow — future commands may target the satellite
(playback, LED) or act hub-side (dismiss an alert, stop an announcement). Inline routing in
`TranscriptDispatcher` does not scale to that and mixes transcript-gating concerns with command
execution.

## Decision summary

- New module folder `McpChannelVoice/Services/LocalCommands/` owns the whole feature.
- Destinations are handlers implementing `ILocalCommandHandler`, injected via DI as a collection.
- Handlers self-declare which `VoiceCommand` values they own; `LocalCommandDispatcher` builds the
  routing map at construction and fails fast on duplicate or uncovered commands.
- This change is a pure refactor: behavior of the four existing commands is unchanged.
- The contract stays in `McpChannelVoice` (not Domain/Infrastructure). Handlers need
  `SatelliteSession`, and other subsystems are separate processes reachable only via MCP/Redis —
  a shared-layer contract would be an interface nothing outside voice can implement. The contract
  carries a comment noting it can be promoted to the shared layers if a second consumer appears.

## Components

All under `McpChannelVoice/Services/LocalCommands/`:

- `VoiceCommand.cs` — the enum, moved out of `VoiceCommandMatcher.cs`. Unchanged values.
- `VoiceCommandMatcher.cs` — moved into the folder, namespace updated. Logic unchanged.
- `ILocalCommandHandler.cs` — the destination contract:
  - `IReadOnlySet<VoiceCommand> Commands { get; }`
  - `Task<bool> HandleAsync(VoiceCommand command, SatelliteSession session, CancellationToken ct)`
  - The bool means "the action reached its destination" and drives the existing
    `command` / `command_failed` telemetry outcome.
- `LocalCommandDispatcher.cs` — takes `VoiceCommandMatcher` and
  `IEnumerable<ILocalCommandHandler>`. At construction it builds the command→handler map,
  throwing on duplicate ownership and on any enum value no handler owns (startup failure, not
  first-utterance failure). Public surface:
  - `Task<LocalCommandResult?> TryHandleAsync(string transcript, SatelliteSession session, CancellationToken ct)`
  - `null` = not a local command (send to the agent); a result carries the matched command and a
    success flag.
- `LocalCommandResult` — small record: `VoiceCommand Command`, `bool Sent`.
- `SpeakerVolumeCommandHandler.cs` — first destination. Owns the four existing commands; contains
  the enum→action-string mapping and the `speaker-volume` control event send currently inlined in
  `TranscriptDispatcher`.

## Data flow

Unchanged placement: `TranscriptDispatcher.DispatchAsync` runs the gibberish gate, then calls
`LocalCommandDispatcher.TryHandleAsync`. On a hit it publishes the utterance event with outcome
`command`/`command_failed` and returns `false` (which `FollowUpConversation` turns into
`EndConversation`, as today). On `null` it proceeds to `GetOrCreateAsync` and normal agent
dispatch. `TranscriptDispatcher` no longer references `VoiceCommand` or the matcher.

## DI wiring

`ConfigModule.ConfigureVoiceChannel`:

- keep `new VoiceCommandMatcher(settings.Commands)` registration;
- add `AddSingleton<ILocalCommandHandler, SpeakerVolumeCommandHandler>()`;
- add `AddSingleton<LocalCommandDispatcher>()`;
- `TranscriptDispatcher` registration swaps the matcher dependency for the dispatcher.

Adding a future command = new enum value + a handler (new or extended) + one registration line.
Dispatcher and `TranscriptDispatcher` stay untouched.

## Error handling

- Duplicate command ownership or an unowned enum value → `InvalidOperationException` at
  construction (container build time).
- A handler returning `false` (e.g. satellite control channel gone) → `command_failed` outcome,
  same as today's failed `TrySendControlAsync`.
- Handler exceptions are not swallowed by the dispatcher; today's behavior (exception propagates
  out of `DispatchAsync`) is preserved.

## Testing

TDD (Red-Green-Refactor) per project rules:

- `LocalCommandDispatcherTests` (new): duplicate-ownership throws; uncovered-command throws;
  non-command transcript returns null; matched command routes to the owning handler and reports
  its result.
- `SpeakerVolumeCommandHandlerTests` (new): each of the four commands produces the correct
  `speaker-volume` action payload; send failure returns false.
- `TranscriptDispatcherTests` (adapt): command paths now exercise the dispatcher seam; outcomes
  `command`/`command_failed`/`dispatched`/`dropped` unchanged.
- `VoiceCommandMatcherTests`: namespace update only.

No integration or E2E work: wire behavior is identical.

## Out of scope

- New commands (hub-side handlers arrive with their own features).
- Any change to `CommandSettings`, satellite code, or the Wyoming protocol.
- Promoting the contract to Domain — revisit only when a second consumer exists.
