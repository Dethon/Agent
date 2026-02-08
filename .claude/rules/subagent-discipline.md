---
paths:
  - "docs/plans/*.md"
---

# Subagent-Driven Development Discipline

When using the subagent-driven-development skill, the orchestrator (you) MUST NOT write or modify code directly. All work goes through subagents.

## Hard Rules

### Implementation
- **Every implementation task MUST be dispatched to an implementer subagent** — no exceptions, no "this is simple enough to do directly"
- The orchestrator reads files and provides context, but never edits code

### Verification
- **After every implementation subagent completes, dispatch a spec reviewer subagent** — verify the work matches the plan
- **After spec review passes, dispatch a code quality reviewer subagent** — verify the code meets standards
- **After all tasks complete, dispatch a final code reviewer subagent** for the entire implementation
- **Before finishing a development, invoke a final verification**

### Review Loops
- If a reviewer finds issues, the implementer subagent fixes them (or a new fix subagent is dispatched)
- The reviewer reviews again after fixes
- Never mark a task complete with open review issues

## What the Orchestrator Does

- Reads the plan and extracts tasks
- Creates and manages the task list
- Provides context to subagents (full task text, relevant code, architectural context)
- Answers subagent questions
- Dispatches reviewers after implementation
- Tracks progress and moves to next task

## What the Orchestrator Does NOT Do

- Edit or write source code files
- Skip reviewer subagents for any reason
- Claim work is complete without verification subagent evidence
- Reference previous test runs instead of running tests in the current step
