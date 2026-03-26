# Test Reorganization Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reorganize the test project so every test folder maps 1:1 to a source project, with consistent naming.

**Architecture:** Pure file moves + namespace/class renames. No new code, no behavioral changes. Each task is a logical group of moves followed by a compile check.

**Tech Stack:** .NET 10, xunit

**Spec:** `docs/superpowers/specs/2026-03-26-test-reorganization-design.md`

---

### Task 1: Move WebChat/Fixtures to WebChat.Client/Fixtures

Fixtures move first because other files depend on them. Update the namespace and all consumers.

**Files:**
- Move: `Tests/Unit/WebChat/Fixtures/FakeApprovalService.cs` → `Tests/Unit/WebChat.Client/Fixtures/FakeApprovalService.cs`
- Move: `Tests/Unit/WebChat/Fixtures/FakeChatMessagingService.cs` → `Tests/Unit/WebChat.Client/Fixtures/FakeChatMessagingService.cs`
- Move: `Tests/Unit/WebChat/Fixtures/FakeTopicService.cs` → `Tests/Unit/WebChat.Client/Fixtures/FakeTopicService.cs`
- Modify: `Tests/Unit/WebChat.Client/State/SendMessageEffectTests.cs` (update using)
- Modify: `Tests/Unit/WebChat.Client/State/TopicDeleteEffectTests.cs` (update using)
- Modify: `Tests/Integration/WebChat/Client/ConcurrentStreamingTests.cs` (update using)

- [ ] **Step 1: Create target directory and move files**

```bash
mkdir -p Tests/Unit/WebChat.Client/Fixtures
git mv Tests/Unit/WebChat/Fixtures/FakeApprovalService.cs Tests/Unit/WebChat.Client/Fixtures/
git mv Tests/Unit/WebChat/Fixtures/FakeChatMessagingService.cs Tests/Unit/WebChat.Client/Fixtures/
git mv Tests/Unit/WebChat/Fixtures/FakeTopicService.cs Tests/Unit/WebChat.Client/Fixtures/
```

- [ ] **Step 2: Update namespace in all three fixture files**

In each of the 3 files in `Tests/Unit/WebChat.Client/Fixtures/`, change:
```csharp
namespace Tests.Unit.WebChat.Fixtures;
```
to:
```csharp
namespace Tests.Unit.WebChat.Client.Fixtures;
```

- [ ] **Step 3: Update using statements in consumers**

Files that import `Tests.Unit.WebChat.Fixtures` need the using changed to `Tests.Unit.WebChat.Client.Fixtures`:

- `Tests/Unit/WebChat/Client/StreamResumeServiceTests.cs` — change `using Tests.Unit.WebChat.Fixtures;` → `using Tests.Unit.WebChat.Client.Fixtures;`
- `Tests/Unit/WebChat/Client/StreamingServiceTests.cs` — change `using Tests.Unit.WebChat.Fixtures;` → `using Tests.Unit.WebChat.Client.Fixtures;`
- `Tests/Unit/WebChat.Client/State/SendMessageEffectTests.cs` — change `using Tests.Unit.WebChat.Fixtures;` → `using Tests.Unit.WebChat.Client.Fixtures;`
- `Tests/Unit/WebChat.Client/State/TopicDeleteEffectTests.cs` — change `using Tests.Unit.WebChat.Fixtures;` → `using Tests.Unit.WebChat.Client.Fixtures;`
- `Tests/Integration/WebChat/Client/ConcurrentStreamingTests.cs` — change `using Tests.Unit.WebChat.Fixtures;` → `using Tests.Unit.WebChat.Client.Fixtures;`

- [ ] **Step 4: Verify build**

```bash
dotnet build Tests/Tests.csproj
```
Expected: Build succeeded.

- [ ] **Step 5: Commit**

```bash
git add -A Tests/Unit/WebChat.Client/Fixtures/ Tests/Unit/WebChat/Fixtures/ Tests/Unit/WebChat/Client/ Tests/Unit/WebChat.Client/State/ Tests/Integration/WebChat/Client/
git commit -m "refactor: move WebChat test fixtures to WebChat.Client/Fixtures"
```

---

### Task 2: Move WebChat/Client test files to WebChat.Client

