# Toast Notifications Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Surface errors that are currently swallowed silently in the WebUI through non-intrusive toast notifications.

**Architecture:** Redux-like state management with ToastStore handling toast lifecycle. ToastContainer component mounts at MainLayout root, renders ToastItem components with auto-dismiss timers. StreamingService dispatches ShowError actions on error chunks and exceptions (filtered for transient errors).

**Tech Stack:** Blazor WebAssembly, Rx.NET observables, CSS animations

---

## Task 1: Toast State and Actions

**Files:**
- Create: `WebChat.Client/State/Toast/ToastActions.cs`
- Create: `WebChat.Client/State/Toast/ToastState.cs`

**Step 1: Write ToastActions.cs**

```csharp
namespace WebChat.Client.State.Toast;

public record ShowError(string Message) : IAction;

public record DismissToast(Guid Id) : IAction;
```

**Step 2: Write ToastState.cs**

```csharp
using System.Collections.Immutable;

namespace WebChat.Client.State.Toast;

public sealed record ToastItem(Guid Id, string Message, DateTime CreatedAt);

public sealed record ToastState(ImmutableList<ToastItem> Toasts)
{
    public static ToastState Initial => new(ImmutableList<ToastItem>.Empty);
}
```

**Step 3: Verify files compile**

Run: `dotnet build WebChat.Client/WebChat.Client.csproj`
Expected: Build succeeded

**Step 4: Commit**

```bash
git add WebChat.Client/State/Toast/
git commit -m "feat(toast): add toast state and actions"
```

---

## Task 2: Toast Store with Reducers

**Files:**
- Create: `WebChat.Client/State/Toast/ToastStore.cs`

**Step 1: Write ToastStore.cs**

```csharp
namespace WebChat.Client.State.Toast;

public sealed class ToastStore : IDisposable
{
    private const int MaxToasts = 3;
    private const int MaxMessageLength = 150;

    private readonly Store<ToastState> _store;

    public ToastStore(Dispatcher dispatcher)
    {
        _store = new Store<ToastState>(ToastState.Initial);

        dispatcher.RegisterHandler<ShowError>(action =>
            _store.Dispatch(action, Reduce));
        dispatcher.RegisterHandler<DismissToast>(action =>
            _store.Dispatch(action, Reduce));
    }

    public ToastState State => _store.State;
    public IObservable<ToastState> StateObservable => _store.StateObservable;
    public void Dispose() => _store.Dispose();

    private static ToastState Reduce(ToastState state, ShowError action)
    {
        var message = TruncateMessage(action.Message);

        // Deduplicate: don't add if same message already visible
        if (state.Toasts.Any(t => t.Message == message))
            return state;

        var toast = new ToastItem(Guid.NewGuid(), message, DateTime.UtcNow);
        var toasts = state.Toasts.Add(toast);

        // Enforce max limit by removing oldest
        if (toasts.Count > MaxToasts)
            toasts = toasts.RemoveAt(0);

        return state with { Toasts = toasts };
    }

    private static ToastState Reduce(ToastState state, DismissToast action)
    {
        var toasts = state.Toasts.RemoveAll(t => t.Id == action.Id);
        return state with { Toasts = toasts };
    }

    private static string TruncateMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return "Something went wrong. Please try again.";

        return message.Length <= MaxMessageLength
            ? message
            : string.Concat(message.AsSpan(0, MaxMessageLength), "...");
    }
}
```

**Step 2: Verify file compiles**

Run: `dotnet build WebChat.Client/WebChat.Client.csproj`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add WebChat.Client/State/Toast/ToastStore.cs
git commit -m "feat(toast): add toast store with reducers"
```

---

## Task 3: Toast Store Unit Tests

**Files:**
- Create: `Tests/Unit/WebChat/Client/ToastStoreTests.cs`

**Step 1: Write ToastStoreTests.cs**

```csharp
using Shouldly;
using WebChat.Client.State;
using WebChat.Client.State.Toast;

