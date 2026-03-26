# Test Project Reorganization

**Date:** 2026-03-26
**Approach:** Project-mirrored structure — every test folder maps 1:1 to a source project.

## Problem

The test project has several organizational issues:
1. Duplicate WebChat.Client test folders (`Unit/WebChat.Client/` and `Unit/WebChat/Client/`) with identical namespaces
2. Misplaced files at wrong nesting levels
3. Empty/dead directories
4. Inconsistent integration test naming (mix of `*IntegrationTests.cs` and `*Tests.cs`)
5. Vague folder names (`Tools/`, `Shared/`) that don't map to source projects

## Design

### Section 1: WebChat.Client Consolidation

Merge all WebChat.Client tests into `Unit/WebChat.Client/` with clear subfolders:

| Current | New |
|---|---|
| `Unit/WebChat/Client/BufferRebuildUtilityTests.cs` | `Unit/WebChat.Client/State/BufferRebuildUtilityTests.cs` |
| `Unit/WebChat/Client/ChatHistoryMessageExtensionsTests.cs` | `Unit/WebChat.Client/State/ChatHistoryMessageExtensionsTests.cs` |
| `Unit/WebChat/Client/MessageMergeTests.cs` | `Unit/WebChat.Client/State/MessageMergeTests.cs` |
| `Unit/WebChat/Client/MessagesReducersTests.cs` | `Unit/WebChat.Client/State/MessagesReducersTests.cs` |
| `Unit/WebChat/Client/StreamResumeServiceTests.cs` | `Unit/WebChat.Client/Services/StreamResumeServiceTests.cs` |
| `Unit/WebChat/Client/StreamingServiceTests.cs` | `Unit/WebChat.Client/Services/StreamingServiceTests.cs` |
| `Unit/WebChat/Client/ToastStoreTests.cs` | `Unit/WebChat.Client/State/ToastStoreTests.cs` |
| `Unit/WebChat/Client/TransientErrorFilterTests.cs` | `Unit/WebChat.Client/Services/TransientErrorFilterTests.cs` |
| `Unit/WebChat/VapidConfigTests.cs` | `Unit/WebChat.Client/Services/VapidConfigTests.cs` |
| `Unit/WebChat/Fixtures/FakeApprovalService.cs` | `Unit/WebChat.Client/Fixtures/FakeApprovalService.cs` |
| `Unit/WebChat/Fixtures/FakeChatMessagingService.cs` | `Unit/WebChat.Client/Fixtures/FakeChatMessagingService.cs` |
| `Unit/WebChat/Fixtures/FakeTopicService.cs` | `Unit/WebChat.Client/Fixtures/FakeTopicService.cs` |

All namespaces update to match new paths. Delete `Unit/WebChat/` entirely after moves.

### Section 2: Misplaced Files

| Current | New | Reason |
|---|---|---|
| `Unit/ChatMessageSerializationTests.cs` | `Unit/Domain/ChatMessageSerializationTests.cs` | Tests `Domain.Extensions` |
| `Unit/Tools/GlobFilesToolTests.cs` | `Unit/Domain/Tools/GlobFilesToolTests.cs` | Tests `Domain.Tools.Files` |

Delete `Unit/Tools/` after move.

### Section 3: Dead Directory Cleanup

Delete empty directories:
- `Unit/Agent/` and `Unit/Agent/OAuth/`
- `Unit/Integration/` and `Unit/Integration/Fixtures/`

### Section 4: Integration Test Naming Standardization

Drop the `IntegrationTests` suffix → just `Tests`. The `Integration/` folder already conveys the test type.

Files to rename:
- `McpAgentIntegrationTests.cs` → `McpAgentTests.cs`
- `McpSamplingHandlerIntegrationTests.cs` → `McpSamplingHandlerTests.cs`
- `McpSubscriptionManagerIntegrationTests.cs` → `McpSubscriptionManagerTests.cs`
- `OpenRouterReasoningIntegrationTests.cs` → `OpenRouterReasoningTests.cs`
- `OpenRouterToolCallingWithReasoningIntegrationTests.cs` → `OpenRouterToolCallingWithReasoningTests.cs`
- `SubAgentIntegrationTests.cs` → `SubAgentTests.cs`
- `ThreadSessionIntegrationTests.cs` → `ThreadSessionTests.cs`
- `ToolApprovalChatClientIntegrationTests.cs` → `ToolApprovalChatClientTests.cs`
- `BraveSearchClientIntegrationTests.cs` → `BraveSearchClientTests.cs`
- `PlaywrightWebBrowserIntegrationTests.cs` → `PlaywrightWebBrowserTests.cs`
- `SubscriptionMonitorIntegrationTests.cs` → `SubscriptionMonitorTests.cs`
- `ResubscribeDownloadsToolIntegrationTests.cs` → `ResubscribeDownloadsToolTests.cs`
- `EmbeddingIntegrationTests.cs` → `EmbeddingTests.cs`

Class names and namespaces update to match.

### Section 5: Unchanged

- `Unit/Shared/ChannelNotificationEmitterTests.cs` stays — tests behavior across 3 channel projects, no single project home.

## Verification

- All tests compile: `dotnet build Tests/`
- All unit tests pass: `dotnet test Tests/ --filter "FullyQualifiedName~Tests.Unit"`
- No orphaned files or broken namespaces