**Files:**
- Move: `Tests/Unit/WebChat/Client/BufferRebuildUtilityTests.cs` → `Tests/Unit/WebChat.Client/State/BufferRebuildUtilityTests.cs`
- Move: `Tests/Unit/WebChat/Client/ChatHistoryMessageExtensionsTests.cs` → `Tests/Unit/WebChat.Client/State/ChatHistoryMessageExtensionsTests.cs`
- Move: `Tests/Unit/WebChat/Client/MessageMergeTests.cs` → `Tests/Unit/WebChat.Client/State/MessageMergeTests.cs`
- Move: `Tests/Unit/WebChat/Client/MessagesReducersTests.cs` → `Tests/Unit/WebChat.Client/State/MessagesReducersTests.cs`
- Move: `Tests/Unit/WebChat/Client/ToastStoreTests.cs` → `Tests/Unit/WebChat.Client/State/ToastStoreTests.cs`
- Move: `Tests/Unit/WebChat/Client/StreamResumeServiceTests.cs` → `Tests/Unit/WebChat.Client/Services/StreamResumeServiceTests.cs`
- Move: `Tests/Unit/WebChat/Client/StreamingServiceTests.cs` → `Tests/Unit/WebChat.Client/Services/StreamingServiceTests.cs`
- Move: `Tests/Unit/WebChat/Client/TransientErrorFilterTests.cs` → `Tests/Unit/WebChat.Client/Services/TransientErrorFilterTests.cs`

- [ ] **Step 1: Move State-related test files**

```bash
git mv Tests/Unit/WebChat/Client/BufferRebuildUtilityTests.cs Tests/Unit/WebChat.Client/State/
git mv Tests/Unit/WebChat/Client/ChatHistoryMessageExtensionsTests.cs Tests/Unit/WebChat.Client/State/
git mv Tests/Unit/WebChat/Client/MessageMergeTests.cs Tests/Unit/WebChat.Client/State/
git mv Tests/Unit/WebChat/Client/MessagesReducersTests.cs Tests/Unit/WebChat.Client/State/
git mv Tests/Unit/WebChat/Client/ToastStoreTests.cs Tests/Unit/WebChat.Client/State/
```

- [ ] **Step 2: Move Services-related test files**

```bash
git mv Tests/Unit/WebChat/Client/StreamResumeServiceTests.cs Tests/Unit/WebChat.Client/Services/
git mv Tests/Unit/WebChat/Client/StreamingServiceTests.cs Tests/Unit/WebChat.Client/Services/
git mv Tests/Unit/WebChat/Client/TransientErrorFilterTests.cs Tests/Unit/WebChat.Client/Services/
```

- [ ] **Step 3: Update namespaces in all 8 moved files**

In each moved file, change:
```csharp
namespace Tests.Unit.WebChat.Client;
```
to the appropriate namespace matching the new folder:

Files in `Tests/Unit/WebChat.Client/State/`:
```csharp
namespace Tests.Unit.WebChat.Client.State;
```

Files in `Tests/Unit/WebChat.Client/Services/`:
```csharp
namespace Tests.Unit.WebChat.Client.Services;
```

- [ ] **Step 4: Move VapidConfigTests.cs**

```bash
git mv Tests/Unit/WebChat/VapidConfigTests.cs Tests/Unit/WebChat.Client/Services/
```

In `Tests/Unit/WebChat.Client/Services/VapidConfigTests.cs`, change:
```csharp
namespace Tests.Unit.WebChat;
```
to:
```csharp
namespace Tests.Unit.WebChat.Client.Services;
```

- [ ] **Step 5: Delete the now-empty Unit/WebChat directory**

```bash
rm -rf Tests/Unit/WebChat
```

- [ ] **Step 6: Verify build**

```bash
dotnet build Tests/Tests.csproj
```
Expected: Build succeeded.

- [ ] **Step 7: Commit**

```bash
git add -A Tests/Unit/WebChat/ Tests/Unit/WebChat.Client/
git commit -m "refactor: consolidate WebChat.Client tests into single directory"
```

---

### Task 3: Move misplaced unit test files

**Files:**
- Move: `Tests/Unit/ChatMessageSerializationTests.cs` → `Tests/Unit/Domain/ChatMessageSerializationTests.cs`
- Move: `Tests/Unit/Tools/GlobFilesToolTests.cs` → `Tests/Unit/Domain/Tools/GlobFilesToolTests.cs`

- [ ] **Step 1: Move ChatMessageSerializationTests.cs**

```bash
git mv Tests/Unit/ChatMessageSerializationTests.cs Tests/Unit/Domain/
```

In `Tests/Unit/Domain/ChatMessageSerializationTests.cs`, change:
```csharp
namespace Tests.Unit;
```
to:
```csharp
namespace Tests.Unit.Domain;
```

- [ ] **Step 2: Move GlobFilesToolTests.cs**

```bash
mkdir -p Tests/Unit/Domain/Tools
git mv Tests/Unit/Tools/GlobFilesToolTests.cs Tests/Unit/Domain/Tools/
rm -rf Tests/Unit/Tools
```

