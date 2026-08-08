---
paths:
  - "McpServerScheduling/**"
  - "Domain/Tools/Scheduling/**"
  - "Domain/Prompts/SchedulingPrompt.cs"
  - "Domain/DTOs/Schedule.cs"
---

# Scheduling Architecture

`McpServerScheduling` is a dual-role MCP server:
- **`filesystem://schedules` resource** (mount `/schedules`) — managed with the standard `domain__filesystem__*` tools. Layout: `/schedules/<agentId>/<scheduleId>/schedule.json` (`{prompt, cron|runAt, userId?, deliverTo?}` — exactly one of recurring `cron` or one-shot `runAt`), plus `agent_info.json` and read-only `status.json` (`createdAt`/`lastRunAt`/`nextRunAt`). `fs_exec run_now.sh` on a schedule directory fires it immediately. The `ScheduleFileSystem` engine (`Domain/Tools/Scheduling/Vfs/`) implements `IFileSystemBackend`, returning typed `FsResult<T>`.
- **Channel** — registered through `AddChannelServer(DeliveryPolicy.GateOnLive)` (`Mcp.Hosting`). `ScheduleDispatcherService` polls `IScheduleStore` for due schedules, `ScheduleFirePlanner` chooses delete-after-fire (one-shot) vs. update-next-run (cron), and the shared `ChannelNotificationEmitter` emits `channel/message`. Gate-on-live is load-bearing: the dispatcher deletes or advances a schedule only when the emit reports a live subscriber, and a false return buffers nothing, so a failed delivery leaves no duplicate to fire alongside the retry. The agent runs the prompt; `ChatMonitor` fans the result out to `deliverTo`, minting conversations as needed.

The `scheduling_prompt` (`Domain/Prompts/SchedulingPrompt.cs`) teaches the `/schedules` idiom.

**Act vs tell decides the subsystem, not how the time is phrased.** `/timers` and the HA alarms
calendar exist to *tell a person something*; `/schedules` exists to *make something happen*. So
"apaga el aire en una hora" is a `/schedules` one-shot (`runAt`) even though it is a duration well
inside the timers ceiling. A rule that asks only how the time is expressed swallows it into
`/timers`, where the command lands in the timer's `text` field and is merely spoken aloud while the
air conditioning stays on. That field is a spoken message, never an instruction — the backstop is
stated in `TimerPrompt` where the field is filled in, and the same boundary is drawn from the other
two sides in `TimerPrompt` and `HomeAssistantPrompt`.

**A schedule defaults to the agent that writes it.** Ownership is the `<agentId>` path segment and
nothing re-derives it at fire time. Told only to read `agent_info.json` to learn what an agent does,
an agent whose own catalog description advertises reply style rather than abilities hands its
deferred actions to whichever agent's blurb happens to name the subject — and the result then comes
back on that agent's channel. Schedule against yourself unless the user names another agent.
