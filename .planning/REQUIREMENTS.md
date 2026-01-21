# Requirements: Agent WebChat

**Defined:** 2026-01-21
**Core Value:** People can have personalized conversations with agents in shared topics

## v1.1 Requirements

Requirements for Users in Web UI milestone.

### User Identity

- [ ] **USER-01**: User can set username via compact picker UI
- [ ] **USER-02**: Username persists in localStorage across sessions
- [ ] **USER-03**: Avatar determined by hardcoded username->avatar lookup

### Message Attribution

- [ ] **MSG-01**: Messages display sender's username
- [ ] **MSG-02**: Messages display sender's avatar (from lookup)
- [ ] **MSG-03**: User's own messages visually distinguished from others

### Backend Integration

- [ ] **BACK-01**: Username sent to backend on SignalR connection
- [ ] **BACK-02**: Username included in message payloads to server
- [ ] **BACK-03**: Agent prompts include username for personalization

## Future Requirements

(None planned — scope focused on core user identity)

## Out of Scope

| Feature | Reason |
|---------|--------|
| Avatar selection UI | Avatars hardcoded per username, no picker needed |
| Topic sharing/broadcasting | Already implemented in current system |
| Authentication/passwords | Lightweight identity only, no auth system |
| User accounts on server | Usernames stored client-side only |
| Private topics | All topics visible to all users by design |

## Traceability

| Requirement | Phase | Status |
|-------------|-------|--------|
| USER-01 | Phase 8 | Pending |
| USER-02 | Phase 8 | Pending |
| USER-03 | Phase 8 | Pending |
| MSG-01 | Phase 9 | Pending |
| MSG-02 | Phase 9 | Pending |
| MSG-03 | Phase 9 | Pending |
| BACK-01 | Phase 10 | Pending |
| BACK-02 | Phase 10 | Pending |
| BACK-03 | Phase 10 | Pending |

**Coverage:**
- v1.1 requirements: 9 total
- Mapped to phases: 9
- Unmapped: 0

---
*Requirements defined: 2026-01-21*
*Last updated: 2026-01-21 — phase mappings added*