In `Tests/Unit/Domain/Tools/GlobFilesToolTests.cs`, change:
```csharp
namespace Tests.Unit.Tools;
```
to:
```csharp
namespace Tests.Unit.Domain.Tools;
```

- [ ] **Step 3: Verify build**

```bash
dotnet build Tests/Tests.csproj
```
Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add -A Tests/Unit/ChatMessageSerializationTests.cs Tests/Unit/Domain/ Tests/Unit/Tools/
git commit -m "refactor: move misplaced unit tests to correct project folders"
```

---

### Task 4: Delete empty directories

- [ ] **Step 1: Remove dead directories**

```bash
rm -rf Tests/Unit/Agent
rm -rf Tests/Unit/Integration
```

- [ ] **Step 2: Verify no files were lost**

```bash
git status
```
Expected: Only the two directory deletions show up. No files should be listed as deleted.

- [ ] **Step 3: Commit**

```bash
git add -A Tests/Unit/Agent Tests/Unit/Integration
git commit -m "refactor: remove empty test directories"
```

---

### Task 5: Rename integration test files and classes

13 files need renaming: drop "Integration" from file names and class names. One cross-reference exists: `ToolApprovalChatClientIntegrationTests.cs` references `McpAgentIntegrationTests` via `.AddUserSecrets<McpAgentIntegrationTests>()` — both are being renamed so update the reference.

`EmbeddingIntegrationTests.cs` contains two classes: `OpenRouterEmbeddingServiceIntegrationTests` and `MemoryStoreWithEmbeddingsIntegrationTests` — rename both.

**Files:**
- Rename: `Tests/Integration/Agents/McpAgentIntegrationTests.cs` → `McpAgentTests.cs`
- Rename: `Tests/Integration/Agents/McpSamplingHandlerIntegrationTests.cs` → `McpSamplingHandlerTests.cs`
- Rename: `Tests/Integration/Agents/McpSubscriptionManagerIntegrationTests.cs` → `McpSubscriptionManagerTests.cs`
- Rename: `Tests/Integration/Agents/OpenRouterReasoningIntegrationTests.cs` → `OpenRouterReasoningTests.cs`
- Rename: `Tests/Integration/Agents/OpenRouterToolCallingWithReasoningIntegrationTests.cs` → `OpenRouterToolCallingWithReasoningTests.cs`
- Rename: `Tests/Integration/Agents/SubAgentIntegrationTests.cs` → `SubAgentTests.cs`
- Rename: `Tests/Integration/Agents/ThreadSessionIntegrationTests.cs` → `ThreadSessionTests.cs`
- Rename: `Tests/Integration/Agents/ToolApprovalChatClientIntegrationTests.cs` → `ToolApprovalChatClientTests.cs`
- Rename: `Tests/Integration/Clients/BraveSearchClientIntegrationTests.cs` → `BraveSearchClientTests.cs`
- Rename: `Tests/Integration/Clients/PlaywrightWebBrowserIntegrationTests.cs` → `PlaywrightWebBrowserTests.cs`
- Rename: `Tests/Integration/McpServerTests/SubscriptionMonitorIntegrationTests.cs` → `SubscriptionMonitorTests.cs`
- Rename: `Tests/Integration/McpTools/ResubscribeDownloadsToolIntegrationTests.cs` → `ResubscribeDownloadsToolTests.cs`
- Rename: `Tests/Integration/Memory/EmbeddingIntegrationTests.cs` → `EmbeddingTests.cs`

- [ ] **Step 1: Rename all 13 files**

```bash
git mv Tests/Integration/Agents/McpAgentIntegrationTests.cs Tests/Integration/Agents/McpAgentTests.cs
git mv Tests/Integration/Agents/McpSamplingHandlerIntegrationTests.cs Tests/Integration/Agents/McpSamplingHandlerTests.cs
git mv Tests/Integration/Agents/McpSubscriptionManagerIntegrationTests.cs Tests/Integration/Agents/McpSubscriptionManagerTests.cs
git mv Tests/Integration/Agents/OpenRouterReasoningIntegrationTests.cs Tests/Integration/Agents/OpenRouterReasoningTests.cs
git mv Tests/Integration/Agents/OpenRouterToolCallingWithReasoningIntegrationTests.cs Tests/Integration/Agents/OpenRouterToolCallingWithReasoningTests.cs
git mv Tests/Integration/Agents/SubAgentIntegrationTests.cs Tests/Integration/Agents/SubAgentTests.cs
git mv Tests/Integration/Agents/ThreadSessionIntegrationTests.cs Tests/Integration/Agents/ThreadSessionTests.cs
git mv Tests/Integration/Agents/ToolApprovalChatClientIntegrationTests.cs Tests/Integration/Agents/ToolApprovalChatClientTests.cs
git mv Tests/Integration/Clients/BraveSearchClientIntegrationTests.cs Tests/Integration/Clients/BraveSearchClientTests.cs
git mv Tests/Integration/Clients/PlaywrightWebBrowserIntegrationTests.cs Tests/Integration/Clients/PlaywrightWebBrowserTests.cs
git mv Tests/Integration/McpServerTests/SubscriptionMonitorIntegrationTests.cs Tests/Integration/McpServerTests/SubscriptionMonitorTests.cs
git mv Tests/Integration/McpTools/ResubscribeDownloadsToolIntegrationTests.cs Tests/Integration/McpTools/ResubscribeDownloadsToolTests.cs
git mv Tests/Integration/Memory/EmbeddingIntegrationTests.cs Tests/Integration/Memory/EmbeddingTests.cs
```

- [ ] **Step 2: Rename class names inside each file**

In each file, find-and-replace the class name to drop "Integration":

- `McpAgentTests.cs`: `McpAgentIntegrationTests` → `McpAgentTests`
- `McpSamplingHandlerTests.cs`: `McpSamplingHandlerIntegrationTests` → `McpSamplingHandlerTests`
- `McpSubscriptionManagerTests.cs`: `McpSubscriptionManagerIntegrationTests` → `McpSubscriptionManagerTests`
- `OpenRouterReasoningTests.cs`: `OpenRouterReasoningIntegrationTests` → `OpenRouterReasoningTests`
- `OpenRouterToolCallingWithReasoningTests.cs`: `OpenRouterToolCallingWithReasoningIntegrationTests` → `OpenRouterToolCallingWithReasoningTests`
- `SubAgentTests.cs`: `SubAgentIntegrationTests` → `SubAgentTests`
- `ThreadSessionTests.cs`: `ThreadSessionIntegrationTests` → `ThreadSessionTests`
- `ToolApprovalChatClientTests.cs`: `ToolApprovalChatClientIntegrationTests` → `ToolApprovalChatClientTests`
- `BraveSearchClientTests.cs`: `BraveSearchClientIntegrationTests` → `BraveSearchClientTests` (note: check if this name conflicts with the unit test `Tests/Unit/Infrastructure/BraveSearchClientTests.cs` — they are in different namespaces so no conflict)
- `PlaywrightWebBrowserTests.cs`: `PlaywrightWebBrowserIntegrationTests` → `PlaywrightWebBrowserTests`
- `SubscriptionMonitorTests.cs`: `SubscriptionMonitorIntegrationTests` → `SubscriptionMonitorTests`
- `ResubscribeDownloadsToolTests.cs`: `ResubscribeDownloadsToolIntegrationTests` → `ResubscribeDownloadsToolTests`
- `EmbeddingTests.cs`: `OpenRouterEmbeddingServiceIntegrationTests` → `OpenRouterEmbeddingServiceTests` AND `MemoryStoreWithEmbeddingsIntegrationTests` → `MemoryStoreWithEmbeddingsTests`

- [ ] **Step 3: Fix cross-reference in ToolApprovalChatClientTests.cs**

In `Tests/Integration/Agents/ToolApprovalChatClientTests.cs`, line 17, change:
```csharp
.AddUserSecrets<McpAgentIntegrationTests>()
```
to:
```csharp
.AddUserSecrets<McpAgentTests>()
```

- [ ] **Step 4: Verify build**

```bash
dotnet build Tests/Tests.csproj
```
Expected: Build succeeded.

- [ ] **Step 5: Commit**

```bash
git add -A Tests/Integration/
git commit -m "refactor: standardize integration test naming to *Tests convention"
```

---

### Task 6: Final verification

- [ ] **Step 1: Run all unit tests**

```bash
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~Tests.Unit" --no-build
```
Expected: All tests pass.

- [ ] **Step 2: Verify no stale directories remain**

```bash
find Tests -type d -empty ! -path "*/bin/*" ! -path "*/obj/*"
```
Expected: No output (no empty directories).

- [ ] **Step 3: Verify folder structure matches source projects**

```bash
ls Tests/Unit/
```
Expected directories: `Dashboard.Client`, `Domain`, `Infrastructure`, `McpChannelServiceBus`, `McpChannelSignalR`, `McpChannelTelegram`, `McpServerLibrary`, `Observability`, `Shared`, `WebChat.Client`

No files at root level. No `Agent/`, `Integration/`, `Tools/`, `WebChat/` directories.