namespace Tests.Unit.WebChat.Client;

public sealed class ToastStoreTests : IDisposable
{
    private readonly Dispatcher _dispatcher = new();
    private readonly ToastStore _store;

    public ToastStoreTests()
    {
        _store = new ToastStore(_dispatcher);
    }

    public void Dispose() => _store.Dispose();

    [Fact]
    public void ShowError_AddsToast()
    {
        _dispatcher.Dispatch(new ShowError("Test error"));

        _store.State.Toasts.Count.ShouldBe(1);
        _store.State.Toasts[0].Message.ShouldBe("Test error");
    }

    [Fact]
    public void ShowError_WithDuplicateMessage_DoesNotAddSecondToast()
    {
        _dispatcher.Dispatch(new ShowError("Same error"));
        _dispatcher.Dispatch(new ShowError("Same error"));

        _store.State.Toasts.Count.ShouldBe(1);
    }

    [Fact]
    public void ShowError_ExceedsMaxToasts_RemovesOldest()
    {
        _dispatcher.Dispatch(new ShowError("Error 1"));
        _dispatcher.Dispatch(new ShowError("Error 2"));
        _dispatcher.Dispatch(new ShowError("Error 3"));
        _dispatcher.Dispatch(new ShowError("Error 4"));

        _store.State.Toasts.Count.ShouldBe(3);
        _store.State.Toasts.ShouldNotContain(t => t.Message == "Error 1");
        _store.State.Toasts.ShouldContain(t => t.Message == "Error 4");
    }

    [Fact]
    public void ShowError_WithLongMessage_TruncatesTo150Chars()
    {
        var longMessage = new string('x', 200);

        _dispatcher.Dispatch(new ShowError(longMessage));

        _store.State.Toasts[0].Message.Length.ShouldBe(153); // 150 + "..."
        _store.State.Toasts[0].Message.ShouldEndWith("...");
    }

    [Fact]
    public void ShowError_WithEmptyMessage_ShowsDefaultMessage()
    {
        _dispatcher.Dispatch(new ShowError(""));

        _store.State.Toasts[0].Message.ShouldBe("Something went wrong. Please try again.");
    }

    [Fact]
    public void DismissToast_RemovesToast()
    {
        _dispatcher.Dispatch(new ShowError("Test error"));
        var toastId = _store.State.Toasts[0].Id;

        _dispatcher.Dispatch(new DismissToast(toastId));

        _store.State.Toasts.ShouldBeEmpty();
    }

    [Fact]
    public void DismissToast_WithNonExistentId_DoesNothing()
    {
        _dispatcher.Dispatch(new ShowError("Test error"));

        _dispatcher.Dispatch(new DismissToast(Guid.NewGuid()));

        _store.State.Toasts.Count.ShouldBe(1);
    }
}
```

**Step 2: Run tests to verify they pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ToastStoreTests" -v n`
Expected: All 7 tests pass

**Step 3: Commit**

```bash
git add Tests/Unit/WebChat/Client/ToastStoreTests.cs
git commit -m "test(toast): add toast store unit tests"
```

---

## Task 4: Register Toast Store in DI

**Files:**
- Modify: `WebChat.Client/Extensions/ServiceCollectionExtensions.cs`

**Step 1: Add ToastStore using directive and registration**

Add at top of file after existing usings:
```csharp
using WebChat.Client.State.Toast;
```

Add inside `AddWebChatStores()` method after other store registrations (after line ~30):
```csharp
services.AddScoped<ToastStore>();
```

**Step 2: Verify build succeeds**

Run: `dotnet build WebChat.Client/WebChat.Client.csproj`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add WebChat.Client/Extensions/ServiceCollectionExtensions.cs
git commit -m "feat(toast): register toast store in DI"
```

---

## Task 5: ToastItem Component

**Files:**
- Create: `WebChat.Client/Components/Toast/ToastItem.razor`

**Step 1: Write ToastItem.razor**

```razor
@using WebChat.Client.State
@using WebChat.Client.State.Toast
@implements IDisposable
@inject IDispatcher Dispatcher

