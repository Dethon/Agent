---
paths:
  - "docs/superpowers/plans/**"
---

# Subagent-Driven Development Rules

## Before Starting

- ALWAYS create a git worktree before dispatching implementer subagents
- Create all task tracking entries before the first dispatch

## Implementer Dispatch

- NEVER dispatch an implementer without requiring RED step evidence in the prompt
- Include in every implementer prompt: "Run tests BEFORE implementation and paste the failure output. Then implement and paste the passing output. If you skip either step, report BLOCKED."
- Record the git SHA before dispatching each implementer

## After Each Task

- NEVER move to the next task until both reviews pass
- NEVER batch multiple tasks into a single review
- NEVER skip code quality review
- A task is NOT complete until its review artifact exists at `docs/superpowers/reviews/`

### Per-task checklist (complete in order):

1. Implementer returns — verify report includes RED and GREEN test output
2. Dispatch spec compliance reviewer — verify code matches task requirements
3. If spec issues found: send implementer back, then re-review
4. Record head SHA, dispatch code quality reviewer
5. If quality issues found: send implementer back, then re-review
6. Both reviews pass — write review artifact to `docs/superpowers/reviews/<plan-date>-<plan-name>/task-<N>-review.md`
7. Mark task complete, move to next

### Review Artifact Format

Each task review file MUST contain:

```markdown
# Task N: <name>

## Spec Compliance
- Reviewer: <spec-reviewer result — ✅ or ❌ with details>

## Code Quality
- Reviewer: <code-quality result — Approved or Issues with details>
- Base SHA: <sha before task>
- Head SHA: <sha after task>

## Resolution
- Issues found: <count>
- Issues fixed: <count>
- Final status: ✅ Approved
```

If the review artifact does not exist, the task is NOT done — regardless of what the implementer reported.

## Parallelization

- Independent tasks MAY be dispatched in parallel
- Each parallel task still gets its own individual review cycle afterward
- Do not start dependent tasks until all their dependencies have passed both reviews

## TDD Requirements

Every implementer task that produces code MUST follow Red-Green-Refactor:

1. **RED** — Write failing test first. Run it. Paste the failure output.
2. **GREEN** — Write minimum implementation to pass. Run it. Paste the pass output.
3. **Refactor** — Clean up while keeping tests green.

- NEVER write implementation code without a failing test first
- NEVER accept an implementer report that lacks both RED (failure) and GREEN (pass) test output
- Tasks that are pure configuration (DI wiring, appsettings) or pure types (DTOs, interfaces) are exempt
- All other tasks — including infrastructure classes, runners, factories — require tests BEFORE implementation

## Test Verification

- After all implementer tasks complete, run the FULL test suite (unit, integration, AND E2E) before claiming completion
- Do not dismiss test failures as "pre-existing" without verifying on the base branch
