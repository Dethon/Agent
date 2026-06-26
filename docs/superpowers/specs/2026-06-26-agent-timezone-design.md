# Agent-wide operating timezone

**Date:** 2026-06-26
**Branch:** alarms-and-reminders
**Status:** Design — pending review

## Problem

The agent attaches time information to what the LLM sees in three places, and all of them are effectively UTC today because the Docker containers run with `TZ=UTC` and the scheduler hard-codes UTC:

1. **System-prompt date** — `McpAgent.BuildInstructions` renders `"Today is {GetLocalNow():dddd, yyyy-MM-dd}"`. `GetLocalNow()` resolves against the container's local zone (UTC), so it reads UTC.
2. **Per-message timestamp** — `OpenRouterChatClient` prepends `[Current time: {timestamp:yyyy-MM-dd HH:mm:ss zzz}]` to each user message. The timestamp is `DateTimeOffset.UtcNow` (set in `ChatMonitor.BuildUserMessageAsync`), so it always shows `+00:00`.
3. **Schedules** — cron is evaluated in UTC (`CronValidator` over NCrontab), `runAt` accepts an offset but is normalized to UTC, and `SchedulingPrompt` tells the LLM "All times are UTC".

We want a single configured **operating timezone** for the whole agent, used for what it tells the LLM, and we want schedules to honor timezones: cron fires at the configured local wall-clock time (DST-aware), and one-shot `runAt` accepts any timezone and converts internally.

## Goals

- One timezone, configured in a single place, is the agent's reference frame for time it surfaces to the LLM.
- System-prompt date and per-message timestamps render in that zone.
- Cron schedules fire at the configured local wall-clock time, correct across DST transitions.
- One-shot `runAt` accepts any zone (explicit offset/`Z` honored; bare local time interpreted in the configured zone), normalized to UTC internally.

## Non-goals

- Per-schedule timezone overrides. A single agent-wide zone governs cron; one-shots carry their own zone in the ISO string. (Decided with the user.)
- Changing what is persisted: stored timestamps (chat history, schedule store) remain UTC. Only what the LLM *reads* changes zone.

## Decisions (settled with the user)

- **Value:** `Europe/Madrid`.
- **Single source via `TZ`:** the timezone is set **once** as the standard `TZ` environment variable in `docker-compose`, applied to the relevant containers via a YAML anchor. The agent and scheduler are separate containers, so a shared `appsettings.json` is impossible; `TZ` is the idiomatic one-place mechanism. Consequence: `TimeZoneInfo.Local` (and therefore `TimeProvider.System.GetLocalNow()` / `LocalTimeZone`) becomes `Europe/Madrid` in those containers, and container log timestamps become local too. There is **no app-level timezone config key**. Trade-off accepted: an invalid `TZ` falls back to UTC silently (no fail-fast).
- **Schedule TZ scope:** single global timezone for cron; one-shot `runAt` accepts any timezone in its input and converts internally.
- **Cron engine:** replace NCrontab with **Cronos** (HangfireIO) — first-class `GetNextOccurrence(DateTimeOffset, TimeZoneInfo)` with correct spring-forward/fall-back handling. Cronos becomes the single cron parser (validation + next-run).

## Configuration

`docker-compose.yml`: a YAML anchor sets `TZ: Europe/Madrid`, applied (at minimum) to the `agent` and `mcp-scheduling` services' `environment`. The literal value appears exactly once. Per the repo env-var rule this is non-secret config that lives in `docker-compose` (the env section); nothing goes in `.env`, and there is no `appsettings.json` key to add.