<div class="toast-item @(_isExiting ? "exiting" : "")">
    <span class="toast-message">@Toast.Message</span>
    <button class="toast-dismiss" @onclick="Dismiss" aria-label="Dismiss">
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill="none"
             stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
            <line x1="18" y1="6" x2="6" y2="18"></line>
            <line x1="6" y1="6" x2="18" y2="18"></line>
        </svg>
    </button>
</div>

@code {
    private const int AutoDismissMs = 8000;
    private const int ExitAnimationMs = 200;

    private Timer? _timer;
    private bool _isExiting;

    [Parameter, EditorRequired]
    public ToastItem Toast { get; set; } = null!;

    protected override void OnInitialized()
    {
        _timer = new Timer(_ => _ = StartDismissAsync(), null, AutoDismissMs, Timeout.Infinite);
    }

    private async Task StartDismissAsync()
    {
        await InvokeAsync(() =>
        {
            _isExiting = true;
            StateHasChanged();
        });

        await Task.Delay(ExitAnimationMs);
        Dispatcher.Dispatch(new DismissToast(Toast.Id));
    }

    private async Task Dismiss()
    {
        _timer?.Dispose();
        _timer = null;
        await StartDismissAsync();
    }

    public void Dispose()
    {
        _timer?.Dispose();
    }
}
```

**Step 2: Verify build succeeds**

Run: `dotnet build WebChat.Client/WebChat.Client.csproj`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add WebChat.Client/Components/Toast/ToastItem.razor
git commit -m "feat(toast): add ToastItem component with auto-dismiss"
```

---

## Task 6: ToastContainer Component

**Files:**
- Create: `WebChat.Client/Components/Toast/ToastContainer.razor`

**Step 1: Write ToastContainer.razor**

```razor
@using WebChat.Client.Components.Shared
@using WebChat.Client.State.Toast
@inherits StoreSubscriberComponent
@inject ToastStore ToastStore

<div class="toast-container">
    @foreach (var toast in _toasts)
    {
        <ToastItem Toast="toast" @key="toast.Id" />
    }
</div>

@code {
    private IReadOnlyList<ToastItem> _toasts = [];

    protected override void OnInitialized()
    {
        Subscribe(ToastStore.StateObservable, state => state.Toasts, toasts =>
        {
            _toasts = toasts;
            InvokeAsync(StateHasChanged);
        });
    }
}
```

**Step 2: Verify build succeeds**

Run: `dotnet build WebChat.Client/WebChat.Client.csproj`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add WebChat.Client/Components/Toast/ToastContainer.razor
git commit -m "feat(toast): add ToastContainer component"
```

---

## Task 7: Toast CSS Styles

**Files:**
- Modify: `WebChat.Client/wwwroot/css/app.css`

**Step 1: Add toast CSS at end of file**

```css
/* ===================================
   Toast Notifications
   =================================== */

.toast-container {
    position: fixed;
    top: 1rem;
    right: 1rem;
    z-index: 1000;
    display: flex;
    flex-direction: column;
    gap: 0.5rem;
    pointer-events: none;
}

.toast-item {
    pointer-events: auto;
    background: var(--bg-elevated);
    border-left: 4px solid var(--error);
    border-radius: 4px;
    padding: 0.75rem 1rem;
    box-shadow: var(--shadow-lg);
    display: flex;
    align-items: flex-start;
    gap: 0.75rem;
    max-width: 360px;
    animation: toastSlideIn 0.2s ease-out;
}

.toast-item.exiting {
    animation: toastSlideOut 0.2s ease-in forwards;
}

.toast-message {
    flex: 1;
    color: var(--text-primary);
    font-size: 0.875rem;
    line-height: 1.4;
    word-break: break-word;
}

