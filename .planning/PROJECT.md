# WebChat Stack Refactoring

## What This Is

A maintainability refactor of the WebChat stack (Blazor client and SignalR server) to establish unidirectional data flow and fix layer violations. The goal is code that's easier to follow, extend, and maintains the existing Clean Architecture boundaries.

## Core Value

State flows in one direction — down from stores, up via events — making it obvious where state lives and how changes propagate.

## Requirements

### Validated

Existing functionality that works and must continue working:

- ✓ Real-time message streaming via SignalR — existing
- ✓ Topic-based conversations with persistence — existing
- ✓ Stream resumption after disconnection — existing
- ✓ Multi-agent selection — existing
- ✓ Tool approval flow via WebChat — existing
- ✓ Message history loading — existing

### Active

- [ ] Establish unidirectional data flow in WebChat.Client
- [ ] Refactor StreamingCoordinator to single-responsibility components
- [ ] Consolidate scattered state into centralized stores
- [ ] Refactor SignalR hub for clear command/event separation
- [ ] Move INotifier implementation from Agent to Infrastructure
- [ ] Ensure all WebChat code respects Domain → Infrastructure → Agent layering

### Out of Scope

- New WebChat features — this is refactoring, not feature work
- Telegram or CLI interfaces — different codepaths, not affected
- MCP server changes — orthogonal concern
- Performance optimization — focus is structure, not speed

## Context

The WebChat stack currently has:
- `StreamingCoordinator` at 416 lines managing streaming state, throttling, and reconnection
- State scattered across multiple services in `WebChat.Client/Services/`
- Bidirectional communication patterns between components
- `INotifier` implemented in `Agent/Hubs/` instead of Infrastructure layer

The existing architecture follows Clean Architecture (Domain → Infrastructure → Agent) and this refactor should align WebChat with those patterns.

Relevant codebase docs:
- `.planning/codebase/ARCHITECTURE.md` — overall system design
- `.planning/codebase/CONCERNS.md` — identified fragile areas including StreamingCoordinator

## Constraints

- **Backwards compatibility**: All existing WebChat functionality must continue working
- **No API changes**: SignalR hub method signatures should remain stable for deployed clients
- **Incremental**: Changes should be deployable incrementally, not a big-bang rewrite

## Key Decisions

| Decision | Rationale | Outcome |
|----------|-----------|---------|
| Unidirectional data flow | Eliminates confusing bidirectional state updates | — Pending |
| Centralized state stores | Single source of truth per concern | — Pending |
| INotifier to Infrastructure | Respects layer boundaries | — Pending |

---
*Last updated: 2026-01-19 after initialization*
