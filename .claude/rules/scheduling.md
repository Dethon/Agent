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
- **Channel** — `ScheduleDispatcherService` polls `IScheduleStore` for due schedules, `ScheduleFirePlanner` chooses delete-after-fire (one-shot) vs. update-next-run (cron), and `ScheduleNotificationEmitter` emits `channel/message`. The agent runs the prompt; `ChatMonitor` fans the result out to `deliverTo`, minting conversations as needed.

The `scheduling_prompt` (`Domain/Prompts/SchedulingPrompt.cs`) teaches the `/schedules` idiom.