.toast-dismiss {
    flex-shrink: 0;
    background: none;
    border: none;
    padding: 0.25rem;
    cursor: pointer;
    color: var(--text-muted);
    transition: color 0.15s ease;
    display: flex;
    align-items: center;
    justify-content: center;
}

.toast-dismiss:hover {
    color: var(--text-primary);
}

.toast-dismiss svg {
    width: 1rem;
    height: 1rem;
}

@keyframes toastSlideIn {
    from {
        transform: translateX(100%);
        opacity: 0;
    }
    to {
        transform: translateX(0);
        opacity: 1;
    }
}

@keyframes toastSlideOut {
    from {
        transform: translateX(0);
        opacity: 1;
    }
    to {
        transform: translateX(50%);
        opacity: 0;
    }
}

/* Mobile responsiveness */
@media (max-width: 640px) {
    .toast-container {
        top: 0.5rem;
        right: 0.5rem;
        left: 0.5rem;
    }

    .toast-item {
        max-width: 100%;
        padding: 0.5rem 0.75rem;
        font-size: 0.875rem;
    }
}
```

**Step 2: Verify build succeeds**

Run: `dotnet build WebChat.Client/WebChat.Client.csproj`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add WebChat.Client/wwwroot/css/app.css
git commit -m "feat(toast): add toast notification styles"
```

---

## Task 8: Mount ToastContainer in MainLayout

**Files:**
- Modify: `WebChat.Client/Layout/MainLayout.razor`

**Step 1: Add ToastContainer after closing div of main-layout**

Add `<ToastContainer />` just before the closing `</div>` tag of the root element (after `</main>`, before the final `</div>`):

Change:
```razor
    <main class="content">
        @Body
    </main>
</div>
```

To:
```razor
    <main class="content">
        @Body
    </main>
    <ToastContainer />
</div>
```

**Step 2: Verify build succeeds**

Run: `dotnet build WebChat.Client/WebChat.Client.csproj`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add WebChat.Client/Layout/MainLayout.razor
git commit -m "feat(toast): mount ToastContainer in MainLayout"
```

---

## Task 9: Add Transient Error Filtering Utility

**Files:**
- Create: `WebChat.Client/Services/Streaming/TransientErrorFilter.cs`

**Step 1: Write TransientErrorFilter.cs**

```csharp
namespace WebChat.Client.Services.Streaming;

public static class TransientErrorFilter
{
    private static readonly string[] TransientPatterns =
    [
        "OperationCanceled",
        "TaskCanceled",
        "operation was canceled"
    ];

    public static bool IsTransientException(Exception ex)
    {
        return ex is OperationCanceledException or TaskCanceledException;
    }

    public static bool IsTransientErrorMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return true;

        return TransientPatterns.Any(pattern =>
            message.Contains(pattern, StringComparison.OrdinalIgnoreCase));
    }
}
```

**Step 2: Verify build succeeds**

Run: `dotnet build WebChat.Client/WebChat.Client.csproj`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add WebChat.Client/Services/Streaming/TransientErrorFilter.cs
git commit -m "feat(toast): add transient error filter utility"
```

---

## Task 10: Transient Error Filter Unit Tests

**Files:**
- Create: `Tests/Unit/WebChat/Client/TransientErrorFilterTests.cs`

**Step 1: Write TransientErrorFilterTests.cs**

