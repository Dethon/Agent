# Phase 8: User Identity - Context

**Gathered:** 2026-01-21
**Status:** Ready for planning

<domain>
## Phase Boundary

Users can establish their identity in the app. A circular avatar button allows selecting from predefined users, with the selection persisted in localStorage. Avatars are mapped to users via configuration. Message attribution and backend integration are separate phases.

</domain>

<decisions>
## Implementation Decisions

### Picker appearance
- Circular avatar button in header/toolbar area
- Clicking opens a dropdown showing available users (avatar + username)
- Question mark icon displayed when no user is selected
- Immediate selection — click a user, dropdown closes, selection applies
- Currently selected user highlighted with different background color in dropdown

### Avatar mapping
- Users and avatars defined in a config file (JSON or similar), not hardcoded
- Avatar images stored as static assets (wwwroot)
- 2-3 users initially
- No style restrictions — photos, icons, or mixed styles all acceptable

### Change username flow
- Same avatar button used to switch users (click to reopen dropdown)
- No confirmation required when switching
- Conversation stays visible when switching users — only sender identity changes
- Per-device user selection stored in localStorage independently

### Claude's Discretion
- Exact dropdown styling and animation
- Config file structure and location
- Avatar image dimensions and naming convention
- Error handling for missing/invalid config

</decisions>

<specifics>
## Specific Ideas

- Dropdown shows both avatar and username for each selectable user
- Keep it compact — this is a quick identity picker, not a full profile system

</specifics>

<deferred>
## Deferred Ideas

None — discussion stayed within phase scope

</deferred>

---

*Phase: 08-user-identity*
*Context gathered: 2026-01-21*