The zone is read at runtime from `TimeProvider.LocalTimeZone` (= `TimeZoneInfo.Local`, driven by `TZ`). Code that needs the zone takes an injected `TimeProvider` (per the repo's "TimeProvider for testable time-dependent code" rule), so tests supply a fake provider with a chosen `LocalTimeZone` and never depend on the host's real `TZ`.

**Image prerequisite:** the runtime image must contain the IANA tz database (`/usr/share/zoneinfo/Europe/Madrid`). Verified/installed as part of implementation (Debian-based .NET images include `tzdata`; if a slim base lacks it, add it).

## Component design

### A. System-prompt date (`McpAgent.BuildInstructions`)

`BuildInstructions` already takes `DateTimeOffset now`, and `CreateRunOptions` passes `_timeProvider.GetLocalNow()` — which now returns Madrid time with no construction change. Extend the date line to name the zone so the LLM has an unambiguous frame for scheduling:

```
Today is Friday, 2026-06-26. Current local time is 16:40 (Europe/Madrid, UTC+02:00).
```

The zone id comes from `_timeProvider.LocalTimeZone.Id` and the offset from `now.Offset`. `BuildInstructions` gains a `TimeZoneInfo` parameter (passed from the provider) so it stays a pure, testable function.

### B. Per-message timestamp (`OpenRouterChatClient`)

Convert the stored UTC timestamp to the local zone **at render time**, before formatting with `zzz`:

```csharp
var local = TimeZoneInfo.ConvertTime(timestamp.Value, _timeProvider.LocalTimeZone);
// [Current time: 2026-06-26 16:40:00 +02:00]
```

`OpenRouterChatClient` gains an injected `TimeProvider` (also replacing the bare `DateTimeOffset.UtcNow` at line 145 with `_timeProvider.GetUtcNow()`). Storage is untouched — `ChatMonitor` keeps writing `DateTimeOffset.UtcNow` — so the persisted chat-history format, parsed independently by the agent's store and the SignalR/WebChat reader, does not change.

### C. Cron (Cronos) — `ICronValidator` / `CronValidator`

Replace NCrontab with Cronos:

```csharp
bool IsValid(string cronExpression);
DateTime? GetNextOccurrence(string cronExpression, DateTimeOffset from, TimeZoneInfo zone);
```

- `IsValid` → `CronExpression.TryParse`.
- `GetNextOccurrence` → `expr.GetNextOccurrence(from, zone)` (a `DateTimeOffset?`), projected to a UTC `DateTime` (`DateTimeKind.Utc`) so the store/score logic is unchanged.

Callers:
- `ScheduleFileSystem.ComputeNextRunAt` and `ScheduleDispatcherService` pass `_timeProvider.GetUtcNow()` + `_timeProvider.LocalTimeZone`.
- `MemoryDreamingService` (internal 3 AM dreaming cron) passes `TimeZoneInfo.Utc` to keep its current meaning.

### D. One-shot `runAt` (`ScheduleFileSystem`)

`ScheduleFileSystem` gains an injected `TimeProvider`; the zone is `_timeProvider.LocalTimeZone`.

- Input carries `Z`/offset (`Kind != Unspecified`) → honor it → `ToUniversalTime()` (as today).
- Input is a bare local datetime (`Kind == Unspecified`) → interpret in the local zone: `TimeZoneInfo.ConvertTimeToUtc(runAt, zone)`. This replaces today's "must include a time zone" rejection.
- Future-check (`> GetUtcNow()`) unchanged; stored `RunAt` stays UTC. `CreatedAt` uses `_timeProvider.GetUtcNow()`.

### E. Schedule rendering (`ScheduleFileSystem.RenderSpec` / `RenderStatus`)

`createdAt` / `lastRunAt` / `nextRunAt` (status.json) and the echoed `runAt` (schedule.json) render in the local zone as offset datetimes, so the LLM reads timing in local terms. Stored values stay UTC. (These become instance methods using the injected zone.)

### F. `SchedulingPrompt`

Becomes `Build(string zoneId)` (currently a `const`; `McpSystemPrompt` is updated to call the builder with `TimeZoneInfo.Local.Id`). New copy: cron times are in `<zone>` and DST-aware; `runAt` accepts any zone (`Z`/offset) or a bare local time interpreted as `<zone>`; everything is stored as UTC. Examples name the actual zone.

## Data flow (after change)

```
TZ=Europe/Madrid (docker-compose, one place) ──► TimeZoneInfo.Local / TimeProvider.LocalTimeZone
        │
        ├─ Agent: McpAgent.GetLocalNow() ─────► system-prompt date (Madrid + zone label)
        │         OpenRouterChatClient ───────► [Current time …] (Madrid offset)
        │
        └─ mcp-scheduling: CronValidator.GetNextOccurrence(now, Local) ─► UTC instant ─► store
                           ScheduleFileSystem runAt: bare→Local→UTC | offset→UTC
                           RenderSpec/RenderStatus ─► Madrid offset datetimes (LLM reads)
```

## Testing (TDD, red first)

- **CronValidator (Cronos):** next occurrence of a daily cron across a spring-forward and a fall-back boundary in a non-UTC zone (zone passed directly as a param) → expected UTC instants; `IsValid` parity with the old accepted/rejected expressions.
- **ScheduleFileSystem** (fake `TimeProvider` with a fixed `LocalTimeZone`): bare `runAt` → local→UTC; offset `runAt` honored; past `runAt` rejected; `ComputeNextRunAt` uses the zone; status/spec render in the zone.
- **McpAgent.BuildInstructions:** given a fixed `DateTimeOffset` + `TimeZoneInfo`, the date line renders in the zone with the zone label.
- **OpenRouterChatClient prefix** (fake `TimeProvider`): a stored UTC timestamp renders with the zone offset.

## Migration

No stored data is rewritten. Setting `TZ=Europe/Madrid` re-interprets existing recurring crons as Madrid wall-clock (intended) and shifts surfaced timestamps to Madrid. Reverting to `TZ=UTC` restores today's behavior exactly.

## Risks

- **tzdata in image** — confirm `Europe/Madrid` resolves in the runtime container; install `tzdata` if a slim base lacks it. (Verification step in the plan.)
- **Container-wide effect** — `TZ` also localizes log timestamps in the affected containers. Acceptable/expected; storage and inter-service payloads stay UTC because they use `UtcNow`, not local time.
- **Cronos syntax** — confirm every existing cron expression in use parses under Cronos (5-field standard expressions are compatible); covered by `IsValid` parity tests.

## Affected files

- `Infrastructure/Infrastructure.csproj` (add Cronos), `Domain/Contracts/ICronValidator.cs`, `Infrastructure/Validation/CronValidator.cs`
- `Infrastructure/Agents/McpAgent.cs` (zone label), `Infrastructure/Agents/ChatClients/OpenRouterChatClient.cs` (inject `TimeProvider`, render conversion) + its DI registration
- `Domain/Tools/Scheduling/Vfs/ScheduleFileSystem.cs` (inject `TimeProvider`; runAt + render), `McpServerScheduling/Services/ScheduleDispatcherService.cs` (inject `TimeProvider`), `McpServerScheduling/Modules/ConfigModule.cs` (register `TimeProvider.System`)
- `Infrastructure/Memory/MemoryDreamingService.cs` (pass `TimeZoneInfo.Utc`)
- `Domain/Prompts/SchedulingPrompt.cs` (`Build(zoneId)`), `McpServerScheduling/McpPrompts/McpSystemPrompt.cs`
- `DockerCompose/docker-compose.yml` (TZ anchor on `agent` + `mcp-scheduling`); runtime Dockerfile(s) if `tzdata` is missing
- Tests under `Tests/Unit` (and existing scheduling/prompt tests touched by signature changes)