```csharp
using Shouldly;
using WebChat.Client.Services.Streaming;

namespace Tests.Unit.WebChat.Client;

public sealed class TransientErrorFilterTests
{
    [Theory]
    [InlineData(typeof(OperationCanceledException))]
    [InlineData(typeof(TaskCanceledException))]
    public void IsTransientException_WithCancellationException_ReturnsTrue(Type exceptionType)
    {
        var ex = (Exception)Activator.CreateInstance(exceptionType)!;

        TransientErrorFilter.IsTransientException(ex).ShouldBeTrue();
    }

    [Fact]
    public void IsTransientException_WithOtherException_ReturnsFalse()
    {
        var ex = new InvalidOperationException("Some error");

        TransientErrorFilter.IsTransientException(ex).ShouldBeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsTransientErrorMessage_WithEmptyMessage_ReturnsTrue(string? message)
    {
        TransientErrorFilter.IsTransientErrorMessage(message).ShouldBeTrue();
    }

    [Theory]
    [InlineData("OperationCanceled")]
    [InlineData("The OperationCanceled happened")]
    [InlineData("TaskCanceled exception")]
    [InlineData("The operation was canceled.")]
    [InlineData("OPERATIONCANCELED")] // case insensitive
    public void IsTransientErrorMessage_WithTransientMessage_ReturnsTrue(string message)
    {
        TransientErrorFilter.IsTransientErrorMessage(message).ShouldBeTrue();
    }

    [Theory]
    [InlineData("Connection reset by peer")]
    [InlineData("Internal server error")]
    [InlineData("Rate limit exceeded")]
    public void IsTransientErrorMessage_WithRealError_ReturnsFalse(string message)
    {
        TransientErrorFilter.IsTransientErrorMessage(message).ShouldBeFalse();
    }
}
```

**Step 2: Run tests to verify they pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~TransientErrorFilterTests" -v n`
Expected: All 6 tests pass

**Step 3: Commit**

```bash
git add Tests/Unit/WebChat/Client/TransientErrorFilterTests.cs
git commit -m "test(toast): add transient error filter unit tests"
```

---

## Task 11: Integrate Toast Dispatch in StreamingService

**Files:**
- Modify: `WebChat.Client/Services/Streaming/StreamingService.cs`

**Step 1: Add ToastStore to constructor and using**

Add using at top:
```csharp
using WebChat.Client.State.Toast;
```

Modify constructor to add ToastStore parameter (line 12-17):

Change:
```csharp
public sealed class StreamingService(
    IChatMessagingService messagingService,
    IDispatcher dispatcher,
    ITopicService topicService,
    TopicsStore topicsStore,
    StreamingStore streamingStore) : IStreamingService
```

To:
```csharp
public sealed class StreamingService(
    IChatMessagingService messagingService,
    IDispatcher dispatcher,
    ITopicService topicService,
    TopicsStore topicsStore,
    StreamingStore streamingStore,
    ToastStore toastStore) : IStreamingService
```

**Step 2: Update error chunk handling in StreamResponseAsync (lines 112-116)**

Change:
```csharp
                // Skip errors - reconnection flow handles recovery
                if (chunk.Error is not null)
                {
                    continue;
                }
```

To:
```csharp
                if (chunk.Error is not null)
                {
                    if (!TransientErrorFilter.IsTransientErrorMessage(chunk.Error))
                        dispatcher.Dispatch(new ShowError(chunk.Error));
                    continue;
                }
```

**Step 3: Update catch block in StreamResponseAsync (lines 193-196)**

Change:
```csharp
        catch
        {
            // Errors are silently ignored - reconnection flow handles recovery
        }
```

To:
```csharp
        catch (Exception ex) when (!TransientErrorFilter.IsTransientException(ex))
        {
            dispatcher.Dispatch(new ShowError(ex.Message));
        }
        catch
        {
            // Transient errors silently ignored - reconnection handles recovery
        }
```

**Step 4: Update error chunk handling in ResumeStreamResponseAsync (lines 228-232)**

Change:
```csharp
                // Skip errors - reconnection flow handles recovery
                if (chunk.Error is not null)
                {
                    continue;
                }
```

To:
```csharp
                if (chunk.Error is not null)
                {
                    if (!TransientErrorFilter.IsTransientErrorMessage(chunk.Error))
                        dispatcher.Dispatch(new ShowError(chunk.Error));
                    continue;
                }
```

**Step 5: Update catch block in ResumeStreamResponseAsync (lines 345-348)**

Change:
```csharp
        catch
        {
            // Errors are silently ignored - reconnection flow handles recovery
        }
```

