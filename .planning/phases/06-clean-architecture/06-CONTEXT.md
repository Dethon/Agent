# Phase 6: Clean Architecture Alignment - Context

**Gathered:** 2026-01-20
**Status:** Ready for planning

<domain>
## Phase Boundary

Move INotifier implementation from Agent/Hubs to Infrastructure, register state stores in proper layer, verify no layer violations in refactored code. Dependency flow: Domain ← Infrastructure ← Agent.

</domain>

<decisions>
## Implementation Decisions

### INotifier Migration
- Abstract hub dependency: Create `IHubNotificationSender` interface in Domain/Contracts
- Implementation split: `HubNotifier` in Infrastructure implements `INotifier` using `IHubNotificationSender`
- Adapter location: `HubNotificationAdapter` stays in Agent/Hubs, wraps `IHubContext<ChatHub>`
- Naming: Rename for clarity — `Notifier` → `HubNotifier` (Infrastructure), new `HubNotificationAdapter` (Agent)

### Store Registration
- Extension method: Create `AddWebChatStores()` for store registration
- Effects separate: Create `AddWebChatEffects()` as separate extension method
- Audit: Verify stores only reference Domain/DTOs, not Domain contracts or services
- Extension location: Claude's discretion based on codebase patterns

### Layer Violation Handling
- Detection method: Manual code review via using statement analysis
- Acceptable references: DTOs can be referenced from any layer (they're just data)
- Resolution: Fix violations immediately during this phase
- ChatHub audit: Verify ChatHub only orchestrates, contains no business logic

### Claude's Discretion
- Extension method file location (Extensions folder vs State folder)
- Specific grep patterns for violation detection
- Order of operations within the phase

</decisions>

<specifics>
## Specific Ideas

- Agent layer should have no state management implementations (only DI registration and adapters)
- Infrastructure contains INotifier implementation
- WebChat.Client contains all client-side stores
- Clean compilation with no layer violation warnings

</specifics>

<deferred>
## Deferred Ideas

None — discussion stayed within phase scope

</deferred>

---

*Phase: 06-clean-architecture*
*Context gathered: 2026-01-20*
