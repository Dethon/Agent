---
status: diagnosed
phase: 10-backend-integration
source: [10-01-SUMMARY.md, 10-02-SUMMARY.md]
started: 2026-01-21T05:00:00Z
updated: 2026-01-21T05:00:00Z
---

## Current Test

[testing complete]

## Tests

### 1. User Registration on Connection
expected: After selecting a user in the header picker and opening a topic, you can send messages normally. The connection registers your identity automatically.
result: pass

### 2. Agent Addresses You By Name
expected: When you send a message like "Hi, what's my name?" or "Who am I?", the agent responds using your selected username (e.g., "Hello Alice!" or "You're Alice").
result: pass

### 3. Identity Persists Across Messages
expected: Send multiple messages in the same conversation. The agent continues to know who you are and can reference your name naturally throughout the conversation.
result: pass

### 4. Identity After Page Refresh
expected: Refresh the page (F5), then send a message. The agent still knows your name (identity is re-registered after reconnection).
result: issue
reported: "it does for new messages, but loaded history is attributed to user ?"
severity: major

## Summary

total: 4
passed: 3
issues: 1
pending: 0
skipped: 0

## Gaps

- truth: "Loaded history messages show sender attribution after page refresh"
  status: failed
  reason: "User reported: it does for new messages, but loaded history is attributed to user ?"
  severity: major
  test: 4
  root_cause: "Sender metadata never persisted to Redis or returned via ChatHistoryMessage DTO"
  artifacts:
    - path: "Domain/DTOs/WebChat/ChatHistoryMessage.cs"
      issue: "Only has Role and Content - no sender fields"
    - path: "Agent/Hubs/ChatHub.cs"
      issue: "GetHistory creates ChatHistoryMessage without sender info"
    - path: "WebChat.Client/State/Effects/TopicSelectionEffect.cs"
      issue: "History loading doesn't map sender fields"
    - path: "WebChat.Client/State/Effects/InitializationEffect.cs"
      issue: "LoadTopicHistoryAsync doesn't populate sender fields"
  missing:
    - "Add SenderId, SenderUsername, SenderAvatarUrl to ChatHistoryMessage"
    - "Extract sender from ChatMessage.AdditionalProperties in GetHistory"
    - "Store sender in AdditionalProperties when creating user messages"
    - "Map sender fields in client effects when loading history"
  debug_session: ".planning/debug/chat-history-sender-attribution.md"