To:
```csharp
        catch (Exception ex) when (!TransientErrorFilter.IsTransientException(ex))
        {
            dispatcher.Dispatch(new ShowError(ex.Message));
        }
        catch
        {
            // Transient errors silently ignored - reconnection handles recovery
        }
```

**Step 6: Verify build succeeds**

Run: `dotnet build WebChat.Client/WebChat.Client.csproj`
Expected: Build succeeded

**Step 7: Commit**

```bash
git add WebChat.Client/Services/Streaming/StreamingService.cs
git commit -m "feat(toast): dispatch ShowError on non-transient errors in StreamingService"
```

---

## Task 12: Update StreamingService Tests

**Files:**
- Modify: `Tests/Unit/WebChat/Client/StreamingServiceTests.cs`

**Step 1: Add ToastStore to test fixture**

Add using at top:
```csharp
using WebChat.Client.State.Toast;
```

Add field after other store fields (around line 23):
```csharp
private readonly ToastStore _toastStore;
```

Initialize in constructor (after line 33):
```csharp
_toastStore = new ToastStore(_dispatcher);
```

Update StreamingService instantiation (line 34):
```csharp
_service = new StreamingService(_messagingService, _dispatcher, _topicService, _topicsStore, _streamingStore, _toastStore);
```

Add to Dispose (after line 43):
```csharp
_toastStore.Dispose();
```

**Step 2: Add test for non-transient error showing toast**

Add new test method:
```csharp
[Fact]
public async Task StreamResponseAsync_WithNonTransientErrorChunk_ShowsToast()
{
    var topic = CreateTopic();
    _dispatcher.Dispatch(new MessagesLoaded(topic.TopicId, []));
    _dispatcher.Dispatch(new StreamStarted(topic.TopicId));

    _messagingService.EnqueueError("Connection reset by peer");

    await _service.StreamResponseAsync(topic, "test");

    _toastStore.State.Toasts.Count.ShouldBe(1);
    _toastStore.State.Toasts[0].Message.ShouldBe("Connection reset by peer");
}
```

**Step 3: Add test for transient error not showing toast**

Add new test method:
```csharp
[Fact]
public async Task StreamResponseAsync_WithTransientErrorChunk_DoesNotShowToast()
{
    var topic = CreateTopic();
    _dispatcher.Dispatch(new MessagesLoaded(topic.TopicId, []));
    _dispatcher.Dispatch(new StreamStarted(topic.TopicId));

    _messagingService.EnqueueError("OperationCanceled");

    await _service.StreamResponseAsync(topic, "test");

    _toastStore.State.Toasts.ShouldBeEmpty();
}
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~StreamingServiceTests" -v n`
Expected: All tests pass

**Step 5: Commit**

```bash
git add Tests/Unit/WebChat/Client/StreamingServiceTests.cs
git commit -m "test(toast): update StreamingService tests for toast integration"
```

---

## Task 13: Manual Integration Test

**Step 1: Run the application**

Run: `dotnet run --project WebChat/WebChat.csproj`

**Step 2: Verify toast appears**

Test by triggering an error condition (if possible) or temporarily add a test dispatch in a component.

**Step 3: Verify toast auto-dismisses after 8 seconds**

**Step 4: Verify toast can be manually dismissed with X button**

**Step 5: Verify mobile view is compact (resize browser to < 640px width)**

---

## Summary

This plan implements:

1. **Toast State Layer** (Tasks 1-4): Actions, state, store with reducers, DI registration
2. **Toast UI Layer** (Tasks 5-8): ToastItem with auto-dismiss timer, ToastContainer, CSS styles, MainLayout mounting
3. **Error Integration** (Tasks 9-12): Transient error filter, StreamingService integration with tests
4. **Verification** (Task 13): Manual integration test

All error chunks and exceptions in StreamingService now surface via toast notifications, except for transient cancellation errors which are still silently handled by the reconnection flow.
