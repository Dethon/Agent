# Requirements: Agent WebChat

**Defined:** 2026-01-21
**Core Value:** People can have personalized conversations with agents in shared topics

## v1.1 Requirements

Requirements for Users in Web UI milestone.

### User Identity

- [ ] **USER-01**: User can set username via compact picker UI
- [ ] **USER-02**: Username persists in localStorage across sessions
- [ ] **USER-03**: Avatar determined by hardcoded username→avatar lookup

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
| USER-01 | TBD | Pending |
| USER-02 | TBD | Pending |
| USER-03 | TBD | Pending |
| MSG-01 | TBD | Pending |
| MSG-02 | TBD | Pending |
| MSG-03 | TBD | Pending |
| BACK-01 | TBD | Pending |
| BACK-02 | TBD | Pending |
| BACK-03 | TBD | Pending |

**Coverage:**
- v1.1 requirements: 9 total
- Mapped to phases: 0
- Unmapped: 9 ⚠️

---
*Requirements defined: 2026-01-21*
*Last updated: 2026-01-21 after initial definition*
